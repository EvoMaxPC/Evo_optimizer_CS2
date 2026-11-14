namespace FragForge.Core.Experiments;

/// <summary>
/// Clasificador de cuello de botella. Sin columnas suficientes el resultado es <see cref="BottleneckKind.Unknown"/>
/// con nota; nunca se adivina.
///
/// En esta máquina (i3-1220P, 28 W, iGPU) CPU e iGPU comparten presupuesto de potencia, así que un
/// <see cref="BottleneckKind.CpuBound"/> puede ser en realidad potencia y no capacidad de la CPU.
/// </summary>
public static class BottleneckClassifier
{
    public const double BusyThreshold = 85.0;
    public const string SharedBudgetNote = "CPU e iGPU comparten el presupuesto de 28 W: un cuello 'CPU' puede ser de potencia.";

    public static BottleneckAssessment Classify(RunMetrics metrics, SensorSeries? sensors = null)
    {
        var cpu = metrics.CpuBusyP95;
        var gpu = metrics.GpuBusyP95;
        var frameTime = metrics.FrameTimeP99;
        if (cpu is null && gpu is null)
            return BottleneckAssessment.Unknown("El CSV no trae CPUBusy/GPUBusy: no se puede clasificar sin inventar datos.");

        var evidence = new Dictionary<string, double>();
        if (cpu is not null) evidence["cpuBusyP95"] = cpu.Value;
        if (gpu is not null) evidence["gpuBusyP95"] = gpu.Value;
        if (frameTime is not null) evidence["frameTimeP99"] = frameTime.Value;
        if (metrics.AvgFps is not null) evidence["avgFps"] = metrics.AvgFps.Value;

        var cpuHigh = cpu is not null && cpu >= BusyThreshold;
        var gpuHigh = gpu is not null && gpu >= BusyThreshold;

        if (cpuHigh && gpuHigh) return new BottleneckAssessment(BottleneckKind.Mixed, "Medium", "CPU y GPU saturan el mismo P95. " + SharedBudgetNote, evidence);
        if (cpuHigh) return new BottleneckAssessment(BottleneckKind.CpuBound, "Medium", "CPUBusy P95 alto con GPU por debajo del umbral. " + SharedBudgetNote, evidence);
        if (gpuHigh) return new BottleneckAssessment(BottleneckKind.GpuBound, "Medium", "GPUBusy P95 alto con CPU por debajo del umbral.", evidence);

        if (cpu is not null && gpu is null)
            return new BottleneckAssessment(BottleneckKind.Unknown, "Low", "Solo hay datos de CPU; sin GPUBusy no se descarta un cuello de GPU.", evidence);

        // Ambos por debajo del umbral pero el frametime es alto ⇒ sospecha de potencia/térmica.
        var powerLimited = sensors?.MedianCpuUtilityPercent is not null && sensors.MedianCpuUtilityPercent < 60 && frameTime is not null;
        if (powerLimited) return new BottleneckAssessment(BottleneckKind.PowerOrThermalLimited, "Low", "CPU y GPU no saturan pero el frametime es alto y la utilidad de CPU es baja. " + SharedBudgetNote, evidence);

        return new BottleneckAssessment(BottleneckKind.Unknown, "Low", "CPU y GPU por debajo del umbral: sin evidencia de cuello en ninguna de las dos.", evidence);
    }
}

/// <summary>
/// Detector de throttling: combina frecuencia real vs máxima (PDH), flags de límite de rendimiento
/// y deriva del frametime entre la primera y la segunda mitad de la corrida.
/// </summary>
public static class ThrottleDetector
{
    public const double FrequencyDeficitThresholdPct = 15.0;
    public const double FrameTimeDriftThresholdPct = 8.0;

    public static ThrottleAssessment Detect(FrameSeries series, SensorSeries? sensors)
    {
        var evidence = new List<string>();
        var flags = new List<string>();
        double? frequencyDeficit = null;
        double? drift = null;

        if (sensors is not null && sensors.Samples.Count > 0)
        {
            var frequencies = sensors.Samples.Select(s => s.CpuFrequencyMhz).Where(v => v is not null).Select(v => v!.Value).ToArray();
            var maxPercent = sensors.Samples.Select(s => s.CpuMaxFrequencyPercent).Where(v => v is not null).Select(v => v!.Value).ToArray();
            if (frequencies.Length >= 3 && maxPercent.Length >= 3)
            {
                var medianFrequency = Statistics.Median(frequencies);
                var medianPercent = Statistics.Median(maxPercent);
                if (medianPercent > 0)
                {
                    frequencyDeficit = 100.0 - medianPercent;
                    evidence.Add($"Frecuencia mediana {medianFrequency:F0} MHz con {medianPercent:F0} % del máximo ⇒ déficit {frequencyDeficit:F1} %.");
                }
            }
            var limitFlags = sensors.Samples.Select(s => s.PerformanceLimitFlags).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            flags.AddRange(limitFlags.Where(f => f is not "0" and not "0x0").Select(f => $"Performance Limit Flags = {f}"));
            evidence.AddRange(flags);
        }
        else
        {
            evidence.Add("Sin datos de sensores: no se evalúa frecuencia ni flags de límite.");
        }

        var times = series.FrameTimesMs.Where(v => v > 0).ToArray();
        if (times.Length >= 600)
        {
            var half = times.Length / 2;
            var first = Statistics.Median(times[..half]);
            var second = Statistics.Median(times[half..]);
            if (first > 0)
            {
                drift = (second - first) / first * 100.0;
                evidence.Add($"Frametime mediano {first:F2} ms (1ª mitad) → {second:F2} ms (2ª mitad) ⇒ deriva {drift:+0.0;-0.0} %.");
            }
        }
        else
        {
            evidence.Add("Menos de 600 frames: no se evalúa deriva de frametime.");
        }

        var frequencySuspect = frequencyDeficit is not null && frequencyDeficit > FrequencyDeficitThresholdPct;
        var driftSuspect = drift is not null && drift > FrameTimeDriftThresholdPct;
        var flagsSuspect = flags.Count > 0;
        var suspected = frequencySuspect || driftSuspect || flagsSuspect;

        var confidence = (frequencySuspect && driftSuspect) || (frequencySuspect && flagsSuspect) ? "Medium" : suspected ? "Low" : "Unknown";
        if (!suspected) evidence.Add("Sin evidencia de estrangulamiento térmico/de potencia en esta corrida.");

        return new ThrottleAssessment(suspected, confidence, evidence, drift, frequencyDeficit);
    }
}
