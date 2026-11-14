using System.Globalization;

namespace FragForge.Core.Analysis;

/// <summary>Dirección en la que un valor más alto es MEJOR. Se declara por métrica para no confundir ms con FPS.</summary>
public enum BetterDirection
{
    /// <summary>Más alto es mejor (FPS).</summary>
    Higher,

    /// <summary>Más bajo es mejor (ms, tirones por minuto).</summary>
    Lower
}

/// <summary>Veredicto de UNA métrica comparando los dos conjuntos de corridas.</summary>
public enum MetricVerdict
{
    /// <summary>TODOS los valores de una condición quedan estrictamente por debajo de TODOS los de la otra.</summary>
    Separado,

    /// <summary>Los rangos se tocan o se cruzan: con estos datos no se distingue.</summary>
    Solapado,

    /// <summary>Faltan corridas (menos de <see cref="RunSetComparer.MinRunsPerCondition"/>) o alguna corrida no tiene el dato.</summary>
    Insuficiente
}

/// <summary>Veredicto global de la comparación A vs B.</summary>
public enum OverallVerdict
{
    /// <summary>B es mejor en la métrica principal y no es peor de forma separada en las demás.</summary>
    Mejora,

    /// <summary>B es peor de forma separada en la métrica principal.</summary>
    Empeora,

    /// <summary>No hay separación suficiente para afirmar nada.</summary>
    SinDiferenciaDistinguible
}

/// <summary>Los valores de una condición para una métrica, con su resumen.</summary>
public sealed record ConditionValues
{
    public string Condition { get; init; } = "";

    /// <summary>Un valor por corrida, en el orden en que se pasaron. Null = esa corrida no tenía el dato.</summary>
    public IReadOnlyList<double?> Values { get; init; } = [];

    /// <summary>Mediana de los valores disponibles. Null si no hay ninguno.</summary>
    public double? Median { get; init; }

    /// <summary>Mínimo de los valores disponibles.</summary>
    public double? Min { get; init; }

    /// <summary>Máximo de los valores disponibles.</summary>
    public double? Max { get; init; }

    /// <summary>Cuántas corridas no aportaron dato (métrica null por pocos frames, p. ej.).</summary>
    public int MissingRuns { get; init; }
}

/// <summary>Resultado de comparar una métrica entre dos condiciones.</summary>
public sealed record MetricComparison
{
    /// <summary>Nombre completo y explícito de la métrica, con unidad, p. ej. "Low1PercentAverage (FPS)".</summary>
    public string Name { get; init; } = "";

    public string Unit { get; init; } = "";
    public BetterDirection Direction { get; init; }
    public ConditionValues A { get; init; } = new();
    public ConditionValues B { get; init; } = new();
    public MetricVerdict Verdict { get; init; }

    /// <summary>Motivo del veredicto, en texto, siempre presente (sobre todo si es Insuficiente o Solapado).</summary>
    public string Reason { get; init; } = "";

    /// <summary>Qué condición queda mejor SI hay separación. Null si Solapado o Insuficiente.</summary>
    public string? BetterCondition { get; init; }
}

/// <summary>Resultado completo de A vs B.</summary>
public sealed record RunSetComparison
{
    public MetricComparison Low1Percent { get; init; } = new();
    public MetricComparison P99FrameTime { get; init; } = new();
    public MetricComparison HitchesPerMinute { get; init; } = new();
    public OverallVerdict Overall { get; init; }
    public string OverallReason { get; init; } = "";

    /// <summary>Avisos obligatorios: número de corridas distinto, duraciones distintas, y el de intercambiabilidad.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Compara dos conjuntos de corridas sin promediar y sin test de hipótesis.
/// </summary>
/// <remarks>
/// <para>
/// <b>El método.</b> Se mira si los dos conjuntos se separan por completo: todos los valores de una
/// condición por debajo de todos los de la otra. Es una prueba de "no hay solape", deliberadamente
/// conservadora. No se habla de "+X FPS" ni de porcentajes de mejora porque con capturas de una sola
/// máquina y condiciones cambiantes eso da una falsa precisión.
/// </para>
/// <para>
/// <b>Por qué hacen falta 4 corridas.</b> Si las corridas fueran intercambiables (el orden no influyera),
/// la probabilidad de que TODAS las de una condición caigan de un lado por puro azar es 2 / C(2n, n), con n
/// corridas por condición. Para n = 4 son 2/70 = 2.9 % y para n = 5, 2/252 = 0.8 %; con n = 3 ya son
/// 2/20 = 10 %, demasiado alto para afirmar nada. HEURÍSTICA NO VALIDADA en cuanto a que el 2.9 % sea "suficiente":
/// es un umbral de prudencia, no un nivel de significación formal de este estudio.
/// </para>
/// <para>
/// <b>El supuesto NO se cumple del todo.</b> Las corridas NO son intercambiables: la CPU limitada a 28 W se
/// calienta, y las últimas corridas parten de un estado térmico distinto. El orden ABBA reparte ese sesgo
/// entre las dos condiciones, no lo elimina. Por eso el aviso aparece SIEMPRE en el informe.
/// </para>
/// </remarks>
public static class RunSetComparer
{
    /// <summary>
    /// HEURÍSTICA NO VALIDADA. Corridas mínimas por condición. Ver el razonamiento combinatorio en el
    /// comentario del tipo: n = 3 deja un 10 % de separación total por azar, n = 4 baja a 2.9 %.
    /// </summary>
    public const int MinRunsPerCondition = 4;

