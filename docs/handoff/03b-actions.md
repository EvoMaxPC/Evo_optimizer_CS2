## 3b. FragForge.Actions — código completo

### `src/FragForge.Actions/ActionParameters.cs`  <sub>(31 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Logging;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions;

internal static class ActionParameters
{
    public static T Parse<T>(ActionSpec specification)
    {
        var json = specification.Parameters.ValueKind == JsonValueKind.Undefined ? "{}" : specification.Parameters.GetRawText();
        try { return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options) ?? throw new JsonException("Null action parameters."); }
        catch (JsonException ex) { throw new CatalogValidationException($"Parámetros inválidos para la acción '{specification.Kind}'.", [ex.Message]); }
    }
    public static string ResolveToken(string value, ActionExecutionContext context) => value switch
    {
        "{Cs2Executable}" => context.Hardware.Games.Cs2ExecutablePath ?? throw new ActionNotApplicableException("No se detectó cs2.exe; usa la ruta manual."),
        "{Cs2Directory}" => Path.GetDirectoryName(context.Hardware.Games.Cs2ExecutablePath ?? throw new ActionNotApplicableException("No se detectó CS2."))!,
        _ => value
    };
}
internal sealed record RegistryActionParameters { public IReadOnlyList<RegistryValueTarget> Values { get; init; } = []; }
internal sealed record RegistryValueTarget
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public JsonElement Value { get; init; }
    public string Mode { get; init; } = "Set";
}
````

### `src/FragForge.Actions/Bcd/BcdeditAction.cs`  <sub>(68 líneas)</sub>

````csharp
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Bcd;

public sealed class BcdeditAction(IProcessRunner runner, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly BcdParameters _parameters = ActionParameters.Parse<BcdParameters>(specification);
    public string Kind => "Bcdedit";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureBcdAllowed(_parameters.Element);
        if (_parameters.Element != _parameters.Element.ToLowerInvariant() || Regex.IsMatch(_parameters.Element, "[^a-z0-9]")) throw new CatalogValidationException("Invalid BCD element.", [_parameters.Element]);
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureBcdAllowed(_parameters.Element);
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable("bcdedit.exe"), Arguments = ["/enum", "{current}", "/v"], Timeout = TimeSpan.FromSeconds(60) }, cancellationToken);
        if (result.TimedOut) throw new ProcessFailedException("bcdedit.exe", result.ExitCode, "timeout");
        var match = Regex.Match(result.StandardOutput, @"^\s*" + Regex.Escape(_parameters.Element) + @"\s+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        string? export = null;
        if (context.ArtifactDirectory is not null)
        {
            export = $"bcd-{_parameters.Element}.bcd";
            var target = Path.Combine(context.ArtifactDirectory, export);
            if (File.Exists(target)) throw new InvalidOperationException($"Ya existe la exportación BCD '{target}'; no se sobrescribe una copia de recuperación.");
            await RunAsync(["/export", target], cancellationToken);
            if (!File.Exists(target)) throw new InvalidOperationException("bcdedit /export no produjo el archivo de recuperación; no se aplicará el cambio BCD.");
        }
        return new BcdActionState { Element = _parameters.Element, Existed = match.Success, PreviousValue = match.Success ? match.Groups[1].Value : null, ExportArtifact = export ?? "" };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (BcdActionState)before;
        if (string.IsNullOrEmpty(state.ExportArtifact)) throw new InvalidOperationException("BCD changes require a persisted bcdedit /export backup.");
        policy.EnsureBcdAllowed(_parameters.Element);
        await RunAsync(["/set", "{current}", _parameters.Element, _parameters.Value], cancellationToken);
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var current = await CaptureAsync(context with { ArtifactDirectory = null }, cancellationToken);
        var state = (BcdActionState)current;
        return state.Existed && string.Equals(state.PreviousValue, _parameters.Value, StringComparison.OrdinalIgnoreCase) ? VerificationResult.Match($"BCD {_parameters.Element}={_parameters.Value}.") : VerificationResult.Missing("BCD no coincide.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (BcdActionState)before;
        policy.EnsureBcdAllowed(state.Element);
        if (state.Existed) await RunAsync(["/set", "{current}", state.Element, state.PreviousValue ?? ""], cancellationToken);
        else await RunAsync(["/deletevalue", "{current}", state.Element], cancellationToken);
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (BcdActionState)before;
        var current = (BcdActionState)await CaptureAsync(context with { ArtifactDirectory = null }, cancellationToken);
        return current.Existed == state.Existed && string.Equals(current.PreviousValue, state.PreviousValue, StringComparison.OrdinalIgnoreCase) ? VerificationResult.Match("BCD restaurado.") : VerificationResult.Missing("BCD no volvió al estado capturado.");
    }
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult($"BCD {{current}} {_parameters.Element}={_parameters.Value}; exportación completa previa; revert restaura o elimina el elemento. Requiere Advanced y doble confirmación.");
    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable("bcdedit.exe"), Arguments = arguments, Timeout = TimeSpan.FromSeconds(60) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("bcdedit.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError + result.StandardOutput);
        return result;
    }
}
internal sealed record BcdParameters { public required string Element { get; init; } public required string Value { get; init; } }
````

### `src/FragForge.Actions/DependencyInjection/ServiceCollectionExtensions.cs`  <sub>(18 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Actions.Diagnostics;
using FragForge.Actions.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace FragForge.Actions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFragForgeActions(this IServiceCollection services)
    {
        services.AddSingleton<IProtectionPolicy>(Core.Persistence.ProtectionPolicy.CreateDefault());
        services.AddSingleton<IRegistry>(sp => OperatingSystem.IsWindows() ? new ProtectedRegistry(new WindowsRegistry(), sp.GetRequiredService<IProtectionPolicy>()) : new ProtectedRegistry(new UnsupportedRegistry(), sp.GetRequiredService<IProtectionPolicy>()));
        services.AddSingleton<ITweakActionFactory, TweakActionFactory>();
        services.AddSingleton<IEnumerable<IDiagnosticProbe>>(sp => DiagnosticProbeFactory.CreateDefaults(sp.GetRequiredService<IRegistry>()));
        return services;
    }
}
````

### `src/FragForge.Actions/DependencyInjection/TweakActionFactory.cs`  <sub>(37 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Actions.Bcd;
using FragForge.Actions.Diagnostics;
using FragForge.Actions.Files;
using FragForge.Actions.Network;
using FragForge.Actions.Power;
using FragForge.Actions.Registry;
using FragForge.Actions.Scripting;
using FragForge.Actions.Services;
using FragForge.Actions.Tasks;

namespace FragForge.Actions.DependencyInjection;

public sealed class TweakActionFactory(IRegistry registry, IProtectionPolicy policy, IServiceController services, IScheduledTaskStore tasks, INetworkAdapterController network, IProcessRunner runner, IEnumerable<IDiagnosticProbe> probes) : ITweakActionFactory
{
    private readonly IReadOnlyList<IDiagnosticProbe> _probes = probes.ToArray();
    public IReadOnlySet<string> SupportedKinds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Registry", "IfeoPerfOptions", "Service", "ScheduledTask", "PowerPlan", "Bcdedit", "NetAdapterProperty", "FileEdit", "PowerShell", "InspectOnly" };
    public ITweakAction Create(ActionSpec specification)
    {
        if (!SupportedKinds.Contains(specification.Kind)) throw new CatalogValidationException($"Acción desconocida '{specification.Kind}'.", [specification.Kind]);
        return specification.Kind.ToLowerInvariant() switch
        {
            "registry" => new RegistryAction(registry, policy, specification),
            "ifeoperfoptions" => new IfeoPerfOptionsAction(registry, policy, specification),
            "service" => new ServiceAction(services, policy, specification),
            "scheduledtask" => new ScheduledTaskAction(tasks, policy, specification),
            "powerplan" => new PowerPlanAction(runner, specification),
            "bcdedit" => new BcdeditAction(runner, policy, specification),
            "netadapterproperty" => new NetAdapterPropertyAction(network, policy, specification),
            "fileedit" => new FileEditAction(policy, specification),
            "powershell" => new PowerShellAction(runner, policy, specification),
            "inspectonly" => new InspectOnlyAction(_probes, specification),
            _ => throw new CatalogValidationException($"Acción desconocida '{specification.Kind}'.", [specification.Kind])
        };
    }
}
````

### `src/FragForge.Actions/Diagnostics/DiagnosticProbes.cs`  <sub>(34 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Diagnostics;

