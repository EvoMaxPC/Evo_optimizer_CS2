using FragForge.Core.Models;

namespace FragForge.Core.Abstractions;

public sealed record ReportDocument(int SchemaVersion, DateTimeOffset CreatedAt, string ApplicationVersion, HardwareProfile Hardware, IReadOnlyList<TweakStatus> Tweaks, PreflightReport Preflight, IReadOnlyList<SnapshotListItem> Snapshots, JournalIntegrity JournalIntegrity, IReadOnlyList<JournalEntry> RecentJournal);
public interface IReportService
{
    Task<ReportDocument> CreateAsync(CancellationToken cancellationToken = default);
    Task ExportAsync(string path, string format, CancellationToken cancellationToken = default);
}
public interface IApplicationContext
{
    string Culture { get; set; }
    string DataRoot { get; }
    string ApplicationVersion { get; }
}
