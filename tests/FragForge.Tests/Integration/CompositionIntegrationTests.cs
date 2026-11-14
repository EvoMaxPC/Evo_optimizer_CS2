using FragForge.Composition;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FragForge.Tests.Integration;

public sealed class CompositionIntegrationTests
{
    private static ShellOptions Options(string dataRoot) => new(dataRoot, "es-MX", ConsoleLogging: false);

    private static readonly string[] SeedIds =
    [
        "cpu.power-throttling-off",
        "cs2.cpu-priority-high",
        "cs2.fullscreen-optimizations-off",
        "diagnostics.system-inspect",
        "latency.game-dvr-capture-off",
        "latency.game-mode-on",
        "latency.global-timer-resolution",
        "latency.mouse-acceleration-off",
        "network.i225v-eee-off",
        "power.fragforge-performance"
    ];

    [Fact]
    public async Task CompositionRoot_ResolvesEveryService_AndLoadsTheEmbeddedCatalog()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var catalog = host.Services.GetRequiredService<ITweakCatalog>();
        // El catálogo es acumulativo por capas: mínimo y presencia, nunca igualdad exacta.
        Assert.True(catalog.Definitions.Count >= 10, $"El catálogo quedó en {catalog.Definitions.Count} tweaks; deben ser al menos 10.");
        foreach (var id in SeedIds) Assert.Contains(catalog.Definitions, d => d.Id == id);
        Assert.NotNull(host.Services.GetRequiredService<ITweakEngine>());
        Assert.NotNull(host.Services.GetRequiredService<IHardwareProfiler>());
        Assert.NotNull(host.Services.GetRequiredService<IReportService>());
        Assert.NotNull(host.Services.GetRequiredService<IJournal>());
        Assert.NotNull(host.Services.GetRequiredService<ISnapshotStore>());
        Assert.NotNull(host.Services.GetRequiredService<IProtectionPolicy>());
        Assert.NotNull(host.Services.GetRequiredService<ITweakActionFactory>());
        Assert.NotNull(host.Services.GetRequiredService<FragForge.Core.Localization.Localizer>());
        Assert.NotNull(host.Services.GetRequiredService<IExecutionGate>());
    }

    [Fact]
    public async Task ScanAndDryRun_WorkOnAnyPlatform_BecauseEverythingIsFakedOrGuarded()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var engine = host.Services.GetRequiredService<ITweakEngine>();
        var catalog = host.Services.GetRequiredService<ITweakCatalog>();
        var scan = await engine.ScanAsync();
        // El scan evalúa cada definición del catálogo: se compara contra el catálogo, no contra 10.
        Assert.Equal(catalog.Definitions.Count, scan.Tweaks.Count);
        if (!OperatingSystem.IsWindows()) Assert.True(scan.Hardware.IsUnsupportedPlatform);
        var report = await engine.ApplyAsync(scan.Tweaks.Select(t => t.Id), new ApplyOptions { DryRun = true, Profile = TweakProfile.Advanced, CreateRestorePoint = true, TargetUserSid = "S-1-5-21-1-2-3-1001" });
        // Diagnóstico: DryRun=false aquí NO significa que el dry-run se haya ignorado, sino que
        // ApplyAsync salió por el catch final (TweakEngine.cs:226-234), cuyo BatchReport no fija
        // DryRun. Los Errors dicen por qué se cortó (p. ej. una lectura WMI que lanza).
        Assert.True(
            report.DryRun,
            $"DryRun={report.DryRun}, Success={report.Success}, Cancelled={report.Cancelled}, " +
            $"HasCriticalFailure={report.HasCriticalFailure}, Results={report.Results.Count}, " +
            $"Errors=[{string.Join(" | ", report.Errors)}], Warnings=[{string.Join(" | ", report.Warnings)}]");
        Assert.Null(report.SnapshotId);
    }

    [Fact]
    public async Task Report_IsGeneratedAsJsonAndHtml_WithAllSections()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var reports = host.Services.GetRequiredService<IReportService>();
        var json = Path.Combine(directory.Path, "reports", "report.json");
        var html = Path.Combine(directory.Path, "reports", "report.html");
        await reports.ExportAsync(json, "json");
        await reports.ExportAsync(html, "html");
        Assert.True(File.Exists(json));
        Assert.True(File.Exists(html));
        var jsonText = await File.ReadAllTextAsync(json);
        Assert.Contains("\"hardware\"", jsonText, StringComparison.Ordinal);
        Assert.Contains("\"tweaks\"", jsonText, StringComparison.Ordinal);
        Assert.Contains("\"preflight\"", jsonText, StringComparison.Ordinal);
        Assert.Contains("\"journalIntegrity\"", jsonText, StringComparison.Ordinal);
        var htmlText = await File.ReadAllTextAsync(html);
        Assert.Contains("<html", htmlText, StringComparison.Ordinal);
        Assert.Contains("FragForge", htmlText, StringComparison.Ordinal);
        Assert.Contains("Journal:", htmlText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersonalDenylist_IsMergedFromProgramData()
    {
        using var directory = new TempDirectory();
        var policyPath = Path.Combine(directory.Path, "policy", "denylist.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        await File.WriteAllTextAsync(policyPath, """{"schemaVersion":1,"services":["DiagTrack"],"registryKeyPrefixes":["HKLM\\SOFTWARE\\Policies\\Example"],"registryValueNames":[],"taskPrefixes":[],"bcdDenyElements":[],"bcdAllowedElements":["disabledynamictick"],"filePrefixes":[],"networkPropertyKeywords":[],"powerShellDeniedTokens":[]}""");
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var policy = host.Services.GetRequiredService<IProtectionPolicy>();
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureServiceAllowed("DiagTrack"));
        Assert.Throws<ProtectedTargetException>(() => policy.EnsureRegistryAllowed(@"HKLM\SOFTWARE\Policies\Example\X", "Y"));
    }

    [Fact]
    public async Task SnapshotStoreAndJournal_PersistUnderTheConfiguredDataRoot()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(Options(directory.Path));
        var engine = host.Services.GetRequiredService<ITweakEngine>();
        var snapshots = host.Services.GetRequiredService<ISnapshotStore>();
        Assert.StartsWith(directory.Path, snapshots.RootPath, StringComparison.Ordinal);
        var report = await engine.ApplyAsync(["diagnostics.system-inspect"], new ApplyOptions { DryRun = true, Profile = TweakProfile.Advanced });
        Assert.True(report.Success, string.Join(" ", report.Errors));
        Assert.True(Directory.Exists(Path.Combine(directory.Path, "journal")));
        var journal = host.Services.GetRequiredService<IJournal>();
        Assert.True((await journal.ReadAllAsync()).Count > 0);
    }
}
