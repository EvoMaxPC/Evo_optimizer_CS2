using FragForge.Actions.Diagnostics;
using FragForge.Actions.Measurement;
using FragForge.Composition;
using FragForge.Core.Abstractions;
using FragForge.Core.Experiments;
using FragForge.Core.Models;
using FragForge.Core.Persistence;
using FragForge.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FragForge.Tests.Experiments;

/// <summary>Fase 0: cada cambio mínimo a la Capa 0, con su test.</summary>
public sealed class Phase0Tests
{
    private sealed class CountingHook : IExperimentHook
    {
        public int Order => 1;
        public Task<IReadOnlyList<ExperimentArtifact>> RunAsync(ExperimentContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExperimentArtifact>>([]);
    }

    [Fact]
    public async Task HookRegisteredViaConfigureServices_ReachesTheEngine()
    {
        using var directory = new TempDirectory();
        var options = new ShellOptions(directory.Path, "es-MX", ConsoleLogging: false, ConfigureServices: services => services.AddSingleton<IExperimentHook, CountingHook>());
        using var host = await CompositionRoot.BuildAsync(options);
        var hooks = host.Services.GetRequiredService<IEnumerable<IExperimentHook>>().ToArray();
        Assert.Contains(hooks, hook => hook is CountingHook);
        Assert.Contains(hooks, hook => hook is EnvironmentSnapshotHook);
    }

    [Fact]
    public async Task DefaultDiagnosticProbes_IncludeTheEightBaseProbes_WithUniqueKeys()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(new ShellOptions(directory.Path, "es-MX", ConsoleLogging: false));
        var probes = host.Services.GetRequiredService<IEnumerable<IDiagnosticProbe>>().ToArray();
        var expected = new[] { "hags", "mpo", "vbs", "hvci", "secureBoot", "dualChannel", "powerLimits", "ssdHealth" };
        foreach (var key in expected) Assert.Contains(probes, probe => probe.Key == key);
        var keys = probes.Select(probe => probe.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task AdditionalProbeRegisteredByThirdParty_IsVisibleAlongsideDefaults()
    {
        using var directory = new TempDirectory();
        var options = new ShellOptions(directory.Path, "es-MX", ConsoleLogging: false, ConfigureServices: services => services.AddSingleton<IDiagnosticProbe>(new CustomProbe()));
        using var host = await CompositionRoot.BuildAsync(options);
        var probes = host.Services.GetRequiredService<IEnumerable<IDiagnosticProbe>>().ToArray();
        Assert.Contains(probes, probe => probe is CustomProbe);
        Assert.Contains(probes, probe => probe.Key == "hags");
    }

    private sealed class CustomProbe : IDiagnosticProbe
    {
        public string Key => "custom-test";
        public Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiagnosticObservation(Key, "ok", "test", "High", "nada"));
    }

    [Fact]
    public async Task MeasurementServices_ResolveOnEveryPlatform()
    {
        // En Linux los servicios existen igual (Unsupported*): si no se pudieran resolver, el CLI
        // fallaría con un error de contenedor en lugar de decir "requiere Windows".
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(new ShellOptions(directory.Path, "es-MX", ConsoleLogging: false));
        Assert.NotNull(host.Services.GetRequiredService<IFrameCapture>());
        Assert.NotNull(host.Services.GetRequiredService<ISensorSampler>());
        Assert.NotNull(host.Services.GetRequiredService<IFactStore>());
        Assert.NotNull(host.Services.GetRequiredService<ExperimentStore>());
        Assert.NotNull(host.Services.GetRequiredService<LocalEvidenceStore>());
        Assert.NotNull(host.Services.GetRequiredService<ToolVerifier>());
        Assert.NotNull(host.Services.GetRequiredService<IExperimentEnvironmentProbe>());
    }

    [Fact]
    public async Task Capa1Probes_AreRegistered_EvenOnNonWindows()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(new ShellOptions(directory.Path, "es-MX", ConsoleLogging: false));
        var probes = host.Services.GetRequiredService<IEnumerable<IDiagnosticProbe>>().ToArray();
        Assert.Contains(probes, probe => probe.Key == "cpuPowerCounters");
        Assert.Contains(probes, probe => probe.Key == "presentMonTool");
    }

