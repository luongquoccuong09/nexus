using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Calls;

public enum DirectCallKind
{
    Voice,
    Video
}

public enum DirectCallState
{
    Ringing,
    Accepted,
    Declined,
    Canceled,
    Missed,
    Ended
}

public sealed class DirectCallEntity
{
    public Guid Id { get; set; }
    public Guid ClientCallId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid CallerId { get; set; }
    public Guid CalleeId { get; set; }
    public string? CallerSessionId { get; set; }
    public string? CalleeSessionId { get; set; }
    public DirectCallKind Kind { get; set; }
    public DirectCallState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int Version { get; set; } = 1;
}

internal sealed class DirectCallConfiguration : IEntityTypeConfiguration<DirectCallEntity>
{
    public void Configure(EntityTypeBuilder<DirectCallEntity> builder)
    {
        builder.ToTable("direct_calls", table =>
        {
            table.HasCheckConstraint("ck_direct_calls_distinct_participants", "caller_id <> callee_id");
            table.HasCheckConstraint("ck_direct_calls_kind", "kind IN ('Voice', 'Video')");
            table.HasCheckConstraint("ck_direct_calls_state",
                "state IN ('Ringing', 'Accepted', 'Declined', 'Canceled', 'Missed', 'Ended')");
            table.HasCheckConstraint("ck_direct_calls_times",
                "created_at < expires_at AND (state IN ('Accepted', 'Ended')) = (accepted_at IS NOT NULL) " +
                "AND (state IN ('Declined', 'Canceled', 'Missed', 'Ended')) = (ended_at IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ClientCallId).HasColumnName("client_call_id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id");
        builder.Property(x => x.CallerId).HasColumnName("caller_id");
        builder.Property(x => x.CalleeId).HasColumnName("callee_id");
        builder.Property(x => x.CallerSessionId).HasColumnName("caller_session_id").HasMaxLength(128);
        builder.Property(x => x.CalleeSessionId).HasColumnName("callee_session_id").HasMaxLength(128);
        builder.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(12);
        builder.Property(x => x.State).HasColumnName("state").HasConversion<string>().HasMaxLength(12);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        builder.Property(x => x.CheckedAt).HasColumnName("checked_at");
        builder.Property(x => x.AcceptedAt).HasColumnName("accepted_at");
        builder.Property(x => x.EndedAt).HasColumnName("ended_at");
        builder.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConversationEntity>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.CallerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.CalleeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppSessionEntity>().WithMany().HasForeignKey(x => x.CallerSessionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppSessionEntity>().WithMany().HasForeignKey(x => x.CalleeSessionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(x => new { x.CallerId, x.ClientCallId }).IsUnique().HasDatabaseName("ux_direct_calls_caller_request");
        builder.HasIndex(x => new { x.CallerId, x.CreatedAt, x.Id }).HasDatabaseName("ix_direct_calls_caller_created");
        builder.HasIndex(x => new { x.CalleeId, x.CreatedAt, x.Id }).HasDatabaseName("ix_direct_calls_callee_created");
        builder.HasIndex(x => new { x.ConversationId, x.CreatedAt });
        builder.HasIndex(x => x.ConversationId).IsUnique()
            .HasFilter("state IN ('Ringing', 'Accepted')").HasDatabaseName("ux_direct_calls_active_conversation");
        builder.HasIndex(x => new { x.ExpiresAt, x.Id }).HasFilter("state = 'Ringing'").HasDatabaseName("ix_direct_calls_pending_ring");
        builder.HasIndex(x => new { x.CheckedAt, x.Id }).HasFilter("state IN ('Ringing', 'Accepted')")
            .HasDatabaseName("ix_direct_calls_access_check");
    }
}