public sealed class RegistryDiagnosticProbe(string key, IRegistry registry, string path, string name, Func<RegistryReadResult, (string Value, string Note)> interpret) : IDiagnosticProbe
{
    public string Key { get; } = key;
    public async Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        var result = await registry.ReadValueAsync(path, name, cancellationToken);
        var (value, note) = interpret(result);
        return new DiagnosticObservation(Key, value, path + "\\" + name, result.ValueExists ? "High" : "Unknown", note);
    }
}
public sealed class ProfileDiagnosticProbe(string key, Func<HardwareProfile, DiagnosticObservation> read) : IDiagnosticProbe
{
    public string Key { get; } = key;
    public Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default) => Task.FromResult(read(profile));
}
public static class DiagnosticProbeFactory
{
    public static IReadOnlyList<IDiagnosticProbe> CreateDefaults(IRegistry registry) =>
    [
        new RegistryDiagnosticProbe("hags", registry, @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", r => !r.ValueExists ? ("Unknown/default", "Ausente significa comportamiento predeterminado del controlador; no equivale necesariamente a desactivado.") : (Convert.ToString(r.Data) == "2" ? "Enabled" : Convert.ToString(r.Data) == "1" ? "Disabled" : $"Unknown ({r.Data})", "HwSchMode observado; no se modifica.")),
        new RegistryDiagnosticProbe("mpo", registry, @"HKLM\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", r => !r.ValueExists ? ("Unknown/default", "Ausente no prueba el estado efectivo de MPO; no se modifica.") : (Convert.ToString(r.Data) == "5" ? "DisabledByOverride" : $"Override={r.Data}", "OverlayTestMode=5 es el override conocido; otros valores requieren diagnóstico.")),
        new ProfileDiagnosticProbe("vbs", p => new("vbs", p.Security.Vbs.ToString(), "DeviceGuard/WMI", p.Security.Vbs == FeatureState.Unknown ? "Unknown" : "Medium", "No se cambia VBS en el perfil por defecto.")),
        new ProfileDiagnosticProbe("hvci", p => new("hvci", p.Security.Hvci.ToString(), "DeviceGuard/registro", p.Security.Hvci == FeatureState.Unknown ? "Unknown" : "Medium", "No se cambia HVCI en el perfil por defecto.")),
        new ProfileDiagnosticProbe("secureBoot", p => new("secureBoot", p.Security.SecureBoot.ToString(), "Registro/UEFI", p.Security.SecureBoot == FeatureState.Unknown ? "Unknown" : "Medium", "Solo lectura.")),
        new ProfileDiagnosticProbe("dualChannel", p => new("dualChannel", p.Memory.Channels.ToString(), "Win32_PhysicalMemory", p.Memory.Channels == MemoryChannelState.Unknown ? "Unknown" : "Medium", p.Memory.ChannelEvidence + " Dos módulos no prueban dual-channel.")),
        new ProfileDiagnosticProbe("powerLimits", p => new("powerLimits", $"PL1={p.Power.Pl1Watts?.ToString() ?? "Unknown"} W; PL2={p.Power.Pl2Watts?.ToString() ?? "Unknown"} W", p.Power.PowerLimitSource, "Unknown", "DTT/IPF puede gobernar límites dinámicos; no se inventan valores.")),
        new ProfileDiagnosticProbe("ssdHealth", p => new("ssdHealth", string.Join("; ", p.Disks.Select(d => $"{d.Model}: {d.Health}, SMART={(d.SmartPredictFailure?.ToString() ?? "Unavailable")}")), "WMI/MSFT_PhysicalDisk", "Medium", "Salud básica, no sustituye un análisis SMART completo."))
    ];
}
````

### `src/FragForge.Actions/Diagnostics/InspectOnlyAction.cs`  <sub>(38 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Diagnostics;

public sealed class InspectOnlyAction : ITweakAction
{
    private readonly IReadOnlyList<IDiagnosticProbe> _probes;
    private readonly InspectionParameters _parameters;
    public string Kind => "InspectOnly";
    public InspectOnlyAction(IEnumerable<IDiagnosticProbe> probes, ActionSpec specification)
    {
        _parameters = ActionParameters.Parse<InspectionParameters>(specification);
        var available = probes.ToArray();
        var keys = _parameters.Probes.Count == 0 ? available.Select(p => p.Key).ToArray() : _parameters.Probes;
        var missing = keys.Except(available.Select(p => p.Key), StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0) throw new CatalogValidationException("Unknown diagnostic probes.", missing);
        _probes = available.Where(p => keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var observations = new List<DiagnosticObservation>();
        foreach (var probe in _probes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { observations.Add(await probe.ReadAsync(context.Hardware, cancellationToken)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { observations.Add(new DiagnosticObservation(probe.Key, "Unknown", probe.GetType().Name, "Unknown", ex.Message)); }
        }
        return new InspectionActionState { Observations = observations };
    }
    public Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult(new VerificationResult(VerificationOutcome.Unknown, "Solo lectura: no hay estado objetivo que verificar."));
    public Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult(VerificationResult.Match("InspectOnly no realizó cambios."));
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult("Solo lectura, cero escrituras. Sondear: " + string.Join(", ", _probes.Select(p => p.Key)) + ".");
}
internal sealed record InspectionParameters { public IReadOnlyList<string> Probes { get; init; } = []; }
````

### `src/FragForge.Actions/Files/FileEditAction.cs`  <sub>(100 líneas)</sub>

````csharp
using System.Text;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Actions.Files;

public sealed class FileEditAction(IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly FileEditParameters _parameters = ActionParameters.Parse<FileEditParameters>(specification);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public string Kind => "FileEdit";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        Validate();
        policy.EnsureFileAllowed(ResolvePath(context));
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(context);
        policy.EnsureFileAllowed(path);
        if (!File.Exists(path))
        {
            var missing = new List<string>();
            var directory = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) { missing.Add(directory); directory = Path.GetDirectoryName(directory); }
            return new FileActionState { Path = path, Existed = false, MissingDirectories = missing };
        }
        var info = new FileInfo(path);
        if (info.Length > _parameters.MaxBytes) throw new FragForgeException("file_too_large", $"{path} supera el límite de {_parameters.MaxBytes} bytes.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new FileActionState { Path = path, Existed = true, ContentBase64 = Convert.ToBase64String(bytes), LastWriteTimeUtc = info.LastWriteTimeUtc };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (FileActionState)before;
        policy.EnsureFileAllowed(state.Path);
        var current = File.Exists(state.Path) ? await File.ReadAllBytesAsync(state.Path, cancellationToken) : null;
        var original = state.ContentBase64 is null ? null : Convert.FromBase64String(state.ContentBase64);
        if (!BytesEqual(current, original)) throw new VerificationFailedException("El archivo cambió desde la captura; no se sobrescribe.");
        var transformed = Transform(original ?? []);
        await AtomicFile.WriteAllBytesAsync(state.Path, transformed, cancellationToken);
        context.Report("apply", 1, $"{state.Path}: { _parameters.Edits.Count } ediciones verificables.");
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (FileActionState)before;
        var expected = Transform(state.ContentBase64 is null ? [] : Convert.FromBase64String(state.ContentBase64));
        var current = File.Exists(state.Path) ? await File.ReadAllBytesAsync(state.Path, cancellationToken) : null;
        return BytesEqual(current, expected) ? VerificationResult.Match("Contenido UTF-8 verificado byte a byte.") : VerificationResult.Missing("El archivo no coincide con la transformación esperada.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (FileActionState)before;
        policy.EnsureFileAllowed(state.Path);
        if (state.Existed)
        {
            await AtomicFile.WriteAllBytesAsync(state.Path, Convert.FromBase64String(state.ContentBase64 ?? ""), cancellationToken);
            if (state.LastWriteTimeUtc is not null) File.SetLastWriteTimeUtc(state.Path, state.LastWriteTimeUtc.Value.UtcDateTime);
        }
        else
        {
            if (File.Exists(state.Path)) File.Delete(state.Path);
            foreach (var directory in state.MissingDirectories) if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Archivo restaurado byte a byte.") : VerificationResult.Missing("El archivo no volvió al estado capturado.");
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(context);
        var current = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "(archivo nuevo)";
        return $"{path}: {_parameters.Edits.Count} sustituciones regex con recuento esperado; UTF-8/BOM preservados; copia completa reversible. Estado actual: {current[..Math.Min(current.Length, 160)]}";
    }
    private byte[] Transform(byte[] original)
    {
        var hasBom = original.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = StrictUtf8.GetString(hasBom ? original[3..] : original);
        foreach (var edit in _parameters.Edits)
        {
            var regex = new Regex(edit.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var count = regex.Matches(text).Count;
            if (count != edit.ExpectedMatches) throw new VerificationFailedException($"'{edit.Pattern}' encontró {count} coincidencias; esperaba {edit.ExpectedMatches}.");
            text = regex.Replace(text, edit.Replacement);
        }
        var bytes = StrictUtf8.GetBytes(text);
        return hasBom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray() : bytes;
    }
    private string ResolvePath(ActionExecutionContext context) => Path.GetFullPath(ActionParameters.ResolveToken(_parameters.Path, context));
    private void Validate()
    {
        if (_parameters.MaxBytes is <= 0 or > 1048576) throw new CatalogValidationException("FileEdit MaxBytes must be 1..1048576.", [_parameters.MaxBytes.ToString()]);
        if (_parameters.Edits.Count == 0) throw new CatalogValidationException("FileEdit requires edits.", ["edits"]);
        foreach (var edit in _parameters.Edits) { _ = new Regex(edit.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); if (edit.ExpectedMatches < 0) throw new CatalogValidationException("ExpectedMatches cannot be negative.", [edit.Pattern]); }
    }
    private static bool BytesEqual(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}
internal sealed record FileEditParameters { public required string Path { get; init; } public int MaxBytes { get; init; } = 1048576; public IReadOnlyList<FileTextEdit> Edits { get; init; } = []; }
internal sealed record FileTextEdit { public required string Pattern { get; init; } public string Replacement { get; init; } = ""; public int ExpectedMatches { get; init; } = 1; }
````

### `src/FragForge.Actions/Files/SteamGameLocator.cs`  <sub>(46 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Files;

public sealed class SteamGameLocator(IRegistry registry) : IGameLocator
{
    private const string RelativeExecutable = @"steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe";
    public async Task<GameInfo> LocateCs2Async(CancellationToken cancellationToken = default)
    {
        var steamRoots = new List<string>();
        foreach (var key in new[] { @"HKCU\Software\Valve\Steam", @"HKLM\SOFTWARE\WOW6432Node\Valve\Steam" })
        {
            foreach (var name in new[] { "SteamPath", "InstallPath", "SteamExe" })
            {
                var value = await registry.ReadValueAsync(key, name, cancellationToken);
                if (value.ValueExists && value.Data is string path)
                {
                    var expanded = Environment.ExpandEnvironmentVariables(path.Replace('/', Path.DirectorySeparatorChar));
                    if (name == "SteamExe") expanded = Path.GetDirectoryName(expanded) ?? expanded;
                    if (Directory.Exists(expanded)) steamRoots.Add(Path.GetFullPath(expanded));
                }
            }
        }
        var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        if (Directory.Exists(defaultRoot)) steamRoots.Add(defaultRoot);
        var libraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            var node = ValveDataFormat.Parse(await File.ReadAllTextAsync(libraryFile, cancellationToken));
            if (!node.Children.TryGetValue("libraryfolders", out var folders)) continue;
            foreach (var folder in folders.Children.Values)
            {
                if (folder.Children.TryGetValue("path", out var pathNode) && pathNode.Value is not null)
                {
                    var path = Path.GetFullPath(pathNode.Value.Replace(@"\\", "\\").Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(path)) libraries.Add(path);
                }
            }
        }
        var candidates = libraries.Select(l => Path.Combine(l, RelativeExecutable)).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return new GameInfo { Cs2ExecutablePath = candidates.Length == 1 ? candidates[0] : null, Cs2DetectionSource = candidates.Length == 1 ? "Steam libraryfolders.vdf" : candidates.Length > 1 ? "Ambiguous; manual selection required" : "Not found", Cs2Candidates = candidates, Cs2Ambiguous = candidates.Length > 1 };
    }
}
````

### `src/FragForge.Actions/Files/ValveDataFormat.cs`  <sub>(76 líneas)</sub>

````csharp
using System.Text;

namespace FragForge.Actions.Files;

public sealed class VdfNode
{
    public string? Value { get; init; }
    public IReadOnlyDictionary<string, VdfNode> Children { get; init; } = new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);
    public bool IsObject => Value is null;
}
public static class ValveDataFormat
{
    public static VdfNode Parse(string text)
    {
        if (text.Length > 1048576) throw new InvalidDataException("VDF exceeds 1 MiB.");
        var index = 0;
        var result = ParseObject(text, ref index, 0, false);
        SkipTrivia(text, ref index);
        if (index != text.Length) throw new InvalidDataException("Trailing characters in VDF.");
        return result;
    }
    private static VdfNode ParseObject(string text, ref int index, int depth, bool root)
    {
        if (depth > 64) throw new InvalidDataException("VDF nesting exceeds 64.");
        var children = new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            SkipTrivia(text, ref index);
            if (index >= text.Length) { if (root) return new VdfNode { Children = children }; throw new InvalidDataException("Unclosed VDF object."); }
            if (text[index] == '}') { index++; if (root) throw new InvalidDataException("Unexpected closing brace."); return new VdfNode { Children = children }; }
            var key = ParseToken(text, ref index);
            SkipTrivia(text, ref index);
            if (index >= text.Length) throw new InvalidDataException("Missing VDF value.");
            VdfNode value;
            if (text[index] == '{') { index++; value = ParseObject(text, ref index, depth + 1, false); }
            else value = new VdfNode { Value = ParseToken(text, ref index) };
            if (!children.TryAdd(key, value)) throw new InvalidDataException($"Duplicate VDF key '{key}'.");
        }
    }
    private static string ParseToken(string text, ref int index)
    {
        SkipTrivia(text, ref index);
        if (index >= text.Length) throw new InvalidDataException("Unexpected end of VDF.");
        if (text[index] == '"')
        {
            index++;
            var builder = new StringBuilder();
            while (index < text.Length)
            {
                var c = text[index++];
                if (c == '"') return builder.ToString();
                if (c == '\\')
                {
                    if (index >= text.Length) break;
                    var escape = text[index++];
                    builder.Append(escape switch { '\\' => '\\', '"' => '"', 'n' => '\n', 't' => '\t', _ => throw new InvalidDataException($"Unsupported VDF escape '\\{escape}'.") });
                }
                else builder.Append(c);
            }
            throw new InvalidDataException("Unclosed VDF string.");
        }
        var start = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not '{' and not '}') index++;
        if (start == index) throw new InvalidDataException("Empty VDF token.");
        return text[start..index];
    }
    private static void SkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index])) { index++; continue; }
            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '/') { while (index < text.Length && text[index] != '\n') index++; continue; }
            break;
        }
    }
}
````

### `src/FragForge.Actions/FragForge.Actions.csproj`  <sub>(6 líneas)</sub>

````xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="../FragForge.Core/FragForge.Core.csproj" />
    <PackageReference Include="System.Management" />
  </ItemGroup>
</Project>
````

### `src/FragForge.Actions/Network/NetAdapterPropertyAction.cs`  <sub>(75 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Network;

