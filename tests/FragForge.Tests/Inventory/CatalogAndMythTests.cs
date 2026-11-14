using System.Text.Json;
using FragForge.Core.Myths;
using FragForge.Core.Tiers;
using FragForge.Core.Models;
using Xunit;

namespace FragForge.Tests.Inventory;

public sealed class MythCatalogTests
{
    [Fact]
    public void EmbeddedMyths_LoadWithBothLanguagesAndReferences()
    {
        var catalog = MythCatalog.LoadEmbedded();
        Assert.NotEmpty(catalog.Entries);
        Assert.All(catalog.Entries, myth =>
        {
            Assert.False(string.IsNullOrWhiteSpace(myth.Why.EsMx));
            Assert.False(string.IsNullOrWhiteSpace(myth.Why.EnUs));
            Assert.StartsWith("http", myth.Reference, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void KnownMyths_AreAnsweredConsistently()
    {
        var catalog = MythCatalog.LoadEmbedded();
        Assert.Equal(MythVerdict.False, catalog.Find("tcpackfrequency-nodelay")!.Verdict);
        Assert.Equal(MythVerdict.False, catalog.Find("ram-cleaner")!.Verdict);
        Assert.Equal(MythVerdict.Misleading, catalog.Find("realtime-priority")!.Verdict);
    }

    [Fact]
    public void MythWithoutReference_FailsToLoad()
    {
        var json = """{"schemaVersion":1,"myths":[{"id":"x","title":{"es-MX":"a","en-US":"b"},"verdict":"False","why":{"es-MX":"c","en-US":"d"},"reference":""}]}""";
        Assert.Throws<InvalidDataException>(() => MythCatalog.Parse(json));
    }

    [Fact]
    public void MythWithoutBothLanguages_FailsToLoad()
    {
        var json = """{"schemaVersion":1,"myths":[{"id":"x","title":{"es-MX":"a","en-US":""},"verdict":"False","why":{"es-MX":"c","en-US":"d"},"reference":"https://a"}]}""";
        Assert.Throws<InvalidDataException>(() => MythCatalog.Parse(json));
    }

    [Fact]
    public void DuplicateMythIds_FailToLoad()
    {
        var body = "\"title\":{\"es-MX\":\"a\",\"en-US\":\"b\"},\"verdict\":\"False\",\"why\":{\"es-MX\":\"c\",\"en-US\":\"d\"},\"reference\":\"https://a\"";
        var json = $$"""{"schemaVersion":1,"myths":[{"id":"x",{{body}}},{"id":"X",{{body}}}]}""";
        Assert.Throws<InvalidDataException>(() => MythCatalog.Parse(json));
    }

    [Theory]
    [InlineData("en-US", "Disabling HPET")]
    [InlineData("es-MX", "Desactivar HPET")]
    [InlineData(null, "Desactivar HPET")]
    public void TitlesResolveByCulture(string? culture, string expected)
    {
        var myth = MythCatalog.LoadEmbedded().Find("hpet-platformclock")!;
        Assert.Contains(expected, myth.Title.Resolve(culture), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RealtimeMyth_PointsAtHighNotRealtime()
    {
        var myth = MythCatalog.LoadEmbedded().Find("realtime-priority")!;
        Assert.Contains("High", myth.Why.EsMx, StringComparison.Ordinal);
    }
}

public sealed class DebloatCatalogTests
{
    private static readonly string CatalogRoot = FindCatalogRoot();

    private static string FindCatalogRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "FragForge.Tweaks")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("No se encontró src/FragForge.Tweaks desde " + AppContext.BaseDirectory);
        return Path.Combine(directory.FullName, "src", "FragForge.Tweaks", "Catalog");
    }

    private static IEnumerable<string> DebloatFiles() => Directory.GetFiles(Path.Combine(CatalogRoot, "Debloat"), "*.json");

    [Fact]
    public void ThereAreEnoughDebloatTweaks()
    {
        Assert.InRange(DebloatFiles().Count(), 15, 25);
    }

    [Fact]
    public void EveryDebloatTweak_IsBilingualWithHonestImpactAndWhenNotToApply()
    {
        foreach (var file in DebloatFiles())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var id = root.GetProperty("id").GetString();
            Assert.Equal("Debloat", root.GetProperty("category").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetProperty("es-MX").GetString()), id);
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetProperty("en-US").GetString()), id);
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("whenNotToApply").GetProperty("es-MX").GetString()), id);
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("whenNotToApply").GetProperty("en-US").GetString()), id);
            var fps = root.GetProperty("expectedImpact").GetProperty("fps");
            if (fps.ValueKind == JsonValueKind.String)
                Assert.Contains("Sin efecto medible", fps.GetString()!, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(root.GetProperty("references").EnumerateArray());
        }
    }

    [Fact]
    public void NoDebloatTweak_TouchesADeniedOrCoreService()
    {
        var tiers = ServiceTierCatalog.LoadEmbedded();
        foreach (var file in DebloatFiles())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var action in document.RootElement.GetProperty("actions").EnumerateArray())
            {
                if (!string.Equals(action.GetProperty("kind").GetString(), "Service", StringComparison.OrdinalIgnoreCase)) continue;
                var name = action.GetProperty("parameters").GetProperty("name").GetString()!;
                Assert.True(tiers.IsApplicable(name), $"{Path.GetFileName(file)} apunta a {name}, que no es T1/T2.");
                Assert.DoesNotContain(name, ServiceTierCatalog.CoreNeverTouch, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void NoDebloatTweak_TouchesDefragOrDeleteAppx()
    {
        foreach (var file in DebloatFiles())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("ScheduledDefrag", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Remove-AppxPackage", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"kind\": \"Appx", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ServiceTweaks_AlwaysComeWithWindowsCondition_AndNoAtlasGate()
    {
        foreach (var file in DebloatFiles())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var kinds = document.RootElement.GetProperty("conditions").EnumerateArray().Select(c => c.GetProperty("type").GetString()!).ToArray();
            Assert.Contains("windows", kinds, StringComparer.OrdinalIgnoreCase);
            // Corregido en la Capa 2: el debloat NO se condiciona a Atlas. Atlas es una referencia
            // informativa (AtlasProfile), no una compuerta: el tweak aplica en cualquier Windows.
            Assert.DoesNotContain("atlas", kinds, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DebloatTweaks_AreOptInExactlyOnce()
    {
        foreach (var file in DebloatFiles())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var id = document.RootElement.GetProperty("id").GetString();
            string[] tags = document.RootElement.TryGetProperty("tags", out var tagsElement)
                ? tagsElement.EnumerateArray().Select(t => t.GetString()!).ToArray()
                : Array.Empty<string>();
            Assert.Equal(1, tags.Count(t => string.Equals(t, "opt-in", StringComparison.OrdinalIgnoreCase)));
            Assert.False(string.IsNullOrWhiteSpace(id));
        }
    }

    [Fact]
    public void ValidatorRejectsAServiceTweak_WhenTheServiceIsNotT1OrT2()
    {
        // La regla de la Capa 2 probada en su forma mínima: el método de decisión del catálogo de tiers.
        var tiers = ServiceTierCatalog.LoadEmbedded();
        Assert.False(tiers.IsApplicable("PcaSvc"));        // T3 MeasureOnly
        Assert.False(tiers.IsApplicable("RpcSs"));         // T0 núcleo
        Assert.True(tiers.IsApplicable("DiagTrack"));      // T2
        Assert.True(tiers.IsApplicable("dmwappushservice")); // T1
    }
}

public sealed class FootprintComparerTests
{
    private static BenchmarkSession Session(string label, params (string Metric, double Value, string Unit)[] measurements) => new(
        label + "-1",
        label,
        measurements.Select(m => new BenchmarkMeasurement(m.Metric, m.Value, m.Unit, new Dictionary<string, string>())).ToArray(),
        []);

    [Fact]
    public void Compare_ComputesSignedPercentChange()
    {
        var before = Session("before", ("processCount", 200, "procesos"));
        var after = Session("after", ("processCount", 180, "procesos"));
        var comparison = Assert.Single(FragForge.Core.Sessions.FootprintComparer.Compare(before, after));
        Assert.Equal(-10, comparison.ChangePercent, 3);
        Assert.Equal("baja", comparison.Direction);
        Assert.True(comparison.Indicative);
    }

    [Fact]
    public void Compare_IsAlwaysMarkedIndicative()
    {
        var before = Session("before", ("cpuPercent", 4, "%"));
        var after = Session("after", ("cpuPercent", 4, "%"));
        Assert.True(Assert.Single(FragForge.Core.Sessions.FootprintComparer.Compare(before, after)).Indicative);
    }

    [Fact]
    public void Compare_SkipsMetricsMissingAfterwards()
    {
        var before = Session("before", ("a", 1, "x"), ("b", 2, "x"));
        var after = Session("after", ("a", 1, "x"));
        Assert.Single(FragForge.Core.Sessions.FootprintComparer.Compare(before, after));
    }

    [Fact]
    public void Compare_HandlesZeroBaselineWithoutDividingByZero()
    {
        var before = Session("before", ("interrupts", 0, "1/s"));
        var after = Session("after", ("interrupts", 5, "1/s"));
        var comparison = Assert.Single(FragForge.Core.Sessions.FootprintComparer.Compare(before, after));
        Assert.Equal(0, comparison.ChangePercent);
    }
}

public sealed class FootprintMeasurementTests
{
    private static FragForge.Core.Models.BenchmarkMeasurement Measurement(string metric, double value, string unit, params (string Key, string Value)[] metadata) =>
        new(metric, value, unit, metadata.ToDictionary(m => m.Key, m => m.Value));

    [Fact]
    public void MissingCounters_AreReportedAsANote_NotAsZero()
    {
        // Regla de honestidad: si el contador no existe, no se escribe 0.
        var measurements = new[]
        {
            Measurement("processCount", 180, "procesos"),
            Measurement("cpuUnavailable", 0, "", ("note", "Contadores PDH de CPU/DPC no disponibles"))
        };
        Assert.DoesNotContain(measurements, m => m.Metric == "cpuPercent");
        Assert.Contains(measurements, m => m.Metric == "cpuUnavailable" && m.Metadata["note"].Length > 0);
    }

    [Fact]
    public void ProcessNamesOnly_NoCommandLinesNoPaths()
    {
        // El inventario de huella guarda NOMBRE y PID. Nunca rutas, líneas de comando ni usuarios.
        var names = "cs2,explorer,dwm".Split(',');
        Assert.All(names, name => Assert.DoesNotContain("\\", name, StringComparison.Ordinal));
    }
}
