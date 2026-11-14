using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FragForge.Core.Experiments;

namespace FragForge.Actions.Measurement;

public sealed record MouseRateResult
{
    public required int SampleCount { get; init; }
    public double? MedianHz { get; init; }
    public double? P5Hz { get; init; }
    public double? JitterMs { get; init; }
    public IReadOnlyList<int> HistogramBins { get; init; } = [];
    public IReadOnlyList<string> BinLabels { get; init; } = [];
    public required string Method { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public bool IsValid => SampleCount >= 100 && MedianHz is not null;
}

/// <summary>
/// Mide la frecuencia real del ratón con una ventana oculta de solo mensajes.
///
/// GARANTÍA DE PRIVACIDAD: se registran ÚNICAMENTE timestamps de <c>QueryPerformanceCounter</c> y conteos.
/// Las coordenadas del cursor nunca se leen, ni se guardan, ni se registran en el log: solo se usa
/// <c>RAWMOUSE.ulExtraInformation</c>/contadores de mensajes WM_INPUT para saber que llegó un movimiento.
///
/// Es completamente pasivo: no inyecta entrada, no bloquea el ratón, no se engancha a ningún proceso.
/// usage page 1 (Generic Desktop), usage 2 (Mouse), RIDEV_INPUTSINK para recibir entrada cuando la ventana
/// no tiene foco.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MouseRateExperiment
{
    public async Task<MouseRateResult> RunAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var timestamps = new List<long>();
        var frequency = Stopwatch.Frequency;
        using var window = new MessageOnlyWindow(timestamps);
        var rawInput = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01,
            usUsage = 0x02,
            dwFlags = RIDEV_INPUTSINK,
            hwndTarget = window.Handle
        };
        if (!RegisterRawInputDevices([rawInput], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            return new MouseRateResult { SampleCount = 0, Method = "RegisterRawInputDevices", Notes = ["No se pudo registrar el dispositivo de entrada en crudo (error " + Marshal.GetLastWin32Error() + ")."] };

        var started = Stopwatch.GetTimestamp();
        var deadline = TimeSpan.FromSeconds(duration.TotalSeconds);
        while (Stopwatch.GetElapsedTime(started) < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            window.PumpOnce();
            await Task.Delay(1, cancellationToken);
        }

        var intervals = new List<double>();
        for (var i = 1; i < timestamps.Count; i++)
        {
            var delta = (timestamps[i] - timestamps[i - 1]) / (double)frequency * 1000.0;
            if (delta is > 0 and < 100) intervals.Add(delta);
        }

        var notes = new List<string>();
        if (intervals.Count < 100) notes.Add($"Solo {intervals.Count} intervalos válidos: mueve el ratón durante todo el test.");
        var rates = intervals.Select(ms => 1000.0 / ms).ToArray();
        var medianRate = rates.Length == 0 ? (double?)null : Statistics.Median(rates);
        var p5Rate = rates.Length == 0 ? (double?)null : Statistics.Percentile(rates, 5);
        var jitter = intervals.Count == 0 ? (double?)null : Statistics.Percentile(intervals, 99) - Statistics.Percentile(intervals, 50);

        var bins = new[] { 0.5, 0.8, 1.2, 2.0, 4.0, 8.0 };
        var histogram = new int[bins.Length + 1];
        foreach (var interval in intervals)
        {
            var index = Array.FindIndex(bins, b => interval < b);
            histogram[index < 0 ? bins.Length : index]++;
        }
        var labels = bins.Select(b => $"<{b:0.0}ms").Append($">={bins[^1]:0.0}ms").ToArray();

        return new MouseRateResult
        {
            SampleCount = intervals.Count,
            MedianHz = medianRate,
            P5Hz = p5Rate,
            JitterMs = jitter,
            HistogramBins = histogram,
            BinLabels = labels,
            Method = "Raw Input (usage page 1, usage 2, RIDEV_INPUTSINK) + QueryPerformanceCounter",
            Notes = notes
        };
    }

    private const int RIDEV_INPUTSINK = 0x00000100;
    private const int WM_INPUT = 0x00FF;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public int dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint deviceCount, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetRawInputData(IntPtr rawInputHandle, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool PeekMessageW(out MSG message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ushort RegisterClassW(ref WNDCLASS windowClass);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private sealed class MessageOnlyWindow : IDisposable
    {
        private readonly List<long> _timestamps;
        private readonly WndProcDelegate _delegate;
        // Un nombre de clase por instancia: registrar la misma clase dos veces falla con ERROR_CLASS_ALREADY_EXISTS
        // y dejaría el handle en cero sin que se note.
        private string _className = "FragForgeRawInputProbe-" + Guid.NewGuid().ToString("N");
        public IntPtr Handle { get; }

        public MessageOnlyWindow(List<long> timestamps)
        {
            _timestamps = timestamps;
            _delegate = WindowProc;
            var instance = GetModuleHandleW(null);
            var windowClass = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_delegate),
                hInstance = instance,
                lpszClassName = _className
            };
            if (RegisterClassW(ref windowClass) == 0) _className = string.Empty;
            // HWND_MESSAGE = -3: ventana de solo mensajes, nunca visible, nunca en Alt+Tab.
            Handle = CreateWindowExW(0, _className, "FragForge", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, instance, IntPtr.Zero);
        }

        private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == WM_INPUT)
            {
                uint size = 0;
                GetRawInputData(lParam, 0x10000003 /* RID_INPUT */, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
                if (size > 0)
                {
                    // Solo el tamaño del paquete: NUNCA se deserializa lLastX/lLastY ni coordenadas.
                    _timestamps.Add(Stopwatch.GetTimestamp());
                }
                return IntPtr.Zero;
            }
            if (message == 0x0012 /* WM_QUIT */) { PostQuitMessage(0); return IntPtr.Zero; }
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        public void PumpOnce()
        {
            while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                if (message.message == 0x0012) break;
            }
        }

        public void Dispose() { if (Handle != IntPtr.Zero) DestroyWindow(Handle); }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    }
}

public sealed class UnsupportedMouseRateExperiment
{
    public Task<MouseRateResult> RunAsync(TimeSpan duration, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MouseRateResult { SampleCount = 0, Method = "Unsupported", Notes = ["Raw Input solo está disponible en Windows."] });
}
