using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Core.Recovery;

public sealed record RecoveryFile(string Name, string RelativePath, string Sha256, long SizeBytes);

public sealed record RecoveryIndex
{
    public int SchemaVersion { get; init; } = 1;
    public required string SnapshotId { get; init; }
    public required string OperationId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string OsBuild { get; init; } = "";
    public string DataRoot { get; init; } = "";
    public IReadOnlyList<RecoveryFile> Files { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record RecoveryResult(string Directory, RecoveryIndex Index);

/// <summary>
/// Red de recuperación INDEPENDIENTE de la aplicación.
///
/// Genera en <c>%ProgramData%\FragForge\recovery\&lt;fecha&gt;\</c> lo necesario para deshacer a mano
/// aunque FragForge no arranque, se haya reinstalado Windows o el perfil esté corrupto:
///
/// <list type="bullet">
/// <item><c>manual-revert.md</c>: instrucciones en es-MX y en-US, con la ruta OFFLINE
///   (<c>ControlSet001</c>) para editar el registro desde WinRE.</item>
/// <item><c>registry-original.reg</c>: valores originales con el formato que ya exporta
///   <see cref="RegistryBackupExporter"/> (los valores ausentes se representan como borrado del
///   valor, nunca borrado de la clave).</item>
/// <item><c>services-original.csv</c>: modo de inicio original de cada servicio tocado, con las
///   instrucciones equivalentes en texto.</item>
/// <item><c>tasks-original.xml</c>: XML de las tareas capturadas.</item>
/// <item>copias de los artefactos <c>.pow</c> y <c>.bcd</c> que el motor ya generó.</item>
/// <item><c>recovery-index.json</c>: SHA-256 de todo lo anterior.</item>
/// </list>
///
/// Solo escribe bajo el directorio de datos. No toca el sistema.
/// </summary>
public sealed class RecoveryWriter(FragForgePaths paths)
{
    public string Root => Path.Combine(paths.Root, "recovery");

    public async Task<RecoveryResult> WriteAsync(SystemSnapshot snapshot, string operationId, CancellationToken cancellationToken = default)
    {
        var stamp = snapshot.CreatedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(Root, stamp);
        Directory.CreateDirectory(directory);

        var registryStates = snapshot.Tweaks.SelectMany(t => t.Actions).Where(a => a.State is RegistryActionState).Select(a => (RegistryActionState)a.State).ToArray();
        var serviceStates = snapshot.Tweaks.SelectMany(t => t.Actions).Where(a => a.State is ServiceActionState).Select(a => (ServiceActionState)a.State).ToArray();
        var taskStates = snapshot.Tweaks.SelectMany(t => t.Actions).Where(a => a.State is ScheduledTaskActionState).Select(a => (ScheduledTaskActionState)a.State).ToArray();
        var notes = new List<string>();

        // 1) Registro original (.reg), reutilizando el exportador existente.
        if (registryStates.Length > 0)
            await RegistryBackupExporter.WriteAsync(Path.Combine(directory, "registry-original.reg"), registryStates, cancellationToken);
        else notes.Add("No había valores de registro capturados.");

        // 2) Tabla CSV de servicios.
        await File.WriteAllTextAsync(Path.Combine(directory, "services-original.csv"), BuildServiceCsv(serviceStates), new UTF8Encoding(false), cancellationToken);

        // 3) XML de tareas.
        var taskXml = BuildTaskXml(taskStates);
        if (taskXml.Length > 0) await File.WriteAllTextAsync(Path.Combine(directory, "tasks-original.xml"), taskXml, new UTF8Encoding(false), cancellationToken);
        else notes.Add("No había tareas programadas capturadas.");

        // 4) Artefactos .pow/.bcd que el motor ya generó para este snapshot.
        var copied = await CopyArtifactsAsync(snapshot, directory, notes, cancellationToken);

        // 5) Manual bilingüe.
        await File.WriteAllTextAsync(Path.Combine(directory, "manual-revert.md"), BuildManual(snapshot, operationId, serviceStates, taskStates, copied, notes), new UTF8Encoding(false), cancellationToken);

        // 6) Índice con SHA-256.
        var files = new List<RecoveryFile>();
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            if (info.Name == "recovery-index.json") continue;
            files.Add(new RecoveryFile(info.Name, info.Name, await Sha256Async(file, cancellationToken), info.Length));
        }
        var index = new RecoveryIndex
        {
            SnapshotId = snapshot.Id,
            OperationId = operationId,
            OsBuild = snapshot.HardwareBuild ?? "",
            DataRoot = paths.Root,
            Files = files,
            Notes = notes
        };
        await AtomicFile.WriteAllTextAsync(Path.Combine(directory, "recovery-index.json"), JsonSerializer.Serialize(index, JsonDefaults.Options), cancellationToken);
        return new RecoveryResult(directory, index);
    }

    /// <summary>Verifica que cada archivo del índice siga teniendo el SHA-256 registrado.</summary>
    public async Task<IReadOnlyList<string>> VerifyAsync(string directory, CancellationToken cancellationToken = default)
    {
        var indexPath = Path.Combine(directory, "recovery-index.json");
        if (!File.Exists(indexPath)) return ["No existe recovery-index.json en " + directory];
        var index = JsonSerializer.Deserialize<RecoveryIndex>(await File.ReadAllTextAsync(indexPath, cancellationToken), JsonDefaults.Options);
        if (index is null) return ["recovery-index.json ilegible."];

        var problems = new List<string>();
        foreach (var file in index.Files)
        {
            var full = Path.Combine(directory, file.RelativePath);
            if (!File.Exists(full)) { problems.Add($"Falta {file.RelativePath}."); continue; }
            var actual = await Sha256Async(full, cancellationToken);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{file.RelativePath}: SHA-256 no coincide (esperado {file.Sha256}, obtenido {actual}).");
        }
        return problems;
    }

    /// <summary>Copia el directorio de recuperación a otra ruta (por ejemplo, una memoria USB).</summary>
    public Task<string> CopyToAsync(string directory, string destination, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var target = Path.Combine(Path.GetFullPath(destination), Path.GetFileName(directory));
        return Task.Run(() =>
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
            return target;
        }, cancellationToken);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private Task<IReadOnlyList<string>> CopyArtifactsAsync(SystemSnapshot snapshot, string directory, List<string> notes, CancellationToken cancellationToken)
    {
        var copied = new List<string>();
        // Snapshot.Artifacts son rutas relativas al directorio del snapshot (así las escribe FileSnapshotStore).
        var artifactRoot = Path.Combine(paths.Snapshots, snapshot.Id);
        foreach (var artifact in snapshot.Artifacts.Where(a => a.EndsWith(".pow", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".bcd", StringComparison.OrdinalIgnoreCase)))
        {
            var source = Path.Combine(artifactRoot, artifact.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) { notes.Add($"El artefacto {artifact} figura en el snapshot pero no está en disco."); continue; }
            var name = Path.GetFileName(source);
            File.Copy(source, Path.Combine(directory, name), overwrite: true);
            copied.Add(name);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(copied);
    }

    private static string BuildServiceCsv(IReadOnlyList<ServiceActionState> states)
    {
        var builder = new StringBuilder("servicio;modo_inicio_original;estado_original;delayed_original;instruccion_equivalente\n");
        foreach (var state in states.OrderBy(s => s.Service.Name, StringComparer.OrdinalIgnoreCase))
        {
            var service = state.Service;
            if (!service.Exists) { builder.Append(Csv(service.Name)).Append(";no_existia;;;No había que restaurar nada.").Append('\n'); continue; }
            var instruction = service.StartMode switch
            {
                "Auto" or "Automatic" => $"sc.exe config \"{service.Name}\" start= auto (o en services.msc: Inicio automático){(service.DelayedAutoStart == true ? " + \"Inicio retrasado\" activado" : "")}",
                "Manual" => $"sc.exe config \"{service.Name}\" start= demand (o en services.msc: Manual)",
                "Disabled" => $"sc.exe config \"{service.Name}\" start= disabled (o en services.msc: Deshabilitado)",
                "Boot" => $"sc.exe config \"{service.Name}\" start= boot",
                "System" => $"sc.exe config \"{service.Name}\" start= system",
                _ => "Modo original desconocido: revisa el valor Start en el registro."
            };
            builder.Append(Csv(service.Name)).Append(';').Append(Csv(service.StartMode ?? "")).Append(';').Append(Csv(service.State ?? "")).Append(';')
                .Append(service.DelayedAutoStart?.ToString() ?? "").Append(';').Append(Csv(instruction)).Append('\n');
        }
        return builder.ToString();
    }

