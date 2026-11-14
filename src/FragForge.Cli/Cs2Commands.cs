using System.Text.Json;
using FragForge.Actions.Files;
using FragForge.Core.Logging;
using FragForge.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Cli;

/// <summary>
/// <c>fragforge cs2 &lt;verbo&gt;</c> con una lista CERRADA de verbos (Capa 8.6).
///
/// Por ahora solo <c>inspect</c>, y es de SOLO LECTURA. No hay verbo de escritura a propósito: escribir la
/// configuración de CS2 exige Steam y el juego cerrados y una copia de seguridad, y no es esta ronda.
/// </summary>
public static class Cs2Commands
{
    public static IReadOnlyList<string> Verbs { get; } = ["inspect"];

    public static async Task<int> RunAsync(IServiceProvider services, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var verb = parsed.Positionals.FirstOrDefault()?.ToLowerInvariant();
        if (verb is null || !Verbs.Contains(verb))
            throw new ArgumentException($"cs2 requiere un verbo de la lista cerrada: {string.Join(" | ", Verbs)}.");

        var inspector = services.GetRequiredService<Cs2Inspector>();
        var inspection = await inspector.InspectAsync(cancellationToken);

        if (parsed.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(inspection, JsonDefaults.Options));
            return inspection.HasAnyData ? ExitCodes.Success : ExitCodes.Preflight;
        }

        Console.WriteLine("CS2 — inspección de SOLO LECTURA (no se escribe nada)");
        Console.WriteLine(new string('-', 64));
        Console.WriteLine($"Ejecutable      : {inspection.ExecutablePath ?? "(no encontrado)"}");
        Console.WriteLine($"Cuenta activa   : {inspection.ActiveSteamAccountId ?? "(desconocida)"}");
        Console.WriteLine($"Userdata        : {inspection.ActiveUserDataDirectory ?? "(no localizada)"}");
        Console.WriteLine($"Cuentas halladas: {inspection.UserDataDirectories.Count}");
        Console.WriteLine($"Refresco (cfg)  : {(inspection.ConfiguredRefreshHz?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(no leído)")} Hz");
        Console.WriteLine();

        PrintSection("cs2_video.txt", inspection.VideoSettings);
        PrintSection("convars guardadas", inspection.StoredConvars);
        PrintSection("autoexec.cfg", inspection.AutoexecConvars);

        if (inspection.Conflicts.Count > 0)
        {
            Console.WriteLine("Conflictos entre lo guardado y autoexec.cfg:");
            foreach (var conflict in inspection.Conflicts) Console.WriteLine($"  - {conflict}");
            Console.WriteLine();
        }

        Console.WriteLine($"Opciones actuales : {inspection.LaunchOptions ?? "(no leídas)"}");
        Console.WriteLine($"Recomendadas      : {inspection.RecommendedLaunchOptions}");
        Console.WriteLine("  (se muestran como texto: no se escribe localconfig.vdf)");

        if (inspection.Notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Notas:");
            foreach (var note in inspection.Notes) Console.WriteLine($"  - {note}");
        }

        return inspection.HasAnyData ? ExitCodes.Success : ExitCodes.Preflight;
    }

    private static void PrintSection(string title, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0) { Console.WriteLine($"{title}: (sin datos)"); Console.WriteLine(); return; }
        Console.WriteLine($"{title}:");
        foreach (var pair in values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {pair.Key,-40} = {pair.Value}");
        Console.WriteLine();
    }
}
