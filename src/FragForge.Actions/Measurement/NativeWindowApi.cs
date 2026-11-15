using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FragForge.Actions.Measurement;

/// <summary>Enumera ventanas y monitores sin consultar memoria del proceso. DPI sin verificar en vivo.</summary>
[SupportedOSPlatform("windows")]
public sealed class NativeWindowApi : INativeWindowApi
{
    private const uint MonitorDefaultToNearest = 2;
    private delegate bool EnumWindowCallback(nint hwnd, nint parameter);

    public IReadOnlyList<NativeWindow> Enumerate()
    {
        nint previous = 0;
        try
        {
            try { previous = SetThreadDpiAwarenessContext(new nint(-4)); }
            catch (EntryPointNotFoundException) { }
            var dpiApplied = previous != 0;
            var windows = new List<NativeWindow>();
            Exception? error = null;
            EnumWindowCallback callback = (hwnd, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (!GetWindowRect(hwnd, out var window) || !GetClientRect(hwnd, out var client))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                    var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                    if (monitor == 0 || !GetMonitorInfoW(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    windows.Add(new NativeWindow(hwnd, pid, IsWindowVisible(hwnd), IsIconic(hwnd), window.ToRectangle(),
                        client.Right - client.Left, client.Bottom - client.Top, monitor, info.Monitor.ToRectangle(), dpiApplied));
                    return true;
                }
                catch (Exception ex) { error = ex; return false; }
            };
            var success = EnumWindows(callback, 0);
            GC.KeepAlive(callback);
            if (error is not null) throw new InvalidOperationException("No se pudieron leer las ventanas.", error);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            return windows;
        }
        finally
        {
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly WindowRectangle ToRectangle() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}
