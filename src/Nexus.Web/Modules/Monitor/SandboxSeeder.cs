using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Monitoring;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Org;
using Npgsql;

namespace Nexus.Web.Modules.Monitor;

internal static class SandboxSeeder
{
    public static async Task SeedAsync(WebApplication app)
    {
        var config = app.Configuration;
        var database = DatabaseOptions.BindFrom(config);
        var connection = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString);
        if (!app.Environment.IsDevelopment() || config["NexusSandbox:Enabled"] != "true" ||
            connection.Host != "sandbox-db" || connection.Database != "nexus_sandbox" ||
            connection.Username != "nexus_runtime" || connection.Port != 5432 ||
            config["NexusStorage:Provider"] != "Local" ||
            !int.TryParse(config["NexusSandbox:Users"], out var users) || users is < 1 or > 300 ||
            config["NexusSandbox:Password"] is not { Length: >= 24 } password)
            throw new InvalidOperationException("Sandbox seeding requires the isolated Docker profile.");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<UserManager<NexusUser>>();
        var identity = scope.ServiceProvider.GetRequiredService<IOptions<IdentityModuleOptions>>().Value;
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (await db.Users.AnyAsync() || await db.Organizations.AnyAsync())
            throw new InvalidOperationException("Sandbox seeding requires a new database.");

        var organization = await IdentityBootstrapService.EnsureDefaultOrganizationAsync(db, identity);
        var now = DateTimeOffset.UtcNow;
        var people = new List<NexusUser>();
        var memberCount = users == 300 ? 1000 : Math.Max(users, 2);
        for (var i = 0; i < memberCount; i++)
        {
            var user = new NexusUser { Id = Guid.CreateVersion7(), UserName = $"sandbox_{i:D4}",
                DisplayName = $"Sandbox user {i + 1}", CreatedAt = now };
            var result = i < Math.Max(users, 2) ? await accounts.CreateAsync(user, password) : await accounts.CreateAsync(user);
            if (!result.Succeeded) throw new InvalidOperationException("Could not seed a synthetic account.");
            people.Add(user);
        }

        var owner = people[0].Id;
        var community = new CommunityEntity { Id = Guid.CreateVersion7(), OrganizationId = organization.Id,
            Name = "Sandbox Community", CreatedByUserId = owner, CreatedAt = now, UpdatedAt = now };
        var channel = NewConversation(ConversationKind.Channel, owner, now);
        channel.OrganizationId = organization.Id;
        channel.CommunityId = community.Id;
        channel.Title = "Sandbox feed";
        channel.IsCommunityMainFeed = true;
        var group = NewConversation(ConversationKind.Group, owner, now);
        group.Title = "Sandbox Group Chat";
        var post = new ChannelPostEntity { Id = Guid.CreateVersion7(), OrganizationId = organization.Id,
            ConversationId = channel.Id, AuthorUserId = owner, Body = "Synthetic discussion",
            CreatedAt = now, UpdatedAt = now };
        db.AddRange(community, channel, group, post);

        for (var i = 0; i < people.Count; i++)
        {
            var user = people[i];
            var membership = OrganizationMembership.Create(organization.Id, user, now);
            membership.TrustLevel = TrustLevel.Verified;
            db.Add(membership);
            var role = i == 0 ? ResourceRole.Owner : ResourceRole.Member;
            db.Add(new CommunityMemberEntity { CommunityId = community.Id, UserId = user.Id, Role = role, JoinedAt = now });
            if (i < Math.Max(users, 2)) AddConversationMember(db, group.Id, user.Id, now, role);
        }

        var pairs = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var directIds = new Guid[users];
        for (var i = 0; i < users; i++)
        {
            var sender = people[i].Id;
            var recipient = people[(i + 1) % Math.Max(users, 2)].Id;
            var key = ConversationEndpoints.BuildDmKey(sender, recipient);
            if (!pairs.TryGetValue(key, out var conversationId))
            {
                var direct = NewConversation(ConversationKind.Dm, sender, now);
                direct.DmKey = key;
                db.Add(direct);
                AddConversationMember(db, direct.Id, sender, now, ResourceRole.Member);
                AddConversationMember(db, direct.Id, recipient, now, ResourceRole.Member);
                pairs.Add(key, conversationId = direct.Id);
            }
            directIds[i] = conversationId;
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        var fixture = new SandboxFixture(organization.Id, channel.Id, group.Id, directIds,
            people.Take(Math.Max(users, 2)).Select(x => x.Id).ToArray(), post.Id);
        Console.WriteLine("NEXUS_SANDBOX_FIXTURE " + JsonSerializer.Serialize(fixture));
    }

    private static ConversationEntity NewConversation(ConversationKind kind, Guid owner, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), Kind = kind, CreatedByUserId = owner, CreatedAt = now, UpdatedAt = now };

    private static void AddConversationMember(NexusDbContext db, Guid conversation, Guid account, DateTimeOffset now, ResourceRole role) =>
        db.Add(new ConversationMemberEntity { ConversationId = conversation, UserId = account, Role = role,
            JoinedAt = now, VisibleFromSeq = 0 });
}