public sealed class NetAdapterPropertyAction(INetworkAdapterController controller, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly NetworkParameters _parameters = ActionParameters.Parse<NetworkParameters>(specification);
    public string Kind => "NetAdapterProperty";
    public async Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        if (_parameters.Properties.Count == 0) throw new CatalogValidationException("Network action requires properties.", ["properties"]);
        foreach (var property in _parameters.Properties)
        {
            policy.EnsureNetworkPropertyAllowed(adapter, property.Keyword);
            var current = await controller.GetAsync(adapter, property.Keyword, cancellationToken);
            if (!current.Exists) throw new ActionNotApplicableException($"El adaptador {adapter} no expone '{property.Keyword}'.");
        }
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        var captures = new List<NetworkPropertyCapture>();
        foreach (var property in _parameters.Properties) captures.Add(await controller.GetAsync(adapter, property.Keyword, cancellationToken));
        return new NetworkPropertyActionState { Properties = captures };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        foreach (var property in _parameters.Properties) { policy.EnsureNetworkPropertyAllowed(adapter, property.Keyword); await controller.SetAsync(adapter, property.Keyword, property.Value, cancellationToken); }
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        foreach (var property in _parameters.Properties)
        {
            var current = await controller.GetAsync(adapter, property.Keyword, cancellationToken);
            if (!current.Exists || !current.RegistryValue.SequenceEqual(property.Value, StringComparer.Ordinal)) return VerificationResult.Missing($"{adapter}:{property.Keyword} no coincide.");
        }
        return VerificationResult.Match("Propiedades avanzadas del adaptador verificadas.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        foreach (var property in ((NetworkPropertyActionState)before).Properties.AsEnumerable().Reverse())
        {
            policy.EnsureNetworkPropertyAllowed(property.AdapterName, property.Keyword);
            if (!property.Exists) throw new InvalidOperationException("Cannot remove an advanced property; capture required an existing property.");
            await controller.SetAsync(property.AdapterName, property.Keyword, property.RegistryValue, cancellationToken);
        }
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Propiedades de red restauradas.") : VerificationResult.Missing("Las propiedades de red no volvieron al estado capturado.");
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var adapter = ResolveAdapter(context);
        var lines = new List<string>();
        foreach (var property in _parameters.Properties)
        {
            var current = await controller.GetAsync(adapter, property.Keyword, cancellationToken);
            lines.Add($"{adapter}:{property.Keyword}: [{string.Join(",", current.RegistryValue)}] -> [{string.Join(",", property.Value)}]. Puede renegociar el enlace; revert restaura el valor previo.");
        }
        return string.Join(Environment.NewLine, lines);
    }
    private string ResolveAdapter(ActionExecutionContext context)
    {
        var name = _parameters.AdapterName;
        if (!name.StartsWith("{AdapterDescription:", StringComparison.Ordinal)) return name;
        var description = name["{AdapterDescription:".Length..^1];
        var matches = context.Hardware.NetworkAdapters.Where(a => a.IsPhysical && a.Description.Contains(description, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new ActionNotApplicableException($"Se requiere exactamente un adaptador físico '{description}'; encontrados {matches.Length}.");
        return matches[0].Name;
    }
}
internal sealed record NetworkParameters { public required string AdapterName { get; init; } public IReadOnlyList<NetworkPropertyTarget> Properties { get; init; } = []; }
internal sealed record NetworkPropertyTarget { public required string Keyword { get; init; } public IReadOnlyList<string> Value { get; init; } = []; }
````

### `src/FragForge.Actions/Network/WindowsNetworkAdapterController.cs`  <sub>(35 líneas)</sub>

````csharp
using System.Runtime.Versioning;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Network;

[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkAdapterController(IProcessRunner runner) : INetworkAdapterController
{
    public async Task<NetworkPropertyCapture> GetAsync(string adapterName, string keyword, CancellationToken cancellationToken = default)
    {
        var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " +
            $"$p=Get-NetAdapterAdvancedProperty -Name '{Escape(adapterName)}' -RegistryKeyword '{Escape(keyword)}' -ErrorAction SilentlyContinue; " +
            "if ($null -eq $p) { '{\"exists\":false}' } else { [pscustomobject]@{exists=$true; keyword=[string]$p.RegistryKeyword; value=@($p.RegistryValueData); display=[string]$p.DisplayValue} | ConvertTo-Json -Compress -Depth 4 }";
        var result = await RunAsync(script, null, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        var root = document.RootElement;
        if (!root.GetProperty("exists").GetBoolean()) return new NetworkPropertyCapture { AdapterName = adapterName, Keyword = keyword, Exists = false };
        return new NetworkPropertyCapture { AdapterName = adapterName, Keyword = root.GetProperty("keyword").GetString() ?? keyword, Exists = true, RegistryValue = root.GetProperty("value").EnumerateArray().Select(x => x.GetString() ?? "").ToArray(), DisplayValue = root.GetProperty("display").GetString() };
    }
    public async Task SetAsync(string adapterName, string keyword, IReadOnlyList<string> value, CancellationToken cancellationToken = default)
    {
        var array = "@(" + string.Join(",", value.Select(v => "'" + Escape(v) + "'")) + ")";
        var script = $"$ErrorActionPreference='Stop'; Set-NetAdapterAdvancedProperty -Name '{Escape(adapterName)}' -RegistryKeyword '{Escape(keyword)}' -RegistryValue {array} -Confirm:$false";
        await RunAsync(script, null, cancellationToken);
    }
    private async Task<ProcessResult> RunAsync(string script, string? input, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], StandardInput = input, Timeout = TimeSpan.FromSeconds(90) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powershell.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
        return result;
    }
    private static string Escape(string value) => value.Replace("'", "''");
}
````

### `src/FragForge.Actions/Platform/CpuTopology.cs`  <sub>(68 líneas)</sub>

````csharp
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FragForge.Actions.Platform;

public sealed record CpuTopologyResult(int? PCores, int? ECores, int LogicalProcessors, bool? IsHybrid, string Source);
public interface ICpuTopologyProbe
{
    Task<CpuTopologyResult> ReadAsync(CancellationToken cancellationToken = default);
}
public static class CpuTopologyInterpreter
{
    public static CpuTopologyResult FromEfficiencyClasses(IReadOnlyList<(byte EfficiencyClass, int LogicalProcessors)> cores)
    {
        if (cores.Count == 0 || cores.Any(c => c.LogicalProcessors <= 0)) return new(null, null, 0, null, "Unavailable");
        var classes = cores.Select(c => c.EfficiencyClass).Distinct().OrderBy(x => x).ToArray();
        var logical = cores.Sum(c => c.LogicalProcessors);
        if (classes.Length == 1) return new(null, null, logical, false, "GetLogicalProcessorInformationEx: single EfficiencyClass");
        var max = classes[^1];
        var p = cores.Count(c => c.EfficiencyClass == max);
        var e = cores.Count - p;
        return new(p, e, logical, true, "GetLogicalProcessorInformationEx: distinct EfficiencyClass values");
    }
}
[SupportedOSPlatform("windows")]
public sealed class WindowsCpuTopologyProbe : ICpuTopologyProbe
{
    public Task<CpuTopologyResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint size = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref size);
        if (size == 0 || size > 1024 * 1024) return Task.FromResult(new CpuTopologyResult(null, null, Environment.ProcessorCount, null, "GetLogicalProcessorInformationEx failed"));
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref size)) return Task.FromResult(new CpuTopologyResult(null, null, Environment.ProcessorCount, null, $"GetLogicalProcessorInformationEx error {Marshal.GetLastWin32Error()}"));
            var cores = new List<(byte, int)>();
            var offset = 0;
            while (offset + 8 <= size)
            {
                var relationship = Marshal.ReadInt32(buffer, offset);
                var recordSize = Marshal.ReadInt32(buffer, offset + 4);
                if (recordSize < 8 || offset + recordSize > size) throw new InvalidDataException("Invalid processor information record.");
                if (relationship == 0)
                {
                    var efficiency = Marshal.ReadByte(buffer, offset + 9);
                    var groupCount = Marshal.ReadInt16(buffer, offset + 30);
                    var threads = 0;
                    for (var g = 0; g < groupCount; g++)
                    {
                        var mask = Marshal.ReadIntPtr(buffer, offset + 32 + g * (IntPtr.Size == 8 ? 24 : 16));
                        threads += BitOperations.PopCount(unchecked((ulong)mask.ToInt64()));
                    }
                    cores.Add((efficiency, threads));
                }
                offset += recordSize;
            }
            return Task.FromResult(CpuTopologyInterpreter.FromEfficiencyClasses(cores));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
}
````

### `src/FragForge.Actions/Platform/HardwareProfiler.cs`  <sub>(210 líneas)</sub>

````csharp
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Platform;

