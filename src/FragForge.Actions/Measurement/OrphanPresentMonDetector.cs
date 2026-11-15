using System.Globalization;
using System.Text;
using FragForge.Actions.Platform;
using FragForge.Core.Abstractions;

namespace FragForge.Actions.Measurement;

/// <summary>PresentMon identificado como una instancia de medición de FragForge.</summary>
public sealed record OrphanPresentMon(int ProcessId, string? ExecutablePath, string? CommandLine, string? SessionName);

/// <summary>Resultado de la búsqueda y notas sobre procesos que no pudieron identificarse.</summary>
public sealed record OrphanPresentMonSearch(IReadOnlyList<OrphanPresentMon> Processes, IReadOnlyList<string> Notes);

/// <summary>Busca procesos que coinciden con la ruta aprobada y el nombre de sesión de FragForge, sin cerrarlos.</summary>
public sealed class OrphanPresentMonDetector(IWmiQuery wmi, string approvedExecutablePath)
{
    public async Task<OrphanPresentMonSearch> Find(CancellationToken cancellationToken = default)
    {
        var rows = await wmi.QueryAsync(@"root\cimv2", "Win32_Process",
            ["Name", "ProcessId", "ExecutablePath", "CommandLine"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var matches = new List<OrphanPresentMon>();
        var notes = new List<string>();
        var approved = Path.GetFullPath(approvedExecutablePath);
        var unknown = false;
        foreach (var row in rows)
        {
            if (!string.Equals(WmiValues.String(row, "Name"), "PresentMon.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var pid = WmiValues.Int32(row, "ProcessId");
            var path = WmiValues.String(row, "ExecutablePath");
            var commandLine = WmiValues.String(row, "CommandLine");
            if (pid is null || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(commandLine))
            {
                unknown = true;
                continue;
            }
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                unknown = true;
                continue;
            }
            if (!string.Equals(fullPath, approved, StringComparison.OrdinalIgnoreCase)) continue;
            var session = ExtractSessionName(commandLine);
            if (session is not null) matches.Add(new OrphanPresentMon(pid.Value, path, commandLine, session));
        }
        if (unknown) notes.Add("Hay procesos PresentMon que no se pudieron identificar; no se propone cerrarlos.");
        return new OrphanPresentMonSearch(matches, notes);
    }

    /// <summary>Extrae una sesión de FragForge de la línea de comandos, o null si no coincide.</summary>
    public static string? ExtractSessionName(string commandLine)
    {
        // Conservador: no identificar líneas con comillas escapadas o sin cerrar.
        if (commandLine.Contains("\\\"", StringComparison.Ordinal)) return null;
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (started) { tokens.Add(current.ToString()); current.Clear(); started = false; }
            }
            else { current.Append(ch); started = true; }
        }
        if (quoted) return null;
        if (started) tokens.Add(current.ToString());
        for (var i = 0; i + 1 < tokens.Count; i++)
            if (tokens[i] == "--session_name" && tokens[i + 1].StartsWith(LivePresentMonArguments.SessionNamePrefix, StringComparison.Ordinal))
                return tokens[i + 1];
        return null;
    }
}

/// <summary>Texto informativo; no cierra procesos ni garantiza el cierre de sesiones ETW.</summary>
public static class OrphanPresentMonAdvice
{
    public static string Describe(OrphanPresentMon orphan) => string.Create(CultureInfo.InvariantCulture,
        $"PresentMon PID {orphan.ProcessId}; ruta: {orphan.ExecutablePath ?? "no se sabe"}; sesión: {orphan.SessionName ?? "no se sabe"}. Cerralo a mano desde el Administrador de tareas. Se observó una vez en una PC que cerrar PresentMon no cerró la sesión ETW; match ofrece cerrar sesiones FragForgeLive- al iniciar.");
}
