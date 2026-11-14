using FragForge.Core.Abstractions;

namespace FragForge.Core.Localization;

public sealed class UiLogSink : IUiLogSink
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _history = new();
    private readonly List<Action<string>> _subscribers = [];
    private readonly object _gate = new();
    public void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{level}] {message}";
        _history.Enqueue(line);
        while (_history.Count > 500 && _history.TryDequeue(out _)) { }
        lock (_gate) { foreach (var subscriber in _subscribers.ToArray()) subscriber(line); }
        MessageWritten?.Invoke(this, line);
    }
    public event EventHandler<string>? MessageWritten;
    public IReadOnlyList<string> History => _history.ToArray();
    public IObservable<string> Messages => new LogObservable(this);
    private sealed class LogObservable(UiLogSink owner) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            foreach (var line in owner._history.ToArray()) observer.OnNext(line);
            void Handler(string line) => observer.OnNext(line);
            lock (owner._gate) owner._subscribers.Add(Handler);
            return new Subscription(() => { lock (owner._gate) owner._subscribers.Remove(Handler); });
        }
    }
    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