internal static class WmiValues
{
    public static string? String(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString()?.Trim() : null;
    public static ulong? UInt64(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null && ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    public static int? Int32(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    public static bool? Bool(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null ? Convert.ToBoolean(value, CultureInfo.InvariantCulture) : null;
    public static IReadOnlyList<object> Array(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is System.Collections.IEnumerable enumerable and not string ? enumerable.Cast<object>().ToArray() : [];
}

[SupportedOSPlatform("windows")]
public sealed class WindowsHardwareProfiler(IWmiQuery wmi, IRegistry registry, IProcessRunner runner, IGameLocator games, ICpuTopologyProbe topology, ISystemRestoreService restore) : IHardwareProfiler
{
    public async Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        var warnings = new List<string>();
        var observations = new List<DiagnosticObservation>();
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Query(string scope, string className, string[] properties, string? where = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new EngineProgress("hardware", null, 0, $"Consultando {className}."));
            try { return await wmi.QueryAsync(scope, className, properties, where, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add($"{className}: {ex.Message}"); return []; }
        }
        var osRows = await Query(@"root\cimv2", "Win32_OperatingSystem", ["Caption", "Version", "BuildNumber", "OSArchitecture"]);
        var osRow = osRows.FirstOrDefault() ?? new Dictionary<string, object?>();
        var ubr = await registry.ReadValueAsync(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", cancellationToken);
        var os = new OsInfo
        {
            Name = WmiValues.String(osRow, "Caption") ?? "Windows",
            Edition = WmiValues.String(osRow, "Caption") ?? "Unknown",
            Version = WmiValues.String(osRow, "Version") ?? "Unknown",
            Build = WmiValues.Int32(osRow, "BuildNumber") ?? 0,
            Ubr = ubr.ValueExists ? Convert.ToInt32(ubr.Data, CultureInfo.InvariantCulture) : 0,
            Architecture = WmiValues.String(osRow, "OSArchitecture") ?? RuntimeInformation.OSArchitecture.ToString(),
            Culture = CultureInfo.CurrentCulture.Name,
            TimeZone = TimeZoneInfo.Local.Id
        };
        var csRows = await Query(@"root\cimv2", "Win32_ComputerSystem", ["UserName", "TotalPhysicalMemory", "Manufacturer", "Model", "PCSystemType"]);
        var cs = csRows.FirstOrDefault() ?? new Dictionary<string, object?>();
        var cpuRows = await Query(@"root\cimv2", "Win32_Processor", ["Name", "Manufacturer", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed", "VirtualizationFirmwareEnabled", "VMMonitorModeExtensions"]);
        var cpuRow = cpuRows.FirstOrDefault() ?? new Dictionary<string, object?>();
        var topologyResult = await topology.ReadAsync(cancellationToken);
        var vendor = (WmiValues.String(cpuRow, "Manufacturer") ?? "").Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : WmiValues.String(cpuRow, "Manufacturer") ?? "Unknown";
        var cpu = new CpuInfo
        {
            Model = WmiValues.String(cpuRow, "Name") ?? "Unknown", Vendor = vendor,
            PhysicalCores = cpuRows.Sum(r => WmiValues.Int32(r, "NumberOfCores") ?? 0),
            LogicalProcessors = topologyResult.LogicalProcessors > 0 ? topologyResult.LogicalProcessors : cpuRows.Sum(r => WmiValues.Int32(r, "NumberOfLogicalProcessors") ?? 0),
            PCores = topologyResult.PCores, ECores = topologyResult.ECores, IsHybrid = topologyResult.IsHybrid, TopologySource = topologyResult.Source,
            FirmwareVirtualizationEnabled = WmiValues.Bool(cpuRow, "VirtualizationFirmwareEnabled"), BaseClockMhz = WmiValues.Int32(cpuRow, "MaxClockSpeed")
        };
        if (topologyResult.IsHybrid is null) warnings.Add("CPU P/E: no se pudo consultar EfficiencyClass; no se asume hibridación por el nombre del modelo.");
        var memoryRows = await Query(@"root\cimv2", "Win32_PhysicalMemory", ["Manufacturer", "PartNumber", "BankLabel", "DeviceLocator", "Capacity", "Speed", "ConfiguredClockSpeed", "SMBIOSMemoryType"]);
        var modules = memoryRows.Select(r => new MemoryModuleInfo { Manufacturer = WmiValues.String(r, "Manufacturer"), PartNumber = WmiValues.String(r, "PartNumber"), BankLabel = WmiValues.String(r, "BankLabel"), DeviceLocator = WmiValues.String(r, "DeviceLocator"), CapacityBytes = WmiValues.UInt64(r, "Capacity") ?? 0, SpeedMtPerSecond = WmiValues.Int32(r, "Speed"), ConfiguredSpeedMtPerSecond = WmiValues.Int32(r, "ConfiguredClockSpeed"), SmbiosMemoryType = WmiValues.Int32(r, "SMBIOSMemoryType") }).ToArray();
        var speeds = modules.Select(m => m.ConfiguredSpeedMtPerSecond ?? m.SpeedMtPerSecond).Where(s => s is not null).Distinct().ToArray();
        var memory = new MemoryInfo { TotalBytes = WmiValues.UInt64(cs, "TotalPhysicalMemory") ?? 0, Modules = modules, Channels = MemoryChannelState.Unknown, ConfiguredSpeedMtPerSecond = speeds.Length == 1 ? speeds[0] : null, ChannelEvidence = "Win32_PhysicalMemory describe módulos, no el entrelazado del controlador. Dos DIMM no prueban dual-channel; verificar con una herramienta de topología de memoria." };
        var gpuRows = await Query(@"root\cimv2", "Win32_VideoController", ["Name", "AdapterCompatibility", "AdapterRAM", "DriverVersion", "CurrentHorizontalResolution", "CurrentVerticalResolution", "CurrentRefreshRate"]);
        var gpus = gpuRows.Select(r =>
        {
            var name = WmiValues.String(r, "Name") ?? "Unknown";
            var compatibility = WmiValues.String(r, "AdapterCompatibility") ?? "";
            var gpuVendor = compatibility.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel : compatibility.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia : compatibility.Contains("AMD", StringComparison.OrdinalIgnoreCase) || compatibility.Contains("ATI", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd : compatibility.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Microsoft : GpuVendor.Unknown;
            return new GpuInfo { Name = name, Vendor = gpuVendor, DriverVersion = WmiValues.String(r, "DriverVersion"), ReportedAdapterRamBytes = WmiValues.UInt64(r, "AdapterRAM"), IsIntegrated = gpuVendor == GpuVendor.Intel && (name.Contains("UHD", StringComparison.OrdinalIgnoreCase) || name.Contains("Iris", StringComparison.OrdinalIgnoreCase) || name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)), SharedMemoryBudgetBytes = null };
        }).ToArray();
        var diskRows = await Query(@"root\cimv2", "Win32_DiskDrive", ["Model", "SerialNumber", "Size", "MediaType", "InterfaceType", "PNPDeviceID", "Status"]);
        var physicalDisks = await Query(@"root\Microsoft\Windows\Storage", "MSFT_PhysicalDisk", ["FriendlyName", "SerialNumber", "Size", "MediaType", "BusType", "HealthStatus", "SpindleSpeed"]);
        var smartRows = await Query(@"root\wmi", "MSStorageDriver_FailurePredictStatus", ["InstanceName", "PredictFailure", "Reason"]);
        var disks = diskRows.Select(r =>
        {
            var model = WmiValues.String(r, "Model") ?? "Unknown";
            var serial = WmiValues.String(r, "SerialNumber");
            var physical = physicalDisks.FirstOrDefault(p => (serial is not null && string.Equals(WmiValues.String(p, "SerialNumber")?.Trim(), serial.Trim(), StringComparison.OrdinalIgnoreCase)) || model.Contains(WmiValues.String(p, "FriendlyName") ?? "\0", StringComparison.OrdinalIgnoreCase));
            var media = physical is null ? null : WmiValues.Int32(physical, "MediaType");
            var bus = physical is null ? WmiValues.String(r, "InterfaceType") ?? "Unknown" : BusName(WmiValues.Int32(physical, "BusType"));
            var pnp = WmiValues.String(r, "PNPDeviceID") ?? "";
            var smart = smartRows.FirstOrDefault(s => (WmiValues.String(s, "InstanceName") ?? "").Contains(pnp, StringComparison.OrdinalIgnoreCase));
            var kind = bus == "NVMe" ? DiskKind.Nvme : media == 4 ? DiskKind.Ssd : media == 3 ? DiskKind.Hdd : media == 5 ? DiskKind.Scm : (WmiValues.String(r, "MediaType") ?? "").Contains("SSD", StringComparison.OrdinalIgnoreCase) ? DiskKind.Ssd : DiskKind.Unknown;
            var health = physical is null ? WmiValues.String(r, "Status") ?? "Unknown" : WmiValues.Int32(physical, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
            return new DiskInfo { Model = model, SerialNumber = serial, SizeBytes = WmiValues.UInt64(r, "Size") ?? 0, Kind = kind, Bus = bus, Health = health, SmartPredictFailure = smart is null ? null : WmiValues.Bool(smart, "PredictFailure"), SmartSource = smart is null ? "MSStorageDriver_FailurePredictStatus unavailable" : "MSStorageDriver_FailurePredictStatus" };
        }).ToArray();
        var adapterRows = await Query(@"root\StandardCimv2", "MSFT_NetAdapter", ["Name", "InterfaceDescription", "DriverVersion", "DriverProvider", "MediaType", "LinkSpeed", "State", "HardwareInterface", "Virtual"]);
        var adapters = adapterRows.Select(r => new NetworkAdapterInfo { Name = WmiValues.String(r, "Name") ?? "Unknown", Description = WmiValues.String(r, "InterfaceDescription") ?? "Unknown", Kind = WmiValues.Int32(r, "MediaType") switch { 1 => NetworkKind.Ethernet, 2 => NetworkKind.WiFi, _ => (WmiValues.String(r, "InterfaceDescription") ?? "").Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ? NetworkKind.WiFi : NetworkKind.Ethernet }, DriverVersion = WmiValues.String(r, "DriverVersion"), DriverProvider = WmiValues.String(r, "DriverProvider"), LinkSpeedBitsPerSecond = WmiValues.UInt64(r, "LinkSpeed"), IsPhysical = WmiValues.Bool(r, "HardwareInterface") == true && WmiValues.Bool(r, "Virtual") != true, State = WmiValues.String(r, "State") ?? "Unknown" }).ToArray();
        if (adapters.Length == 0)
        {
            var fallback = await Query(@"root\cimv2", "Win32_NetworkAdapter", ["Name", "NetConnectionID", "AdapterTypeId", "Speed", "PhysicalAdapter", "NetConnectionStatus"]);
            adapters = fallback.Select(r => new NetworkAdapterInfo { Name = WmiValues.String(r, "NetConnectionID") ?? WmiValues.String(r, "Name") ?? "Unknown", Description = WmiValues.String(r, "Name") ?? "Unknown", Kind = WmiValues.Int32(r, "AdapterTypeId") == 9 ? NetworkKind.WiFi : NetworkKind.Ethernet, LinkSpeedBitsPerSecond = WmiValues.UInt64(r, "Speed"), IsPhysical = WmiValues.Bool(r, "PhysicalAdapter") == true, State = WmiValues.String(r, "NetConnectionStatus") ?? "Unknown" }).ToArray();
        }
        var monitors = gpuRows.Where(r => (WmiValues.Int32(r, "CurrentHorizontalResolution") ?? 0) > 0).Select(r => new MonitorInfo { Name = WmiValues.String(r, "Name") ?? "Display", Width = WmiValues.Int32(r, "CurrentHorizontalResolution"), Height = WmiValues.Int32(r, "CurrentVerticalResolution"), RefreshRateHz = WmiValues.Int32(r, "CurrentRefreshRate"), Source = "Win32_VideoController (controlador, no enumeración exhaustiva por monitor)" }).ToArray();
        var security = await ReadSecurityAsync(Query, cancellationToken);
        var power = await ReadPowerAsync(cancellationToken);
        var enclosure = await Query(@"root\cimv2", "Win32_SystemEnclosure", ["ChassisTypes"]);
        var chassis = enclosure.SelectMany(r => WmiValues.Array(r, "ChassisTypes")).Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).Distinct().ToArray();
        var batteries = await Query(@"root\cimv2", "Win32_Battery", ["Name", "EstimatedChargeRemaining"]);
        var bios = (await Query(@"root\cimv2", "Win32_BIOS", ["Manufacturer", "SMBIOSBIOSVersion"])).FirstOrDefault() ?? new Dictionary<string, object?>();
        bool? portable = chassis.Length == 0 ? null : chassis.Any(t => t is 8 or 9 or 10 or 11 or 12 or 14 or 30 or 31 or 32);
        var form = new FormFactorInfo { IsPortable = portable, HasBattery = batteries.Count > 0, ChassisTypes = chassis.Select(ChassisName).ToArray(), SystemModel = WmiValues.String(cs, "Model"), BiosVersion = WmiValues.String(bios, "SMBIOSBIOSVersion") };
        var playbooks = await DetectAtlasAsync(Query, cancellationToken);
        var devices = await Query(@"root\cimv2", "Win32_PnPEntity", ["Name", "PNPDeviceID", "ConfigManagerErrorCode", "Service", "PNPClass"], "ConfigManagerErrorCode!=0 OR PNPClass='SoftwareDevice'");
        var errors = devices.Where(d => (WmiValues.Int32(d, "ConfigManagerErrorCode") ?? 0) != 0).Select(d => new DeviceErrorInfo(WmiValues.String(d, "Name") ?? "(sin nombre)", WmiValues.String(d, "PNPDeviceID") ?? "", WmiValues.Int32(d, "ConfigManagerErrorCode") ?? -1, WmiValues.String(d, "Service"))).ToArray();
        foreach (var device in devices.Where(d => (WmiValues.String(d, "PNPClass") ?? "").Equals("SoftwareDevice", StringComparison.OrdinalIgnoreCase))) observations.Add(new DiagnosticObservation("softwareDevice", WmiValues.String(device, "Name") ?? "(sin nombre)", WmiValues.String(device, "PNPDeviceID") ?? "WMI", "High", $"ConfigManagerErrorCode={WmiValues.Int32(device, "ConfigManagerErrorCode")}"));
        var maintenance = await ReadMaintenanceAsync(cancellationToken, warnings);
        var gameInfo = await games.LocateCs2Async(cancellationToken);
        var processSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var interactiveName = WmiValues.String(cs, "UserName");
        var interactiveSid = ResolveSid(interactiveName, processSid, warnings);
        return new HardwareProfile { MachineName = Environment.MachineName, InteractiveUserName = interactiveName, InteractiveUserSid = interactiveSid, ProcessUserSid = processSid, Os = os, Cpu = cpu, Memory = memory, Gpus = gpus, Disks = disks, NetworkAdapters = adapters, Monitors = monitors, Security = security, Power = power, FormFactor = form, Playbooks = playbooks, DeviceErrors = errors, Games = gameInfo, Maintenance = maintenance, Observations = observations, ProbeWarnings = warnings };
    }
    private async Task<SecurityInfo> ReadSecurityAsync(Func<string, string, string[], string?, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> query, CancellationToken cancellationToken)
    {
        var guard = (await query(@"root\Microsoft\Windows\DeviceGuard", "Win32_DeviceGuard", ["VirtualizationBasedSecurityStatus", "SecurityServicesConfigured", "SecurityServicesRunning"], null)).FirstOrDefault();
        var vbs = guard is null ? FeatureState.Unknown : WmiValues.Int32(guard, "VirtualizationBasedSecurityStatus") switch { 0 => FeatureState.Disabled, 2 => FeatureState.Enabled, _ => FeatureState.Unknown };
        var running = guard is null ? [] : WmiValues.Array(guard, "SecurityServicesRunning").Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray();
        var configured = guard is null ? [] : WmiValues.Array(guard, "SecurityServicesConfigured").Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray();
        var hvci = running.Contains(2) ? FeatureState.Enabled : configured.Contains(2) ? FeatureState.Unknown : guard is not null && running.Length > 0 ? FeatureState.Disabled : FeatureState.Unknown;
        var secure = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", cancellationToken);
        var secureState = secure.ValueExists ? Convert.ToInt32(secure.Data, CultureInfo.InvariantCulture) == 1 ? FeatureState.Enabled : FeatureState.Disabled : FeatureState.Unknown;
        if (secureState == FeatureState.Unknown)
        {
            try
            {
                var result = await runner.RunAsync(new ProcessRequest { FileName = ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-Command", "try { if (Confirm-SecureBootUEFI) { 'Enabled' } else { 'Disabled' } } catch { 'Unknown' }"], Timeout = TimeSpan.FromSeconds(20) }, cancellationToken);
                secureState = result.StandardOutput.Trim() switch { "Enabled" => FeatureState.Enabled, "Disabled" => FeatureState.Disabled, _ => FeatureState.Unknown };
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { secureState = FeatureState.Unknown; }
        }
        var tpmRows = await query(@"root\Microsoft\Windows\TPM", "Win32_Tpm", ["IsEnabled_InitialValue", "IsActivated_InitialValue", "SpecVersion"], null);
        var tpm = tpmRows.FirstOrDefault();
        var tpmState = tpm is null ? FeatureState.NotPresent : WmiValues.Bool(tpm, "IsEnabled_InitialValue") == true && WmiValues.Bool(tpm, "IsActivated_InitialValue") == true ? FeatureState.Enabled : FeatureState.Disabled;
        var hags = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", cancellationToken);
        var mpo = await registry.ReadValueAsync(@"HKLM\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", cancellationToken);
        return new SecurityInfo { Vbs = vbs, Hvci = hvci, SecureBoot = secureState, Tpm = tpmState, TpmVersion = tpm is null ? null : WmiValues.String(tpm, "SpecVersion"), Hags = hags.ValueExists ? Convert.ToInt32(hags.Data) == 2 ? FeatureState.Enabled : Convert.ToInt32(hags.Data) == 1 ? FeatureState.Disabled : FeatureState.Unknown : FeatureState.Unknown, Mpo = mpo.ValueExists && Convert.ToInt32(mpo.Data) == 5 ? FeatureState.Disabled : FeatureState.Unknown, Evidence = "WMI DeviceGuard/TPM, registro SecureBoot/HAGS/MPO; Unknown no se interpreta como Disabled." };
    }
    private async Task<PowerInfo> ReadPowerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(new ProcessRequest { FileName = ProcessRunner.SystemExecutable("powercfg.exe"), Arguments = ["/getactivescheme"], Timeout = TimeSpan.FromSeconds(20) }, cancellationToken);
            if (!result.IsSuccess) return new PowerInfo();
            var match = Regex.Match(result.StandardOutput, @"([0-9a-fA-F-]{36})\s+\(([^)]*)\)");
            return new PowerInfo { ActiveSchemeGuid = match.Success ? match.Groups[1].Value.ToLowerInvariant() : null, ActiveSchemeName = match.Success ? match.Groups[2].Value : null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new PowerInfo { PowerLimitSource = "powercfg failed: " + ex.Message }; }
    }
    private async Task<IReadOnlyList<PlaybookDetection>> DetectAtlasAsync(Func<string, string, string[], string?, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> query, CancellationToken cancellationToken)
    {
        var evidence = new List<string>();
        foreach (var name in new[] { "RegisteredOrganization", "Organization" })
        {
            var value = await registry.ReadValueAsync(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", name, cancellationToken);
            if (value.ValueExists && (value.Data as string ?? "").Contains("Atlas", StringComparison.OrdinalIgnoreCase)) evidence.Add($"{name}={value.Data}");
        }
        if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Atlas"))) evidence.Add("ProgramDataAtlas folder");
        var services = await query(@"root\cimv2", "Win32_Service", ["Name", "State", "StartMode"], "Name='SysMain' OR Name='WSearch' OR Name='Spooler' OR Name='DiagTrack'");
        var stopped = services.Where(s => WmiValues.String(s, "State") != "Running").Select(s => WmiValues.String(s, "Name") ?? "").ToArray();
        if (stopped.Length > 0) evidence.Add("Servicios no corriendo (no concluyente): " + string.Join(",", stopped));
        return evidence.Count == 0 ? [] : [new PlaybookDetection("Atlas Playbook (heurística)", evidence.Any(e => e.StartsWith("RegisteredOrganization") || e.StartsWith("Organization")) ? "Medium" : "Low", evidence)];
    }
    private async Task<SystemMaintenanceInfo> ReadMaintenanceAsync(CancellationToken cancellationToken, List<string> warnings)
    {
        var reasons = new List<string>();
        if (await registry.KeyExistsAsync(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", cancellationToken)) reasons.Add("CBS RebootPending");
        if (await registry.KeyExistsAsync(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", cancellationToken)) reasons.Add("WindowsUpdate RebootRequired");
        var pending = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations", cancellationToken);
        if (pending.ValueExists && pending.Data is string[] { Length: > 0 }) reasons.Add("PendingFileRenameOperations");
        var activeName = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName", "ComputerName", cancellationToken);
        var pendingName = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName", "ComputerName", cancellationToken);
        if (activeName.ValueExists && pendingName.ValueExists && !string.Equals(activeName.Data as string, pendingName.Data as string, StringComparison.OrdinalIgnoreCase)) reasons.Add("ComputerName change pending");
        var servicing = new List<string>();
        foreach (var name in new[] { "TiWorker", "TrustedInstaller" }) { try { if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0) servicing.Add(name); } catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add($"Process {name}: {ex.Message}"); } }
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        ulong free = 0; try { free = (ulong)new DriveInfo(systemRoot).AvailableFreeSpace; } catch (Exception ex) { warnings.Add("Disk free space: " + ex.Message); }
        var admin = false; try { using var identity = WindowsIdentity.GetCurrent(); admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } catch (Exception ex) { warnings.Add("Administrator check: " + ex.Message); }
        FeatureState protection; try { protection = await restore.GetProtectionStateAsync(cancellationToken); } catch (Exception ex) when (ex is not OperationCanceledException) { protection = FeatureState.Unknown; warnings.Add("System protection: " + ex.Message); }
        return new SystemMaintenanceInfo { PendingReboot = reasons.Count > 0, PendingRebootReasons = reasons, WindowsServicingActive = servicing.Count > 0, ServicingProcesses = servicing, SystemProtection = protection, IsAdministrator = admin, SystemDriveFreeBytes = free };
    }
    private static string? ResolveSid(string? userName, string processSid, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            if (string.Equals(current.Name, userName, StringComparison.OrdinalIgnoreCase)) return processSid;
            return new NTAccount(userName).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("Interactive user SID: " + ex.Message); return null; }
    }
    private static string BusName(int? bus) => bus switch { 3 => "ATA", 7 => "USB", 10 => "SAS", 11 => "SATA", 17 => "NVMe", 18 => "SCM", _ => "Unknown" };
    private static string ChassisName(int type) => type switch { 3 => "Desktop", 4 => "LowProfileDesktop", 8 => "Portable", 9 => "Laptop", 10 => "Notebook", 30 => "Tablet", 31 => "Convertible", 32 => "Detachable", 35 => "MiniPC", _ => $"Type{type}" };
}

public sealed class UnsupportedHardwareProfiler : IHardwareProfiler
{
    public Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => Task.FromResult(new HardwareProfile
    {
        IsUnsupportedPlatform = true,
        Os = new OsInfo { Name = Environment.OSVersion.ToString(), Architecture = RuntimeInformation.OSArchitecture.ToString(), Culture = CultureInfo.CurrentCulture.Name, TimeZone = TimeZoneInfo.Local.Id },
        Cpu = new CpuInfo { Model = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount, IsHybrid = null, TopologySource = "Unsupported platform" },
        ProbeWarnings = ["Esta plataforma no es Windows. El perfil es parcial y las acciones reales están bloqueadas."]
    });
}
````

### `src/FragForge.Actions/Platform/ProcessRunner.cs`  <sub>(52 líneas)</sub>

````csharp
using System.Diagnostics;
using System.Text;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Platform;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(request), "Process timeout must be between 0 and 30 minutes.");
        var start = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        var timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }
        var output = await stdout.WaitAsync(CancellationToken.None);
        var error = await stderr.WaitAsync(CancellationToken.None);
        if (timedOut) return new ProcessResult(-1, output, error, true);
        return new ProcessResult(process.ExitCode, output, error, false);
    }
    public static string SystemExecutable(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
}
````

### `src/FragForge.Actions/Platform/SystemRestoreService.cs`  <sub>(84 líneas)</sub>

````csharp
using System.Management;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Actions.Registry;

namespace FragForge.Actions.Platform;

[SupportedOSPlatform("windows")]
public sealed class SystemRestoreService(IWmiQuery wmi, IRegistry registry, IProcessRunner runner) : ISystemRestoreService
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string ValueName = "SystemRestorePointCreationFrequency";
    public async Task<FeatureState> GetProtectionStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var systemDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
            var volumes = await wmi.QueryAsync(@"root\cimv2", "Win32_Volume", ["DeviceID", "DriveLetter"], $"DriveLetter='{systemDrive}'", cancellationToken);
            var device = volumes.FirstOrDefault()?["DeviceID"] as string;
            if (device is null) return FeatureState.Unknown;
            var storage = await wmi.QueryAsync(@"root\cimv2", "Win32_ShadowStorage", ["Volume", "DiffVolume", "AllocatedSpace", "MaximumSpace"], null, cancellationToken);
            return storage.Any(s => (s["Volume"] as string ?? s["DiffVolume"] as string ?? "").Contains(device, StringComparison.OrdinalIgnoreCase)) ? FeatureState.Enabled : FeatureState.Disabled;
        }
        catch (Exception ex) when (ex is ManagementException or InvalidOperationException or IOException) { return FeatureState.Unknown; }
    }
    public async Task EnableProtectionAsync(CancellationToken cancellationToken = default)
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var script = $"$ErrorActionPreference='Stop'; Enable-ComputerRestore -Drive '{drive.Replace("'", "''")}' -Confirm:$false";
        var result = await runner.RunAsync(new ProcessRequest { FileName = ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], Timeout = TimeSpan.FromMinutes(2) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("Enable-ComputerRestore", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
    }
    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken = default)
    {
        if (description.Length > 256) throw new ArgumentException("Restore point description is limited to 256 characters.", nameof(description));
        var original = await registry.ReadValueAsync(Key, ValueName, cancellationToken);
        var missing = new List<string>();
        foreach (var ancestor in RegistryPath.AncestorsFromRoot(Key)) if (!await registry.KeyExistsAsync(ancestor, cancellationToken)) missing.Add(ancestor);
        var capture = RegistryValueConverter.Capture(Key, ValueName, original, missing);
        await registry.WriteValueAsync(Key, ValueName, new RegistryWriteValue("DWord", 0), cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(() =>
            {
                using var cls = new ManagementClass(@"root\default:SystemRestore");
                using var input = cls.GetMethodParameters("CreateRestorePoint");
                input["Description"] = description;
                input["RestorePointType"] = 12u;
                input["EventType"] = 100u;
                using var output = cls.InvokeMethod("CreateRestorePoint", input, null);
                return Convert.ToUInt32(output["ReturnValue"]);
            }, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != 0) throw new FragForgeException("restore_point_failed", $"SystemRestore.CreateRestorePoint devolvió {result}. No se aplicarán cambios.");
            long? sequence = null;
            try
            {
                var points = await wmi.QueryAsync(@"root\default", "SystemRestore", ["Description", "SequenceNumber"], null, CancellationToken.None);
                var point = points.Where(p => (p["Description"] as string) == description).OrderByDescending(p => Convert.ToInt64(p["SequenceNumber"] ?? 0L)).FirstOrDefault();
                if (point is not null) sequence = Convert.ToInt64(point["SequenceNumber"]);
            }
            catch (ManagementException) { }
            return new RestorePointResult(sequence, sequence is null ? "Punto creado; WMI no expuso su número de secuencia." : $"Punto de restauración {sequence} creado.");
        }
        finally
        {
            try
            {
                if (capture.ValueExisted) await registry.WriteValueAsync(Key, ValueName, RegistryValueConverter.ToWriteValue(capture), CancellationToken.None);
                else await registry.DeleteValueAsync(Key, ValueName, CancellationToken.None);
                foreach (var ancestor in capture.MissingAncestors)
                {
                    var parent = ancestor[..ancestor.LastIndexOf('\\')];
                    var child = ancestor[(ancestor.LastIndexOf('\\') + 1)..];
                    await registry.DeleteEmptySubKeyAsync(parent, child, CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw new RollbackException("No se pudo restaurar SystemRestorePointCreationFrequency tras crear el punto.", ex); }
        }
    }
}
````

### `src/FragForge.Actions/Platform/WmiQuery.cs`  <sub>(42 líneas)</sub>

````csharp
using System.Management;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;

namespace FragForge.Actions.Platform;

[SupportedOSPlatform("windows")]
public sealed class WmiQuery : IWmiQuery
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string className, IReadOnlyList<string> properties, string? where = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!System.Text.RegularExpressions.Regex.IsMatch(className, "^[A-Za-z_][A-Za-z0-9_]*$") || properties.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p, "^[A-Za-z_][A-Za-z0-9_]*$"))) throw new ArgumentException("Invalid WMI identifier.");
        var query = "SELECT " + (properties.Count == 0 ? "*" : string.Join(",", properties)) + " FROM " + className + (where is null ? "" : " WHERE " + where);
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = false });
        using var collection = searcher.Get();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in collection)
        {
            using var managementObject = (ManagementBaseObject)item;
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties.Count == 0 ? managementObject.Properties.Cast<PropertyData>().Select(p => p.Name) : properties)
            {
                try { row[property] = Normalize(managementObject[property]); }
                catch (ManagementException) { row[property] = null; }
            }
            rows.Add(row);
            cancellationToken.ThrowIfCancellationRequested();
        }
        return (IReadOnlyList<IReadOnlyDictionary<string, object?>>)rows;
    }, cancellationToken);
    private static object? Normalize(object? value) => value switch
    {
        string[] strings => strings.ToArray(),
        ushort[] numbers => numbers.ToArray(),
        uint[] numbers => numbers.ToArray(),
        ulong[] numbers => numbers.ToArray(),
        bool[] values => values.ToArray(),
        ManagementBaseObject embedded => embedded.ToString(),
        _ => value
    };
}
````

