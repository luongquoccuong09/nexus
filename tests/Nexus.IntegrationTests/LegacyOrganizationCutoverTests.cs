using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;

namespace Nexus.IntegrationTests;

public sealed class LegacyOrganizationCutoverTests(PostgresContainerFixture pg) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task UpgradePreservesHistoryAndFilesAndRequiresExplicitReviewedAccess()
    {
        var options = new DbContextOptionsBuilder<NexusDbContext>().UseNpgsql(pg.MigrationConnectionString).Options;
        await using var db = new NexusDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261123091148_M5CommunitiesAndAnnouncements");

        var orgId = Guid.CreateVersion7();
        var ownerId = Guid.CreateVersion7();
        var excludedId = Guid.CreateVersion7();
        var groupId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();
        var fileId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations (id, name, slug, created_at) VALUES ({orgId}, {"Legacy"}, {"legacy"}, {now})");
        db.Users.AddRange(
            new NexusUser { Id = ownerId, OrganizationId = orgId, UserName = "legacy_owner", DisplayName = "Owner", TwoFactorEnabled = true, CreatedAt = now },
            new NexusUser { Id = excludedId, OrganizationId = orgId, UserName = "legacy_excluded", DisplayName = "Excluded", CreatedAt = now });
        db.Conversations.Add(new ConversationEntity
        {
            Id = groupId, OrganizationId = orgId, Kind = ConversationKind.Group, CreatedByUserId = excludedId,
            Title = "Legacy group", NextSeq = 1, ChangeSeq = 1, CreatedAt = now, UpdatedAt = now
        });
        db.ConversationMembers.AddRange(
            new ConversationMemberEntity { ConversationId = groupId, UserId = ownerId, Role = ResourceRole.Member, JoinedAt = now },
            new ConversationMemberEntity { ConversationId = groupId, UserId = excludedId, Role = ResourceRole.Owner, JoinedAt = now });
        db.Messages.Add(new MessageEntity
        {
            Id = messageId, ConversationId = groupId, Seq = 1, SenderId = excludedId,
            Body = "Legacy fixture message", ClientMsgId = "legacy-fixture", PayloadSha256 = new string('a', 64), CreatedAt = now
        });
        db.Attachments.Add(new AttachmentEntity
        {
            Id = fileId, OrganizationId = orgId, ConversationId = groupId, MessageId = messageId, UploaderUserId = excludedId,
            FileName = "fixture.pdf", ContentType = "application/pdf", ClientUploadId = "legacy-file",
            ObjectKey = "legacy/fixture", Sha256Hex = new string('a', 64), ScanStatus = "clean", State = AttachmentState.Attached, CreatedAt = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await migrator.MigrateAsync();

        Assert.True((await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == orgId)).LegacyReviewPending);
        Assert.Equal(2, await db.OrganizationMemberships.CountAsync(m => m.OrganizationId == orgId && m.Status == AccountStatus.Former));
        var manifest = new LegacyOrganizationReview.Manifest(orgId, ownerId, [ownerId], null,
            [new LegacyOrganizationReview.OwnershipTransfer(groupId, ownerId)]);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web));
            await using (var preflight = new NexusDbContext(options)) await LegacyOrganizationReview.ReviewAsync(preflight, path, apply: false);
            Assert.True((await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == orgId)).LegacyReviewPending);
            await using (var cutover = new NexusDbContext(options)) await LegacyOrganizationReview.ReviewAsync(cutover, path, apply: true);
        }
        finally { File.Delete(path); }

        var approved = await db.OrganizationMemberships.AsNoTracking().SingleAsync(m => m.OrganizationId == orgId && m.Status == AccountStatus.Active);
        Assert.Equal(ownerId, approved.Id);
        Assert.True(approved.IsOwner);
        Assert.True(await db.Users.AnyAsync(u => u.Id == excludedId && u.Status == AccountStatus.Active));
        Assert.Equal(ResourceRole.Owner, (await db.ConversationMembers.SingleAsync(m => m.ConversationId == groupId && m.UserId == ownerId)).Role);
        Assert.NotNull((await db.ConversationMembers.SingleAsync(m => m.ConversationId == groupId && m.UserId == excludedId)).LeftAt);
        Assert.Equal("Legacy fixture message", (await db.Messages.FindAsync(messageId))!.Body);
        Assert.Equal("legacy/fixture", (await db.Attachments.FindAsync(fileId))!.ObjectKey);
    }
}
