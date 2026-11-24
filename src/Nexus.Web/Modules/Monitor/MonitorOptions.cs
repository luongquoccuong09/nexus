namespace Nexus.Web.Modules.Monitor;

internal sealed class MonitorOptions
{
    public const string SectionName = "NexusMonitor";
    public bool Enabled { get; set; }
    public string EnvironmentName { get; set; } = "Production";
    public Guid[] Readers { get; set; } = [];
    public Guid[] Operators { get; set; } = [];
    public string? RunnerKey { get; set; }
    public int LeaseSeconds { get; set; } = 30;
}