    private static string Csv(string value) => value.Contains(';', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal)
        ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        : value;

    private static string BuildTaskXml(IReadOnlyList<ScheduledTaskActionState> states)
    {
        var tasks = states.Where(s => s.Exists && !string.IsNullOrWhiteSpace(s.XmlDefinition)).ToArray();
        if (tasks.Length == 0) return "";
        var builder = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<tareas fragforge=\"agrupadas-por-tarea\">\n");
        foreach (var task in tasks)
        {
            builder.Append("  <tarea ruta=\"").Append(System.Security.SecurityElement.Escape(task.Path) ?? "").Append("\" enabled=\"").Append(task.Enabled ? "true" : "false").Append("\">\n");
            builder.Append("    <definicion><![CDATA[").Append(task.XmlDefinition!.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal)).Append("]]></definicion>\n");
            builder.Append("  </tarea>\n");
        }
        builder.Append("</tareas>\n");
        return builder.ToString();
    }

    private static string BuildManual(SystemSnapshot snapshot, string operationId, IReadOnlyList<ServiceActionState> services, IReadOnlyList<ScheduledTaskActionState> tasks, IReadOnlyList<string> copied, List<string> notes)
    {
        var b = new StringBuilder();
        var tweaks = string.Join(", ", snapshot.Tweaks.Select(t => t.TweakId));
        b.Append("# FragForge — Recuperación manual / Manual recovery\n\n");
        b.Append("Snapshot: `").Append(snapshot.Id).Append("` · operación `").Append(operationId).Append("` \n");
        b.Append("Capturado: ").Append(snapshot.CreatedAt.ToString("u", CultureInfo.InvariantCulture)).Append(" · Windows build ").Append(snapshot.HardwareBuild ?? "desconocido").Append("\n\n");
        b.Append("Tweaks en este snapshot: ").Append(tweaks.Length == 0 ? "(ninguno)" : tweaks).Append("\n\n");

        b.Append("## es-MX\n\n");
        b.Append("Este directorio existe para que puedas deshacer los cambios **sin** FragForge: sin la app, sin .NET, desde otro equipo o desde el propio Windows.\n\n");
        b.Append("1. **Camino normal (recomendado).** Abre FragForge y ejecuta `fragforge revert --snapshot ").Append(snapshot.Id).Append("`. Eso usa el motor y verifica el resultado.\n");
        b.Append("2. **Sin la app.** Doble clic en `registry-original.reg` y acepta la fusión. Ese archivo contiene los valores originales tal como estaban antes del cambio; un valor que no existía se restaura como *borrado del valor*, no de la clave.\n");
        b.Append("3. **Servicios.** `services-original.csv` trae el modo de inicio original y la instrucción exacta equivalente en la última columna. Se puede ejecutar como administrador.\n");
        if (tasks.Count > 0) b.Append("4. **Tareas programadas.** `tasks-original.xml` agrupa la definición XML de cada tarea tocada. Para restaurar una: copia su bloque `<definicion>` a un archivo propio y ejecuta `schtasks /Create /XML archivo.xml /TN \"\\Ruta\\Tarea\"` como administrador.\n");
        if (copied.Count > 0) b.Append("5. **Energía y arranque.** Se copiaron ").Append(string.Join(", ", copied)).Append(". Un `.pow` se importa con `powercfg /import <archivo> <GUID>`; un `.bcd` con `bcdedit /import <archivo>` **solo desde WinRE**, porque `bcdedit` no puede importar el almacén en uso.\n");

        b.Append("\n### Modo seguro / WinRE (ruta OFFLINE)\n\n");
        b.Append("Si Windows no arranca o el servicio que tocaste impide el inicio:\n\n");
        b.Append("1. Mantén `Shift` mientras pulsas *Reiniciar*, o enciende y apaga tres veces para entrar en WinRE.\n");
        b.Append("2. `Solucionar problemas` → `Opciones avanzadas` → `Símbolo del sistema`.\n");
        b.Append("3. Identifica la unidad de Windows: `bcdedit /enum` muestra la partición (`C:` normalmente, pero en WinRE suele ser `D:` o `E:`).\n");
        b.Append("4. El registro offline vive en `<Unidad>:\\Windows\\System32\\config\\`. El archivo del servicio es `SYSTEM` y el control set activo es `ControlSet001`.\n");
        b.Append("5. Carga el hive: `reg load HKLM\\OFFLINE <Unidad>:\\Windows\\System32\\config\\SYSTEM`.\n");
        b.Append("6. Cambia el modo de inicio: `reg add \"HKLM\\OFFLINE\\ControlSet001\\Services\\<servicio>\" /v Start /t REG_DWORD /d 3 /f` (3 = Manual; 2 = Automático; 4 = Deshabilitado).\n");
        b.Append("7. Descarga el hive: `reg unload HKLM\\OFFLINE` y reinicia.\n\n");
        b.Append("> Advertencia: si el servicio es de núcleo, NO lo deshabilites. Vuelve a `Manual` y deja que Windows lo arranque.\n\n");

        b.Append("## en-US\n\n");
        b.Append("This directory exists so you can undo the changes **without** FragForge: without the app, without .NET, from another machine, or from Windows itself.\n\n");
        b.Append("1. **Normal path (recommended).** Open FragForge and run `fragforge revert --snapshot ").Append(snapshot.Id).Append("`. That uses the engine and verifies the result.\n");
        b.Append("2. **Without the app.** Double-click `registry-original.reg` and accept the merge. It contains the original values; a value that did not exist is restored as a *value deletion*, never a key deletion.\n");
        b.Append("3. **Services.** `services-original.csv` has the original start mode and the exact equivalent command in the last column.\n");
        if (tasks.Count > 0) b.Append("4. **Scheduled tasks.** `tasks-original.xml` groups the XML definition of each touched task. To restore one, copy its `<definicion>` block into its own file and run `schtasks /Create /XML file.xml /TN \"\\Path\\Task\"` as administrator.\n");
        if (copied.Count > 0) b.Append("5. **Power and boot.** Copied: ").Append(string.Join(", ", copied)).Append(". A `.pow` is imported with `powercfg /import <file> <GUID>`; a `.bcd` with `bcdedit /import <file>` **only from WinRE**, because bcdedit cannot import the store in use.\n");

        b.Append("\n### Safe mode / WinRE (OFFLINE path)\n\n");
        b.Append("1. Hold `Shift` while clicking *Restart*, or power-cycle three times to reach WinRE.\n");
        b.Append("2. `Troubleshoot` → `Advanced options` → `Command Prompt`.\n");
        b.Append("3. Find the Windows volume: `bcdedit /enum` (often `D:` or `E:` inside WinRE).\n");
        b.Append("4. The offline registry lives in `<Drive>:\\Windows\\System32\\config\\`. The service database is `SYSTEM` and the active control set is `ControlSet001`.\n");
        b.Append("5. Load it: `reg load HKLM\\OFFLINE <Drive>:\\Windows\\System32\\config\\SYSTEM`.\n");
        b.Append("6. Set the start mode: `reg add \"HKLM\\OFFLINE\\ControlSet001\\Services\\<service>\" /v Start /t REG_DWORD /d 3 /f` (3 = Manual, 2 = Automatic, 4 = Disabled).\n");
        b.Append("7. Unload: `reg unload HKLM\\OFFLINE`, then reboot.\n\n");
        b.Append("> Warning: if the service is a core service, do NOT disable it. Set it back to `Manual` and let Windows start it.\n");

        if (notes.Count > 0)
        {
            b.Append("\n## Notas del generador / Generator notes\n\n");
            foreach (var note in notes) b.Append("- ").Append(note).Append('\n');
        }
        return b.ToString();
    }
}
