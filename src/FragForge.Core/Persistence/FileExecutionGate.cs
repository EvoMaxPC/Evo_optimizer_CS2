using FragForge.Core.Abstractions;

namespace FragForge.Core.Persistence;

public sealed class FileExecutionGate(FragForgePaths paths) : IExecutionGate
{
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
                return new Lease(stream);
            }
            catch (IOException) { await Task.Delay(100, cancellationToken); }
            catch (UnauthorizedAccessException) { await Task.Delay(100, cancellationToken); }
        }
    }
    private sealed class Lease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
public sealed class NullExecutionGate : IExecutionGate
{
    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) => Task.FromResult<IAsyncDisposable>(new Lease());
    private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
