using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Files;

public sealed class SharedLinkEntity
{
    public Guid Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? MessageId { get; set; }
    public long? MessageSeq { get; set; }
    public Guid? PostId { get; set; }
    public Guid SenderUserId { get; set; }
    public required string Url { get; set; }
    public required string Host { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record ExtractedSharedLink(string Url, string Host, string? Label);

public static partial class SharedLinkExtractor
{
    public const int MaxLinksPerItem = 20;
    public const int MaxUrlLength = 2048;
    public const int MaxHostLength = 255;
    public const int MaxLabelLength = 160;

    [GeneratedRegex(@"\[(?<label>[^\[\]\r\n]{1,160})\]\((?<url>[^\s\)\r\n]{1,2048})\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"https?://[^\s<>\[\]""'`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex BareUrlRegex();

    public static IReadOnlyList<ExtractedSharedLink> Extract(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var results = new List<ExtractedSharedLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var coveredRanges = new List<(int Start, int End)>();

        try
        {
            foreach (Match m in MarkdownLinkRegex().Matches(body))
            {
                coveredRanges.Add((m.Index, m.Index + m.Length));
                var rawLabel = m.Groups["label"].Value.Trim();
                var rawUrl = m.Groups["url"].Value.Trim();
                if (TryNormalizeSafeUrl(rawUrl, out var url, out var host) && seen.Add(url))
                {
                    var label = string.IsNullOrWhiteSpace(rawLabel)
                        ? null
                        : rawLabel.Length <= MaxLabelLength ? rawLabel : rawLabel[..MaxLabelLength];
                    results.Add(new ExtractedSharedLink(url, host, label));
                    if (results.Count >= MaxLinksPerItem)
                    {
                        return results;
                    }
                }
            }

            foreach (Match m in BareUrlRegex().Matches(body))
            {
                var insideMarkdown = coveredRanges.Any(r => m.Index >= r.Start && m.Index < r.End);
                if (insideMarkdown)
                {
                    continue;
                }

                var trimmed = TrimTrailingPunctuation(m.Value);
                if (TryNormalizeSafeUrl(trimmed, out var url, out var host) && seen.Add(url))
                {
                    results.Add(new ExtractedSharedLink(url, host, null));
                    if (results.Count >= MaxLinksPerItem)
                    {
                        return results;
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Return whatever safe links were collected before timeout.
        }

        return results;
    }

    private static string TrimTrailingPunctuation(string candidate)
    {
        var end = candidate.Length;
        while (end > 0)
        {
            var ch = candidate[end - 1];
            if (ch is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"')
            {
                end--;
                continue;
            }

            if (ch == ')')
            {
                var open = 0;
                var close = 0;
                for (var i = 0; i < end; i++)
                {
                    if (candidate[i] == '(') open++;
                    else if (candidate[i] == ')') close++;
                }
                if (close > open)
                {
                    end--;
                    continue;
                }
            }

            break;
        }

        return candidate[..end];
    }

    private static bool TryNormalizeSafeUrl(string raw, out string normalizedUrl, out string host)
    {
        normalizedUrl = string.Empty;
        host = string.Empty;

        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxUrlLength)
        {
            return false;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Host.Length > MaxHostLength)
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        if (normalizedUrl.Length > MaxUrlLength)
        {
            return false;
        }

        host = uri.Host.ToLowerInvariant();
        return true;
    }
}

public sealed class SharedLinkEntityConfiguration : IEntityTypeConfiguration<SharedLinkEntity>
{
    public void Configure(EntityTypeBuilder<SharedLinkEntity> builder)
    {
        builder.ToTable("shared_links", table =>
        {
            table.HasCheckConstraint(
                "ck_shared_links_single_parent",
                "(message_id IS NOT NULL AND post_id IS NULL AND message_seq IS NOT NULL) OR (post_id IS NOT NULL AND message_id IS NULL AND message_seq IS NULL)");
            table.HasCheckConstraint(
                "ck_shared_links_safe_scheme",
                "url ~* '^https?://'");
            table.HasCheckConstraint(
                "ck_shared_links_lengths",
                "char_length(url) <= 2048 AND char_length(host) <= 255 AND (label IS NULL OR char_length(label) <= 160)");
        });
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrganizationId).HasColumnName("organization_id");
        builder.Property(x => x.ConversationId).HasColumnName("conversation_id").IsRequired();
        builder.Property(x => x.MessageId).HasColumnName("message_id");
        builder.Property(x => x.MessageSeq).HasColumnName("message_seq");
        builder.Property(x => x.PostId).HasColumnName("post_id");
        builder.Property(x => x.SenderUserId).HasColumnName("sender_user_id").IsRequired();
        builder.Property(x => x.Url).HasColumnName("url").HasColumnType("text").IsRequired();
        builder.Property(x => x.Host).HasColumnName("host").HasColumnType("text").IsRequired();
        builder.Property(x => x.Label).HasColumnName("label").HasColumnType("text");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasOne<OrganizationEntity>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConversationEntity>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<MessageEntity>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ChannelPostEntity>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<NexusUser>().WithMany().HasForeignKey(x => x.SenderUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ConversationId, x.CreatedAt, x.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_shared_links_conv_created");

        builder.HasIndex(x => x.MessageId)
            .HasFilter("message_id IS NOT NULL")
            .HasDatabaseName("ix_shared_links_message_id");

        builder.HasIndex(x => x.PostId)
            .HasFilter("post_id IS NOT NULL")
            .HasDatabaseName("ix_shared_links_post_id");
    }
}
