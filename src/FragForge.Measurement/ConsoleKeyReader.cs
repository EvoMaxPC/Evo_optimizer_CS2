namespace FragForge.Cli;

/// <summary>Lectura no bloqueante de teclas sin requerir una consola real en los tests.</summary>
public interface IConsoleKeyReader
{
    bool IsAvailable { get; }
    ConsoleKey? TryReadKey();
}

/// <summary>Lee teclas de consola solo cuando la entrada no está redirigida.</summary>
public sealed class ConsoleKeyReader : IConsoleKeyReader
{
    private readonly Func<bool> _isRedirected;
    private readonly Func<bool> _keyAvailable;
    private readonly Func<ConsoleKey> _readKey;
    private bool _failed;

    public ConsoleKeyReader() : this(() => Console.IsInputRedirected, () => Console.KeyAvailable,
        () => Console.ReadKey(intercept: true).Key) { }

    public ConsoleKeyReader(Func<bool> isRedirected, Func<bool> keyAvailable, Func<ConsoleKey> readKey)
    {
        _isRedirected = isRedirected;
        _keyAvailable = keyAvailable;
        _readKey = readKey;
    }

    public bool IsAvailable
    {
        get
        {
            try { return !_failed && !_isRedirected(); }
            catch (Exception) { _failed = true; return false; }
        }
    }

    public ConsoleKey? TryReadKey()
    {
        try
        {
            if (_failed || _isRedirected() || !_keyAvailable()) return null;
            return _readKey();
        }
        catch (Exception) { _failed = true; return null; }
    }
}
