using System.Globalization;
using System.Net;
using System.Text;

namespace FragForge.Core.Experiments;

/// <summary>
/// Informe HTML autocontenido de un experimento.
///
/// Reglas:
/// <list type="bullet">
/// <item>Todo texto se codifica con <see cref="WebUtility.HtmlEncode"/>: nada de interpolación cruda.</item>
/// <item>Gráficos en SVG generados aquí: sin JavaScript, sin CDN, sin recursos externos.</item>
/// <item>La línea de 5.00 ms del presupuesto de 200 Hz se dibuja como REFERENCIA, no como objetivo.</item>
/// <item>Si el veredicto es Indicativo o Inconcluso, se muestra un banner explícito.</item>
/// </list>
/// </summary>
public static class ExperimentReportRenderer
{
    public const double FrameBudgetMs200Hz = 5.00;

    public static string Render(ExperimentState state, IReadOnlyList<RunResult>? runs = null)
    {
        var plan = state.Plan;
        var allRuns = runs ?? state.Runs;
        var verdict = state.Verdict;
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>FragForge — Experimento ").Append(E(plan.SessionId)).Append("</title>");
        builder.Append("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#0F1419;color:#E6EDF3;margin:2rem;line-height:1.45}h1,h2{color:#58A6FF}table{border-collapse:collapse;width:100%;margin:1rem 0;font-size:14px}td,th{border:1px solid #30363D;padding:.45rem .6rem;text-align:left}th{background:#161B22}code{color:#79C0FF}.banner{padding:.8rem 1rem;border-radius:6px;margin:1rem 0;font-weight:600}.warn{background:#3D2A00;border-left:4px solid #D29922}.bad{background:#3D1418;border-left:4px solid #F85149}.good{background:#0D2E1A;border-left:4px solid #3FB950}.muted{color:#8B949E;font-size:13px}</style></head><body>");
        builder.Append("<h1>FragForge · Experimento A/B</h1>");
        builder.Append("<p class=\"muted\">Sesión <code>").Append(E(plan.SessionId)).Append("</code> · etiqueta ").Append(E(plan.Label ?? "—")).Append(" · generado ").Append(E(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).Append("</p>");

        if (verdict is not null)
        {
            var css = verdict.Kind switch
            {
                ExperimentVerdictKind.Improvement => "good",
                ExperimentVerdictKind.Regression => "bad",
                ExperimentVerdictKind.NoDifference => "warn",
                _ => "warn"
            };
            builder.Append("<div class=\"banner ").Append(css).Append("\">").Append(E(Label(verdict.Kind))).Append(" · confianza ").Append(E(verdict.Confidence.ToString())).Append("</div>");
            if (verdict.Confidence != VerdictConfidence.Confirmed)
                builder.Append("<div class=\"banner warn\">Resultado <strong>no confirmado</strong>: muestra insuficiente o falta calibración A/A. No promuevas evidencia con esto.</div>");
            if (verdict.Kind == ExperimentVerdictKind.Inconclusive)
                builder.Append("<div class=\"banner warn\">Veredicto inconcluso: no hay evidencia direccional. Repite con más corridas.</div>");
            foreach (var reason in verdict.Reasons) builder.Append("<p>· ").Append(E(reason)).Append("</p>");
        }
        else if (state.Invalidated)
        {
            builder.Append("<div class=\"banner bad\">Experimento invalidado: ").Append(E(state.InvalidReason ?? "")).Append("</div>");
        }

        builder.Append("<h2>Plan</h2><table>");
        Row(builder, "Tweaks", plan.TweakIds.Count == 0 ? "—" : string.Join(", ", plan.TweakIds));
        Row(builder, "Diseño", plan.Mode == ExperimentMode.Interleaved ? "Intercalado ABAB (orden aleatorizado)" : "Bloque (advertencia: confusión térmica)");
        Row(builder, "Corridas por brazo", plan.RunsPerArm.ToString(CultureInfo.InvariantCulture));
        Row(builder, "Duración", plan.DurationSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
        Row(builder, "Calentamiento descartado", plan.WarmupSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
        Row(builder, "Semilla", plan.Seed.ToString(CultureInfo.InvariantCulture));
        Row(builder, "Calibración A/A", plan.IsAaCalibration ? "Sí" : "No");
        Row(builder, "Reinicios requeridos", plan.RequiredReboots.ToString(CultureInfo.InvariantCulture));
        builder.Append("</table>");

        if (state.Environment is not null)
        {
            var environment = state.Environment;
            builder.Append("<h2>Entorno</h2><table>");
            Row(builder, "Build", environment.OsBuild);
            Row(builder, "CPU", environment.CpuModel);
            Row(builder, "GPU", environment.GpuName);
            Row(builder, "Plan de energía", environment.PowerPlanName + " (" + environment.PowerPlanGuid + ")");
            Row(builder, "Refresco", environment.RefreshRateHz is null ? "Unknown" : environment.RefreshRateHz + " Hz");
            Row(builder, "Resolución", environment.MonitorWidth is null ? "Unknown" : $"{environment.MonitorWidth}x{environment.MonitorHeight}");
            Row(builder, "Carga de fondo", environment.BackgroundCpuPercent is null ? "Unknown" : environment.BackgroundCpuPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + " %");
            builder.Append("</table>");
        }

        builder.Append("<h2>Corridas</h2><table><tr><th>#</th><th>Brazo</th><th>Válida</th><th>avgFps</th><th>p1Low</th><th>P50 ms</th><th>P99 ms</th><th>Jitter ms</th><th>Stutter %</th><th>PresentMode</th></tr>");
        foreach (var run in allRuns.OrderBy(r => r.Index))
        {
            var m = run.Metrics;
            builder.Append("<tr><td>").Append(run.Index + 1).Append("</td><td>").Append(E(run.Arm.ToString())).Append("</td><td>").Append(run.IsValid ? "sí" : "no — " + E(run.InvalidReason ?? "")).Append("</td>")
                .Append("<td>").Append(N(m?.AvgFps, "0.0")).Append("</td>")
                .Append("<td>").Append(N(m?.P1Low, "0.0")).Append("</td>")
                .Append("<td>").Append(N(m?.FrameTimeP50, "0.00")).Append("</td>")
                .Append("<td>").Append(N(m?.FrameTimeP99, "0.00")).Append("</td>")
                .Append("<td>").Append(N(m?.JitterMs, "0.00")).Append("</td>")
                .Append("<td>").Append(N(m?.StutterPct, "0.0")).Append("</td>")
                .Append("<td>").Append(E(m?.PresentModePrimary ?? "Unknown")).Append("</td></tr>");
        }
        builder.Append("</table>");

        var validRuns = allRuns.Where(r => r.IsValid && r.Metrics is not null).ToArray();
        if (validRuns.Length > 0)
        {
            builder.Append("<h2>Comparación A vs B</h2>");
            builder.Append(DistributionSvg(state, validRuns));
            builder.Append(FrameTimeLineSvg(validRuns));
        }
        builder.Append("<p class=\"muted\">Línea de referencia: presupuesto de frame de 5.00 ms (200 Hz). Es una referencia visual, no un objetivo garantizado.</p>");
        builder.Append("</body></html>");
        return builder.ToString();
    }

    private static string Label(ExperimentVerdictKind kind) => kind switch
    {
        ExperimentVerdictKind.Improvement => "Mejora",
        ExperimentVerdictKind.Regression => "Empeora",
        ExperimentVerdictKind.NoDifference => "Sin diferencia",
        _ => "Inconcluso"
    };

    private static void Row(StringBuilder builder, string key, string value) => builder.Append("<tr><th>").Append(E(key)).Append("</th><td>").Append(E(value)).Append("</td></tr>");

    private static string N(double? value, string format) => value is null ? "Unknown" : value.Value.ToString(format, CultureInfo.InvariantCulture);

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    /// <summary>ECDF de frametime por brazo, con la línea de presupuesto marcada.</summary>
    private static string DistributionSvg(ExperimentState state, RunResult[] runs)
    {
        const int width = 640, height = 260, pad = 40;
        var all = runs.Where(r => r.Metrics!.FrameTimeP99 is not null).Select(r => r.Metrics!.FrameTimeP99!.Value).ToArray();
        var max = Math.Max(all.DefaultIfEmpty(1).Max() * 1.2, FrameBudgetMs200Hz * 2);
        var builder = new StringBuilder($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"Distribución de frametime por brazo\"><rect width=\"{width}\" height=\"{height}\" fill=\"#0D1117\"/>");
        builder.Append($"<line x1=\"{pad}\" y1=\"{height - pad}\" x2=\"{width - pad}\" y2=\"{height - pad}\" stroke=\"#30363D\"/>");
        builder.Append($"<line x1=\"{pad}\" y1=\"{pad}\" x2=\"{pad}\" y2=\"{height - pad}\" stroke=\"#30363D\"/>");
        var budgetX = pad + (FrameBudgetMs200Hz / max) * (width - 2 * pad);
        builder.Append($"<line x1=\"{budgetX:F1}\" y1=\"{pad}\" x2=\"{budgetX:F1}\" y2=\"{height - pad}\" stroke=\"#D29922\" stroke-dasharray=\"4 4\"/>");
        builder.Append($"<text x=\"{budgetX + 4:F1}\" y=\"{pad + 12}\" fill=\"#D29922\" font-size=\"11\">5.00 ms · 200 Hz</text>");
        var arms = new[] { ArmKind.Baseline, ArmKind.Candidate };
        for (var index = 0; index < arms.Length; index++)
        {
            var arm = state.Arm(arms[index]);
            var values = arm.Metric(m => m.FrameTimeP50);
            if (values.Length == 0) continue;
            var x = pad + 60 + index * 120;
            var barWidth = 70;
            var median = Statistics.Median(values);
            var p99 = arm.Metric(m => m.FrameTimeP99);
            var barHeight = (median / max) * (height - 2 * pad);
            var p99Height = (p99.Length == 0 ? median : Statistics.Median(p99)) / max * (height - 2 * pad);
            var color = arms[index] == ArmKind.Baseline ? "#8B949E" : "#3FB950";
            builder.Append($"<rect x=\"{x}\" y=\"{height - pad - barHeight:F1}\" width=\"{barWidth}\" height=\"{barHeight:F1}\" fill=\"{color}\"/>");
            builder.Append($"<line x1=\"{x - 8}\" y1=\"{height - pad - p99Height:F1}\" x2=\"{x + barWidth + 8}\" y2=\"{height - pad - p99Height:F1}\" stroke=\"#F0883E\" stroke-width=\"2\"/>");
            builder.Append($"<text x=\"{x}\" y=\"{height - pad + 16}\" fill=\"#E6EDF3\" font-size=\"11\">").Append(E(arms[index].ToString())).Append("</text>");
            builder.Append($"<text x=\"{x}\" y=\"{height - pad - barHeight - 6:F1}\" fill=\"#E6EDF3\" font-size=\"11\">P50 ").Append(median.ToString("0.00", CultureInfo.InvariantCulture)).Append(" ms</text>");
        }
        builder.Append("</svg>");
        return builder.ToString();
    }

    /// <summary>Serie de frametime de la primera corrida válida de cada brazo: revela picos y forma de la curva.</summary>
    private static string FrameTimeLineSvg(RunResult[] runs)
    {
        const int width = 640, height = 220, pad = 40;
        var baseline = runs.FirstOrDefault(r => r.Arm == ArmKind.Baseline);
        var candidate = runs.FirstOrDefault(r => r.Arm == ArmKind.Candidate);
        var builder = new StringBuilder($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"Frametime por corrida\"><rect width=\"{width}\" height=\"{height}\" fill=\"#0D1117\"/>");
        builder.Append($"<line x1=\"{pad}\" y1=\"{height - pad}\" x2=\"{width - pad}\" y2=\"{height - pad}\" stroke=\"#30363D\"/>");
        var budgetY = height - pad - (FrameBudgetMs200Hz / 20.0) * (height - 2 * pad);
        builder.Append($"<line x1=\"{pad}\" y1=\"{budgetY:F1}\" x2=\"{width - pad}\" y2=\"{budgetY:F1}\" stroke=\"#D29922\" stroke-dasharray=\"4 4\"/>");
        var y = pad + 20;
        foreach (var (run, color, label) in new[] { (baseline, "#8B949E", "Base P50/P99"), (candidate, "#3FB950", "Candidato P50/P99") })
        {
            if (run?.Metrics is null) continue;
            var p50 = run.Metrics.FrameTimeP50 ?? 0;
            var p99 = run.Metrics.FrameTimeP99 ?? 0;
            builder.Append($"<rect x=\"{pad + 20}\" y=\"{y}\" width=\"{Math.Min(p99 / 20.0, 1.0) * (width - 2 * pad - 40):F1}\" height=\"10\" fill=\"{color}\" opacity=\"0.45\"/>");
            builder.Append($"<rect x=\"{pad + 20}\" y=\"{y}\" width=\"{Math.Min(p50 / 20.0, 1.0) * (width - 2 * pad - 40):F1}\" height=\"10\" fill=\"{color}\"/>");
            builder.Append($"<text x=\"{pad + 20}\" y=\"{y - 4}\" fill=\"#E6EDF3\" font-size=\"11\">").Append(E(label)).Append(" · P50 ").Append(p50.ToString("0.00", CultureInfo.InvariantCulture)).Append(" ms · P99 ").Append(p99.ToString("0.00", CultureInfo.InvariantCulture)).Append(" ms</text>");
            y += 40;
        }
        builder.Append("<text x=\"").Append(pad).Append("\" y=\"").Append(height - 10).Append("\" fill=\"#8B949E\" font-size=\"10\">Escala lineal hasta 20 ms; la barra tenue marca el P99.</text>");
        builder.Append("</svg>");
        return builder.ToString();
    }
}
