using FragForge.Core.Abstractions;

namespace FragForge.Composition;

public sealed class ApplicationContext(string dataRoot, string culture) : IApplicationContext
{
    public string Culture { get; set; } = culture;
    public string DataRoot { get; } = dataRoot;
    public string ApplicationVersion { get; } = typeof(ApplicationContext).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
