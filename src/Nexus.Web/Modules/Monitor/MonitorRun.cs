using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Nexus.Web.Modules.Monitor;

internal sealed class MonitorRun
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public Guid? Generation { get; set; }
    public string Status { get; set; } = "queued";
    public string Configuration { get; set; } = "";
    public string? Latest { get; set; }
    public bool StopRequested { get; set; }
    public bool? Cleaned { get; set; }
    public bool Active { get; set; } = true;
    public uint Revision { get; set; }
}

internal sealed class MonitorRunMapping : IEntityTypeConfiguration<MonitorRun>
{
    public void Configure(EntityTypeBuilder<MonitorRun> b)
    {
        b.ToTable("monitor_runs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Configuration).HasColumnType("jsonb");
        b.Property(x => x.Latest).HasColumnType("jsonb");
        b.Property(x => x.Status).HasMaxLength(24);
        b.Property(x => x.Revision).IsRowVersion();
        b.HasIndex(x => x.Active).IsUnique().HasFilter("\"Active\" = true").HasDatabaseName("ux_monitor_one_active");
        b.HasIndex(x => new { x.CreatedAt, x.Id });
    }
}

internal sealed class MonitorAudit
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public Guid? ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Action { get; set; } = "";
}

internal sealed class MonitorAuditMapping : IEntityTypeConfiguration<MonitorAudit>
{
    public void Configure(EntityTypeBuilder<MonitorAudit> b)
    {
        b.ToTable("monitor_audit_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(32);
        b.HasIndex(x => new { x.RunId, x.OccurredAt });
    }
}
