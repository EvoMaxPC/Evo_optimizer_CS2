using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Platform;

internal static class WmiValues
{
    public static string? String(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString()?.Trim() : null;
    public static ulong? UInt64(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null && ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    public static int? Int32(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    public static bool? Bool(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not null ? Convert.ToBoolean(value, CultureInfo.InvariantCulture) : null;
    public static IReadOnlyList<object> Array(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is System.Collections.IEnumerable enumerable and not string ? enumerable.Cast<object>().ToArray() : [];
}

[SupportedOSPlatform("windows")]
public sealed class WindowsHardwareProfiler(IWmiQuery wmi, IRegistry registry, IProcessRunner runner, IGameLocator games, ICpuTopologyProbe topology, ISystemRestoreService restore) : IHardwareProfiler
{
    /// <summary>Namespace real de <c>Win32_Tpm</c>. El anterior (<c>root\Microsoft\Windows\TPM</c>) no existe.</summary>
    public const string TpmScope = @"root\CIMV2\Security\MicrosoftTpm";
    /// <summary>Clase del proveedor de TPM.</summary>
    public const string TpmClass = "Win32_Tpm";

    /// <summary>
    /// Namespace de <c>MSFT_NetAdapter</c>. Ya era el correcto; se deja como constante para que el
    /// valor quede fijado y comprobado por un test en vez de vivir enterrado en una llamada.
    /// </summary>
    public const string NetAdapterScope = @"root\StandardCimv2";
    public const string NetAdapterClass = "MSFT_NetAdapter";

    public async Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null)
    {
        var warnings = new List<string>();
        var observations = new List<DiagnosticObservation>();
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Query(string scope, string className, string[] properties, string? where = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new EngineProgress("hardware", null, 0, $"Consultando {className}."));
            try { return await wmi.QueryAsync(scope, className, properties, where, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(DescribeQueryFailure(scope, className, properties, ex));
                return [];
            }
        }

        // Igual que Query, pero informando si la consulta FALLÓ. Necesario para no convertir una
        // excepción en "no hay instancias" (R-L). El aviso se registra igual que en Query.
        async Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool Failed)> QueryOrFail(string scope, string className, string[] properties, string? where = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new EngineProgress("hardware", null, 0, $"Consultando {className}."));
            try { return (await wmi.QueryAsync(scope, className, properties, where, cancellationToken), false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add(DescribeQueryFailure(scope, className, properties, ex));
                return ([], true);
            }
        }
        // La arquitectura sale del VALOR NUMÉRICO de Win32_Processor.Architecture
        // (0=x86, 5=ARM, 9=x64, 12=ARM64), nunca de un texto localizado (ver ResolveOsArchitecture).
        var cpuRows = await Query(@"root\cimv2", "Win32_Processor", ["Name", "Manufacturer", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed", "VirtualizationFirmwareEnabled", "VMMonitorModeExtensions", "Architecture"]);
        // OSArchitecture NO se pide a propósito: Win32_OperatingSystem lo devuelve traducido
        // ("64 bits" en es-MX) y eso es exactamente lo que rompía la condición de arquitectura.
        var osRows = await Query(@"root\cimv2", "Win32_OperatingSystem", ["Caption", "Version", "BuildNumber"]);
        var osRow = osRows.FirstOrDefault() ?? new Dictionary<string, object?>();
        var ubr = await registry.ReadValueAsync(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", cancellationToken);
        var os = new OsInfo
        {
            Name = WmiValues.String(osRow, "Caption") ?? "Windows",
            Edition = WmiValues.String(osRow, "Caption") ?? "Unknown",
            Version = WmiValues.String(osRow, "Version") ?? "Unknown",
            Build = WmiValues.Int32(osRow, "BuildNumber") ?? 0,
            Ubr = ubr.ValueExists ? Convert.ToInt32(ubr.Data, CultureInfo.InvariantCulture) : 0,
            Architecture = ArchitectureInterpreter.ToWireValue(ResolveOsArchitecture(cpuRows.FirstOrDefault(), warnings)),
            Culture = CultureInfo.CurrentCulture.Name,
            TimeZone = TimeZoneInfo.Local.Id
        };
        var csRows = await Query(@"root\cimv2", "Win32_ComputerSystem", ["UserName", "TotalPhysicalMemory", "Manufacturer", "Model", "PCSystemType"]);
        var cs = csRows.FirstOrDefault() ?? new Dictionary<string, object?>();
        var cpuRow = cpuRows.FirstOrDefault() ?? new Dictionary<string, object?>();
        var topologyResult = await topology.ReadAsync(cancellationToken);
        var vendor = (WmiValues.String(cpuRow, "Manufacturer") ?? "").Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : WmiValues.String(cpuRow, "Manufacturer") ?? "Unknown";
        var cpu = new CpuInfo
        {
            Model = WmiValues.String(cpuRow, "Name") ?? "Unknown", Vendor = vendor,
            PhysicalCores = cpuRows.Sum(r => WmiValues.Int32(r, "NumberOfCores") ?? 0),
            LogicalProcessors = topologyResult.LogicalProcessors > 0 ? topologyResult.LogicalProcessors : cpuRows.Sum(r => WmiValues.Int32(r, "NumberOfLogicalProcessors") ?? 0),
            PCores = topologyResult.PCores, ECores = topologyResult.ECores, IsHybrid = topologyResult.IsHybrid, TopologySource = topologyResult.Source,
            FirmwareVirtualizationEnabled = WmiValues.Bool(cpuRow, "VirtualizationFirmwareEnabled"), BaseClockMhz = WmiValues.Int32(cpuRow, "MaxClockSpeed")
        };
        if (topologyResult.IsHybrid is null) warnings.Add("CPU P/E: no se pudo consultar EfficiencyClass; no se asume hibridación por el nombre del modelo.");
        var memoryRows = await Query(@"root\cimv2", "Win32_PhysicalMemory", ["Manufacturer", "PartNumber", "BankLabel", "DeviceLocator", "Capacity", "Speed", "ConfiguredClockSpeed", "SMBIOSMemoryType"]);
        var modules = memoryRows.Select(r => new MemoryModuleInfo { Manufacturer = WmiValues.String(r, "Manufacturer"), PartNumber = WmiValues.String(r, "PartNumber"), BankLabel = WmiValues.String(r, "BankLabel"), DeviceLocator = WmiValues.String(r, "DeviceLocator"), CapacityBytes = WmiValues.UInt64(r, "Capacity") ?? 0, SpeedMtPerSecond = WmiValues.Int32(r, "Speed"), ConfiguredSpeedMtPerSecond = WmiValues.Int32(r, "ConfiguredClockSpeed"), SmbiosMemoryType = WmiValues.Int32(r, "SMBIOSMemoryType") }).ToArray();
        var speeds = modules.Select(m => m.ConfiguredSpeedMtPerSecond ?? m.SpeedMtPerSecond).Where(s => s is not null).Distinct().ToArray();
        var memory = new MemoryInfo { TotalBytes = WmiValues.UInt64(cs, "TotalPhysicalMemory") ?? 0, Modules = modules, Channels = MemoryChannelState.Unknown, ConfiguredSpeedMtPerSecond = speeds.Length == 1 ? speeds[0] : null, ChannelEvidence = DescribeChannels(modules) };
        var gpuRows = await Query(@"root\cimv2", "Win32_VideoController", ["Name", "AdapterCompatibility", "AdapterRAM", "DriverVersion", "CurrentHorizontalResolution", "CurrentVerticalResolution", "CurrentRefreshRate"]);
        // Capa 6.1: presupuesto de memoria COMPARTIDA de la iGPU, tomado del contador de memoria del
        // adaptador y no de AdapterRAM (uint32 de firmware que se desborda a 4 GiB). Si el contador no
        // existe queda null: null es "no se sabe", nunca cero.
        var gpuMemory = await new FragForge.Actions.Measurement.GpuPerformanceProbe(wmi).ReadAdapterMemoryAsync(cancellationToken);
        var sharedBudget = gpuMemory.Adapters.Select(a => a.CommittedBytes ?? a.SharedBytes).OfType<ulong>().DefaultIfEmpty(0UL).Max();
        warnings.AddRange(gpuMemory.Warnings);
        var gpus = gpuRows.Select(r =>
        {
            var name = WmiValues.String(r, "Name") ?? "Unknown";
            var compatibility = WmiValues.String(r, "AdapterCompatibility") ?? "";
            var gpuVendor = compatibility.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel : compatibility.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia : compatibility.Contains("AMD", StringComparison.OrdinalIgnoreCase) || compatibility.Contains("ATI", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd : compatibility.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Microsoft : GpuVendor.Unknown;
            return new GpuInfo { Name = name, Vendor = gpuVendor, DriverVersion = WmiValues.String(r, "DriverVersion"), ReportedAdapterRamBytes = WmiValues.UInt64(r, "AdapterRAM"), IsIntegrated = gpuVendor == GpuVendor.Intel && (name.Contains("UHD", StringComparison.OrdinalIgnoreCase) || name.Contains("Iris", StringComparison.OrdinalIgnoreCase) || name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)), SharedMemoryBudgetBytes = sharedBudget == 0 ? null : sharedBudget };
        }).ToArray();
        var diskRows = await Query(@"root\cimv2", "Win32_DiskDrive", ["Model", "SerialNumber", "Size", "MediaType", "InterfaceType", "PNPDeviceID", "Status"]);
        var physicalDisks = await Query(@"root\Microsoft\Windows\Storage", "MSFT_PhysicalDisk", ["FriendlyName", "SerialNumber", "Size", "MediaType", "BusType", "HealthStatus", "SpindleSpeed"]);
        var smartRows = await Query(@"root\wmi", "MSStorageDriver_FailurePredictStatus", ["InstanceName", "PredictFailure", "Reason"]);
        var disks = diskRows.Select(r =>
        {
            var model = WmiValues.String(r, "Model") ?? "Unknown";
            var serial = WmiValues.String(r, "SerialNumber");
            var physical = physicalDisks.FirstOrDefault(p => (serial is not null && string.Equals(WmiValues.String(p, "SerialNumber")?.Trim(), serial.Trim(), StringComparison.OrdinalIgnoreCase)) || model.Contains(WmiValues.String(p, "FriendlyName") ?? "\0", StringComparison.OrdinalIgnoreCase));
            var media = physical is null ? null : WmiValues.Int32(physical, "MediaType");
            var bus = physical is null ? WmiValues.String(r, "InterfaceType") ?? "Unknown" : BusName(WmiValues.Int32(physical, "BusType"));
            var pnp = WmiValues.String(r, "PNPDeviceID") ?? "";
            var smart = smartRows.FirstOrDefault(s => (WmiValues.String(s, "InstanceName") ?? "").Contains(pnp, StringComparison.OrdinalIgnoreCase));
            var kind = bus == "NVMe" ? DiskKind.Nvme : media == 4 ? DiskKind.Ssd : media == 3 ? DiskKind.Hdd : media == 5 ? DiskKind.Scm : (WmiValues.String(r, "MediaType") ?? "").Contains("SSD", StringComparison.OrdinalIgnoreCase) ? DiskKind.Ssd : DiskKind.Unknown;
            var health = physical is null ? WmiValues.String(r, "Status") ?? "Unknown" : WmiValues.Int32(physical, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
            return new DiskInfo { Model = model, SerialNumber = serial, SizeBytes = WmiValues.UInt64(r, "Size") ?? 0, Kind = kind, Bus = bus, Health = health, SmartPredictFailure = smart is null ? null : WmiValues.Bool(smart, "PredictFailure"), SmartSource = smart is null ? "MSStorageDriver_FailurePredictStatus unavailable" : "MSStorageDriver_FailurePredictStatus" };
        }).ToArray();
        // MSFT_NetAdapter: el namespace YA era root\StandardCimv2 (correcto). "Consulta no válida"
        // no es un problema de scope, así que se consulta en DOS pasos: primero el núcleo de
        // propiedades que trae cualquier build, y solo después las opcionales. Si el núcleo falla,
        // se degrada con aviso y el perfil sigue siendo válido (lista vacía + Unknown), sin romperlo.
        var adapterRows = await ReadNetAdapterRowsAsync(QueryOrFail);
        // Kind sale SOLO de NdisPhysicalMedium; si el valor no es uno de los documentados, Unknown.
        // Antes se hacía un switch por MediaType (inexistente) y, como último recurso, se adivinaba
        // Ethernet por defecto: convertir "no sé" en "Ethernet" es justo lo que R-L prohíbe.
        var adapters = adapterRows.Select(r => new NetworkAdapterInfo
        {
            Name = WmiValues.String(r, "Name") ?? "Unknown",
            Description = WmiValues.String(r, "InterfaceDescription") ?? "Unknown",
            Kind = MapPhysicalMedium(WmiValues.Int32(r, "NdisPhysicalMedium")),
            DriverVersion = WmiValues.String(r, "DriverVersionString"),
            DriverProvider = WmiValues.String(r, "DriverProvider"),
            // Speed es la velocidad negociada actual; ReceiveLinkSpeed/TransmitLinkSpeed son por
            // dirección y pueden no estar presentes en enlaces dúplex. Se prefiere Speed.
            LinkSpeedBitsPerSecond = WmiValues.UInt64(r, "Speed"),
            IsPhysical = WmiValues.Bool(r, "HardwareInterface") == true && WmiValues.Bool(r, "Virtual") != true,
            State = WmiValues.String(r, "State") ?? "Unknown"
        }).ToArray();
        if (adapters.Length == 0)
        {
            var fallback = await Query(@"root\cimv2", "Win32_NetworkAdapter", ["Name", "NetConnectionID", "AdapterTypeId", "Speed", "PhysicalAdapter", "NetConnectionStatus"]);
            adapters = fallback.Select(r => new NetworkAdapterInfo { Name = WmiValues.String(r, "NetConnectionID") ?? WmiValues.String(r, "Name") ?? "Unknown", Description = WmiValues.String(r, "Name") ?? "Unknown", Kind = WmiValues.Int32(r, "AdapterTypeId") == 9 ? NetworkKind.WiFi : NetworkKind.Ethernet, LinkSpeedBitsPerSecond = WmiValues.UInt64(r, "Speed"), IsPhysical = WmiValues.Bool(r, "PhysicalAdapter") == true, State = WmiValues.String(r, "NetConnectionStatus") ?? "Unknown" }).ToArray();
        }
        var monitors = gpuRows.Where(r => (WmiValues.Int32(r, "CurrentHorizontalResolution") ?? 0) > 0).Select(r => new MonitorInfo { Name = WmiValues.String(r, "Name") ?? "Display", Width = WmiValues.Int32(r, "CurrentHorizontalResolution"), Height = WmiValues.Int32(r, "CurrentVerticalResolution"), RefreshRateHz = WmiValues.Int32(r, "CurrentRefreshRate"), Source = "Win32_VideoController (controlador, no enumeración exhaustiva por monitor)" }).ToArray();
        var security = await ReadSecurityAsync(Query, QueryOrFail, cancellationToken);
        var power = await ReadPowerAsync(cancellationToken);
        var enclosure = await Query(@"root\cimv2", "Win32_SystemEnclosure", ["ChassisTypes"]);
        var chassis = enclosure.SelectMany(r => WmiValues.Array(r, "ChassisTypes")).Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).Distinct().ToArray();
        var batteries = await Query(@"root\cimv2", "Win32_Battery", ["Name", "EstimatedChargeRemaining"]);
        var bios = (await Query(@"root\cimv2", "Win32_BIOS", ["Manufacturer", "SMBIOSBIOSVersion"])).FirstOrDefault() ?? new Dictionary<string, object?>();
        bool? portable = chassis.Length == 0 ? null : chassis.Any(t => t is 8 or 9 or 10 or 11 or 12 or 14 or 30 or 31 or 32);
        var form = new FormFactorInfo { IsPortable = portable, HasBattery = batteries.Count > 0, ChassisTypes = chassis.Select(ChassisName).ToArray(), SystemModel = WmiValues.String(cs, "Model"), BiosVersion = WmiValues.String(bios, "SMBIOSBIOSVersion") };
        var playbooks = await DetectAtlasAsync(Query, cancellationToken);
        var devices = await Query(@"root\cimv2", "Win32_PnPEntity", ["Name", "PNPDeviceID", "ConfigManagerErrorCode", "Service", "PNPClass"], "ConfigManagerErrorCode!=0 OR PNPClass='SoftwareDevice'");
        var errors = devices.Where(d => (WmiValues.Int32(d, "ConfigManagerErrorCode") ?? 0) != 0).Select(d => new DeviceErrorInfo(WmiValues.String(d, "Name") ?? "(sin nombre)", WmiValues.String(d, "PNPDeviceID") ?? "", WmiValues.Int32(d, "ConfigManagerErrorCode") ?? -1, WmiValues.String(d, "Service"))).ToArray();
        foreach (var device in devices.Where(d => (WmiValues.String(d, "PNPClass") ?? "").Equals("SoftwareDevice", StringComparison.OrdinalIgnoreCase))) observations.Add(new DiagnosticObservation("softwareDevice", WmiValues.String(device, "Name") ?? "(sin nombre)", WmiValues.String(device, "PNPDeviceID") ?? "WMI", "High", $"ConfigManagerErrorCode={WmiValues.Int32(device, "ConfigManagerErrorCode")}"));
        var maintenance = await ReadMaintenanceAsync(cancellationToken, warnings);
        var gameInfo = await games.LocateCs2Async(cancellationToken);
        var processSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        var interactiveName = WmiValues.String(cs, "UserName");
        var interactiveSid = ResolveSid(interactiveName, processSid, warnings);
        return new HardwareProfile { MachineName = Environment.MachineName, InteractiveUserName = interactiveName, InteractiveUserSid = interactiveSid, ProcessUserSid = processSid, Os = os, Cpu = cpu, Memory = memory, Gpus = gpus, Disks = disks, NetworkAdapters = adapters, Monitors = monitors, Security = security, Power = power, FormFactor = form, Playbooks = playbooks, DeviceErrors = errors, Games = gameInfo, Maintenance = maintenance, Observations = observations, ProbeWarnings = warnings };
    }
    /// <summary>
    /// Texto del aviso cuando una consulta WMI falla.
    ///
    /// Nombra el SCOPE, la CLASE y las PROPIEDADES pedidas. Antes solo decía el mensaje de la
    /// excepción ("Consulta no válida"), que no permite saber qué se intentó: con 9 propiedades de
    /// golpe, cualquiera inexistente produce el mismo texto. Saber la lista es lo que permitió
    /// encontrar que DriverVersion, MediaType y LinkSpeed no existen en MSFT_NetAdapter.
    /// </summary>
    public static string DescribeQueryFailure(string scope, string className, IReadOnlyList<string> properties, Exception exception)
    {
        var props = properties.Count == 0 ? "*" : string.Join(",", properties);
        return $"{scope}:{className} ({props}): {exception.Message}";
    }

    /// <summary>
    /// Arquitectura del SO a partir del valor NUMÉRICO de <c>Win32_Processor.Architecture</c>.
    ///
    /// Orden (R-C: nunca texto localizado; R-L: Unknown antes que adivinar):
    /// <list type="number">
    /// <item>Valor numérico de <c>Win32_Processor.Architecture</c> (0=x86, 5=ARM, 9=x64, 12=ARM64).</item>
    /// <item>Si WMI no lo dio, <see cref="RuntimeInformation.OSArchitecture"/> del propio proceso.</item>
    /// <item>Si tampoco, <see cref="OsArchitectureKind.Unknown"/>: nunca "X64" por defecto.</item>
    /// </list>
    /// Nota: <c>Win32_OperatingSystem.OSArchitecture</c> NUNCA se usa como fuente; es texto traducido
    /// y fue la causa de que todo el catálogo saliera "No aplicable" en un equipo x64 con es-MX.
    /// </summary>
    public static OsArchitectureKind ResolveOsArchitecture(IReadOnlyDictionary<string, object?>? cpuRow, IList<string> warnings)
    {
        var numeric = cpuRow is null ? null : WmiValues.Int32(cpuRow, "Architecture");
        if (numeric is not null)
        {
            var fromWmi = ArchitectureInterpreter.FromProcessorArchitectureNumber(numeric.Value);
            if (fromWmi != OsArchitectureKind.Unknown) return fromWmi;
            warnings.Add($"Win32_Processor.Architecture={numeric.Value}: valor sin equivalente conocido; se usa la arquitectura del proceso.");
        }
        else
        {
            warnings.Add("Win32_Processor.Architecture no disponible; se usa la arquitectura del proceso.");
        }
        return ArchitectureInterpreter.FromCurrentRuntime();
    }

    /// <summary>
    /// Estado del TPM a partir de las filas devueltas por la consulta y de si esa consulta FALLÓ.
    ///
    /// Regla dura (R-L): un fallo de consulta NUNCA es "no presente".
    /// <list type="bullet">
    /// <item><paramref name="queryFailed"/> = true ⇒ <see cref="FeatureState.Unknown"/> y queda el aviso.</item>
    /// <item>Consulta correcta y 0 instancias ⇒ <see cref="FeatureState.NotPresent"/>.</item>
    /// <item>Consulta correcta con instancia y habilitado+activado ⇒ <see cref="FeatureState.Enabled"/>.</item>
    /// <item>Consulta correcta con instancia pero no habilitado/activado ⇒ <see cref="FeatureState.Disabled"/>.</item>
    /// </list>
    /// </summary>
    public static FeatureState ResolveTpmState(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, bool queryFailed)
    {
        if (queryFailed) return FeatureState.Unknown;
        var tpm = rows.FirstOrDefault();
        if (tpm is null) return FeatureState.NotPresent;
        return WmiValues.Bool(tpm, "IsEnabled_InitialValue") == true && WmiValues.Bool(tpm, "IsActivated_InitialValue") == true
            ? FeatureState.Enabled
            : FeatureState.Disabled;
    }

    /// <summary>
    /// Propiedades que se piden a <c>MSFT_NetAdapter</c>.
    ///
    /// VERIFICADO en la PC objetivo (root\StandardCimv2:MSFT_NetAdapter, 2 instancias): estas 8
    /// existen y responden. Las tres que se pedían antes NO existen y hacían fallar la consulta con
    /// <c>InvalidQuery</c> ("Consulta no válida"), que es lo que salía 3 veces en los avisos:
    /// <list type="bullet">
    /// <item><c>DriverVersion</c>   → no existe; la real es <c>DriverVersionString</c>.</item>
    /// <item><c>MediaType</c>       → no existe; el concepto es <c>NdisPhysicalMedium</c>.</item>
    /// <item><c>LinkSpeed</c>       → no existe; las reales son <c>Speed</c>, <c>ReceiveLinkSpeed</c>,
    /// <c>TransmitLinkSpeed</c> (<c>MaxSpeed</c> en versiones más nuevas).</item>
    /// </list>
    /// El test <c>NetAdapterPropertiesTests</c> comprueba que TODA propiedad listada aquí pertenece a
    /// la lista real de la clase, para que no vuelva a pedirse algo inventado.
    /// </summary>
    public static readonly string[] NetAdapterProperties =
    [
        "Name",
        "InterfaceDescription",
        "InterfaceGuid",
        "DriverProvider",
        "DriverVersionString",   // antes: DriverVersion (inexistente)
        "State",
        "NdisPhysicalMedium",    // antes: MediaType (inexistente)
        "Speed",                 // antes: LinkSpeed (inexistente)
        "HardwareInterface",
        "Virtual"
    ];

    /// <summary>
    /// Mapea <c>NdisPhysicalMedium</c> a <see cref="NetworkKind"/>.
    ///
    /// Solo los valores documentados en NDIS_PHYSICAL_MEDIUM:
    /// <c>14 = NdisPhysicalMedium802_3</c> (Ethernet), <c>9 = NdisPhysicalMediumNative802_11</c> (Wi-Fi).
    /// Cualquier otro valor (0 = Unspecified, 15 = 802.3, etc.) devuelve
    /// <see cref="NetworkKind.Unknown"/>: R-L, no se adivina el tipo de medio.
    ///
    /// NO VERIFICADO en hardware real: el valor que devuelve esta clase en la I225-V y la AX210 no se
    /// midió. El mapeo anterior (1 = Ethernet, 2 = Wi-Fi) era incorrecto además de ilegible: esos
    /// números no corresponden a NDIS_PHYSICAL_MEDIUM.
    /// </summary>
    public static NetworkKind MapPhysicalMedium(int? ndisPhysicalMedium) => ndisPhysicalMedium switch
    {
        14 => NetworkKind.Ethernet,  // NdisPhysicalMedium802_3
        9 => NetworkKind.WiFi,       // NdisPhysicalMediumNative802_11
        _ => NetworkKind.Unknown
    };

    /// <summary>
    /// Lee <c>MSFT_NetAdapter</c> con la lista verificada de propiedades.
    ///
    /// Ya no hace falta la consulta en dos pasos: las propiedades que se piden existen todas en la
    /// clase (verificado en la PC objetivo). Aun así se mantiene el comportamiento defensivo: si la
    /// consulta falla, lista vacía + aviso, sin romper el perfil.
    /// </summary>
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadNetAdapterRowsAsync(
        Func<string, string, string[], string?, Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool Failed)>> queryOrFail)
    {
        var (rows, failed) = await queryOrFail(NetAdapterScope, NetAdapterClass, NetAdapterProperties, null);
        return failed ? [] : rows;
    }

    private async Task<SecurityInfo> ReadSecurityAsync(Func<string, string, string[], string?, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> query, Func<string, string, string[], string?, Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool Failed)>> queryOrFail, CancellationToken cancellationToken)
    {
        var guard = (await query(@"root\Microsoft\Windows\DeviceGuard", "Win32_DeviceGuard", ["VirtualizationBasedSecurityStatus", "SecurityServicesConfigured", "SecurityServicesRunning"], null)).FirstOrDefault();
        var vbs = guard is null ? FeatureState.Unknown : WmiValues.Int32(guard, "VirtualizationBasedSecurityStatus") switch { 0 => FeatureState.Disabled, 2 => FeatureState.Enabled, _ => FeatureState.Unknown };
        var running = guard is null ? [] : WmiValues.Array(guard, "SecurityServicesRunning").Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray();
        var configured = guard is null ? [] : WmiValues.Array(guard, "SecurityServicesConfigured").Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToArray();
        var hvci = running.Contains(2) ? FeatureState.Enabled : configured.Contains(2) ? FeatureState.Unknown : guard is not null && running.Length > 0 ? FeatureState.Disabled : FeatureState.Unknown;
        var secure = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", cancellationToken);
        var secureState = secure.ValueExists ? Convert.ToInt32(secure.Data, CultureInfo.InvariantCulture) == 1 ? FeatureState.Enabled : FeatureState.Disabled : FeatureState.Unknown;
        if (secureState == FeatureState.Unknown)
        {
            try
            {
                var result = await runner.RunAsync(new ProcessRequest { FileName = ProcessRunner.SystemExecutable(@"WindowsPowerShell\v1.0\powershell.exe"), Arguments = ["-NoProfile", "-NonInteractive", "-Command", "try { if (Confirm-SecureBootUEFI) { 'Enabled' } else { 'Disabled' } } catch { 'Unknown' }"], Timeout = TimeSpan.FromSeconds(20) }, cancellationToken);
                secureState = result.StandardOutput.Trim() switch { "Enabled" => FeatureState.Enabled, "Disabled" => FeatureState.Disabled, _ => FeatureState.Unknown };
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { secureState = FeatureState.Unknown; }
        }
        // TPM: clase Win32_Tpm del namespace Security\MicrosoftTpm (el de root\Microsoft\Windows\TPM NO
        // existe y hacía fallar la consulta). Además se distingue FALLO de LISTA VACÍA: un fallo es
        // Unknown, nunca NotPresent (R-L). Antes, la excepción se tragaba y el valor salía NotPresent.
        var (tpmRows, tpmQueryFailed) = await queryOrFail(TpmScope, TpmClass, ["IsEnabled_InitialValue", "IsActivated_InitialValue", "SpecVersion"], null);
        var tpm = tpmRows.FirstOrDefault();
        var tpmState = ResolveTpmState(tpmRows, tpmQueryFailed);
        var hags = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", cancellationToken);
        var mpo = await registry.ReadValueAsync(@"HKLM\SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", cancellationToken);
        return new SecurityInfo { Vbs = vbs, Hvci = hvci, SecureBoot = secureState, Tpm = tpmState, TpmVersion = tpm is null ? null : WmiValues.String(tpm, "SpecVersion"), Hags = hags.ValueExists ? Convert.ToInt32(hags.Data) == 2 ? FeatureState.Enabled : Convert.ToInt32(hags.Data) == 1 ? FeatureState.Disabled : FeatureState.Unknown : FeatureState.Unknown, Mpo = mpo.ValueExists && Convert.ToInt32(mpo.Data) == 5 ? FeatureState.Disabled : FeatureState.Unknown, Evidence = "WMI DeviceGuard/TPM, registro SecureBoot/HAGS/MPO; Unknown no se interpreta como Disabled." };
    }
    private async Task<PowerInfo> ReadPowerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(new ProcessRequest { FileName = ProcessRunner.SystemExecutable("powercfg.exe"), Arguments = ["/getactivescheme"], Timeout = TimeSpan.FromSeconds(20) }, cancellationToken);
            if (!result.IsSuccess) return new PowerInfo();
            var match = Regex.Match(result.StandardOutput, @"([0-9a-fA-F-]{36})\s+\(([^)]*)\)");
            return new PowerInfo { ActiveSchemeGuid = match.Success ? match.Groups[1].Value.ToLowerInvariant() : null, ActiveSchemeName = match.Success ? match.Groups[2].Value : null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new PowerInfo { PowerLimitSource = "powercfg failed: " + ex.Message }; }
    }
    private async Task<IReadOnlyList<PlaybookDetection>> DetectAtlasAsync(Func<string, string, string[], string?, Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>>> query, CancellationToken cancellationToken)
    {
        var evidence = new List<string>();
        foreach (var name in new[] { "RegisteredOrganization", "Organization" })
        {
            var value = await registry.ReadValueAsync(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", name, cancellationToken);
            if (value.ValueExists && (value.Data as string ?? "").Contains("Atlas", StringComparison.OrdinalIgnoreCase)) evidence.Add($"{name}={value.Data}");
        }
        if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Atlas"))) evidence.Add("ProgramDataAtlas folder");
        var services = await query(@"root\cimv2", "Win32_Service", ["Name", "State", "StartMode"], "Name='SysMain' OR Name='WSearch' OR Name='Spooler' OR Name='DiagTrack'");
        var stopped = services.Where(s => WmiValues.String(s, "State") != "Running").Select(s => WmiValues.String(s, "Name") ?? "").ToArray();
        if (stopped.Length > 0) evidence.Add("Servicios no corriendo (no concluyente): " + string.Join(",", stopped));
        return evidence.Count == 0 ? [] : [new PlaybookDetection("Atlas Playbook (heurística)", evidence.Any(e => e.StartsWith("RegisteredOrganization") || e.StartsWith("Organization")) ? "Medium" : "Low", evidence)];
    }
    private async Task<SystemMaintenanceInfo> ReadMaintenanceAsync(CancellationToken cancellationToken, List<string> warnings)
    {
        var reasons = new List<string>();
        if (await registry.KeyExistsAsync(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", cancellationToken)) reasons.Add("CBS RebootPending");
        if (await registry.KeyExistsAsync(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", cancellationToken)) reasons.Add("WindowsUpdate RebootRequired");
        var pending = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations", cancellationToken);
        if (pending.ValueExists && pending.Data is string[] { Length: > 0 }) reasons.Add("PendingFileRenameOperations");
        var activeName = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName", "ComputerName", cancellationToken);
        var pendingName = await registry.ReadValueAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName", "ComputerName", cancellationToken);
        if (activeName.ValueExists && pendingName.ValueExists && !string.Equals(activeName.Data as string, pendingName.Data as string, StringComparison.OrdinalIgnoreCase)) reasons.Add("ComputerName change pending");
        var servicing = new List<string>();
        foreach (var name in new[] { "TiWorker", "TrustedInstaller" }) { try { if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0) servicing.Add(name); } catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add($"Process {name}: {ex.Message}"); } }
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        ulong free = 0; try { free = (ulong)new DriveInfo(systemRoot).AvailableFreeSpace; } catch (Exception ex) { warnings.Add("Disk free space: " + ex.Message); }
        var admin = false; try { using var identity = WindowsIdentity.GetCurrent(); admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } catch (Exception ex) { warnings.Add("Administrator check: " + ex.Message); }
        FeatureState protection; try { protection = await restore.GetProtectionStateAsync(cancellationToken); } catch (Exception ex) when (ex is not OperationCanceledException) { protection = FeatureState.Unknown; warnings.Add("System protection: " + ex.Message); }
        return new SystemMaintenanceInfo { PendingReboot = reasons.Count > 0, PendingRebootReasons = reasons, WindowsServicingActive = servicing.Count > 0, ServicingProcesses = servicing, SystemProtection = protection, IsAdministrator = admin, SystemDriveFreeBytes = free };
    }
    private static string? ResolveSid(string? userName, string processSid, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            if (string.Equals(current.Name, userName, StringComparison.OrdinalIgnoreCase)) return processSid;
            return new NTAccount(userName).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("Interactive user SID: " + ex.Message); return null; }
    }

    /// <summary>
    /// Capa 6.1: frase de canales de RAM calculada desde los módulos, SIN afirmar el entrelazado.
    ///
    /// <c>Win32_PhysicalMemory</c> describe módulos instalados; el entrelazado lo decide el controlador de
    /// memoria y WMI no lo expone. Por eso, como máximo, se puede decir "probable" y marcar que no se puede
    /// confirmar. Con un solo módulo o con capacidades distintas no se sugiere dual-channel en absoluto.
    /// </summary>
    public static string DescribeChannels(IReadOnlyList<MemoryModuleInfo> modules)
    {
        if (modules.Count == 0)
            return "Win32_PhysicalMemory no devolvió módulos; no se puede decir nada sobre los canales.";

        const string BaseText = "Win32_PhysicalMemory describe módulos, no el entrelazado del controlador.";

        if (modules.Count == 1)
            return $"{BaseText} 1 módulo: single-channel. No se puede confirmar por WMI.";

        var capacities = modules.Select(m => m.CapacityBytes).Distinct().ToArray();
        var speeds = modules.Select(m => (int?)(m.ConfiguredSpeedMtPerSecond ?? m.SpeedMtPerSecond)).Distinct().ToArray();

        if (capacities.Length != 1 || speeds.Length != 1)
            return $"{BaseText} {modules.Count} módulos con capacidad o velocidad distinta: no se puede afirmar dual-channel; verificar con una herramienta de topología.";

        return $"{BaseText} {modules.Count} módulos iguales, dual-channel probable, no confirmable por WMI.";
    }

    private static string BusName(int? bus) => bus switch { 3 => "ATA", 7 => "USB", 10 => "SAS", 11 => "SATA", 17 => "NVMe", 18 => "SCM", _ => "Unknown" };
    private static string ChassisName(int type) => type switch { 3 => "Desktop", 4 => "LowProfileDesktop", 8 => "Portable", 9 => "Laptop", 10 => "Notebook", 30 => "Tablet", 31 => "Convertible", 32 => "Detachable", 35 => "MiniPC", _ => $"Type{type}" };
}

public sealed class UnsupportedHardwareProfiler : IHardwareProfiler
{
    public Task<HardwareProfile> ProfileAsync(CancellationToken cancellationToken = default, IProgress<EngineProgress>? progress = null) => Task.FromResult(new HardwareProfile
    {
        IsUnsupportedPlatform = true,
        Os = new OsInfo { Name = Environment.OSVersion.ToString(), Architecture = RuntimeInformation.OSArchitecture.ToString(), Culture = CultureInfo.CurrentCulture.Name, TimeZone = TimeZoneInfo.Local.Id },
        Cpu = new CpuInfo { Model = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount, IsHybrid = null, TopologySource = "Unsupported platform" },
        ProbeWarnings = ["Esta plataforma no es Windows. El perfil es parcial y las acciones reales están bloqueadas."]
    });
}