### `src/FragForge.Actions/Power/PowerPlanAction.cs`  <sub>(176 líneas)</sub>

````csharp
using System.Globalization;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Power;

public sealed class PowerPlanAction : ITweakAction
{
    private readonly IProcessRunner _runner;
    private readonly PowerPlanParameters _parameters;
    public string Kind => "PowerPlan";
    public PowerPlanAction(IProcessRunner runner, ActionSpec specification)
    {
        _runner = runner;
        _parameters = ActionParameters.Parse<PowerPlanParameters>(specification);
        Validate();
    }
    private Task<ProcessResult> RunPowerCfgAsync(IReadOnlyList<string> args, CancellationToken token) => _runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable("powercfg.exe"), Arguments = args, Timeout = TimeSpan.FromSeconds(60) }, token);
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        Validate();
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        Validate();
        var active = await GetActivePlanAsync(cancellationToken);
        var plans = await ListPlansAsync(cancellationToken);
        var existing = FindPlan(plans);
        var settings = new List<PowerSettingCapture>();
        string? export = null;
        if (existing is not null)
        {
            foreach (var setting in _parameters.Settings) settings.Add(await QuerySettingAsync(existing.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken));
            if (context.ArtifactDirectory is not null)
            {
                export = $"power-{existing.Value.Guid}.pow";
                await RunAsync(["/export", Path.Combine(context.ArtifactDirectory, export), existing.Value.Guid], cancellationToken);
            }
        }
        return new PowerPlanActionState { PlanName = _parameters.PlanName, PreviousActiveGuid = active, PlanExisted = existing is not null, Settings = settings, ExportArtifact = export };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (PowerPlanActionState)before;
        var plans = await ListPlansAsync(cancellationToken);
        var guid = FindPlan(plans)?.Guid;
        if (guid is null)
        {
            if (state.PlanExisted) throw new VerificationFailedException("El plan preexistente desapareció antes de aplicar.");
            var result = await RunAsync(["/duplicatescheme", _parameters.SourceSchemeGuid], cancellationToken);
            guid = ParseGuid(result, "duplicated power scheme");
        }
        foreach (var setting in _parameters.Settings)
        {
            await RunAsync(["/setacvalueindex", guid, setting.SubgroupGuid, setting.SettingGuid, setting.AcValue.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            await RunAsync(["/setdcvalueindex", guid, setting.SubgroupGuid, setting.SettingGuid, setting.DcValue.ToString(CultureInfo.InvariantCulture)], cancellationToken);
        }
        var active = await GetActivePlanAsync(cancellationToken);
        if (_parameters.Activate || string.Equals(active, guid, StringComparison.OrdinalIgnoreCase)) await RunAsync(["/setactive", guid], cancellationToken);
        context.Report("apply", 1, $"Plan '{_parameters.PlanName}' configurado ({guid}).");
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var plan = FindPlan(await ListPlansAsync(cancellationToken));
        if (plan is null) return VerificationResult.Missing("El plan FragForge no existe.");
        foreach (var setting in _parameters.Settings)
        {
            var current = await QuerySettingAsync(plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken);
            if (current.AcIndex != setting.AcValue || current.DcIndex != setting.DcValue) return VerificationResult.Missing($"El ajuste {setting.SettingGuid} no coincide.");
        }
        if (_parameters.Activate)
        {
            var active = await GetActivePlanAsync(cancellationToken);
            if (!string.Equals(active, plan.Value.Guid, StringComparison.OrdinalIgnoreCase)) return VerificationResult.Missing("El plan FragForge no está activo.");
        }
        return VerificationResult.Match($"Plan '{_parameters.PlanName}' y ajustes verificados.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (PowerPlanActionState)before;
        if (string.IsNullOrWhiteSpace(state.PreviousActiveGuid)) throw new InvalidDataException("El snapshot no contiene el plan activo previo.");
        await RunAsync(["/setactive", state.PreviousActiveGuid], cancellationToken);
        var plan = FindPlan(await ListPlansAsync(cancellationToken));
        if (state.PlanExisted)
        {
            if (plan is null) throw new VerificationFailedException("El plan preexistente ya no existe.");
            foreach (var setting in state.Settings)
            {
                await RunAsync(["/setacvalueindex", plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, setting.AcIndex.ToString(CultureInfo.InvariantCulture)], cancellationToken);
                await RunAsync(["/setdcvalueindex", plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, setting.DcIndex.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            }
            await RunAsync(["/setactive", state.PreviousActiveGuid], cancellationToken);
        }
        else if (plan is not null) await RunAsync(["/delete", plan.Value.Guid], cancellationToken);
        context.Report("revert", 1, "Plan anterior reactivado; solo se elimina el plan creado por FragForge.");
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (PowerPlanActionState)before;
        var active = await GetActivePlanAsync(cancellationToken);
        if (!string.Equals(active, state.PreviousActiveGuid, StringComparison.OrdinalIgnoreCase)) return VerificationResult.Missing("El plan activo previo no fue restaurado.");
        var plan = FindPlan(await ListPlansAsync(cancellationToken));
        if (!state.PlanExisted) return plan is null ? VerificationResult.Match("Plan creado eliminado y plan previo activo.") : VerificationResult.Missing("El plan creado no fue eliminado.");
        if (plan is null) return VerificationResult.Missing("El plan preexistente fue eliminado incorrectamente.");
        foreach (var setting in state.Settings)
        {
            var current = await QuerySettingAsync(plan.Value.Guid, setting.SubgroupGuid, setting.SettingGuid, cancellationToken);
            if (current != setting) return VerificationResult.Missing("Los ajustes AC/DC previos no fueron restaurados.");
        }
        return VerificationResult.Match("Plan preexistente restaurado sin eliminarlo.");
    }
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var existing = FindPlan(await ListPlansAsync(cancellationToken));
        var action = existing is null ? $"Duplicar esquema {_parameters.SourceSchemeGuid} como '{_parameters.PlanName}'" : $"Actualizar el plan existente '{_parameters.PlanName}' sin eliminarlo";
        var settings = string.Join(", ", _parameters.Settings.Select(s => $"{s.SettingGuid} AC={s.AcValue} DC={s.DcValue}"));
        return $"{action}; {settings}; activar={_parameters.Activate}. Revert reactiva el plan anterior y elimina solo el plan nuevo, o restaura los ajustes previos si ya existía.";
    }
    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(_parameters.PlanName) || _parameters.PlanName.Contains('(') || _parameters.PlanName.Contains(')') || _parameters.PlanName.Length > 80) throw new CatalogValidationException("Invalid power plan name.", [_parameters.PlanName]);
        if (!Guid.TryParseExact(_parameters.SourceSchemeGuid, "D", out _)) throw new CatalogValidationException("Invalid source scheme GUID.", [_parameters.SourceSchemeGuid]);
        if (_parameters.Settings.Count == 0) throw new CatalogValidationException("Power plan requires settings.", ["settings"]);
        foreach (var setting in _parameters.Settings)
        {
            if (!Guid.TryParseExact(setting.SubgroupGuid, "D", out _) || !Guid.TryParseExact(setting.SettingGuid, "D", out _) || setting.AcValue is < 0 or > 100 || setting.DcValue is < 0 or > 100) throw new CatalogValidationException("Invalid power setting GUID or percentage.", [setting.SettingGuid]);
        }
    }
    private async Task<IReadOnlyList<(string Guid, string Name)>> ListPlansAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["/list"], cancellationToken);
        return Regex.Matches(result.StandardOutput, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\(([^)]*)\)").Select(m => (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim())).ToArray();
    }
    private async Task<string> GetActivePlanAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["/getactivescheme"], cancellationToken);
        var match = Regex.Match(result.StandardOutput, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((Active|Current|[^)]*)\)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : ParseGuid(result, "active power scheme");
    }
    private (string Guid, string Name)? FindPlan(IReadOnlyList<(string Guid, string Name)> plans)
    {
        var matches = plans.Where(p => p.Name.Equals(_parameters.PlanName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new FragForgeException("ambiguous_power_plan", $"Hay varios planes llamados '{_parameters.PlanName}'.");
        return matches.Length == 0 ? null : matches[0];
    }
    private async Task<PowerSettingCapture> QuerySettingAsync(string planGuid, string subgroupGuid, string settingGuid, CancellationToken cancellationToken)
    {
        var result = await RunAsync(["/query", planGuid, subgroupGuid, settingGuid], cancellationToken);
        var indices = Regex.Matches(result.StandardOutput, @"0x([0-9a-fA-F]+)\s*$", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        if (indices.Length < 2) throw new ActionNotApplicableException($"No se pudieron leer los índices AC/DC de {settingGuid}.");
        return new PowerSettingCapture(subgroupGuid, settingGuid, indices[^2], indices[^1]);
    }
    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunPowerCfgAsync(arguments, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powercfg.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError + result.StandardOutput);
        return result;
    }
    private static string ParseGuid(ProcessResult result, string context) => Regex.Match(result.StandardOutput, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value is var guid && Guid.TryParseExact(guid, "D", out _) ? guid.ToLowerInvariant() : throw new InvalidDataException($"No GUID found in {context} output.");
}
internal sealed record PowerPlanParameters
{
    public string PlanName { get; init; } = "FragForge Performance";
    public string SourceSchemeGuid { get; init; } = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public bool Activate { get; init; } = true;
    public IReadOnlyList<PowerPlanTarget> Settings { get; init; } = [];
}
public sealed record PowerPlanTarget
{
    public required string SubgroupGuid { get; init; }
    public required string SettingGuid { get; init; }
    public int AcValue { get; init; }
    public int DcValue { get; init; }
}
````

### `src/FragForge.Actions/Registry/IfeoPerfOptionsAction.cs`  <sub>(27 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Registry;

public sealed class IfeoPerfOptionsAction : ITweakAction
{
    private readonly RegistryAction _inner;
    public string Kind => "IfeoPerfOptions";
    public IfeoPerfOptionsAction(IRegistry registry, IProtectionPolicy policy, ActionSpec specification)
    {
        var p = ActionParameters.Parse<IfeoParameters>(specification);
        if (string.IsNullOrWhiteSpace(p.ImageName) || p.ImageName.Contains('\\') || p.ImageName.Contains('/') || p.ImageName.Contains('*') || p.ImageName.Contains('?')) throw new CatalogValidationException("IFEO requires an exact image file name.", [p.ImageName]);
        if (p.PriorityClass is not (1 or 2 or 3 or 5 or 6)) throw new CatalogValidationException("Realtime (4) and unknown CPU priority classes are forbidden.", [p.PriorityClass.ToString()]);
        var parameters = new RegistryActionParameters { Values = [new RegistryValueTarget { Key = $@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{p.ImageName}\PerfOptions", Name = "CpuPriorityClass", Kind = "DWord", Value = JsonSerializer.SerializeToElement(p.PriorityClass) }] };
        _inner = new RegistryAction(registry, policy, parameters);
    }
    public Task PreflightAsync(ActionExecutionContext c, CancellationToken t = default) => _inner.PreflightAsync(c, t);
    public Task<ActionState> CaptureAsync(ActionExecutionContext c, CancellationToken t = default) => _inner.CaptureAsync(c, t);
    public Task ApplyAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.ApplyAsync(s, c, t);
    public Task<VerificationResult> VerifyAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.VerifyAsync(s, c, t);
    public Task RevertAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.RevertAsync(s, c, t);
    public Task<VerificationResult> VerifyRevertAsync(ActionState s, ActionExecutionContext c, CancellationToken t = default) => _inner.VerifyRevertAsync(s, c, t);
    public Task<string> DryRunAsync(ActionExecutionContext c, CancellationToken t = default) => _inner.DryRunAsync(c, t);
}
internal sealed record IfeoParameters { public string ImageName { get; init; } = "cs2.exe"; public int PriorityClass { get; init; } = 3; }
````

### `src/FragForge.Actions/Registry/ProtectedRegistry.cs`  <sub>(14 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Actions.Registry;

public sealed class ProtectedRegistry(IRegistry inner, IProtectionPolicy policy) : IRegistry
{
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default) => inner.KeyExistsAsync(key, cancellationToken);
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default) => inner.ReadValueAsync(key, name, cancellationToken);
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default) => inner.GetValueNamesAsync(key, cancellationToken);
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default) => inner.GetSubKeyNamesAsync(key, cancellationToken);
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default) { policy.EnsureRegistryAllowed(key, name); return inner.WriteValueAsync(key, name, value, cancellationToken); }
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default) { policy.EnsureRegistryAllowed(key, name); return inner.DeleteValueAsync(key, name, cancellationToken); }
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default) { policy.EnsureRegistryAllowed(parentKey + "\\" + childName); return inner.DeleteEmptySubKeyAsync(parentKey, childName, cancellationToken); }
}
````

