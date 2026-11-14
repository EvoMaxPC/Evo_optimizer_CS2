namespace FragForge.Core.Models;

public sealed record HardwareProfile
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public string MachineName { get; init; } = Environment.MachineName;
    public string? InteractiveUserName { get; init; }
    public string? InteractiveUserSid { get; init; }
    public string ProcessUserSid { get; init; } = "";
    public OsInfo Os { get; init; } = new();
    public CpuInfo Cpu { get; init; } = new();
    public MemoryInfo Memory { get; init; } = new();
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];
    public IReadOnlyList<NetworkAdapterInfo> NetworkAdapters { get; init; } = [];
    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = [];
    public SecurityInfo Security { get; init; } = new();
    public PowerInfo Power { get; init; } = new();
    public FormFactorInfo FormFactor { get; init; } = new();
    public IReadOnlyList<PlaybookDetection> Playbooks { get; init; } = [];
    public IReadOnlyList<DeviceErrorInfo> DeviceErrors { get; init; } = [];
    public GameInfo Games { get; init; } = new();
    public SystemMaintenanceInfo Maintenance { get; init; } = new();
    public IReadOnlyList<DiagnosticObservation> Observations { get; init; } = [];
    public IReadOnlyList<string> ProbeWarnings { get; init; } = [];
    public bool IsUnsupportedPlatform { get; init; }
}

public sealed record OsInfo
{
    public string Name { get; init; } = "Unknown";
    public string Edition { get; init; } = "Unknown";
    public string Version { get; init; } = "Unknown";
    public int Build { get; init; }
    public int Ubr { get; init; }
    public string Architecture { get; init; } = "Unknown";
    public string Culture { get; init; } = "es-MX";
    public string TimeZone { get; init; } = "Unknown";
}

public sealed record CpuInfo
{
    public string Model { get; init; } = "Unknown";
    public string Vendor { get; init; } = "Unknown";
    public int PhysicalCores { get; init; }
    public int LogicalProcessors { get; init; }
    public int? PCores { get; init; }
    public int? ECores { get; init; }
    public bool? IsHybrid { get; init; }
    public string TopologySource { get; init; } = "Unknown";
    public bool? FirmwareVirtualizationEnabled { get; init; }
    public int? BaseClockMhz { get; init; }
}

public sealed record MemoryInfo
{
    public ulong TotalBytes { get; init; }
    public MemoryChannelState Channels { get; init; } = MemoryChannelState.Unknown;
    public string ChannelEvidence { get; init; } = "WMI does not expose memory-controller interleaving reliably.";
    public int? ConfiguredSpeedMtPerSecond { get; init; }
    public IReadOnlyList<MemoryModuleInfo> Modules { get; init; } = [];
}

public sealed record MemoryModuleInfo
{
    public string? Manufacturer { get; init; }
    public string? PartNumber { get; init; }
    public string? BankLabel { get; init; }
    public string? DeviceLocator { get; init; }
    public ulong CapacityBytes { get; init; }
    public int? SpeedMtPerSecond { get; init; }
    public int? ConfiguredSpeedMtPerSecond { get; init; }
    public int? SmbiosMemoryType { get; init; }
}

public sealed record GpuInfo
{
    public string Name { get; init; } = "Unknown";
    public GpuVendor Vendor { get; init; }
    public string? DriverVersion { get; init; }
    public ulong? ReportedAdapterRamBytes { get; init; }
    public ulong? SharedMemoryBudgetBytes { get; init; }
    public bool IsIntegrated { get; init; }
    public string MemoryNote { get; init; } = "AdapterRAM is a firmware/reporting value, not a measurement of usable shared VRAM.";
}

public sealed record DiskInfo
{
    public string Model { get; init; } = "Unknown";
    public string? SerialNumber { get; init; }
    public ulong SizeBytes { get; init; }
    public DiskKind Kind { get; init; }
    public string Bus { get; init; } = "Unknown";
    public string Health { get; init; } = "Unknown";
    public bool? SmartPredictFailure { get; init; }
    public string SmartSource { get; init; } = "Unavailable";
    public ulong FreeBytes { get; init; }
    public string? SystemDrive { get; init; }
}

public sealed record NetworkAdapterInfo
{
    public string Name { get; init; } = "Unknown";
    public string Description { get; init; } = "Unknown";
    public NetworkKind Kind { get; init; }
    public string? DriverVersion { get; init; }
    public string? DriverProvider { get; init; }
    public ulong? LinkSpeedBitsPerSecond { get; init; }
    public bool IsPhysical { get; init; }
    public string State { get; init; } = "Unknown";
}

public sealed record MonitorInfo
{
    public string Name { get; init; } = "Unknown";
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? RefreshRateHz { get; init; }
    public string Source { get; init; } = "Unknown";
}

public sealed record SecurityInfo
{
    public FeatureState Vbs { get; init; } = FeatureState.Unknown;
    public FeatureState Hvci { get; init; } = FeatureState.Unknown;
    public FeatureState SecureBoot { get; init; } = FeatureState.Unknown;
    public FeatureState Tpm { get; init; } = FeatureState.Unknown;
    public string? TpmVersion { get; init; }
    public FeatureState Hags { get; init; } = FeatureState.Unknown;
    public FeatureState Mpo { get; init; } = FeatureState.Unknown;
    public string Evidence { get; init; } = "No probe has run.";
}

public sealed record PowerInfo
{
    public string? ActiveSchemeGuid { get; init; }
    public string? ActiveSchemeName { get; init; }
    public int? Pl1Watts { get; init; }
    public int? Pl2Watts { get; init; }
    public string PowerLimitSource { get; init; } = "Unavailable; DTT/IPF may own PL1/PL2.";
}

public sealed record FormFactorInfo
{
    public bool? IsPortable { get; init; }
    public bool HasBattery { get; init; }
    public IReadOnlyList<string> ChassisTypes { get; init; } = [];
    public string? SystemModel { get; init; }
    public string? BiosVersion { get; init; }
}

public sealed record PlaybookDetection(string Name, string Confidence, IReadOnlyList<string> Evidence);
public sealed record DeviceErrorInfo(string Name, string InstanceId, int ErrorCode, string? DriverName);
public sealed record GameInfo
{
    public string? Cs2ExecutablePath { get; init; }
    public string Cs2DetectionSource { get; init; } = "Not detected";
    public IReadOnlyList<string> Cs2Candidates { get; init; } = [];
    public bool Cs2Ambiguous { get; init; }
}
public sealed record SystemMaintenanceInfo
{
    public bool PendingReboot { get; init; }
    public IReadOnlyList<string> PendingRebootReasons { get; init; } = [];
    public bool WindowsServicingActive { get; init; }
    public IReadOnlyList<string> ServicingProcesses { get; init; } = [];
    public FeatureState SystemProtection { get; init; } = FeatureState.Unknown;
    public bool IsAdministrator { get; init; }
    public ulong SystemDriveFreeBytes { get; init; }
}
public sealed record DiagnosticObservation(string Key, string Value, string Source, string Confidence, string? Note = null);
