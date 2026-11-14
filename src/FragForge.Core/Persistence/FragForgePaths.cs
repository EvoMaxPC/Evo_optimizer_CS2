namespace FragForge.Core.Persistence;

public sealed class FragForgePaths
{
    public string Root { get; }
    public string Snapshots => Path.Combine(Root, "snapshots");
    public string Journal => Path.Combine(Root, "journal", "journal.ndjson");
    public string Logs => Path.Combine(Root, "logs");
    public string Reports => Path.Combine(Root, "reports");
    public string AdditionalPolicy => Path.Combine(Root, "policy", "denylist.json");
    public string LockFile => Path.Combine(Root, ".fragforge.lock");
    public FragForgePaths(string? root = null)
    {
        Root = string.IsNullOrWhiteSpace(root) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FragForge") : Path.GetFullPath(root);
    }
    public void EnsureCreated()
    {
        foreach (var path in new[] { Root, Snapshots, Path.GetDirectoryName(Journal)!, Logs, Reports, Path.GetDirectoryName(AdditionalPolicy)! }) Directory.CreateDirectory(path);
    }
}