### `src/FragForge.Actions/Registry/RegistryAction.cs`  <sub>(144 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Core.Persistence;

namespace FragForge.Actions.Registry;

public class RegistryAction : ITweakAction
{
    private readonly IRegistry _registry;
    private readonly IProtectionPolicy _policy;
    private readonly RegistryActionParameters _parameters;
    public virtual string Kind => "Registry";
    public RegistryAction(IRegistry registry, IProtectionPolicy policy, ActionSpec specification)
    {
        _registry = registry;
        _policy = policy;
        _parameters = ActionParameters.Parse<RegistryActionParameters>(specification);
        if (_parameters.Values.Count == 0) throw new CatalogValidationException("Registry action requires at least one value.", ["values"]);
        foreach (var value in _parameters.Values)
        {
            RegistryPath.Canonicalize(value.Key);
            if (!RegistryValueConverter.SupportedKinds.Contains(value.Kind)) throw new CatalogValidationException($"Unsupported registry kind '{value.Kind}'.", [value.Kind]);
            if (value.Mode is not ("Set" or "AddToken")) throw new CatalogValidationException($"Unsupported registry mode '{value.Mode}'.", [value.Mode]);
            if (value.Mode == "AddToken" && (value.Kind != "String" || value.Value.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(value.Value.GetString()) || value.Value.GetString()!.Contains('\n'))) throw new CatalogValidationException("AddToken requires a single-line String token.", [value.Name]);
            RegistryValueConverter.FromJson(value.Kind, value.Value);
            policy.EnsureRegistryAllowed(value.Key, value.Name);
        }
    }
    internal RegistryAction(IRegistry registry, IProtectionPolicy policy, RegistryActionParameters parameters)
    {
        _registry = registry; _policy = policy; _parameters = parameters;
        foreach (var value in parameters.Values) policy.EnsureRegistryAllowed(value.Key, value.Name);
    }
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var target in _parameters.Values)
        {
            var key = RegistryPath.ResolveUserHive(ActionParameters.ResolveToken(target.Key, context), context.TargetUserSid);
            var name = ActionParameters.ResolveToken(target.Name, context);
            _policy.EnsureRegistryAllowed(key, name);
        }
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var captures = new List<RegistryValueCapture>();
        foreach (var target in ResolveTargets(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
            var missing = new List<string>();
            foreach (var ancestor in RegistryPath.AncestorsFromRoot(target.Key))
                if (!await _registry.KeyExistsAsync(ancestor, cancellationToken)) missing.Add(ancestor);
            captures.Add(RegistryValueConverter.Capture(target.Key, target.Name, result, missing));
        }
        context.Report("capture", 0.25, $"{captures.Count} valores capturados, incluidos ausentes.");
        return new RegistryActionState { Values = captures };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (before is not RegistryActionState state) throw new ArgumentException("Registry snapshot required.", nameof(before));
        var targets = ResolveTargets(context);
        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            var desired = RegistryValueConverter.FromJson(target.Spec.Kind, target.Spec.Value);
            if (target.Spec.Mode == "AddToken")
            {
                var current = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
                var original = current.ValueExists && current.Kind == "String" ? current.Data as string ?? "" : "";
                var token = desired.Data as string ?? throw new InvalidOperationException("Token missing.");
                if (HasToken(original, token)) continue;
                desired = new RegistryWriteValue("String", original.Length == 0 ? token : original + (original.EndsWith(' ') ? "" : " ") + token);
            }
            await _registry.WriteValueAsync(target.Key, target.Name, desired, cancellationToken);
            context.Report("apply", (double)(i + 1) / targets.Count, $"{target.Key}\\{target.Name} escrito.");
        }
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (before is not RegistryActionState state) return new(VerificationOutcome.Unknown, "Snapshot de registro inválido.");
        var targets = ResolveTargets(context);
        var matches = 0;
        var messages = new List<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            var current = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
            var desired = RegistryValueConverter.FromJson(target.Spec.Kind, target.Spec.Value);
            bool ok;
            if (target.Spec.Mode == "AddToken")
            {
                var token = (string)desired.Data;
                var original = state.Values[i].ValueExisted ? state.Values[i].StringData ?? "" : "";
                ok = current.ValueExists && current.Kind == "String" && current.Data is string text && HasToken(text, token) && (original.Length == 0 || text.StartsWith(original, StringComparison.Ordinal));
            }
            else ok = RegistryValueConverter.DataEqual(desired, current);
            if (ok) matches++; else messages.Add($"{target.Key}\\{target.Name} no coincide.");
        }
        return matches == targets.Count ? VerificationResult.Match($"Registro: {matches}/{targets.Count} valores coinciden.") : new VerificationResult(matches == 0 ? VerificationOutcome.DoesNotMatch : VerificationOutcome.Partial, string.Join(" ", messages));
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (before is not RegistryActionState state) throw new ArgumentException("Registry snapshot required.", nameof(before));
        foreach (var capture in state.Values.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            _policy.EnsureRegistryAllowed(capture.Key, capture.Name);
            if (capture.ValueExisted) await _registry.WriteValueAsync(capture.Key, capture.Name, RegistryValueConverter.ToWriteValue(capture), cancellationToken);
            else await _registry.DeleteValueAsync(capture.Key, capture.Name, cancellationToken);
            foreach (var missing in capture.MissingAncestors)
            {
                var parent = missing[..missing.LastIndexOf('\\')];
                var child = missing[(missing.LastIndexOf('\\') + 1)..];
                _policy.EnsureRegistryAllowed(missing);
                await _registry.DeleteEmptySubKeyAsync(parent, child, cancellationToken);
            }
        }
        context.Report("revert", 1, "Registro restaurado exactamente; valores ausentes eliminados.");
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var after = await CaptureAsync(context, cancellationToken);
        return Core.Engine.ActionStateEquivalence.Equivalent(before, after) ? VerificationResult.Match("Registro idéntico al estado capturado.") : VerificationResult.Missing("El registro no volvió exactamente al estado capturado.");
    }
    public async Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        foreach (var target in ResolveTargets(context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _registry.ReadValueAsync(target.Key, target.Name, cancellationToken);
            var desired = RegistryValueConverter.FromJson(target.Spec.Kind, target.Spec.Value);
            var previous = current.ValueExists ? $"{current.Kind}={current.Data}" : "(no existía)";
            lines.Add($"{target.Key}\\{target.Name}: {previous} -> {target.Spec.Kind}={RegistryValueConverter.Describe(desired)} ({target.Spec.Mode}); revert restaura el valor previo o lo elimina.");
        }
        return string.Join(Environment.NewLine, lines);
    }
    private IReadOnlyList<(string Key, string Name, RegistryValueTarget Spec)> ResolveTargets(ActionExecutionContext context) => _parameters.Values.Select(v => (RegistryPath.ResolveUserHive(ActionParameters.ResolveToken(v.Key, context), context.TargetUserSid), ActionParameters.ResolveToken(v.Name, context), v)).ToArray();
    private static bool HasToken(string value, string token) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.Ordinal);
}
````

