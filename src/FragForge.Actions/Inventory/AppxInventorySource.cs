using System.Runtime.Versioning;
using System.Text.Json;
using FragForge.Core.Abstractions;
using FragForge.Core.Inventory;
using FragForge.Core.Models;

namespace FragForge.Actions.Inventory;

/// <summary>
/// Inventario de paquetes Appx. SOLO INVENTARIO.
///
/// No existe —ni existirá— un tweak de eliminación de AppX en FragForge: desinstalar un paquete no es
/// exactamente reversible (la versión, el estado por usuario y los datos asociados no vuelven con
/// <c>Add-AppxPackage</c>), y eso viola el invariante de reversibilidad. Este código se limita a listar.
///
/// Se usa un script fijo propio y pasa por <see cref="IProtectionPolicy.EnsurePowerShellAllowed"/>
/// antes de ejecutarse, igual que el resto de acciones PowerShell.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppxInventorySource(IProcessRunner runner, IProtectionPolicy policy) : IAppxInventorySource
{
    private const string Script = """
        $ErrorActionPreference='Stop'
        [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new()
        Get-AppxPackage | Select-Object -Property Name,Publisher,PackageFullName,IsFramework |
            ConvertTo-Json -Compress -Depth 3
        """;

    public async Task<(IReadOnlyList<AppxEntry> Entries, IReadOnlyList<string> Warnings)> ReadAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        try { policy.EnsurePowerShellAllowed(Script); }
        catch (ProtectedTargetException ex) { warnings.Add("Script de inventario Appx rechazado: " + ex.Message); return ([], warnings); }

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(new ProcessRequest
            {
                FileName = Platform.ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", Script],
                Timeout = TimeSpan.FromMinutes(2)
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add("Get-AppxPackage: " + ex.Message);
            return ([], warnings);
        }

        if (!result.IsSuccess)
        {
            warnings.Add($"Get-AppxPackage falló (exit {result.ExitCode}): {Trim(result.StandardError)}");
            return ([], warnings);
        }

        var entries = new List<AppxEntry>();
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput.TrimStart('\uFEFF'));
            var root = document.RootElement;
            // ConvertTo-Json emite un OBJETO, no un array, cuando hay un solo paquete. Eso no es un error.
            var items = root.ValueKind switch
            {
                JsonValueKind.Array => root.EnumerateArray().ToArray(),
                JsonValueKind.Object => [root],
                _ => []
            };
            if (items.Length == 0)
            {
                warnings.Add($"Get-AppxPackage devolvió {root.ValueKind}, que no es una lista de paquetes.");
                return ([], warnings);
            }
            foreach (var item in items)
            {
                var name = item.TryGetProperty("Name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                entries.Add(new AppxEntry
                {
                    Name = name!,
                    Publisher = item.TryGetProperty("Publisher", out var p) ? p.GetString() : null,
                    PackageFullName = item.TryGetProperty("PackageFullName", out var f) ? f.GetString() : null,
                    IsFramework = item.TryGetProperty("IsFramework", out var i) && i.ValueKind is JsonValueKind.True or JsonValueKind.False ? i.GetBoolean() : null
                });
            }
        }
        catch (JsonException ex)
        {
            warnings.Add("JSON de Get-AppxPackage ilegible: " + ex.Message);
            return ([], warnings);
        }

        return (entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray(), warnings);
    }

    private static string Trim(string value) => value.Length <= 200 ? value.Trim() : value[..200].Trim();
}

public sealed class UnsupportedAppxInventorySource : IAppxInventorySource
{
    public Task<(IReadOnlyList<AppxEntry> Entries, IReadOnlyList<string> Warnings)> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(((IReadOnlyList<AppxEntry>)[], (IReadOnlyList<string>)["El inventario Appx requiere Windows."]));
}
