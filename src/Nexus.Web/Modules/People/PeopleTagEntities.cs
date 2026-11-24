using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.People;

public sealed class PeopleTagEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? CommunityId { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? Description { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PeopleTagMemberEntity
{
    public Guid TagId { get; set; }
    public Guid UserId { get; set; }
    public Guid AddedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public static partial class PeopleTagValidator
{
    public const int MinNameLength = 2;
    public const int MaxNameLength = 48;
    public const int MaxDescriptionLength = 200;

    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "all",
        "channel",
        "everyone",
        "here"
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{1,31}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SlugRegex();

    public static bool TryNormalizeName(string? raw, out string name, out string? error)
    {
        name = (raw ?? string.Empty).Trim();
        if (name.Length is < MinNameLength or > MaxNameLength)
        {
            error = $"Tag name must be {MinNameLength} to {MaxNameLength} characters.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryNormalizeSlug(string? raw, out string slug, out string? error)
    {
        slug = (raw ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();
        if (!SlugRegex().IsMatch(slug))
        {
            error = "Tag handle must be 2 to 32 lowercase letters, numbers, hyphens, or underscores, starting with a letter or number.";
            return false;
        }

        if (ReservedSlugs.Contains(slug))
        {
            error = $"The tag handle '{slug}' is reserved.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryNormalizeDescription(string? raw, out string? description, out string? error)
    {
        description = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        if (description is not null && description.Length > MaxDescriptionLength)
        {
            error = $"Tag description must be at most {MaxDescriptionLength} characters.";
            return false;
        }

        error = null;
        return true;
    }
}

public sealed class PeopleTagEntityConfiguration : IEntityTypeConfiguration<PeopleTagEntity>
{
    public void Configure(EntityTypeBuilder<PeopleTagEntity> builder)
    {
        builder.ToTable("people_tags", table =>
        {
            table.HasCheckConstraint(
                "ck_people_tags_slug_format",
                "slug ~ '^[a-z0-9][a-z0-9_-]{1,31}$'");
            table.HasCheckConstraint(
                "ck_people_tags_lengths",
                "char_length(name) BETWEEN 2 AND 48 AND (description IS NULL OR char_length(description) <= 200)");
        });
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(x => x.CommunityId).HasColumnName("community_id");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        builder.Property(x => x.Slug).HasColumnName("slug").HasColumnType("text").IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasColumnType("text");
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CommunityEntity>().WithMany().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.OrganizationId, x.CommunityId, x.Slug })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_people_tags_scope_slug");
    }
}

public sealed class PeopleTagMemberEntityConfiguration : IEntityTypeConfiguration<PeopleTagMemberEntity>
{
    public void Configure(EntityTypeBuilder<PeopleTagMemberEntity> builder)
    {
        builder.ToTable("people_tag_members");
        builder.HasKey(x => new { x.TagId, x.UserId });

        builder.Property(x => x.TagId).HasColumnName("tag_id").IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.AddedByUserId).HasColumnName("added_by_user_id").IsRequired();
        builder.Property(x => x.AddedAt).HasColumnName("added_at").IsRequired();

        builder.HasOne<PeopleTagEntity>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.UserId)
            .HasDatabaseName("ix_people_tag_members_user_id");
    }
}