### `src/FragForge.Actions/Registry/RegistryValueConverter.cs`  <sub>(61 líneas)</sub>

````csharp
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Registry;

public static class RegistryValueConverter
{
    public static readonly IReadOnlySet<string> SupportedKinds = new HashSet<string>(StringComparer.Ordinal) { "String", "ExpandString", "DWord", "QWord", "Binary", "MultiString" };
    public static RegistryWriteValue FromJson(string kind, JsonElement value)
    {
        if (!SupportedKinds.Contains(kind)) throw new CatalogValidationException($"Unsupported registry kind '{kind}'.", [kind]);
        object data = kind switch
        {
            "String" or "ExpandString" => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("Expected string."),
            "DWord" => value.TryGetInt32(out var i) ? i : throw new JsonException("Expected Int32."),
            "QWord" => value.TryGetInt64(out var l) ? l : throw new JsonException("Expected Int64."),
            "Binary" => Convert.FromBase64String(value.GetString() ?? throw new JsonException("Expected base64 string.")),
            "MultiString" => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(x => x.GetString() ?? throw new JsonException("Null string in array.")).ToArray() : throw new JsonException("Expected string array."),
            _ => throw new JsonException("Unsupported kind.")
        };
        return new RegistryWriteValue(kind, data);
    }
    public static RegistryValueCapture Capture(string key, string name, RegistryReadResult result, IReadOnlyList<string> missingAncestors) => new()
    {
        Key = key, Name = name, KeyExisted = result.KeyExists, ValueExisted = result.ValueExists, ValueKind = result.Kind,
        StringData = result.Data as string,
        IntegerData = result.Data is int i ? i : result.Data is long l ? l : null,
        MultiStringData = result.Data is string[] strings ? strings : null,
        BinaryBase64 = result.Data is byte[] bytes ? Convert.ToBase64String(bytes) : null,
        MissingAncestors = missingAncestors
    };
    public static RegistryWriteValue ToWriteValue(RegistryValueCapture capture) => capture.ValueKind switch
    {
        "String" or "ExpandString" => new(capture.ValueKind, capture.StringData ?? ""),
        "DWord" => new(capture.ValueKind, unchecked((int)(capture.IntegerData ?? 0))),
        "QWord" => new(capture.ValueKind, capture.IntegerData ?? 0L),
        "Binary" => new(capture.ValueKind, Convert.FromBase64String(capture.BinaryBase64 ?? "")),
        "MultiString" => new(capture.ValueKind, (capture.MultiStringData ?? []).ToArray()),
        _ => throw new InvalidDataException($"No se puede restaurar el tipo '{capture.ValueKind}'.")
    };
    public static bool DataEqual(RegistryWriteValue expected, RegistryReadResult actual)
    {
        if (!actual.ValueExists || !string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal)) return false;
        return expected.Data switch
        {
            string s => actual.Data is string a && s == a,
            int i => actual.Data is int ai && i == ai,
            long l => actual.Data is long al && l == al,
            byte[] b => actual.Data is byte[] ab && b.AsSpan().SequenceEqual(ab),
            string[] s => actual.Data is string[] aa && s.SequenceEqual(aa),
            _ => false
        };
    }
    public static string Describe(RegistryWriteValue value) => value.Data switch
    {
        byte[] bytes => Convert.ToBase64String(bytes),
        string[] strings => string.Join(" | ", strings),
        _ => Convert.ToString(value.Data, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };
}
````

### `src/FragForge.Actions/Registry/UnsupportedRegistry.cs`  <sub>(15 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;

namespace FragForge.Actions.Registry;

public sealed class UnsupportedRegistry : IRegistry
{
    private static T Fail<T>() => throw new PlatformNotSupportedException("El registro de Windows no está disponible en esta plataforma.");
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default) => Task.FromResult(new RegistryReadResult(false, false, null, null));
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default) => Fail<Task>();
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default) => Fail<Task>();
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default) => Fail<Task>();
}
````

### `src/FragForge.Actions/Registry/WindowsRegistry.cs`  <sub>(86 líneas)</sub>

````csharp
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Persistence;
using Microsoft.Win32;

namespace FragForge.Actions.Registry;

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistry : IRegistry
{
    public Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        return Task.FromResult(subkey is not null);
    }
    public Task<RegistryReadResult> ReadValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        if (subkey is null) return Task.FromResult(new RegistryReadResult(false, false, null, null));
        var data = subkey.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return Task.FromResult(data is null ? new RegistryReadResult(true, false, null, null) : new RegistryReadResult(true, true, subkey.GetValueKind(name).ToString(), data));
    }
    public Task<IReadOnlyList<string>> GetValueNamesAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        return Task.FromResult<IReadOnlyList<string>>(subkey?.GetValueNames() ?? []);
    }
    public Task<IReadOnlyList<string>> GetSubKeyNamesAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path);
        return Task.FromResult<IReadOnlyList<string>>(subkey?.GetSubKeyNames() ?? []);
    }
    public Task WriteValueAsync(string key, string name, RegistryWriteValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.CreateSubKey(path, true);
        subkey.SetValue(name, value.Data, Enum.Parse<RegistryValueKind>(value.Kind));
        subkey.Flush();
        return Task.CompletedTask;
    }
    public Task DeleteValueAsync(string key, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(key);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var subkey = root.OpenSubKey(path, true);
        subkey?.DeleteValue(name, false);
        subkey?.Flush();
        return Task.CompletedTask;
    }
    public Task DeleteEmptySubKeyAsync(string parentKey, string childName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hive, path) = Split(parentKey);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var parent = root.OpenSubKey(path, true);
        if (parent is null) return Task.CompletedTask;
        using var child = parent.OpenSubKey(childName);
        if (child is null || child.SubKeyCount != 0 || child.ValueCount != 0) return Task.CompletedTask;
        parent.DeleteSubKey(childName, false);
        parent.Flush();
        return Task.CompletedTask;
    }
    private static (RegistryHive Hive, string Path) Split(string key)
    {
        var canonical = RegistryPath.Canonicalize(key);
        var index = canonical.IndexOf('\\');
        var root = index < 0 ? canonical : canonical[..index];
        var path = index < 0 ? "" : canonical[(index + 1)..];
        return (root switch { "HKLM" => RegistryHive.LocalMachine, "HKU" => RegistryHive.Users, "HKCU" => RegistryHive.CurrentUser, "HKCR" => RegistryHive.ClassesRoot, "HKCC" => RegistryHive.CurrentConfig, _ => throw new ArgumentException("Unsupported registry root.") }, path);
    }
}
````

### `src/FragForge.Actions/Scripting/PowerShellAction.cs`  <sub>(91 líneas)</sub>

````csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Logging;
using FragForge.Core.Models;

namespace FragForge.Actions.Scripting;

