using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Tests.Fakes;

public sealed class FakeHardwareProfiler : IHardwareProfiler
{
    public HardwareProfile Profile { get; set; } = TestProfiles.Pn64();
    public Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => Task.FromResult(Profile);
}
public static class TestProfiles
{
    public static HardwareProfile Pn64() => new()
    {
        MachineName = "PN64-TEST",
        InteractiveUserName = "PN64\\ZuKA",
        InteractiveUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
        ProcessUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
        Os = new OsInfo { Name = "Microsoft Windows 11 Pro", Edition = "Microsoft Windows 11 Pro", Version = "10.0.26200", Build = 26200, Ubr = 6725, Architecture = "X64", Culture = "es-MX", TimeZone = "Argentina Standard Time" },
        Cpu = new CpuInfo { Model = "12th Gen Intel(R) Core(TM) i3-1220P", Vendor = "Intel", PhysicalCores = 10, LogicalProcessors = 12, PCores = 2, ECores = 8, IsHybrid = true, TopologySource = "EfficiencyClass", FirmwareVirtualizationEnabled = false, BaseClockMhz = 1500 },
        Memory = new MemoryInfo
        {
            TotalBytes = 16UL * 1024 * 1024 * 1024, Channels = MemoryChannelState.Dual, ConfiguredSpeedMtPerSecond = 3200,
            Modules = [new MemoryModuleInfo { BankLabel = "BANK 0", DeviceLocator = "Controller0-ChannelA", CapacityBytes = 8UL * 1024 * 1024 * 1024, SpeedMtPerSecond = 3200, ConfiguredSpeedMtPerSecond = 3200 }, new MemoryModuleInfo { BankLabel = "BANK 2", DeviceLocator = "Controller0-ChannelB", CapacityBytes = 8UL * 1024 * 1024 * 1024, SpeedMtPerSecond = 3200, ConfiguredSpeedMtPerSecond = 3200 }]
        },
        Gpus = [new GpuInfo { Name = "Intel(R) UHD Graphics", Vendor = GpuVendor.Intel, IsIntegrated = true, DriverVersion = "32.0.101.6129" }],
        Disks = [new DiskInfo { Model = "KINGSTON SA400S37480G", Kind = DiskKind.Ssd, Bus = "SATA", Health = "Healthy", SmartPredictFailure = false, SizeBytes = 480103981056 }],
        NetworkAdapters = [new NetworkAdapterInfo { Name = "Ethernet", Description = "Intel(R) Ethernet Controller I225-V", Kind = NetworkKind.Ethernet, IsPhysical = true, LinkSpeedBitsPerSecond = 1_000_000_000, State = "Connected" }, new NetworkAdapterInfo { Name = "Wi-Fi", Description = "Intel(R) Wi-Fi 6E AX210 160MHz", Kind = NetworkKind.WiFi, IsPhysical = true, LinkSpeedBitsPerSecond = 2_400_000_000, State = "Disconnected" }],
        Monitors = [new MonitorInfo { Name = "Generic PnP Monitor", Width = 1920, Height = 1080, RefreshRateHz = 144, Source = "Win32_VideoController" }],
        Security = new SecurityInfo { Vbs = FeatureState.Disabled, Hvci = FeatureState.Disabled, SecureBoot = FeatureState.Enabled, Tpm = FeatureState.Enabled, TpmVersion = "2.0", Hags = FeatureState.Disabled, Mpo = FeatureState.Disabled },
        Power = new PowerInfo { ActiveSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e", ActiveSchemeName = "Balanced", Pl1Watts = 28, Pl2Watts = 64 },
        FormFactor = new FormFactorInfo { IsPortable = false, HasBattery = false, ChassisTypes = ["MiniPC"], SystemModel = "MINIPC PN64", BiosVersion = "3.12.00" },
        Playbooks = [new PlaybookDetection("Atlas Playbook (heurística)", "Low", ["Servicios no corriendo (no concluyente): SysMain,WSearch,Spooler,DiagTrack"])],
        DeviceErrors = [new DeviceErrorInfo("PawnIO", @"ROOT\PAWNIO\0000", 0, "PawnIO"), new DeviceErrorInfo("(sin nombre)", @"USB\VID_0000&PID_0000\5&1", 28, null)],
        Games = new GameInfo { Cs2ExecutablePath = @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", Cs2DetectionSource = "Steam registry" },
        Maintenance = new SystemMaintenanceInfo { IsAdministrator = true, PendingReboot = false, WindowsServicingActive = false, SystemProtection = FeatureState.Enabled, SystemDriveFreeBytes = 120UL * 1024 * 1024 * 1024 },
        Observations = [new DiagnosticObservation("hags", "Disabled", "Registry HwSchMode", "High")]
    };
    public static HardwareProfile NonWindows() => new() { IsUnsupportedPlatform = true, Os = new OsInfo { Build = 0, Architecture = "X64" }, Cpu = new CpuInfo { IsHybrid = null }, Maintenance = new SystemMaintenanceInfo { IsAdministrator = false, SystemProtection = FeatureState.Unknown } };
    public static HardwareProfile Windows10() => Pn64() with { Os = Pn64().Os with { Build = 19045 } };
    public static HardwareProfile GpuOnlyDiscrete() => Pn64() with { Gpus = [new GpuInfo { Name = "NVIDIA GeForce RTX 4060", Vendor = GpuVendor.Nvidia }] };
    public static HardwareProfile SingleChannel() => Pn64() with { Memory = Pn64().Memory with { Channels = MemoryChannelState.Single, Modules = [Pn64().Memory.Modules[0]] } };
    public static HardwareProfile NoCs2() => Pn64() with { Games = new GameInfo { Cs2DetectionSource = "Not detected" } };
    public static HardwareProfile PendingReboot() => Pn64() with { Maintenance = Pn64().Maintenance with { PendingReboot = true, PendingRebootReasons = ["CBS RebootPending"] } };
    public static HardwareProfile NoAdmin() => Pn64() with { Maintenance = Pn64().Maintenance with { IsAdministrator = false } };
    public static HardwareProfile NoProtection() => Pn64() with { Maintenance = Pn64().Maintenance with { SystemProtection = FeatureState.Disabled } };
}
