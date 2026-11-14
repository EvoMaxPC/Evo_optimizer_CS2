using FragForge.Core.Abstractions;

namespace FragForge.Tests.Fakes;

public sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];
    public Func<ProcessRequest, ProcessResult>? Handler { get; set; }
    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Handler?.Invoke(request) ?? new ProcessResult(0, "", "", false));
    }
    public int CountContaining(string argument) => Requests.Count(r => r.Arguments.Any(a => a.Contains(argument, StringComparison.OrdinalIgnoreCase)));
}
