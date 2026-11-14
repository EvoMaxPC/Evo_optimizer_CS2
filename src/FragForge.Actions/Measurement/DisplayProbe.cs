using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FragForge.Core.Abstractions;
using FragForge.Core.Models;

namespace FragForge.Actions.Measurement;

public sealed record DisplayModeInfo(string DeviceName, int Width, int Height, int RefreshHz, int BitsPerPixel, bool IsPrimary)
{
    public string Key => $"{DeviceName} {Width}x{Height}@{RefreshHz}Hz";
}

/// <summary>
/// Enumera los modos reales de cada pantalla activa. Corrige la limitación conocida de
/// <c>Win32_VideoController.CurrentRefreshRate</c>, que devuelve el modo por defecto del controlador y no
/// necesariamente el activo (frecuentemente reporta 60 Hz en un monitor de 200 Hz).
///
/// P/Invoke con <see cref="DefaultDllImportSearchPathsAttribute"/> restringido a System32: nunca se resuelve
/// user32.dll desde el directorio actual ni desde una ruta del PATH.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DisplayModeProbe
{
    public IReadOnlyList<DisplayModeInfo> ReadActiveModes()
    {
        var modes = new List<DisplayModeInfo>();
        for (var index = 0; index < 16; index++)
        {
            var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, index, ref device, 0)) break;
            if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            if ((device.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;

            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettingsW(device.DeviceName, ENUM_CURRENT_SETTINGS, ref mode)) continue;
            modes.Add(new DisplayModeInfo(device.DeviceName, mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency, mode.dmBitsPerPel, (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0));
        }
        return modes;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
    private const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, int iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool EnumDisplaySettingsW(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);
}

/// <summary>Sonda <c>monitorRefresh</c>: refresco y resolución REALES de cada pantalla activa.</summary>
[SupportedOSPlatform("windows")]
public sealed class MonitorRefreshProbe(DisplayModeProbe? display = null) : IDiagnosticProbe
{
    private readonly DisplayModeProbe _display = display ?? new DisplayModeProbe();
    public string Key => "monitorRefresh";

    public Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            var modes = _display.ReadActiveModes();
            if (modes.Count == 0) return Task.FromResult(new DiagnosticObservation(Key, "Unknown", "EnumDisplaySettings", "Unknown", "No se enumeraron pantallas activas."));
            var primary = modes.FirstOrDefault(m => m.IsPrimary) ?? modes[0];
            var value = string.Join("; ", modes.Select(m => m.Key + (m.IsPrimary ? " (primary)" : "")));
            var note = $"Refresco real por EnumDisplaySettings (no Win32_VideoController). Primaria: {primary.Width}x{primary.Height}@{primary.RefreshHz}Hz.";
            return Task.FromResult(new DiagnosticObservation(Key, value, "EnumDisplaySettings(ENUM_CURRENT_SETTINGS)", "High", note));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            return Task.FromResult(new DiagnosticObservation(Key, "Unknown", "EnumDisplaySettings", "Unknown", ex.Message));
        }
    }
}

/// <summary>Sonda <c>cpuPowerCounters</c>: qué contadores PDH existen realmente en este equipo.</summary>
public sealed class CpuPowerCountersProbe(ISensorSampler sampler) : IDiagnosticProbe
{
    public string Key => "cpuPowerCounters";
    public async Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        var availability = await sampler.CheckAsync(cancellationToken);
        var value = availability.AvailableCounters.Count == 0 ? "None" : string.Join(",", availability.AvailableCounters);
        var note = availability.MissingCounters.Count == 0 ? "Todos los contadores candidatos existen." : "Ausentes: " + string.Join(",", availability.MissingCounters) + ". Ausente ≠ cero.";
        return new DiagnosticObservation(Key, value, availability.Platform, availability.AnyAvailable ? "Medium" : "Unknown", note);
    }
}

/// <summary>Sonda <c>presentMonTool</c>: herramienta presente y hash contra la lista blanca.</summary>
public sealed class PresentMonToolProbe(IFrameCapture capture) : IDiagnosticProbe
{
    public string Key => "presentMonTool";
    public async Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default)
    {
        var availability = await capture.CheckAvailabilityAsync(cancellationToken);
        var confidence = availability.IsAvailable ? "High" : availability.IsPlaceholderHash ? "Medium" : "High";
        return new DiagnosticObservation(Key, availability.IsAvailable ? "Approved" : availability.IsPlaceholderHash ? "HashPlaceholder" : "Unavailable", capture.Name, confidence, availability.Reason);
    }
}

/// <summary>
/// Sonda <c>monitorRefresh</c> para plataformas sin Win32. Existe para que la clave siga estando presente
/// en el diagnóstico (y el usuario sepa POR QUÉ no hay dato) en lugar de desaparecer en silencio.
/// </summary>
public sealed class UnsupportedMonitorRefreshProbe : IDiagnosticProbe
{
    public string Key => "monitorRefresh";
    public Task<DiagnosticObservation> ReadAsync(HardwareProfile profile, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DiagnosticObservation(Key, "Unknown", "Unsupported platform", "Unknown", "EnumDisplaySettings solo existe en Windows; no se inventa un refresco."));
}