    /// <summary>
    /// HEURÍSTICA NO VALIDADA. Si la duración total de una condición difiere de la otra en más de este
    /// factor, se avisa: capturas de duración muy distinta no son comparables en tirones por minuto.
    /// </summary>
    public const double DurationMismatchFactor = 1.5;

    /// <summary>Aviso fijo. Va siempre, aunque no se cumpla ninguna otra condición.</summary>
    public const string InterchangeabilityWarning =
        "Las corridas no son del todo intercambiables: con la CPU limitada a 28 W el calentamiento térmico " +
        "desplaza los resultados según el orden. El orden ABBA reparte ese efecto entre las dos condiciones, " +
        "no lo elimina. Trata el resultado como una descripción de estas corridas, no como una promesa.";

    /// <summary>
    /// Compara. <paramref name="a"/> y <paramref name="b"/> son las corridas de cada condición, ya reducidas
    /// a las tres métricas que importan; null en una métrica = esa corrida no tenía el dato.
    /// </summary>
    public static RunSetComparison Compare(
        IReadOnlyList<RunMetricSet> a,
        IReadOnlyList<RunMetricSet> b,
        double aDurationSeconds = 0,
        double bDurationSeconds = 0)
    {
        var warnings = new List<string> { InterchangeabilityWarning };
        if (a.Count != b.Count)
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"A tiene {a.Count} corridas y B tiene {b.Count}: con distinto número de corridas el reparto del sesgo térmico no es simétrico."));
        if (aDurationSeconds > 0 && bDurationSeconds > 0)
        {
            var ratio = Math.Max(aDurationSeconds, bDurationSeconds) / Math.Min(aDurationSeconds, bDurationSeconds);
            if (ratio > DurationMismatchFactor)
                warnings.Add("Duración muy distinta entre condiciones: " + Num(aDurationSeconds) + " s frente a " + Num(bDurationSeconds) + " s (factor " + Num(ratio, "0.##") + " > " + Num(DurationMismatchFactor) + "). Los tirones por minuto no son directamente comparables.");
        }

        var low1 = CompareMetric("Low1PercentAverage (FPS)", "FPS", BetterDirection.Higher, a, b, r => r.Low1PercentAverageFps);
        var p99 = CompareMetric("P99FrameTime (ms)", "ms", BetterDirection.Lower, a, b, r => r.P99FrameTimeMs);
        var hitches = CompareMetric("Tirones por minuto", "tirones/min", BetterDirection.Lower, a, b, r => r.HitchesPerMinute);

        // Veredicto global: la métrica principal decide; las otras dos solo pueden VETAR una mejora.
        OverallVerdict overall;
        string reason;
        var low1ForB = low1.Verdict == MetricVerdict.Separado && low1.BetterCondition == "B";
        var low1AgainstB = low1.Verdict == MetricVerdict.Separado && low1.BetterCondition == "A";
        var p99AgainstB = p99.Verdict == MetricVerdict.Separado && p99.BetterCondition == "A";
        var hitchesAgainstB = hitches.Verdict == MetricVerdict.Separado && hitches.BetterCondition == "A";

        if (low1AgainstB)
        {
            overall = OverallVerdict.Empeora;
            reason = "Low1PercentAverage se separa a favor de A: B es peor en la métrica principal.";
        }
        else if (low1ForB && !p99AgainstB && !hitchesAgainstB)
        {
            overall = OverallVerdict.Mejora;
            reason = "Low1PercentAverage se separa a favor de B, y ni P99FrameTime ni los tirones por minuto se separan en contra de B.";
        }
        else if (low1ForB)
        {
            overall = OverallVerdict.SinDiferenciaDistinguible;
            var vetoes = new List<string>();
            if (p99AgainstB) vetoes.Add("P99FrameTime se separa en contra de B");
            if (hitchesAgainstB) vetoes.Add("los tirones por minuto se separan en contra de B");
            reason = "Low1PercentAverage mejora, pero " + string.Join(" y ", vetoes) + ": no se declara mejora.";
        }
        else
        {
            overall = OverallVerdict.SinDiferenciaDistinguible;
            reason = low1.Verdict == MetricVerdict.Insuficiente
                ? "No hay corridas suficientes para comparar la métrica principal."
                : "Low1PercentAverage no se separa entre condiciones.";
        }