    [Fact]
    public async Task UnsupportedCapture_ReportsWhyAndNeverReturnsFakeFrames()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(new ShellOptions(directory.Path, "es-MX", ConsoleLogging: false));
        var capture = host.Services.GetRequiredService<IFrameCapture>();
        if (OperatingSystem.IsWindows()) return;
        var availability = await capture.CheckAvailabilityAsync();
        Assert.False(availability.IsAvailable);
        var series = await capture.CaptureAsync(new CaptureRequest { ProcessName = "cs2", OutputDirectory = directory.Path, RunId = "r1", Duration = TimeSpan.FromSeconds(1) });
        Assert.True(series.IsPartial);
        Assert.True(series.IsEmpty);
    }

    [Fact]
    public async Task ProtectionPolicy_IsASingleInstance()
    {
        using var directory = new TempDirectory();
        using var host = await CompositionRoot.BuildAsync(new ShellOptions(directory.Path, "es-MX", ConsoleLogging: false));
        Assert.Same(host.Services.GetRequiredService<IProtectionPolicy>(), host.Services.GetRequiredService<IProtectionPolicy>());
        Assert.NotNull(host.Services.GetRequiredService<ITweakActionFactory>());
    }

    [Fact]
    public void Paths_IncludeExperimentsToolsAndFacts()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        Assert.True(Directory.Exists(paths.Experiments));
        Assert.True(Directory.Exists(paths.Tools));
        Assert.EndsWith("facts.json", paths.Facts, StringComparison.Ordinal);
        Assert.EndsWith(".fragforge-experiment.lock", paths.ExperimentLockFile, StringComparison.Ordinal);
        Assert.NotEqual(paths.LockFile, paths.ExperimentLockFile);
    }

    [Fact]
    public async Task ExperimentGate_IsExclusiveAndIndependentOfTheEngineLock()
    {
        using var directory = new TempDirectory();
        var paths = new FragForgePaths(directory.Path);
        paths.EnsureCreated();
        await using var first = new ExperimentGate(paths);
        Assert.True(await first.TryAcquireAsync());
        await using var second = new ExperimentGate(paths);
        Assert.False(await second.TryAcquireAsync());
        await first.DisposeAsync();
        await using var third = new ExperimentGate(paths);
        Assert.True(await third.TryAcquireAsync());
    }
}

public sealed class ToolVerifierTests
{
    private static ToolAllowlist Allowlist(string? sha) => new()
    {
        Tools = [new AllowedTool { Key = ToolVerifier.PresentMonKey, FileName = "PresentMon.exe", Sha256 = sha }]
    };

    [Fact]
    public void MissingTool_IsReportedMissing_AndNeverDownloaded()
    {
        using var directory = new TempDirectory();
        var verification = new ToolVerifier(directory.Path, Allowlist(null)).Verify(ToolVerifier.PresentMonKey);
        Assert.Equal(ToolStatus.Missing, verification.Status);
        Assert.False(verification.IsUsable);
        Assert.Contains("no descarga", verification.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void PlaceholderHash_FailsWithAnExplicitMessageAndReportsTheRealHash()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "PresentMon.exe"), "fake binary");
        var verification = new ToolVerifier(directory.Path, Allowlist(null)).Verify(ToolVerifier.PresentMonKey);
        Assert.Equal(ToolStatus.HashPlaceholder, verification.Status);
        Assert.False(verification.IsUsable);
        Assert.Equal(ToolVerifier.ComputeSha256(Path.Combine(directory.Path, "PresentMon.exe")), verification.ActualSha256);
        Assert.Contains("SHA-256", verification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongHash_IsRejected()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "PresentMon.exe"), "fake binary");
        var verification = new ToolVerifier(directory.Path, Allowlist(new string('0', 64))).Verify(ToolVerifier.PresentMonKey);
        Assert.Equal(ToolStatus.HashMismatch, verification.Status);
        Assert.False(verification.IsUsable);
    }

    [Fact]
    public void CorrectHash_IsApproved()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "PresentMon.exe");
        File.WriteAllText(path, "fake binary");
        var verification = new ToolVerifier(directory.Path, Allowlist(ToolVerifier.ComputeSha256(path))).Verify(ToolVerifier.PresentMonKey);
        Assert.Equal(ToolStatus.Approved, verification.Status);
        Assert.True(verification.IsUsable);
    }

    [Fact]
    public void EmbeddedAllowlist_HasUserProvidedHash()
    {
        var allowlist = ToolVerifier.LoadEmbedded();
        var presentmon = allowlist.Find(ToolVerifier.PresentMonKey);
        Assert.NotNull(presentmon);
        Assert.Equal("b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af", presentmon!.Sha256);
    }

    [Fact]
    public void UnlistedTool_IsNotListed()
    {
        using var directory = new TempDirectory();
        Assert.Equal(ToolStatus.NotListed, new ToolVerifier(directory.Path, Allowlist(null)).Verify("latencymon").Status);
    }
}