public sealed class PowerShellAction(IProcessRunner runner, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly PowerShellParameters _parameters = ActionParameters.Parse<PowerShellParameters>(specification);
    public string Kind => "PowerShell";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.AllowPowerShellActions) throw new ProtectedTargetException("PowerShell", "deshabilitado por defecto; requiere AllowPowerShellActions y doble confirmación Advanced");
        Validate();
        foreach (var script in Scripts()) policy.EnsurePowerShellAllowed(script);
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var result = await RunAsync(_parameters.CaptureScript, null, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new InvalidDataException("CaptureScript must return a JSON object or array.");
        return new ScriptActionState { ScriptSha256 = HashScripts(), CapturedJson = document.RootElement.Clone() };
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var state = (ScriptActionState)before;
        EnsureHash(state);
        await RunAsync(_parameters.ApplyScript, JsonSerializer.Serialize(state.CapturedJson, JsonDefaults.Create(false)), cancellationToken);
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var state = (ScriptActionState)before;
        var result = await RunAsync(_parameters.VerifyScript, JsonSerializer.Serialize(state.CapturedJson, JsonDefaults.Create(false)), cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        var matches = document.RootElement.GetProperty("matches").GetBoolean();
        var message = document.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "Script verification.";
        return matches ? VerificationResult.Match(message) : VerificationResult.Missing(message);
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        var state = (ScriptActionState)before;
        EnsureHash(state);
        await RunAsync(_parameters.RevertScript, JsonSerializer.Serialize(state.CapturedJson, JsonDefaults.Create(false)), cancellationToken);
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var after = await CaptureAsync(context, cancellationToken);
        return Core.Engine.ActionStateEquivalence.Equivalent(before, after) ? VerificationResult.Match("El script de captura volvió al JSON original.") : VerificationResult.Missing("El script de captura no reproduce el estado original.");
    }
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(context);
        return Task.FromResult($"PowerShell local explícito (Experimental/Advanced), SHA256={HashScripts()}. Captura/aplica/verifica/revertir con JSON por stdin. No se descarga código.\nAplicación:\n{_parameters.ApplyScript}\nReversión:\n{_parameters.RevertScript}");
    }
    private void EnsureAllowed(ActionExecutionContext context)
    {
        if (!context.AllowPowerShellActions) throw new ProtectedTargetException("PowerShell", "acción no habilitada");
        Validate();
        foreach (var script in Scripts()) policy.EnsurePowerShellAllowed(script);
    }
    private void EnsureHash(ScriptActionState state) { if (state.ScriptSha256 != HashScripts()) throw new FragForgeException("script_changed", "Los scripts difieren de la captura persistida."); }
    private void Validate()
    {
        if (Scripts().Any(string.IsNullOrWhiteSpace)) throw new CatalogValidationException("PowerShell requires capture/apply/verify/revert scripts.", ["scripts"]);
        if (_parameters.TimeoutSeconds is <= 0 or > 300) throw new CatalogValidationException("PowerShell timeout must be 1..300 seconds.", [_parameters.TimeoutSeconds.ToString()]);
    }
    private IEnumerable<string> Scripts() => [_parameters.CaptureScript, _parameters.ApplyScript, _parameters.VerifyScript, _parameters.RevertScript];
    private string HashScripts() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Scripts().ToArray(), JsonDefaults.Create(false))))).ToLowerInvariant();
    private async Task<ProcessResult> RunAsync(string script, string? input, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " + script], StandardInput = input, Timeout = TimeSpan.FromSeconds(_parameters.TimeoutSeconds) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powershell.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
        return result;
    }
}
internal sealed record PowerShellParameters
{
    public string CaptureScript { get; init; } = "";
    public string ApplyScript { get; init; } = "";
    public string VerifyScript { get; init; } = "";
    public string RevertScript { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 60;
}
````

### `src/FragForge.Actions/Services/ServiceAction.cs`  <sub>(50 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Services;

public sealed class ServiceAction : ITweakAction
{
    private readonly IServiceController _controller;
    private readonly IProtectionPolicy _policy;
    private readonly ServiceParameters _parameters;
    public string Kind => "Service";
    public ServiceAction(IServiceController controller, IProtectionPolicy policy, ActionSpec specification)
    {
        _controller = controller; _policy = policy; _parameters = ActionParameters.Parse<ServiceParameters>(specification);
        if (string.IsNullOrWhiteSpace(_parameters.Name)) throw new CatalogValidationException("Service name is required.", ["name"]);
        if (_parameters.StartMode is not null and not ("Auto" or "Manual" or "Disabled")) throw new CatalogValidationException("Invalid service start mode.", [_parameters.StartMode]);
        if (_parameters.State is not null and not ("Running" or "Stopped")) throw new CatalogValidationException("Invalid service state.", [_parameters.State]);
        if (_parameters.StartMode is null && _parameters.State is null && _parameters.DelayedAutoStart is null) throw new CatalogValidationException("Service action has no target.", ["empty"]);
        _policy.EnsureServiceAllowed(_parameters.Name);
    }
    public async Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        _policy.EnsureServiceAllowed(_parameters.Name);
        var current = await _controller.GetAsync(_parameters.Name, cancellationToken);
        if (!current.Exists) throw new ActionNotApplicableException($"El servicio {_parameters.Name} no existe.");
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => new ServiceActionState { Service = await _controller.GetAsync(_parameters.Name, cancellationToken) };
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (_parameters.StartMode is not null || _parameters.DelayedAutoStart is not null) await _controller.SetStartModeAsync(_parameters.Name, _parameters.StartMode ?? "Manual", _parameters.DelayedAutoStart, cancellationToken);
        if (_parameters.State is not null) await _controller.SetStateAsync(_parameters.Name, _parameters.State, cancellationToken);
    }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var current = await _controller.GetAsync(_parameters.Name, cancellationToken);
        var ok = (_parameters.StartMode is null || current.StartMode == _parameters.StartMode) && (_parameters.State is null || current.State == _parameters.State) && (_parameters.DelayedAutoStart is null || current.DelayedAutoStart == _parameters.DelayedAutoStart);
        return ok ? VerificationResult.Match($"Servicio {_parameters.Name}: {current.StartMode}/{current.State}.") : VerificationResult.Missing($"Servicio {_parameters.Name} no coincide.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var state = (ServiceActionState)before;
        if (!state.Service.Exists) throw new InvalidOperationException("Cannot revert a service that did not exist at capture.");
        _policy.EnsureServiceAllowed(state.Service.Name);
        await _controller.SetStartModeAsync(state.Service.Name, state.Service.StartMode ?? "Manual", _parameters.DelayedAutoStart is null ? null : state.Service.DelayedAutoStart, cancellationToken, !state.Service.DelayedAutoStartExisted);
        await _controller.SetStateAsync(state.Service.Name, state.Service.State ?? "Stopped", cancellationToken);
    }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Servicio restaurado.") : VerificationResult.Missing("El servicio no volvió al estado capturado.");
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult($"Servicio {_parameters.Name}: inicio={_parameters.StartMode ?? "sin cambio"}, estado={_parameters.State ?? "sin cambio"}, delayed={_parameters.DelayedAutoStart?.ToString() ?? "sin cambio"}. Revert restaura inicio y estado exactos.");
}
internal sealed record ServiceParameters { public required string Name { get; init; } public string? StartMode { get; init; } public string? State { get; init; } public bool? DelayedAutoStart { get; init; } }
````

### `src/FragForge.Actions/Services/WindowsServiceController.cs`  <sub>(56 líneas)</sub>

````csharp
using System.Management;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceController(IRegistry registry) : IServiceController
{
    private const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services";
    public Task<ServiceCapture> GetAsync(string name, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery($"SELECT Name,StartMode,State FROM Win32_Service WHERE Name='{Escape(name)}'"), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15) });
        using var results = searcher.Get();
        using var service = results.Cast<ManagementObject>().FirstOrDefault();
        if (service is null) return new ServiceCapture { Name = name, Exists = false };
        var delayed = await registry.ReadValueAsync(ServicesKey + "\\" + name, "DelayedAutostart", cancellationToken);
        return new ServiceCapture { Name = name, Exists = true, StartMode = service["StartMode"] as string, State = service["State"] as string, DelayedAutoStart = delayed.ValueExists ? Convert.ToInt32(delayed.Data) != 0 : false, DelayedAutoStartExisted = delayed.ValueExists };
    }, cancellationToken);
    public Task SetStartModeAsync(string name, string startMode, bool? delayedAutoStart, CancellationToken cancellationToken = default, bool restoreAbsentDelayed = false) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var service = Find(name);
        using var parameters = service.GetMethodParameters("ChangeStartMode");
        parameters["StartMode"] = startMode switch { "Auto" => "Automatic", "Manual" => "Manual", "Disabled" => "Disabled", _ => throw new ArgumentException("Invalid start mode.") };
        using var result = service.InvokeMethod("ChangeStartMode", parameters, null);
        var code = Convert.ToUInt32(result["ReturnValue"]);
        if (code != 0) throw new FragForgeException("service_change_failed", $"ChangeStartMode({name}) devolvió {code}.");
        if (delayedAutoStart is not null)
        {
            var key = ServicesKey + "\\" + name;
            if (restoreAbsentDelayed) await registry.DeleteValueAsync(key, "DelayedAutostart", cancellationToken);
            else await registry.WriteValueAsync(key, "DelayedAutostart", new RegistryWriteValue("DWord", delayedAutoStart.Value ? 1 : 0), cancellationToken);
        }
    }, cancellationToken);
    public Task SetStateAsync(string name, string state, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var service = Find(name);
        var current = service["State"] as string;
        if (string.Equals(current, state, StringComparison.OrdinalIgnoreCase)) return;
        var method = state switch { "Running" => "StartService", "Stopped" => "StopService", _ => throw new ArgumentException("Invalid service state.") };
        using var result = service.InvokeMethod(method, null, null);
        var code = Convert.ToUInt32(result["ReturnValue"]);
        if (code is not (0 or 10)) throw new FragForgeException("service_state_failed", $"{method}({name}) devolvió {code}.");
    }, cancellationToken);
    private static ManagementObject Find(string name)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery($"SELECT * FROM Win32_Service WHERE Name='{Escape(name)}'"), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15) });
        using var results = searcher.Get();
        return results.Cast<ManagementObject>().FirstOrDefault() ?? throw new ActionNotApplicableException($"El servicio {name} no existe.");
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
````

### `src/FragForge.Actions/Tasks/ScheduledTaskAction.cs`  <sub>(33 líneas)</sub>

````csharp
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Tasks;

public sealed class ScheduledTaskAction(IScheduledTaskStore store, IProtectionPolicy policy, ActionSpec specification) : ITweakAction
{
    private readonly ScheduledTaskParameters _parameters = ActionParameters.Parse<ScheduledTaskParameters>(specification);
    public string Kind => "ScheduledTask";
    public Task PreflightAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureTaskAllowed(_parameters.Path);
        if (!_parameters.Path.StartsWith('\\') || _parameters.Path.Contains("..")) throw new CatalogValidationException("Scheduled task path must be absolute and safe.", [_parameters.Path]);
        return Task.CompletedTask;
    }
    public async Task<ActionState> CaptureAsync(ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        policy.EnsureTaskAllowed(_parameters.Path);
        var state = await store.GetAsync(_parameters.Path, cancellationToken);
        if (!state.Exists) throw new ActionNotApplicableException($"La tarea {_parameters.Path} no existe.");
        return state;
    }
    public async Task ApplyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) { policy.EnsureTaskAllowed(_parameters.Path); await store.SetEnabledAsync(_parameters.Path, _parameters.Enabled, cancellationToken); }
    public async Task<VerificationResult> VerifyAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default)
    {
        var current = await store.GetAsync(_parameters.Path, cancellationToken);
        return current.Exists && current.Enabled == _parameters.Enabled ? VerificationResult.Match($"Tarea {_parameters.Path} enabled={current.Enabled}.") : VerificationResult.Missing("La tarea programada no coincide.");
    }
    public async Task RevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) { var state = (ScheduledTaskActionState)before; policy.EnsureTaskAllowed(state.Path); await store.SetEnabledAsync(state.Path, state.Enabled, cancellationToken); }
    public async Task<VerificationResult> VerifyRevertAsync(ActionState before, ActionExecutionContext context, CancellationToken cancellationToken = default) => Core.Engine.ActionStateEquivalence.Equivalent(before, await CaptureAsync(context, cancellationToken)) ? VerificationResult.Match("Tarea restaurada.") : VerificationResult.Missing("La tarea no volvió al estado capturado.");
    public Task<string> DryRunAsync(ActionExecutionContext context, CancellationToken cancellationToken = default) => Task.FromResult($"Tarea {_parameters.Path}: Enabled={_parameters.Enabled}; no se elimina ni se reescribe su XML.");
}
internal sealed record ScheduledTaskParameters { public required string Path { get; init; } public bool Enabled { get; init; } }
````

### `src/FragForge.Actions/Tasks/WindowsScheduledTaskStore.cs`  <sub>(42 líneas)</sub>

````csharp
using System.Runtime.Versioning;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Tasks;

[SupportedOSPlatform("windows")]
public sealed class WindowsScheduledTaskStore(IProcessRunner runner) : IScheduledTaskStore
{
    public async Task<ScheduledTaskActionState> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var (folder, name) = Split(path);
        var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); " +
            $"$t=Get-ScheduledTask -TaskPath '{Escape(folder)}' -TaskName '{Escape(name)}' -ErrorAction SilentlyContinue; " +
            "if ($null -eq $t) { '{\"exists\":false}' } else { [pscustomobject]@{exists=$true; enabled=[bool]$t.Settings.Enabled; xml=(Export-ScheduledTask -TaskPath $t.TaskPath -TaskName $t.TaskName)} | ConvertTo-Json -Compress -Depth 4 }";
        var result = await RunAsync(script, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
        var root = document.RootElement;
        return new ScheduledTaskActionState { Path = path, Exists = root.GetProperty("exists").GetBoolean(), Enabled = root.TryGetProperty("enabled", out var e) && e.GetBoolean(), XmlDefinition = root.TryGetProperty("xml", out var x) ? x.GetString() : null };
    }
    public async Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default)
    {
        var (folder, name) = Split(path);
        var cmdlet = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        await RunAsync($"$ErrorActionPreference='Stop'; {cmdlet} -TaskPath '{Escape(folder)}' -TaskName '{Escape(name)}' -Confirm:$false | Out-Null", cancellationToken);
    }
    private async Task<ProcessResult> RunAsync(string script, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest { FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], Timeout = TimeSpan.FromSeconds(60) }, cancellationToken);
        if (!result.IsSuccess) throw new ProcessFailedException("powershell.exe", result.ExitCode, result.TimedOut ? "timeout" : result.StandardError);
        return result;
    }
    private static (string Folder, string Name) Split(string path)
    {
        var normalized = path.Replace('/', '\\');
        var index = normalized.LastIndexOf('\\');
        if (index <= 0 || index == normalized.Length - 1) throw new ArgumentException("Invalid scheduled task path.");
        return (normalized[..(index + 1)], normalized[(index + 1)..]);
    }
    private static string Escape(string value) => value.Replace("'", "''");
}
````