        return new RunSetComparison
        {
            Low1Percent = low1,
            P99FrameTime = p99,
            HitchesPerMinute = hitches,
            Overall = overall,
            OverallReason = reason,
            Warnings = warnings
        };
    }

    private static MetricComparison CompareMetric(
        string name,
        string unit,
        BetterDirection direction,
        IReadOnlyList<RunMetricSet> a,
        IReadOnlyList<RunMetricSet> b,
        Func<RunMetricSet, double?> selector)
    {
        var aValues = a.Select(selector).ToArray();
        var bValues = b.Select(selector).ToArray();

        var aCondition = Summarize("A", aValues);
        var bCondition = Summarize("B", bValues);

        if (a.Count < MinRunsPerCondition || b.Count < MinRunsPerCondition)
            return new MetricComparison
            {
                Name = name, Unit = unit, Direction = direction, A = aCondition, B = bCondition,
                Verdict = MetricVerdict.Insuficiente,
                Reason = string.Create(CultureInfo.InvariantCulture, $"Se necesitan al menos {MinRunsPerCondition} corridas por condición; hay {a.Count} y {b.Count}.")
            };

        if (aCondition.MissingRuns > 0 || bCondition.MissingRuns > 0)
            return new MetricComparison
            {
                Name = name, Unit = unit, Direction = direction, A = aCondition, B = bCondition,
                Verdict = MetricVerdict.Insuficiente,
                Reason = string.Create(CultureInfo.InvariantCulture, $"Hay corridas sin dato para esta métrica (A: {aCondition.MissingRuns}, B: {bCondition.MissingRuns}); normalmente por no llegar a {FrameStatisticsCalculator.MinFramesForLow1Percent} frames.")
            };

        var aMin = aCondition.Min!.Value;
        var aMax = aCondition.Max!.Value;
        var bMin = bCondition.Min!.Value;
        var bMax = bCondition.Max!.Value;

        // Separado cuando el rango de una condición queda por completo del lado bueno del otro.
        var aSeparatelyBetter = direction == BetterDirection.Higher ? bMax < aMin : aMax < bMin;
        var bSeparatelyBetter = direction == BetterDirection.Higher ? aMax < bMin : bMax < aMin;

        if (aSeparatelyBetter)
            return new MetricComparison
            {
                Name = name, Unit = unit, Direction = direction, A = aCondition, B = bCondition,
                Verdict = MetricVerdict.Separado, BetterCondition = "A",
                Reason = "Todos los valores de A (" + Num(aMin) + " a " + Num(aMax) + ") están del lado mejor que todos los de B (" + Num(bMin) + " a " + Num(bMax) + ")."
            };

        if (bSeparatelyBetter)
            return new MetricComparison
            {
                Name = name, Unit = unit, Direction = direction, A = aCondition, B = bCondition,
                Verdict = MetricVerdict.Separado, BetterCondition = "B",
                Reason = "Todos los valores de B (" + Num(bMin) + " a " + Num(bMax) + ") están del lado mejor que todos los de A (" + Num(aMin) + " a " + Num(aMax) + ")."
            };

        return new MetricComparison
        {
            Name = name, Unit = unit, Direction = direction, A = aCondition, B = bCondition,
            Verdict = MetricVerdict.Solapado,
            Reason = "Los rangos se tocan o se cruzan (A: " + Num(aMin) + " a " + Num(aMax) + "; B: " + Num(bMin) + " a " + Num(bMax) + "). Con estos datos no se distingue."
        };
    }

    /// <summary>Números como texto invariante (R-E): un informe no puede cambiar de "10.4" a "10,4" por la cultura.</summary>
    private static string Num(double value, string format = "0.###") => value.ToString(format, CultureInfo.InvariantCulture);

    private static ConditionValues Summarize(string condition, double?[] values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return new ConditionValues
        {
            Condition = condition,
            Values = values,
            Median = present.Length == 0 ? null : FrameStatisticsCalculator.Percentile(present.OrderBy(v => v).ToArray(), 50.0),
            Min = present.Length == 0 ? null : present.Min(),
            Max = present.Length == 0 ? null : present.Max(),
            MissingRuns = values.Length - present.Length
        };
    }
}

/// <summary>
/// Las tres métricas de UNA corrida, ya calculadas, listas para comparar. Es la interfaz entre el motor de
/// una sola captura y la comparación por conjuntos.
/// </summary>
/// <param name="Label">Etiqueta de la corrida, normalmente el nombre del fichero.</param>
/// <param name="Low1PercentAverageFps">Null si la corrida no tenía frames suficientes.</param>
/// <param name="P99FrameTimeMs">Percentil 99 del frametime.</param>
/// <param name="HitchesPerMinute">Tirones por minuto.</param>
/// <param name="DurationSeconds">Duración analizada, para el aviso de duraciones dispares.</param>
public sealed record RunMetricSet(
    string Label,
    double? Low1PercentAverageFps,
    double? P99FrameTimeMs,
    double? HitchesPerMinute,
    double DurationSeconds);
