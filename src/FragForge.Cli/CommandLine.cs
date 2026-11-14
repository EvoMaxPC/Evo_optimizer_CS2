namespace FragForge.Cli;

public sealed class ParsedArgs
{
    public string Command { get; init; } = "help";
    public IReadOnlyList<string> Positionals { get; init; } = [];
    public IReadOnlyDictionary<string, List<string>> Options { get; init; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    public bool Has(string name) => Options.ContainsKey(name);
    public string? Value(string name) => Options.TryGetValue(name, out var values) ? values.LastOrDefault() : null;
    public IReadOnlyList<string> Values(string name) => Options.TryGetValue(name, out var values) ? values : [];
}
public static class CommandLineParser
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "json", "dry-run", "force", "no-restore-point", "enable-system-protection", "last", "allow-powershell", "help", "json", "aa", "blocked", "partial-ok", "reapply", "exclude-pauses", "include-unverified", "yes", "overwrite" };
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase) { "profile", "category", "risk", "out", "format", "snapshot", "label", "culture", "data-root", "cs2-path", "user-sid", "accept-risk", "risk-phrase", "duration", "runs", "warmup", "seed", "session", "probe-key", "mode", "confirm", "host", "count", "warmup-seconds", "dest", "objective", "fps-cap" };
    /// <summary>
    /// Opciones que toman VARIOS valores seguidos (<c>--versus b1.csv b2.csv</c>). El parser las ACEPTA y las
    /// marca como presentes, pero NO consume ningún valor. Si estuvieran en <c>ValueOptions</c> se tomaría
    /// solo el primero y el resto quedaría suelto. Quien las usa parte la línea con
    /// <see cref="CommandLineParser.SplitAt(string, string[])"/>.
    /// </summary>
    private static readonly HashSet<string> MultiValueOptions = new(StringComparer.OrdinalIgnoreCase) { "versus" };
    /// <summary>
    /// Parte la línea de comandos en el token <c>--opción</c>: devuelve lo de ANTES ya interpretado con
    /// <see cref="Parse(string[])"/> y los valores sueltos que vienen DESPUÉS (hasta el siguiente <c>--</c>).
    /// </summary>
    /// <remarks>
    /// Existe porque <see cref="Parse(string[])"/> mete en <see cref="ParsedArgs.Positionals"/> TAMBIÉN los
    /// valores de una opción multi-valor: con <c>analyze a.csv --versus b.csv</c>, "b.csv" acabaría entre los
    /// CSV de A. Partir la línea antes de parsear lo evita sin duplicar la lógica del parser.
    /// </remarks>
    public static (ParsedArgs Before, IReadOnlyList<string> ValuesAfter) SplitAt(string option, string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--" + option, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return (Parse(args), []);

        var before = Parse(args[..index]);
        var values = new List<string>();
        for (var i = index + 1; i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal); i++) values.Add(args[i]);
        return (before, values);
    }

    public static ParsedArgs Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help") return new ParsedArgs { Command = "help" };
        var command = args[0].ToLowerInvariant();
        var positionals = new List<string>();
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal)) { positionals.Add(token); continue; }
            var name = token[2..];
            if (Flags.Contains(name)) { options[name] = ["true"]; continue; }
            if (MultiValueOptions.Contains(name)) { options[name] = ["true"]; continue; }
            if (!ValueOptions.Contains(name)) throw new ArgumentException($"Opción desconocida: --{name}");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Falta el valor de --{name}");
            if (!options.TryGetValue(name, out var values)) { values = []; options[name] = values; }
            values.Add(args[++i]);
        }
        return new ParsedArgs { Command = command, Positionals = positionals, Options = options };
    }
}
public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int Preflight = 3;
    public const int Cancelled = 4;
    public const int RollbackCritical = 5;
    public const int MeasurementInvalid = 6;
}
