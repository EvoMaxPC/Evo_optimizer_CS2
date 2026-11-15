using System.Globalization;
using FragForge.Core.Abstractions;
using FragForge.Core.Localization;
using FragForge.Core.Models;
using FragForge.Tests.Fakes;
using FragForge.Tweaks.Localization;
using FragForge.UI.Core;
using FragForge.UI.Core.Services;
using Xunit;

namespace FragForge.Tests.UI;

public sealed class HardwareCardTests
{
    private sealed class Dialogs : IConfirmationService, IFileDialogService
    {
        public bool Confirm(string message, string title) => false;
        public bool ConfirmRisk(string first, string second, string title) => false;
        public string? SaveReport() => null;
        public string? OpenCs2() => null;
    }

    private sealed class ScanEngine(HardwareProfile hardware) : ITweakEngine
    {
        public HardwareProfile Hardware { get; set; } = hardware;
        public Task<ScanReport> ScanAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) =>
            Task.FromResult(new ScanReport(Hardware, [], DateTimeOffset.UnixEpoch));
        public Task<BatchReport> ApplyAsync(IEnumerable<string> ids, ApplyOptions options, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
        public Task<BatchReport> RevertAsync(RevertRequest request, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
        public Task<TweakStatus> GetStateAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SystemSnapshot> CreateSnapshotAsync(string label, bool createRestorePoint = true, CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => throw new NotSupportedException();
    }

    private static async Task<(MainViewModel Vm, ScanEngine Engine)> CreateAsync(HardwareProfile profile)
    {
        var engine = new ScanEngine(profile);
        var dialogs = new Dialogs();
        var vm = new MainViewModel(engine, new FakeCatalog(), await LocalizationLoader.LoadAsync("es-MX"),
            new StubReports(), new UiLogSink(), dialogs, dialogs, new StubPreflight(), new FakeHardwareProfiler(), new StubContext());
        return (vm, engine);
    }

    [Fact]
    public async Task Scan_CompleteHardware_ShowsOrderedPairsAndPreservesSummary()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var profile = new HardwareProfile
            {
                MachineName = "Equipo-1",
                FormFactor = new FormFactorInfo { SystemModel = "Modelo X" },
                Os = new OsInfo { Name = "Windows 11", Build = 22631, Ubr = 1 },
                Cpu = new CpuInfo { Model = "CPU X", PCores = 4, ECores = 0, LogicalProcessors = 8, IsHybrid = true },
                Memory = new MemoryInfo { TotalBytes = 17179869184, Channels = MemoryChannelState.Dual, ConfiguredSpeedMtPerSecond = 3200, Modules = [new MemoryModuleInfo()] },
                Gpus = [new GpuInfo { Name = "GPU X", Vendor = GpuVendor.Nvidia }],
                NetworkAdapters = [new NetworkAdapterInfo { Name = "Ethernet", Description = "Cable", IsPhysical = true }],
                Security = new SecurityInfo { Vbs = FeatureState.Enabled, Hvci = FeatureState.Disabled, SecureBoot = FeatureState.Enabled, Tpm = FeatureState.Enabled },
                Power = new PowerInfo { ActiveSchemeName = "Equilibrado" },
                Games = new GameInfo { Cs2ExecutablePath = "C:/Games/cs2.exe" }
            };
            var (vm, _) = await CreateAsync(profile);
            using var cleanup = vm;
            await vm.ScanCommand.ExecuteAsync(null);

            // 17179869184 bytes / 1073741824 = 16 GiB; el cero de núcleos E es un dato conocido.
            Assert.Equal(new HardwareItem[]
            {
                new("Equipo", "Equipo-1"), new("Modelo", "Modelo X"), new("Sistema operativo", "Windows 11"),
                new("CPU", "CPU X"), new("Núcleos P", "4"), new("Núcleos E", "0"), new("Hilos", "8"),
                new("RAM", "16.00 GiB"), new("Velocidad RAM", "3200 MT/s"), new("GPU", "GPU X"),
                new("Red", "Ethernet"), new("Plan de energía", "Equilibrado"), new("CS2", "C:/Games/cs2.exe")
            }, vm.HardwareItems);
            Assert.Equal(string.Join(Environment.NewLine,
                "Equipo-1 · Modelo X · Windows 11 22631.1",
                "CPU: CPU X · P=4 E=0 · hilos=8 · híbrida=True",
                "RAM: 16.00 GiB · canales=Dual · módulos=1 · 3200 MT/s",
                "GPU: GPU X [Nvidia]",
                "Red: Ethernet (Cable)",
                "VBS=Enabled · HVCI=Disabled · SecureBoot=Enabled · TPM=Enabled · HAGS=Unknown",
                "Plan: Equilibrado · reinicio pendiente=False · TiWorker/TrustedInstaller=False",
                "CS2: C:/Games/cs2.exe"), vm.HardwareSummary);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public async Task Scan_UnknownHardware_UsesNoSeSabeAndNotifiesOnRescan()
    {
        var (vm, engine) = await CreateAsync(new HardwareProfile { MachineName = "Unknown" });
        using var cleanup = vm;
        Assert.Equal("Ejecuta Escanear para detectar el equipo.", vm.HardwareSummary);
        Assert.Empty(vm.HardwareItems);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        await vm.ScanCommand.ExecuteAsync(null);

        // Sin datos: las 13 celdas son desconocidas; las listas vacías no se presentan como cero.
        Assert.Equal(new HardwareItem[]
        {
            new("Equipo", "no se sabe"), new("Modelo", "no se sabe"), new("Sistema operativo", "no se sabe"),
            new("CPU", "no se sabe"), new("Núcleos P", "no se sabe"), new("Núcleos E", "no se sabe"),
            new("Hilos", "no se sabe"), new("RAM", "no se sabe"), new("Velocidad RAM", "no se sabe"),
            new("GPU", "no se sabe"), new("Red", "no se sabe"), new("Plan de energía", "no se sabe"), new("CS2", "no se sabe")
        }, vm.HardwareItems);
        Assert.Contains("HardwareItems", notifications);

        engine.Hardware = new HardwareProfile { MachineName = "Nuevo" };
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(new HardwareItem("Equipo", "Nuevo"), vm.HardwareItems[0]);
        Assert.Equal(2, notifications.Count(name => name == "HardwareItems"));
    }

    [Fact]
    public async Task LocalizedTweakHeaders_HaveExactValuesInBothCultures()
    {
        var es = await LocalizationLoader.LoadAsync("es-MX");
        var en = await LocalizationLoader.LoadAsync("en-US");
        Assert.Equal("Evidencia", es["evidence.column"]);
        Assert.Equal("Riesgo", es["risk.column"]);
        Assert.Equal("Requiere reinicio", es["reboot.required"]);
        Assert.Equal("Evidence", en["evidence.column"]);
        Assert.Equal("Risk", en["risk.column"]);
        Assert.Equal("Requires restart", en["reboot.required"]);
    }
}
