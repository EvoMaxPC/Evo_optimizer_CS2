namespace FragForge.Core.Abstractions;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}
public sealed record ProcessRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public string? StandardInput { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut;
}
