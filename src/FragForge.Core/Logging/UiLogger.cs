using FragForge.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace FragForge.Core.Logging;

public sealed class UiLoggerProvider(IUiLogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new UiLogger(categoryName, sink);
    public void Dispose() { }
    private sealed class UiLogger(string category, IUiLogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = $"{category}: {formatter(state, exception)}";
            if (exception is not null) message += $" | {exception.GetType().Name}: {exception.Message}";
            sink.Write(logLevel.ToString(), message);
        }
    }
}
