using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FragForge.Actions.Measurement;

public enum HotkeyAction { ShowNow, Finish, ToggleSegment }
public sealed record HotkeyRegistration(HotkeyAction Action, string Description);

/// <summary>Fuente de eventos: el evento simple evita crear un stream asíncrono para tres acciones discretas.</summary>
public interface IHotkeySource : IDisposable
{
    event Action<HotkeyAction>? Pressed;
    bool TryStart(out string? error);
    IReadOnlyList<HotkeyRegistration> Registrations { get; }
}

/// <summary>
/// Hotkeys globales durante una medición. Una tecla registrada no llega al juego mientras esté registrada;
/// llamar TryStart al comenzar y Dispose al terminar. No verificado en vivo.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GlobalHotkeyListener : IHotkeySource
{
    private const uint ModNoRepeat = 0x4000;
    private const uint HotkeyMessage = 0x0312;
    private const uint QuitMessage = 0x0012;
    private static readonly TimeSpan ThreadWait = TimeSpan.FromSeconds(5); // límite de espera, no validado
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _started = new(false);
    private readonly List<HotkeyRegistration> _registrations = [];
    private Thread? _thread;
    private uint _threadId;
    private bool _startCalled;
    private bool _disposed;
    private string? _error;

    public event Action<HotkeyAction>? Pressed;
    public IReadOnlyList<HotkeyRegistration> Registrations { get { lock (_gate) return _registrations.ToArray(); } }

    public bool TryStart(out string? error)
    {
        lock (_gate)
        {
            if (_startCalled || _disposed) { error = "ya iniciado o cerrado"; return false; }
            _startCalled = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "FragForge hotkeys" };
            _thread.Start();
        }
        if (!_started.Wait(ThreadWait)) { error = "no se pudo iniciar el hilo de hotkeys"; return false; }
        lock (_gate) { error = _error; return error is null; }
    }

    private void Run()
    {
        var registeredIds = new List<int>();
        try
        {
            var id = GetCurrentThreadId();
            // Crea la cola antes de publicar el ID, para que PostThreadMessage pueda despertarla.
            PeekMessageW(out _, 0, 0, 0, 0);
            lock (_gate) _threadId = id;
            var failed = new List<string>();
            foreach (var entry in HotkeyMapping.Defaults)
            {
                if (RegisterHotKey(0, entry.Id, ModNoRepeat, entry.Key))
                {
                    registeredIds.Add(entry.Id);
                    lock (_gate) _registrations.Add(entry.Registration);
                }
                else failed.Add(entry.Registration.Description);
            }
            lock (_gate) if (failed.Count > 0) _error = HotkeyMapping.PartialError(failed, _registrations);
            _started.Set();
            lock (_gate) if (_disposed) return;
            while (GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.MessageId == HotkeyMessage && HotkeyMapping.MapId(unchecked((int)message.WParam)) is HotkeyAction action)
                {
                    try { Pressed?.Invoke(action); }
                    catch (Exception) { /* un observador no debe impedir la limpieza de hotkeys */ }
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate) _error = "no se pudo iniciar hotkeys: " + ex.Message;
            _started.Set();
        }
        finally
        {
            foreach (var id in registeredIds) UnregisterHotKey(0, id);
            lock (_gate) _registrations.Clear();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
        }
        if (thread is null) return;
        _started.Wait(ThreadWait);
        uint id;
        lock (_gate) id = _threadId;
        if (id != 0) PostThreadMessageW(id, QuitMessage, 0, 0);
        if (thread != Thread.CurrentThread) thread.Join(ThreadWait);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Hwnd;
        public uint MessageId;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetMessageW(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Message message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint threadId, uint message, nuint wparam, nint lparam);
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetCurrentThreadId();
}

internal static class HotkeyMapping
{
    internal static readonly (int Id, uint Key, HotkeyRegistration Registration)[] Defaults =
    [
        (1, 0x78, new(HotkeyAction.ToggleSegment, "F9")),
        (2, 0x79, new(HotkeyAction.ShowNow, "F10")),
        (3, 0x7A, new(HotkeyAction.Finish, "F11")),
    ];
    internal static IReadOnlyList<HotkeyRegistration> DefaultRegistrations => Defaults.Select(x => x.Registration).ToArray();
    internal static HotkeyAction? MapId(int id) => id switch { 1 => HotkeyAction.ToggleSegment, 2 => HotkeyAction.ShowNow, 3 => HotkeyAction.Finish, _ => null };
    internal static string PartialError(IReadOnlyList<string> failed, IReadOnlyList<HotkeyRegistration> registered) =>
        string.Join("; ", failed.Select(key => $"no se pudo registrar {key} (otra aplicación la usa)")) +
        "; teclas registradas: " + (registered.Count == 0 ? "ninguna" : string.Join(", ", registered.Select(x => x.Description)));

}
