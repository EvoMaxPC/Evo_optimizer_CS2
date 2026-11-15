using System.Globalization;

namespace FragForge.Cli;

/// <summary>Opciones exclusivas de la medición en vivo.</summary>
public sealed record MatchOptions(double? DurationSeconds, bool SaveLog)
{
    public static MatchOptions Parse(IReadOnlyList<string> args)
    {
        double? duration = null;
        var saveLog = false;
        var durationSeen = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--guardar-log")
            {
                if (saveLog) throw new ArgumentException("La opción --guardar-log está repetida.");
                saveLog = true;
            }
            else if (arg == "--duration")
            {
                if (durationSeen) throw new ArgumentException("La opción --duration está repetida.");
                durationSeen = true;
                if (++i >= args.Count || !double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || !double.IsFinite(value) || value <= 0 || value > 86400)
                    throw new ArgumentException("La duración debe ser un número mayor que 0 y no mayor que 86400 segundos.");
                duration = value;
            }
            else throw new ArgumentException("Argumento desconocido: " + arg);
        }
        return new MatchOptions(duration, saveLog);
    }
}
