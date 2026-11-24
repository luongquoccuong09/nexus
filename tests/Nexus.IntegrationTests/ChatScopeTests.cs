using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Conversations;
using Nexus.Web.Modules.Files;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Messaging;
using Nexus.Web.Modules.Notifications;
using Nexus.Web.Modules.Org;
using Nexus.Web.Modules.People;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace Nexus.IntegrationTests;

public sealed class ChatScopeTests(PostgresContainerFixture pg, ITestOutputHelper output) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task PersonalChatAndFilesPagePastFiftyTiedRowsWithFreshScopeChecks()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=240", $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], additionalLogSink: new ErrorSink(output));
        await app.StartAsync();
        var baseUri = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        var suffix = Guid.NewGuid().ToString("N")[..6];
        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);
        var aliceId = await RegisterAsync(alice, "pagealice_" + suffix);
        var bobId = await RegisterAsync(bob, "pagebob_" + suffix);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var createdAt = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var conversations = Enumerable.Range(1, 52).Select(number => new ConversationEntity
        {
            Id = Guid.NewGuid(), CreatedByUserId = aliceId, Title = $"Paged group {number}",
            Kind = ConversationKind.Group, CreatedAt = createdAt, UpdatedAt = createdAt
        }).ToArray();
        db.Conversations.AddRange(conversations);
        db.ConversationMembers.AddRange(conversations.SelectMany(conversation => new[]
        {
            new ConversationMemberEntity { ConversationId = conversation.Id, UserId = aliceId, Role = ResourceRole.Owner, JoinedAt = createdAt },
            new ConversationMemberEntity { ConversationId = conversation.Id, UserId = bobId, Role = ResourceRole.Member, JoinedAt = createdAt }
        }));

        var fileConversation = conversations[0];
        fileConversation.NextSeq = 1;
        fileConversation.LatestMainSeq = 1;
        var message = new MessageEntity
        {
            Id = Guid.NewGuid(), ConversationId = fileConversation.Id, Seq = 1, SenderId = aliceId,
            ClientMsgId = "paged-fixture", PayloadSha256 = new string('0', 64), Body = "File list fixture", CreatedAt = createdAt
        };
        db.Messages.Add(message);
        var files = Enumerable.Range(1, 52).Select(number => new AttachmentEntity
        {
            Id = Guid.NewGuid(), ConversationId = fileConversation.Id, MessageId = message.Id, UploaderUserId = aliceId,
            ClientUploadId = $"page-upload-{number}", FileName = $"paged-{number}.pdf", ContentType = "application/pdf",
            SizeBytes = 100, Sha256Hex = new string('0', 64), ObjectKey = $"fixture/{number}", State = AttachmentState.Attached,
            ScanStatus = "clean", CreatedAt = createdAt, AttachedAt = createdAt
        }).ToArray();
        db.Attachments.AddRange(files);
        await db.SaveChangesAsync();

        const string chatPath = "/api/personal/conversations?limit=50";
        var chats = await GetAsync(bob, chatPath);
        Assert.Equal(50, chats.GetProperty("items").GetArrayLength());
        var cursor = chats.GetProperty("nextBefore");
        var beforeAt = Uri.EscapeDataString(cursor.GetProperty("updatedAt").GetString()!);
        var nextChats = await GetAsync(bob, $"{chatPath}&beforeAt={beforeAt}&beforeId={cursor.GetProperty("id").GetGuid()}");
        Assert.Equal(2, nextChats.GetProperty("items").GetArrayLength());
        Assert.False(nextChats.GetProperty("hasMore").GetBoolean());
        Assert.Equal(conversations.Select(item => item.Id).Order(), ReadIds(chats).Concat(ReadIds(nextChats)).Order());

        var filesPath = $"/api/personal/conversations/{fileConversation.Id}/files?limit=50";
        var firstFiles = await GetAsync(bob, filesPath);
        Assert.Equal(50, firstFiles.GetProperty("items").GetArrayLength());
        var createdBefore = Uri.EscapeDataString(firstFiles.GetProperty("nextBefore").GetString()!);
        var nextFilesPath = $"{filesPath}&before={createdBefore}&beforeId={firstFiles.GetProperty("nextBeforeId").GetGuid()}";
        var nextFiles = await GetAsync(bob, nextFilesPath);
        Assert.Equal(2, nextFiles.GetProperty("items").GetArrayLength());
        Assert.False(nextFiles.GetProperty("hasMore").GetBoolean());
        Assert.Equal(files.Select(item => item.Id).Order(), ReadIds(firstFiles).Concat(ReadIds(nextFiles)).Order());

        await db.ConversationMembers.Where(member => member.ConversationId == fileConversation.Id && member.UserId == bobId)
            .ExecuteUpdateAsync(update => update.SetProperty(member => member.LeftAt, DateTimeOffset.UtcNow));
        using var revokedPage = await bob.GetAsync(nextFilesPath);
        Assert.Equal(HttpStatusCode.Forbidden, revokedPage.StatusCode);
    }

    private static IEnumerable<Guid> ReadIds(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid());

    [Fact]
    public async Task EmailDigestRequiresOptInAndOnlyIncludesUnreadActivityWithoutAcceptedPush()
    {
        var mail = new RecordingDigestTransport();
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=240", $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], builder => builder.Services.AddSingleton<IEmailDigestTransport>(mail), additionalLogSink: new ErrorSink(output));
        await app.StartAsync();
        var baseUri = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var bobName = "digestbob_" + suffix;
        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);
        await RegisterAsync(alice, "digestalice_" + suffix);
        var bobId = await RegisterAsync(bob, bobName);

        const string settingsPath = "/api/notifications/email-digest";
        Assert.False((await GetAsync(bob, settingsPath)).GetProperty("eligible").GetBoolean());
        await ProtectedAsync(bob, settingsPath, new { enabled = true }, HttpStatusCode.Conflict, HttpMethod.Put);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        const string address = "digest-fixture@example.test";
        await db.Users.Where(user => user.Id == bobId).ExecuteUpdateAsync(update =>
            update.SetProperty(user => user.Email, address).SetProperty(user => user.EmailConfirmed, true));
        await ProtectedAsync(bob, settingsPath, new { enabled = true }, HttpStatusCode.NoContent, HttpMethod.Put);
        Assert.True((await GetAsync(bob, settingsPath)).GetProperty("enabled").GetBoolean());

        await ProtectedAsync(alice, "/api/personal/contacts/requests", new { username = bobName }, HttpStatusCode.Accepted);
        var invitation = Assert.Single((await GetAsync(bob, "/api/personal/contacts/requests")).GetProperty("items").EnumerateArray());
        await ProtectedAsync(bob, $"/api/personal/contacts/requests/{invitation.GetProperty("id").GetGuid()}",
            new { accept = true }, HttpStatusCode.NoContent, HttpMethod.Put);
        var dm = await PostAsync(alice, "/api/personal/conversations/dm", new { targetUserId = bobId }, HttpStatusCode.OK);
        var conversationId = dm.GetProperty("id").GetGuid();
        var path = $"/api/personal/conversations/{conversationId}";
        var seen = await PostAsync(alice, $"{path}/messages", new { body = "Already read", clientMsgId = "digest-seen" }, HttpStatusCode.Created);
        var pushed = await PostAsync(alice, $"{path}/messages", new { body = "Already pushed", clientMsgId = "digest-pushed" }, HttpStatusCode.Created);
        var candidate = await PostAsync(alice, $"{path}/messages", new { body = "Private text remains in Nexus", clientMsgId = "digest-pending" }, HttpStatusCode.Created);
        var candidateId = candidate.GetProperty("id").GetGuid();
        var pushedId = pushed.GetProperty("id").GetGuid();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var notices = db.Notifications.Where(item => item.ConversationId == conversationId && item.RecipientId == bobId);
        while (await notices.CountAsync(timeout.Token) < 3)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }

        var now = DateTimeOffset.UtcNow;
        var jobs = await db.OutboxMessages.AsNoTracking().Where(item => item.Kind == "notification.digest" && item.DestinationId == bobId)
            .Select(item => item.Id).ToArrayAsync(timeout.Token);
        Assert.Equal(3, jobs.Length);
        await ProtectedAsync(bob, $"{path}/read", new { lastReadSeq = seen.GetProperty("seq").GetInt64() }, HttpStatusCode.OK, HttpMethod.Put);
        await notices.Where(item => item.ResourceId == pushedId).ExecuteUpdateAsync(update => update.SetProperty(item => item.PushAcceptedAt, now), timeout.Token);
        await db.EmailDigestOptIns.Where(item => item.UserId == bobId).ExecuteUpdateAsync(update => update.SetProperty(item => item.EnabledAt, now.AddHours(-3)), timeout.Token);
        await notices.ExecuteUpdateAsync(update => update.SetProperty(item => item.CreatedAt, now.AddHours(-2)), timeout.Token);
        await db.OutboxMessages.Where(item => jobs.Contains(item.Id)).ExecuteUpdateAsync(update => update.SetProperty(item => item.AvailableAt, now), timeout.Token);

        var delivered = await mail.Deliveries.Reader.ReadAsync(timeout.Token);
        Assert.Equal((address, 1), delivered);
        while (await db.OutboxMessages.CountAsync(item => jobs.Contains(item.Id) && item.CompletedAt != null, timeout.Token) < jobs.Length)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }

        Assert.False(mail.Deliveries.Reader.TryRead(out _));
        var digest = Assert.Single(await notices.Where(item => item.DigestedAt != null).ToListAsync(timeout.Token));
        Assert.Equal(candidateId, digest.ResourceId);
        await ProtectedAsync(bob, settingsPath, new { enabled = false }, HttpStatusCode.NoContent, HttpMethod.Put);
        Assert.False((await GetAsync(bob, settingsPath)).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task WebPushDeliversAnEmptyVapidRequestAndDiscardsExpiredEndpoints()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fakePush = new RecordingPushHandler();
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=240", $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NexusPush:VapidPrivateKeyPem"] = key.ExportECPrivateKeyPem(),
                ["NexusPush:VapidSubject"] = "mailto:fixture@example.test"
            });
            builder.Services.AddHttpClient(WebPushTransport.ClientName).ConfigurePrimaryHttpMessageHandler(() => fakePush);
        }, additionalLogSink: new ErrorSink(output));
        await app.StartAsync();
        var baseUri = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var bobName = "pushbob_" + suffix;
        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);
        await RegisterAsync(alice, "pushalice_" + suffix);
        var bobId = await RegisterAsync(bob, bobName);

        const string pushPath = "/api/notifications/push";
        var settings = await GetAsync(bob, pushPath);
        var publicKey = settings.GetProperty("publicKey").GetString();
        Assert.NotNull(publicKey);
        Assert.False(settings.GetProperty("subscribed").GetBoolean());
        await ProtectedAsync(bob, pushPath, new { endpoint = "https://fcm.googleapis.com.invalid/subscription" }, HttpStatusCode.BadRequest, HttpMethod.Put);
        const string endpoint = "https://fcm.googleapis.com/fcm/send/synthetic-push-endpoint";
        await ProtectedAsync(bob, pushPath, new { endpoint }, HttpStatusCode.NoContent, HttpMethod.Put);

        await ProtectedAsync(alice, "/api/personal/contacts/requests", new { username = bobName }, HttpStatusCode.Accepted);
        var request = Assert.Single((await GetAsync(bob, "/api/personal/contacts/requests")).GetProperty("items").EnumerateArray());
        await ProtectedAsync(bob, $"/api/personal/contacts/requests/{request.GetProperty("id").GetGuid()}",
            new { accept = true }, HttpStatusCode.NoContent, HttpMethod.Put);
        var dm = await PostAsync(alice, "/api/personal/conversations/dm", new { targetUserId = bobId }, HttpStatusCode.OK);
        var conversationId = dm.GetProperty("id").GetGuid();
        var path = $"/api/personal/conversations/{conversationId}/messages";
        await PostAsync(alice, path, new { body = "Private fixture message", clientMsgId = "push-all" }, HttpStatusCode.Created);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sent = await fakePush.Requests.Reader.ReadAsync(timeout.Token);
        Assert.Equal(endpoint, sent.Endpoint);
        Assert.Equal("300", sent.Ttl);
        Assert.Empty(sent.Body);
        Assert.InRange(sent.Topic.Length, 1, 32);
        var authorization = sent.Authorization.Split(", k=", StringSplitOptions.None);
        Assert.Equal(publicKey, authorization[1]);
        var segments = authorization[0]["vapid t=".Length..].Split('.');
        using var claims = JsonDocument.Parse(WebEncoders.Base64UrlDecode(segments[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"), WebEncoders.Base64UrlDecode(segments[2]),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        const string quietPath = "/api/notifications/quiet-hours";
        var now = DateTimeOffset.UtcNow;
        await ProtectedAsync(bob, quietPath, new
        {
            enabled = true,
            start = now.AddHours(-1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            end = now.AddHours(1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            timeZoneId = "UTC"
        }, HttpStatusCode.NoContent, HttpMethod.Put);
        var quietMessage = await PostAsync(alice, path, new { body = "Quiet hours", clientMsgId = "push-quiet" }, HttpStatusCode.Created);
        var quietMessageId = quietMessage.GetProperty("id").GetGuid();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var deferredQuery = from delivery in db.OutboxMessages.AsNoTracking()
                            join notice in db.Notifications.AsNoTracking() on delivery.ResourceId equals notice.Id
                            where notice.ResourceId == quietMessageId && delivery.Kind == "notification.push"
                            select delivery;
        while (!await deferredQuery.AnyAsync(delivery => delivery.AvailableAt > now.AddMinutes(30), timeout.Token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }

        var deferred = await deferredQuery.SingleAsync(timeout.Token);
        Assert.Equal(0, deferred.Attempts);
        Assert.False(fakePush.Requests.Reader.TryRead(out _));
        await ProtectedAsync(bob, $"/api/personal/conversations/{conversationId}/read",
            new { lastReadSeq = quietMessage.GetProperty("seq").GetInt64() }, HttpStatusCode.OK, HttpMethod.Put);
        await ProtectedAsync(bob, quietPath, new { enabled = false }, HttpStatusCode.NoContent, HttpMethod.Put);
        await db.OutboxMessages.Where(delivery => delivery.Id == deferred.Id).ExecuteUpdateAsync(
            update => update.SetProperty(delivery => delivery.AvailableAt, DateTimeOffset.UtcNow), timeout.Token);
        while (!await deferredQuery.AnyAsync(delivery => delivery.CompletedAt != null, timeout.Token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }

        Assert.False(fakePush.Requests.Reader.TryRead(out _));

        fakePush.ResponseCode = HttpStatusCode.Gone;
        await PostAsync(alice, path, new { body = $"Hello @{bobName}", clientMsgId = "push-expired" }, HttpStatusCode.Created);
        var expired = await fakePush.Requests.Reader.ReadAsync(timeout.Token);
        Assert.Equal(sent.Topic, expired.Topic);
        while ((await GetAsync(bob, pushPath)).GetProperty("subscribed").GetBoolean())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    [Fact]
    public async Task PersonalMentionsDeduplicateAndFollowTheConversationReadCursor()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=240", $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], additionalLogSink: new ErrorSink(output));
        await app.StartAsync();
        var baseUri = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var bobName = "mentionbob_" + suffix;
        var outsiderName = "mentionoutsider_" + suffix;
        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);
        using var outsider = Client(baseUri, out _);
        await RegisterAsync(alice, "mentionalice_" + suffix);
        var bobId = await RegisterAsync(bob, bobName);
        await RegisterAsync(outsider, outsiderName);

        await ProtectedAsync(alice, "/api/personal/contacts/requests", new { username = bobName }, HttpStatusCode.Accepted);
        var request = Assert.Single((await GetAsync(bob, "/api/personal/contacts/requests")).GetProperty("items").EnumerateArray());
        await ProtectedAsync(bob, $"/api/personal/contacts/requests/{request.GetProperty("id").GetGuid()}",
            new { accept = true }, HttpStatusCode.NoContent, HttpMethod.Put);
        var dm = await PostAsync(alice, "/api/personal/conversations/dm", new { targetUserId = bobId }, HttpStatusCode.OK);
        var conversationId = dm.GetProperty("id").GetGuid();
        var path = $"/api/personal/conversations/{conversationId}";
        var body = new { body = $"Hello @{bobName}, @{bobName.ToUpperInvariant()}. @{outsiderName} @all", clientMsgId = "mention-a" };
        var message = await PostAsync(alice, $"{path}/messages", body, HttpStatusCode.Created);
        await PostAsync(alice, $"{path}/messages", body, HttpStatusCode.OK);
        var ignored = await PostAsync(alice, $"{path}/messages", new
        {
            body = $"`@{bobName}` mail@{bobName}.com https://example.com/@{bobName}", clientMsgId = "mention-code"
        }, HttpStatusCode.Created);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var messageId = message.GetProperty("id").GetGuid();
        while (!await db.OutboxMessages.AnyAsync(item => item.ResourceId == messageId && item.CompletedAt != null, timeout.Token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }

        var effect = Assert.Single(await db.Notifications.Where(item => item.ConversationId == conversationId).ToListAsync(timeout.Token));
        Assert.Equal(bobId, effect.RecipientId);
        var ignoredId = ignored.GetProperty("id").GetGuid();
        Assert.False(await db.OutboxMessages.AnyAsync(item => item.ResourceId == ignoredId, timeout.Token));

        const string counts = "/api/notifications/mentions/unread";
        Assert.Equal(1, (await GetAsync(bob, $"{counts}?conversationId={conversationId}")).GetProperty("unreadMentionCount").GetInt32());
        Assert.Equal(0, (await GetAsync(outsider, $"{counts}?conversationId={conversationId}")).GetProperty("unreadMentionCount").GetInt32());
        var activity = Assert.Single((await GetAsync(bob, "/api/notifications?unreadOnly=true")).GetProperty("items").EnumerateArray());
        Assert.True(activity.GetProperty("isUnread").GetBoolean());
        Assert.Equal(effect.Id, activity.GetProperty("id").GetGuid());
        var sourcePath = $"/api/notifications/{effect.Id}/source";
        Assert.Equal(body.body, (await GetAsync(bob, sourcePath)).GetProperty("body").GetString());
        using var inaccessibleSource = await outsider.GetAsync(sourcePath);
        Assert.Equal(HttpStatusCode.NotFound, inaccessibleSource.StatusCode);
        await ProtectedAsync(bob, $"{path}/read", new { lastReadSeq = message.GetProperty("seq").GetInt64() }, HttpStatusCode.OK, HttpMethod.Put);
        Assert.Equal(0, (await GetAsync(bob, counts)).GetProperty("unreadMentionCount").GetInt32());
        var readPath = $"/api/notifications/{effect.Id}/read";
        using var noCsrf = await bob.PutAsync(readPath, null);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var outsiderReadRequest = await ProtectedMessageAsync(outsider, readPath, null, HttpMethod.Put);
        using var outsiderRead = await outsider.SendAsync(outsiderReadRequest);
        Assert.Equal(HttpStatusCode.NotFound, outsiderRead.StatusCode);
        await ProtectedAsync(bob, readPath, null, HttpStatusCode.NoContent, HttpMethod.Put);
        var firstReadAt = await db.Notifications.Where(item => item.Id == effect.Id).Select(item => item.ReadAt).SingleAsync();
        Assert.NotNull(firstReadAt);
        await ProtectedAsync(bob, readPath, null, HttpStatusCode.NoContent, HttpMethod.Put);
        Assert.Equal(firstReadAt, await db.Notifications.Where(item => item.Id == effect.Id).Select(item => item.ReadAt).SingleAsync());
        Assert.Empty((await GetAsync(bob, "/api/notifications?unreadOnly=true")).GetProperty("items").EnumerateArray());

        var reply = await ProtectedAsync(alice, $"{path}/messages", new
        {
            body = $"Thread mention @{bobName}", clientMsgId = "mention-thread", parentMessageId = messageId
        }, HttpStatusCode.Created);
        var replyId = reply.GetProperty("id").GetGuid();
        var laterMain = await PostAsync(alice, $"{path}/messages", new
        {
            body = "A later main message", clientMsgId = "mention-later-main"
        }, HttpStatusCode.Created);
        await ProtectedAsync(bob, $"{path}/read", new { lastReadSeq = laterMain.GetProperty("seq").GetInt64() },
            HttpStatusCode.OK, HttpMethod.Put);
        while (!await db.OutboxMessages.AnyAsync(item => item.ResourceId == replyId && item.CompletedAt != null, timeout.Token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
        Assert.Equal(1, (await GetAsync(bob, counts)).GetProperty("unreadMentionCount").GetInt32());
        await ProtectedAsync(bob, $"{path}/messages/{messageId}/thread/read",
            new { lastReadReplySeq = reply.GetProperty("seq").GetInt64() }, HttpStatusCode.OK, HttpMethod.Put);
        Assert.Equal(0, (await GetAsync(bob, counts)).GetProperty("unreadMentionCount").GetInt32());

        var preferencePath = $"/api/notifications/conversations/{conversationId}/preference";
        Assert.Equal("all", (await GetAsync(bob, preferencePath)).GetProperty("mode").GetString());
        await ProtectedAsync(bob, preferencePath, new { mode = "muted" }, HttpStatusCode.NoContent, HttpMethod.Put);
        Assert.Equal("muted", (await GetAsync(bob, preferencePath)).GetProperty("mode").GetString());
        Assert.Equal("all", (await GetAsync(alice, preferencePath)).GetProperty("mode").GetString());
        using var outsiderPreference = await outsider.GetAsync(preferencePath);
        Assert.Equal(HttpStatusCode.NotFound, outsiderPreference.StatusCode);

        var muted = await PostAsync(alice, $"{path}/messages", new { body = $"Muted @{bobName}", clientMsgId = "mention-muted" }, HttpStatusCode.Created);
        var mutedId = muted.GetProperty("id").GetGuid();
        while (!await db.OutboxMessages.AnyAsync(item => item.ResourceId == mutedId && item.CompletedAt != null, timeout.Token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
        Assert.False(await db.Notifications.AnyAsync(item => item.ResourceId == mutedId, timeout.Token));

        const string quietPath = "/api/notifications/quiet-hours";
        await ProtectedAsync(bob, quietPath, new { enabled = true, start = "22:00", end = "07:00", timeZoneId = "Asia/Bangkok" },
            HttpStatusCode.NoContent, HttpMethod.Put);
        var quietHours = await GetAsync(bob, quietPath);
        Assert.True(quietHours.GetProperty("enabled").GetBoolean());
        Assert.Equal("22:00", quietHours.GetProperty("start").GetString());
        Assert.Equal("07:00", quietHours.GetProperty("end").GetString());
        Assert.Equal("Asia/Bangkok", quietHours.GetProperty("timeZoneId").GetString());
        Assert.False((await GetAsync(alice, quietPath)).GetProperty("enabled").GetBoolean());
        await ProtectedAsync(bob, quietPath, new { enabled = false }, HttpStatusCode.NoContent, HttpMethod.Put);
        Assert.False((await GetAsync(bob, quietPath)).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task PersonalConsentOrganizationInvitationAndRevocationEnforceScope()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusOrganizations:AllowCreation=true", "--NexusSecurity:RateLimitPermitsPerMinute=240",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], additionalLogSink: new ErrorSink(output));
        await app.StartAsync();
        var baseUri = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        var suffix = Guid.NewGuid().ToString("N")[..6];
        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out var bobCookies);
        using var other = Client(baseUri, out _);
        var aliceId = await RegisterAsync(alice, "scopealice_" + suffix);
        var bobUser = "scopebob_" + suffix;
        var bobId = await RegisterAsync(bob, bobUser);
        var otherId = await RegisterAsync(other, "scopeother_" + suffix);

        Assert.Empty((await GetAsync(alice, "/api/organizations")).GetProperty("items").EnumerateArray());
        Assert.Empty((await GetAsync(alice, "/api/personal/people/directory")).GetProperty("items").EnumerateArray());
        using var blockedDm = await alice.PostAsJsonAsync("/api/personal/conversations/dm", new { targetUserId = bobId });
        Assert.Equal(HttpStatusCode.NotFound, blockedDm.StatusCode);
        await ProtectedAsync(alice, "/api/personal/contacts/requests", new { username = bobUser }, HttpStatusCode.Accepted);
        var request = Assert.Single((await GetAsync(bob, "/api/personal/contacts/requests")).GetProperty("items").EnumerateArray());
        await ProtectedAsync(bob, $"/api/personal/contacts/requests/{request.GetProperty("id").GetGuid()}", new { accept = true }, HttpStatusCode.NoContent, HttpMethod.Put);

        var dm = await PostAsync(alice, "/api/personal/conversations/dm", new { targetUserId = bobId }, HttpStatusCode.OK);
        var conversation = dm.GetProperty("id").GetGuid();
        var personalPath = $"/api/personal/conversations/{conversation}";
        await PostAsync(alice, $"{personalPath}/messages", new { body = "Personal fixture", clientMsgId = "personal-first" }, HttpStatusCode.Created);
        Assert.Single((await GetAsync(bob, $"{personalPath}/messages")).GetProperty("items").EnumerateArray());
        using var outsider = await other.GetAsync($"{personalPath}/messages");
        Assert.Equal(HttpStatusCode.Forbidden, outsider.StatusCode);

        // Blocking remains private to the actor and unblocking restores the accepted contact.
        using var block = await alice.PutAsync($"/api/personal/people/blocks/{bobId}", null);
        Assert.Equal(HttpStatusCode.NoContent, block.StatusCode);
        var entry = Assert.Single((await GetAsync(alice, "/api/personal/people/blocks"))
            .GetProperty("items").EnumerateArray());
        Assert.Equal(bobId, entry.GetProperty("blockedUserId").GetGuid());
        Assert.Equal(bobUser, entry.GetProperty("username").GetString());
        Assert.Empty((await GetAsync(bob, "/api/personal/people/blocks")).GetProperty("items").EnumerateArray());
        Assert.Empty((await GetAsync(alice, "/api/personal/people/directory")).GetProperty("items").EnumerateArray());
        var lookup = $"/api/personal/conversations?limit=1&conversationId={conversation}";
        var ownDm = Assert.Single((await GetAsync(alice, lookup)).GetProperty("items").EnumerateArray());
        Assert.Equal(conversation, ownDm.GetProperty("id").GetGuid());
        Assert.True(ownDm.GetProperty("peer").GetProperty("isBlockedByMe").GetBoolean());
        var peerDm = Assert.Single((await GetAsync(bob, lookup)).GetProperty("items").EnumerateArray());
        Assert.False(peerDm.GetProperty("peer").GetProperty("isBlockedByMe").GetBoolean());
        Assert.Empty((await GetAsync(other, lookup)).GetProperty("items").EnumerateArray());
        using var unblock = await alice.DeleteAsync($"/api/personal/people/blocks/{bobId}");
        Assert.Equal(HttpStatusCode.NoContent, unblock.StatusCode);
        Assert.Empty((await GetAsync(alice, "/api/personal/people/blocks")).GetProperty("items").EnumerateArray());
        Assert.Single((await GetAsync(alice, "/api/personal/people/directory")).GetProperty("items").EnumerateArray());

        var contactLink = (await ProtectedAsync(alice, "/api/personal/contacts/links", new { }, HttpStatusCode.OK)).GetProperty("token").GetString();
        await ProtectedAsync(other, "/api/personal/contacts/links/redeem", new { token = contactLink }, HttpStatusCode.Accepted);
        using var pendingDm = await alice.PostAsJsonAsync("/api/personal/conversations/dm", new { targetUserId = otherId });
        Assert.Equal(HttpStatusCode.NotFound, pendingDm.StatusCode);
        var pending = Assert.Single((await GetAsync(alice, "/api/personal/contacts/requests?limit=1")).GetProperty("items").EnumerateArray());
        await ProtectedAsync(alice, $"/api/personal/contacts/requests/{pending.GetProperty("id").GetGuid()}", new { accept = true }, HttpStatusCode.NoContent, HttpMethod.Put);
        var contacts = await GetAsync(alice, "/api/personal/people/directory?limit=1");
        Assert.Single(contacts.GetProperty("items").EnumerateArray());
        Assert.Single((await GetAsync(alice, $"/api/personal/people/directory?limit=1&after={contacts.GetProperty("nextAfter").GetGuid()}")).GetProperty("items").EnumerateArray());

        using var groupRequest = new HttpRequestMessage(HttpMethod.Post, "/api/personal/conversations/groups")
        {
            Content = JsonContent.Create(new { title = "Personal group", memberUserIds = new[] { bobId } })
        };
        groupRequest.Headers.Add("Idempotency-Key", "personal-group");
        using var groupResponse = await alice.SendAsync(groupRequest);
        Assert.Equal(HttpStatusCode.Created, groupResponse.StatusCode);
        var groupId = (await groupResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(2, (await GetAsync(bob, $"/api/personal/conversations/{groupId}/members")).GetProperty("items").GetArrayLength());

        var org = (await ProtectedAsync(alice, "/api/organizations", new { name = "Scope fixture" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var orgPath = $"/api/organizations/{org}";
        var aliceProfile = await GetAsync(alice, $"{orgPath}/me");
        Assert.True(aliceProfile.GetProperty("isOwner").GetBoolean());
        Assert.False(aliceProfile.GetProperty("isUnverified").GetBoolean());
        using var missingMembership = await bob.GetAsync($"{orgPath}/people/directory");
        Assert.Equal(HttpStatusCode.Forbidden, missingMembership.StatusCode);
        var token = (await ProtectedAsync(alice, $"{orgPath}/invitations", new { }, HttpStatusCode.OK)).GetProperty("token").GetString()!;
        var preview = await ProtectedAsync(bob, "/api/organizations/invitations/preview", new { token }, HttpStatusCode.OK);
        Assert.Equal("Scope fixture", preview.GetProperty("name").GetString());
        Assert.Empty((await GetAsync(bob, "/api/organizations")).GetProperty("items").EnumerateArray());
        var competing = await Task.WhenAll(RedeemAsync(bob, token), RedeemAsync(other, token));
        Assert.Single(competing, status => status == HttpStatusCode.OK);
        Assert.Single(competing, status => status == HttpStatusCode.NotFound);
        if (competing[0] != HttpStatusCode.OK)
        {
            var bobToken = (await ProtectedAsync(alice, $"{orgPath}/invitations", new { }, HttpStatusCode.OK)).GetProperty("token").GetString()!;
            Assert.Equal(HttpStatusCode.OK, await RedeemAsync(bob, bobToken));
        }
        Assert.Contains((await GetAsync(bob, $"{orgPath}/people/directory")).GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == aliceId);
        using var crossScope = await bob.GetAsync($"{orgPath}/conversations/{conversation}/messages");
        Assert.Equal(HttpStatusCode.Forbidden, crossScope.StatusCode);

        var secondOrg = (await ProtectedAsync(bob, "/api/organizations", new { name = "Independent scope" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var secondPath = $"/api/organizations/{secondOrg}";
        Assert.Equal("Member", (await GetAsync(bob, $"{orgPath}/me")).GetProperty("orgRole").GetString());
        var bobSecondProfile = await GetAsync(bob, $"{secondPath}/me");
        Assert.True(bobSecondProfile.GetProperty("isOwner").GetBoolean());
        Assert.False(bobSecondProfile.GetProperty("isUnverified").GetBoolean());
        var page = await GetAsync(bob, "/api/organizations?limit=1");
        Assert.Single(page.GetProperty("items").EnumerateArray());
        var next = await GetAsync(bob, $"/api/organizations?limit=1&after={page.GetProperty("nextAfter").GetGuid()}");
        Assert.Single(next.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextAfter").ValueKind);
        Assert.DoesNotContain((await GetAsync(bob, $"{secondPath}/people/directory")).GetProperty("items").EnumerateArray(), p => p.GetProperty("id").GetGuid() == aliceId);

        var revokedInvite = await ProtectedAsync(alice, $"{orgPath}/invitations", new { }, HttpStatusCode.OK);
        await ProtectedAsync(alice, $"{orgPath}/invitations/{revokedInvite.GetProperty("id").GetGuid()}", null, HttpStatusCode.NoContent, HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NotFound, await RedeemAsync(other, revokedInvite.GetProperty("token").GetString()!));
        var expiredInvite = await ProtectedAsync(alice, $"{orgPath}/invitations", new { }, HttpStatusCode.OK);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var invitationId = expiredInvite.GetProperty("id").GetGuid();
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.OrganizationInvitations.Where(i => i.Id == invitationId)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.NotFound, await RedeemAsync(other, expiredInvite.GetProperty("token").GetString()!));

        var organizationDm = (await PostAsync(alice, $"{orgPath}/conversations/dm", new { targetUserId = bobId }, HttpStatusCode.OK))
            .GetProperty("id").GetGuid();
        var organizationDmPath = $"{orgPath}/conversations/{organizationDm}";
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(baseUri, $"/hubs/organizations/{org}/messaging"), o => o.Cookies = bobCookies).Build();
        await using var secondHub = new HubConnectionBuilder().WithUrl(new Uri(baseUri, $"/hubs/organizations/{secondOrg}/messaging"), o => o.Cookies = bobCookies).Build();
        var firstScopeDelivery = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondScopeDeliveries = new ConcurrentQueue<Guid>();
        var secondScopeDelivery = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstSubscription = hub.On<JsonElement>("ConversationChanged", payload =>
            firstScopeDelivery.TrySetResult(payload.GetProperty("conversationId").GetGuid()));
        using var secondSubscription = secondHub.On<JsonElement>("ConversationChanged", payload =>
        {
            var id = payload.GetProperty("conversationId").GetGuid();
            secondScopeDeliveries.Enqueue(id);
            if (id != organizationDm) secondScopeDelivery.TrySetResult(id);
        });
        await hub.StartAsync();
        await secondHub.StartAsync();
        await PostAsync(alice, $"{organizationDmPath}/messages", new { body = "Organization fixture", clientMsgId = "organization-first" }, HttpStatusCode.Created);
        Assert.Equal(organizationDm, await firstScopeDelivery.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var unread = Assert.Single((await GetAsync(bob, $"{orgPath}/conversations")).GetProperty("items").EnumerateArray());
        Assert.Equal(1, unread.GetProperty("unreadCount").GetInt64());
        Assert.Empty((await GetAsync(bob, $"{secondPath}/conversations")).GetProperty("items").EnumerateArray());
        Assert.Single((await GetAsync(bob, $"{orgPath}/sync/reconcile")).GetProperty("items").EnumerateArray());
        Assert.Empty((await GetAsync(bob, $"{secondPath}/sync/reconcile")).GetProperty("items").EnumerateArray());
        using var crossOrgSync = await bob.GetAsync($"{secondPath}/conversations/{organizationDm}/changes");
        Assert.Equal(HttpStatusCode.Forbidden, crossOrgSync.StatusCode);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await ProtectedAsync(alice, $"{orgPath}/members/{bobId}", null, HttpStatusCode.NoContent, HttpMethod.Delete);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var revoked = await bob.GetAsync($"{orgPath}/people/directory");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        using var revokedSync = await bob.GetAsync($"{orgPath}/sync/reconcile");
        Assert.Equal(HttpStatusCode.Forbidden, revokedSync.StatusCode);
        Assert.Equal(HubConnectionState.Connected, secondHub.State);
        using var secondGroupRequest = new HttpRequestMessage(HttpMethod.Post, $"{secondPath}/conversations/groups")
        {
            Content = JsonContent.Create(new { title = "Second Organization group" })
        };
        secondGroupRequest.Headers.Add("Idempotency-Key", "second-org-group");
        using var secondGroupResponse = await bob.SendAsync(secondGroupRequest);
        Assert.Equal(HttpStatusCode.Created, secondGroupResponse.StatusCode);
        var secondGroupId = (await secondGroupResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(secondGroupId, await secondScopeDelivery.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(organizationDm, secondScopeDeliveries);
        Assert.True((await GetAsync(bob, $"{secondPath}/me")).GetProperty("isOwner").GetBoolean());
        Assert.Single((await GetAsync(bob, $"{personalPath}/messages")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task UnifiedPersonalChatAllowsSharedWorkspaceMembersAndPreservesHistoryAfterRevocation()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusOrganizations:AllowCreation=true", "--NexusSecurity:RateLimitPermitsPerMinute=240",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], additionalLogSink: new ErrorSink(output));
        await app.StartAsync();
        var baseUri = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        var suffix = Guid.NewGuid().ToString("N")[..6];
        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out var bobCookies);
        await RegisterAsync(alice, "unialice_" + suffix);
        var bobId = await RegisterAsync(bob, "unibob_" + suffix);

        // Before sharing a workspace or becoming contacts, bob is not a chat candidate and DM is denied.
        Assert.Empty((await GetAsync(alice, "/api/personal/people/chat-candidates")).GetProperty("items").EnumerateArray());
        using var deniedDm = await alice.PostAsJsonAsync("/api/personal/conversations/dm", new { targetUserId = bobId });
        Assert.Equal(HttpStatusCode.NotFound, deniedDm.StatusCode);

        // Alice creates a workspace and invites Bob (no personal contact request).
        var org = (await ProtectedAsync(alice, "/api/organizations", new { name = "Unified Workspace" }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var orgPath = $"/api/organizations/{org}";
        var token = (await ProtectedAsync(alice, $"{orgPath}/invitations", new { }, HttpStatusCode.OK)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, await RedeemAsync(bob, token));

        // Bob now appears in Alice's unified chat candidates with the shared workspace name.
        var candidate = Assert.Single((await GetAsync(alice, "/api/personal/people/chat-candidates")).GetProperty("items").EnumerateArray());
        Assert.Equal(bobId, candidate.GetProperty("id").GetGuid());
        Assert.False(candidate.GetProperty("isContact").GetBoolean());
        Assert.Contains("Unified Workspace", candidate.GetProperty("workspaceNames").EnumerateArray().Select(e => e.GetString()));

        // Alice can open a unified personal DM directly with Bob without a friend request.
        var dm = await PostAsync(alice, "/api/personal/conversations/dm", new { targetUserId = bobId }, HttpStatusCode.OK);
        var dmId = dm.GetProperty("id").GetGuid();
        await PostAsync(alice, $"/api/personal/conversations/{dmId}/messages", new { body = "Shared workspace DM", clientMsgId = "uni-msg-1" }, HttpStatusCode.Created);
        Assert.Single((await GetAsync(bob, $"/api/personal/conversations/{dmId}/messages")).GetProperty("items").EnumerateArray());

        // Bob connects to the single unified personal hub and receives OrganizationAccessRevoked when removed.
        await using var personalHub = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "/hubs/personal/messaging"), o => o.Cookies = bobCookies)
            .Build();
        var revokedOrg = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var revokedSub = personalHub.On<JsonElement>("OrganizationAccessRevoked", payload =>
            revokedOrg.TrySetResult(payload.GetProperty("organizationId").GetGuid()));
        await personalHub.StartAsync();

        await ProtectedAsync(alice, $"{orgPath}/members/{bobId}", null, HttpStatusCode.NoContent, HttpMethod.Delete);
        Assert.Equal(org, await revokedOrg.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // Workspace directory and communities are immediately revoked, while unified 1-on-1 DM history persists.
        using var revokedDir = await bob.GetAsync($"{orgPath}/people/directory");
        Assert.Equal(HttpStatusCode.Forbidden, revokedDir.StatusCode);
        using var revokedComm = await bob.GetAsync($"{orgPath}/communities");
        Assert.Equal(HttpStatusCode.Forbidden, revokedComm.StatusCode);
        Assert.Single((await GetAsync(bob, $"/api/personal/conversations/{dmId}/messages")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task PeopleCommunitiesMembersAndFeedsPagePastFiftyAcrossTwoOrganizations()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=600", $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], additionalLogSink: new ErrorSink(output));
        await app.StartAsync();
        var baseUri = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);
        var aliceId = await RegisterAsync(alice, "p23alice_" + suffix);
        var bobId = await RegisterAsync(bob, "p23bob_" + suffix);

        var orgAlphaId = (await ProtectedAsync(alice, "/api/organizations", new { name = "Alpha Org " + suffix }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var orgBetaId = (await ProtectedAsync(alice, "/api/organizations", new { name = "Beta Org " + suffix }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var alphaInvite = (await ProtectedAsync(alice, $"/api/organizations/{orgAlphaId}/invitations", new { }, HttpStatusCode.OK)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, await RedeemAsync(bob, alphaInvite));

        var alphaPath = $"/api/organizations/{orgAlphaId}";
        var betaPath = $"/api/organizations/{orgBetaId}";
        var now = new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

        Guid alphaDeptId;
        Guid alphaCommunityId;
        Guid alphaMainFeedId;
        Guid groupConversationId;
        Guid[] alphaUserIds;
        Guid[] betaUserIds;
        Guid[] contactUserIds;
        Guid[] alphaCommunityIds;
        Guid[] betaDiscoverCommunityIds;
        Guid[] pinnedPostIds;
        Guid[] regularPostIds;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

            // Seed 52 departments in Alpha to verify department pagination too.
            var departments = Enumerable.Range(1, 52).Select(i => new DepartmentEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgAlphaId,
                Code = $"D{i:000}_{suffix}",
                Name = i is >= 49 and <= 51 ? $"Tied Dept {suffix}" : $"Dept {i:000} {suffix}",
                CreatedAt = now
            }).ToArray();
            alphaDeptId = departments[0].Id;
            db.Departments.AddRange(departments);

            // Seed 52 additional active members in Alpha (with tied display names across the 50 boundary) and 52 in Beta.
            var alphaUsers = Enumerable.Range(1, 52).Select(i =>
            {
                var uname = $"a_{suffix}_{i:000}";
                return new NexusUser
                {
                    Id = Guid.NewGuid(),
                    UserName = uname,
                    NormalizedUserName = uname.ToUpperInvariant(),
                    DisplayName = i is >= 48 and <= 51 ? $"Alpha Tie {suffix}" : $"Alpha Person {i:000} {suffix}",
                    Status = AccountStatus.Active,
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    CreatedAt = now
                };
            }).ToArray();
            var betaUsers = Enumerable.Range(1, 52).Select(i =>
            {
                var uname = $"b_{suffix}_{i:000}";
                return new NexusUser
                {
                    Id = Guid.NewGuid(),
                    UserName = uname,
                    NormalizedUserName = uname.ToUpperInvariant(),
                    DisplayName = $"Beta Person {i:000} {suffix}",
                    Status = AccountStatus.Active,
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    CreatedAt = now
                };
            }).ToArray();
            // Plus 52 accepted external contacts for Alice to test personal contacts directory paging and case-insensitive filter.
            var contactUsers = Enumerable.Range(1, 52).Select(i =>
            {
                var uname = $"c_{suffix}_{i:000}";
                return new NexusUser
                {
                    Id = Guid.NewGuid(),
                    UserName = uname,
                    NormalizedUserName = uname.ToUpperInvariant(),
                    DisplayName = i == 52 ? $"Zeta Contact Target {suffix}" : $"Contact User {i:000} {suffix}",
                    Status = AccountStatus.Active,
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    CreatedAt = now
                };
            }).ToArray();

            alphaUserIds = alphaUsers.Select(u => u.Id).ToArray();
            betaUserIds = betaUsers.Select(u => u.Id).ToArray();
            contactUserIds = contactUsers.Select(u => u.Id).ToArray();

            db.Users.AddRange(alphaUsers);
            db.Users.AddRange(betaUsers);
            db.Users.AddRange(contactUsers);

            db.OrganizationMemberships.AddRange(alphaUsers.Select((u, idx) =>
            {
                var m = OrganizationMembership.Create(orgAlphaId, u, now);
                if (idx < 30) m.DepartmentId = alphaDeptId;
                if (idx == 50) m.TrustLevel = TrustLevel.Verified;
                return m;
            }));
            await db.OrganizationMemberships
                .Where(m => m.OrganizationId == orgAlphaId && (m.Id == aliceId || m.Id == bobId))
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.TrustLevel, TrustLevel.Verified));
            db.OrganizationMemberships.AddRange(betaUsers.Select(u => OrganizationMembership.Create(orgBetaId, u, now)));
            db.PersonalConnections.AddRange(contactUsers.Select(u =>
            {
                var first = aliceId.CompareTo(u.Id) < 0 ? aliceId : u.Id;
                var second = first == aliceId ? u.Id : aliceId;
                return new PersonalConnection
                {
                    Id = Guid.NewGuid(),
                    FirstUserId = first,
                    SecondUserId = second,
                    RequestedBy = aliceId,
                    AcceptedAt = now
                };
            }));

            // Seed 52 member communities in Alpha (including 2 official and tied names) + 52 discoverable communities in Beta.
            var alphaComms = Enumerable.Range(1, 52).Select(i => new CommunityEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgAlphaId,
                Name = i is >= 49 and <= 51 ? $"Tied Community {suffix}" : $"Alpha Community {i:000} {suffix}",
                Description = i == 52 ? $"Late match {suffix}" : null,
                Visibility = Visibility.Public,
                IsOfficial = i <= 2,
                CreatedByUserId = aliceId,
                CreatedAt = now
            }).ToArray();
            alphaCommunityIds = alphaComms.Select(c => c.Id).ToArray();
            alphaCommunityId = alphaComms[0].Id;

            var alphaMainFeeds = alphaComms.Select(c => new ConversationEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgAlphaId,
                CommunityId = c.Id,
                IsCommunityMainFeed = true,
                Kind = ConversationKind.Channel,
                Title = c.Name,
                CreatedByUserId = aliceId,
                CreatedAt = now,
                UpdatedAt = now
            }).ToArray();
            alphaMainFeedId = alphaMainFeeds[0].Id;

            var betaComms = Enumerable.Range(1, 52).Select(i => new CommunityEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgBetaId,
                Name = i == 52 ? $"Zeta Discover Match {suffix}" : $"Beta Discover {i:000} {suffix}",
                Description = $"Discoverable in Beta {i:000}",
                Visibility = Visibility.Public,
                IsOfficial = false,
                CreatedByUserId = betaUsers[0].Id,
                CreatedAt = now
            }).ToArray();
            betaDiscoverCommunityIds = betaComms.Select(c => c.Id).ToArray();

            var betaMainFeeds = betaComms.Select(c => new ConversationEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgBetaId,
                CommunityId = c.Id,
                IsCommunityMainFeed = true,
                Kind = ConversationKind.Channel,
                Title = c.Name,
                CreatedByUserId = betaUsers[0].Id,
                CreatedAt = now,
                UpdatedAt = now
            }).ToArray();

            db.Communities.AddRange(alphaComms);
            db.Communities.AddRange(betaComms);
            db.Conversations.AddRange(alphaMainFeeds);
            db.Conversations.AddRange(betaMainFeeds);

            db.CommunityMembers.AddRange(alphaComms.SelectMany(c => new[]
            {
                new CommunityMemberEntity { CommunityId = c.Id, UserId = aliceId, Role = ResourceRole.Owner, JoinedAt = now },
                new CommunityMemberEntity { CommunityId = c.Id, UserId = bobId, Role = ResourceRole.Member, JoinedAt = now }
            }));
            // Add 50 Alpha users to alphaCommunityId (total 52 members including Alice and Bob, leaving 2 Alpha users eligible to add).
            db.CommunityMembers.AddRange(alphaUsers.Take(50).Select(u => new CommunityMemberEntity
            {
                CommunityId = alphaCommunityId,
                UserId = u.Id,
                Role = ResourceRole.Member,
                JoinedAt = now
            }));
            db.CommunityMembers.AddRange(betaComms.Select(c => new CommunityMemberEntity
            {
                CommunityId = c.Id,
                UserId = betaUsers[0].Id,
                Role = ResourceRole.Owner,
                JoinedAt = now
            }));

            // Seed a personal Group Chat with 52 members (Alice Owner + Bob Member + 50 Alpha users).
            var group = new ConversationEntity
            {
                Id = Guid.NewGuid(),
                Kind = ConversationKind.Group,
                Title = "Large Group " + suffix,
                CreatedByUserId = aliceId,
                CreatedAt = now,
                UpdatedAt = now
            };
            groupConversationId = group.Id;
            db.Conversations.Add(group);
            db.ConversationMembers.Add(new ConversationMemberEntity { ConversationId = group.Id, UserId = aliceId, Role = ResourceRole.Owner, JoinedAt = now });
            db.ConversationMembers.Add(new ConversationMemberEntity { ConversationId = group.Id, UserId = bobId, Role = ResourceRole.Member, JoinedAt = now });
            db.ConversationMembers.AddRange(alphaUsers.Take(50).Select(u => new ConversationMemberEntity
            {
                ConversationId = group.Id,
                UserId = u.Id,
                Role = ResourceRole.Member,
                JoinedAt = now
            }));

            // Seed 3 pinned posts (with tied timestamps) and 52 regular posts (with tied timestamps across the page 50 boundary).
            var pinnedPosts = Enumerable.Range(1, 3).Select(i => new ChannelPostEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgAlphaId,
                ConversationId = alphaMainFeedId,
                AuthorUserId = aliceId,
                Body = $"Pinned post {i}",
                IsPinned = true,
                PinnedAt = now,
                PinnedByUserId = aliceId,
                CreatedAt = now.AddMinutes(-100),
                UpdatedAt = now.AddMinutes(-100)
            }).ToArray();
            var regularPosts = Enumerable.Range(1, 52).Select(i => new ChannelPostEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgAlphaId,
                ConversationId = alphaMainFeedId,
                AuthorUserId = aliceId,
                Body = $"Regular post {i:000}",
                IsPinned = false,
                CreatedAt = i is >= 46 and <= 49 ? now.AddMinutes(-50) : now.AddMinutes(-i),
                UpdatedAt = i is >= 46 and <= 49 ? now.AddMinutes(-50) : now.AddMinutes(-i)
            }).ToArray();
            pinnedPostIds = pinnedPosts.Select(p => p.Id).ToArray();
            regularPostIds = regularPosts.Select(p => p.Id).ToArray();
            db.ChannelPosts.AddRange(pinnedPosts);
            db.ChannelPosts.AddRange(regularPosts);

            await db.SaveChangesAsync();
        }

        // 1. Organization Directory paging (>50 with tied names, cross-org isolation, department filter, excludeCommunityId, invalid cursor).
        var alphaDirIds = await CollectCursorIdsAsync(alice, $"{alphaPath}/people/directory?limit=50", "cursor", "nextCursor", "id");
        Assert.Equal(54, alphaDirIds.Count); // Alice + Bob + 52 Alpha users
        Assert.Equal(54, alphaDirIds.Distinct().Count());
        Assert.DoesNotContain(betaUserIds[0], alphaDirIds);

        var deptDirIds = await CollectCursorIdsAsync(alice, $"{alphaPath}/people/directory?limit=50&departmentId={alphaDeptId}", "cursor", "nextCursor", "id");
        Assert.Equal(30, deptDirIds.Count);

        // Official community excludeCommunityId enforces Verified eligibility (SEC-20b) while non-official pages >50.
        var officialAddableIds = await CollectCursorIdsAsync(alice, $"{alphaPath}/people/directory?limit=50&excludeCommunityId={alphaCommunityId}", "cursor", "nextCursor", "id");
        Assert.Equal([alphaUserIds[50]], officialAddableIds);
        var standardAddableIds = await CollectCursorIdsAsync(alice, $"{alphaPath}/people/directory?limit=50&excludeCommunityId={alphaCommunityIds[2]}", "cursor", "nextCursor", "id");
        Assert.Equal(52, standardAddableIds.Count);
        Assert.Equal(52, standardAddableIds.Distinct().Count());

        // Non-manager Bob cannot use excludeCommunityId.
        using (var forbiddenExclude = await bob.GetAsync($"{alphaPath}/people/directory?excludeCommunityId={alphaCommunityId}"))
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenExclude.StatusCode);

        using (var invalidDirCursor = await alice.GetAsync($"{alphaPath}/people/directory?cursor=not-valid-base64!"))
            Assert.Equal(HttpStatusCode.BadRequest, invalidDirCursor.StatusCode);

        // 2. Departments paging (>50 with tied names).
        var deptIds = await CollectCursorIdsAsync(alice, $"{alphaPath}/org/departments?limit=50", "cursor", "nextCursor", "id");
        Assert.Equal(52, deptIds.Count);
        Assert.Equal(52, deptIds.Distinct().Count());

        // 3. Personal Contacts directory paging (after) + case-insensitive search, and unified chat-candidates paging (>50 across 2 orgs + contacts).
        var contactIds = await CollectCursorIdsAsync(alice, "/api/personal/people/directory?limit=50", "after", "nextAfter", "id");
        Assert.Equal(52, contactIds.Count);
        Assert.Equal(52, contactIds.Distinct().Count());
        var filteredContact = await GetAsync(alice, $"/api/personal/people/directory?limit=50&q={Uri.EscapeDataString("zeta contact target")}");
        Assert.Equal(contactUserIds[51], Assert.Single(filteredContact.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        var candidateIds = await CollectCursorIdsAsync(alice, "/api/personal/people/chat-candidates?limit=50", "cursor", "nextCursor", "id");
        Assert.Equal(157, candidateIds.Count); // Bob + 52 Alpha + 52 Beta + 52 Contacts
        Assert.Equal(157, candidateIds.Distinct().Count());

        var groupAddableIds = await CollectCursorIdsAsync(alice, $"/api/personal/people/chat-candidates?limit=50&excludeConversationId={groupConversationId}", "cursor", "nextCursor", "id");
        Assert.Equal(106, groupAddableIds.Count); // 2 remaining Alpha + 52 Beta + 52 Contacts
        using (var forbiddenGroupExclude = await bob.GetAsync($"/api/personal/people/chat-candidates?excludeConversationId={groupConversationId}"))
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenGroupExclude.StatusCode);

        // 4. Communities & Discover paging across 2 orgs (official-first + tied names + server search).
        var memberCommIds = await CollectCursorIdsAsync(alice, $"{alphaPath}/communities?limit=50", "cursor", "nextCursor", "id");
        Assert.Equal(52, memberCommIds.Count);
        Assert.Equal(52, memberCommIds.Distinct().Count());
        var firstCommPage = await GetAsync(alice, $"{alphaPath}/communities?limit=50");
        var firstTwoOfficial = firstCommPage.GetProperty("items").EnumerateArray().Take(2).Select(x => x.GetProperty("isOfficial").GetBoolean()).ToArray();
        Assert.All(firstTwoOfficial, Assert.True);

        var discoverIds = await CollectCursorIdsAsync(alice, $"{betaPath}/communities?discover=true&limit=50", "cursor", "nextCursor", "id");
        Assert.Equal(52, discoverIds.Count);
        Assert.Equal(52, discoverIds.Distinct().Count());
        var discoverMatch = await GetAsync(alice, $"{betaPath}/communities?discover=true&limit=50&q={Uri.EscapeDataString("Zeta Discover Match")}");
        Assert.Equal(betaDiscoverCommunityIds[51], Assert.Single(discoverMatch.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        // 5. Community members & Group members paging (>50).
        var commMemberIds = await CollectCursorIdsAsync(alice, $"{alphaPath}/communities/{alphaCommunityId}/members?limit=50", "cursor", "nextCursor", "userId");
        Assert.Equal(52, commMemberIds.Count);
        Assert.Equal(52, commMemberIds.Distinct().Count());

        var groupMemberIds = await CollectCursorIdsAsync(alice, $"/api/personal/conversations/{groupConversationId}/members?limit=50", "cursor", "nextCursor", "userId");
        Assert.Equal(52, groupMemberIds.Count);
        Assert.Equal(52, groupMemberIds.Distinct().Count());

        // 6. Channel feed posts paging (>50 with pinned-first order and tied timestamps).
        var postsPage1 = await GetAsync(bob, $"{alphaPath}/channels/{alphaMainFeedId}/posts?limit=50");
        var p1Items = postsPage1.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(50, p1Items.Count);
        Assert.True(postsPage1.GetProperty("hasMore").GetBoolean());
        var postsCursor = postsPage1.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(postsCursor));
        // First 3 posts must be the 3 pinned posts.
        Assert.All(p1Items.Take(3), item => Assert.True(item.GetProperty("isPinned").GetBoolean()));
        Assert.Equal(pinnedPostIds.ToHashSet(), p1Items.Take(3).Select(i => i.GetProperty("id").GetGuid()).ToHashSet());
        Assert.All(p1Items.Skip(3), item => Assert.False(item.GetProperty("isPinned").GetBoolean()));

        var postsPage2 = await GetAsync(bob, $"{alphaPath}/channels/{alphaMainFeedId}/posts?limit=50&cursor={Uri.EscapeDataString(postsCursor!)}");
        var p2Items = postsPage2.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(5, p2Items.Count);
        Assert.False(postsPage2.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, postsPage2.GetProperty("nextCursor").ValueKind);

        var allPostIds = p1Items.Concat(p2Items).Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(55, allPostIds.Count);
        Assert.Equal(55, allPostIds.Distinct().Count());
        Assert.Equal(pinnedPostIds.Concat(regularPostIds).ToHashSet(), allPostIds.ToHashSet());

        // Passing both before and cursor is rejected with 400.
        using (var bothCursors = await alice.GetAsync($"{alphaPath}/channels/{alphaMainFeedId}/posts?before={Uri.EscapeDataString(now.ToString("O"))}&cursor={Uri.EscapeDataString(postsCursor!)}"))
            Assert.Equal(HttpStatusCode.BadRequest, bothCursors.StatusCode);

        // 7. Revocation between pages: Bob fetches page 1 cursors, then Alice revokes Bob from Org Alpha.
        var bobDirPage1 = await GetAsync(bob, $"{alphaPath}/people/directory?limit=50");
        var bobDirCursor = bobDirPage1.GetProperty("nextCursor").GetString()!;
        var bobCommPage1 = await GetAsync(bob, $"{alphaPath}/communities?limit=50");
        var bobCommCursor = bobCommPage1.GetProperty("nextCursor").GetString()!;
        var bobMembersPage1 = await GetAsync(bob, $"{alphaPath}/communities/{alphaCommunityId}/members?limit=50");
        var bobMembersCursor = bobMembersPage1.GetProperty("nextCursor").GetString()!;

        await ProtectedAsync(alice, $"{alphaPath}/members/{bobId}", null, HttpStatusCode.NoContent, HttpMethod.Delete);

        using (var r1 = await bob.GetAsync($"{alphaPath}/people/directory?limit=50&cursor={Uri.EscapeDataString(bobDirCursor)}"))
            Assert.Equal(HttpStatusCode.Forbidden, r1.StatusCode);
        using (var r2 = await bob.GetAsync($"{alphaPath}/communities?limit=50&cursor={Uri.EscapeDataString(bobCommCursor)}"))
            Assert.Equal(HttpStatusCode.Forbidden, r2.StatusCode);
        using (var r3 = await bob.GetAsync($"{alphaPath}/communities/{alphaCommunityId}/members?limit=50&cursor={Uri.EscapeDataString(bobMembersCursor)}"))
            Assert.Equal(HttpStatusCode.Forbidden, r3.StatusCode);
        using (var r4 = await bob.GetAsync($"{alphaPath}/channels/{alphaMainFeedId}/posts?limit=50&cursor={Uri.EscapeDataString(postsCursor!)}"))
            Assert.Equal(HttpStatusCode.Forbidden, r4.StatusCode);

        // Bob's unified chat-candidates no longer exposes Org Alpha members after revocation.
        var bobCandidatesAfterRevoke = await CollectCursorIdsAsync(bob, "/api/personal/people/chat-candidates?limit=50", "cursor", "nextCursor", "id");
        Assert.Empty(bobCandidatesAfterRevoke);
    }

    [Fact]
    public async Task ContentLinksResolveAcrossPersonalAndTwoOrganizationsWithContextAndNonDisclosure()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=600", $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], additionalLogSink: new ErrorSink(output));
        await app.StartAsync();
        var baseUri = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);
        using var eve = Client(baseUri, out _);
        var aliceId = await RegisterAsync(alice, "l25alice_" + suffix);
        var bobId = await RegisterAsync(bob, "l25bob_" + suffix);
        var eveId = await RegisterAsync(eve, "l25eve_" + suffix);

        // Two isolated Organizations: Alpha (Alice + Bob) and Beta (Eve only).
        var orgAlphaId = (await ProtectedAsync(alice, "/api/organizations", new { name = "Link Alpha " + suffix }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var orgBetaId = (await ProtectedAsync(eve, "/api/organizations", new { name = "Link Beta " + suffix }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var alphaInvite = (await ProtectedAsync(alice, $"/api/organizations/{orgAlphaId}/invitations", new { }, HttpStatusCode.OK)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, await RedeemAsync(bob, alphaInvite));

        var alphaPath = $"/api/organizations/{orgAlphaId}";
        var now = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

        Guid groupId;
        Guid oldMsgId;
        Guid hiddenMsgId;
        Guid deletedMsgId;
        Guid visibleReplyId;
        Guid hiddenReplyId;
        Guid hiddenAttachmentId;
        Guid standardCommId;
        Guid standardFeedId;
        Guid oldPostId;
        Guid deletedPostId;
        Guid officialCommId;
        Guid officialFeedId;
        Guid officialPostId;

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

            // Personal group with 80 messages; Bob has VisibleFromSeq = 10 (cannot resolve seq <= 10).
            var group = new ConversationEntity
            {
                Id = Guid.NewGuid(),
                Kind = ConversationKind.Group,
                Title = "Deep Link Group " + suffix,
                CreatedByUserId = aliceId,
                NextSeq = 82,
                LatestMainSeq = 80,
                ChangeSeq = 82,
                CreatedAt = now,
                UpdatedAt = now
            };
            groupId = group.Id;
            db.Conversations.Add(group);
            db.ConversationMembers.Add(new ConversationMemberEntity { ConversationId = group.Id, UserId = aliceId, Role = ResourceRole.Owner, JoinedAt = now, VisibleFromSeq = 0 });
            db.ConversationMembers.Add(new ConversationMemberEntity { ConversationId = group.Id, UserId = bobId, Role = ResourceRole.Member, JoinedAt = now, VisibleFromSeq = 10 });

            var msgs = Enumerable.Range(1, 80).Select(seq => new MessageEntity
            {
                Id = Guid.NewGuid(),
                ConversationId = group.Id,
                Seq = seq,
                SenderId = aliceId,
                ClientMsgId = $"link-msg-{suffix}-{seq}",
                PayloadSha256 = new string('a', 64),
                Body = $"Message #{seq}",
                CreatedAt = now.AddMinutes(-100 + seq),
                DeletedAt = seq == 25 ? now : null
            }).ToArray();
            hiddenMsgId = msgs[4].Id;  // seq = 5 (<= Bob's VisibleFromSeq 10)
            oldMsgId = msgs[19].Id;    // seq = 20 (well outside latest 50!)
            deletedMsgId = msgs[24].Id; // seq = 25 (deleted)
            db.Messages.AddRange(msgs);

            MessageEntity Reply(MessageEntity parent, long seq)
            {
                parent.ReplyCount = 1;
                parent.LastReplyAt = now;
                parent.LastReplySeq = seq;
                return new MessageEntity
                {
                    Id = Guid.NewGuid(), ConversationId = groupId, ParentMessageId = parent.Id,
                    Seq = seq, SenderId = aliceId, ClientMsgId = $"link-reply-{suffix}-{seq}",
                    PayloadSha256 = new string('b', 64), Body = "Thread reply", CreatedAt = now
                };
            }

            var visibleReply = Reply(msgs[19], 81);
            var hiddenReply = Reply(msgs[4], 82);
            visibleReplyId = visibleReply.Id;
            hiddenReplyId = hiddenReply.Id;
            db.Messages.AddRange(visibleReply, hiddenReply);
            hiddenAttachmentId = Guid.NewGuid();
            db.Attachments.Add(new AttachmentEntity
            {
                Id = hiddenAttachmentId, ConversationId = groupId, MessageId = hiddenReplyId, UploaderUserId = aliceId,
                ClientUploadId = "hidden-reply-" + suffix, FileName = "thread.pdf", ContentType = "application/pdf",
                SizeBytes = 100, Sha256Hex = new string('0', 64), ObjectKey = "fixture/thread.pdf", State = AttachmentState.Attached,
                ScanStatus = "clean", CreatedAt = now, AttachedAt = now
            });

            // Standard community in Org Alpha with 75 posts (1 pinned + 74 regular, 1 deleted).
            var stdComm = new CommunityEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgAlphaId,
                Name = "Link Standard " + suffix,
                Visibility = Visibility.Public,
                IsOfficial = false,
                CreatedByUserId = aliceId,
                CreatedAt = now
            };
            standardCommId = stdComm.Id;
            var stdFeed = new ConversationEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgAlphaId,
                CommunityId = stdComm.Id,
                IsCommunityMainFeed = true,
                Kind = ConversationKind.Channel,
                Title = stdComm.Name,
                CreatedByUserId = aliceId,
                CreatedAt = now,
                UpdatedAt = now
            };
            standardFeedId = stdFeed.Id;

            // Official community in Org Alpha (Bob stays Unverified to verify SEC-20b non-disclosure).
            var offComm = new CommunityEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgAlphaId,
                Name = "Link Official " + suffix,
                Visibility = Visibility.Public,
                IsOfficial = true,
                CreatedByUserId = aliceId,
                CreatedAt = now
            };
            officialCommId = offComm.Id;
            var offFeed = new ConversationEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgAlphaId,
                CommunityId = offComm.Id,
                IsCommunityMainFeed = true,
                Kind = ConversationKind.Channel,
                Title = offComm.Name,
                CreatedByUserId = aliceId,
                CreatedAt = now,
                UpdatedAt = now
            };
            officialFeedId = offFeed.Id;

            db.Communities.AddRange(stdComm, offComm);
            db.Conversations.AddRange(stdFeed, offFeed);
            db.CommunityMembers.AddRange(
                new CommunityMemberEntity { CommunityId = stdComm.Id, UserId = aliceId, Role = ResourceRole.Owner, JoinedAt = now },
                new CommunityMemberEntity { CommunityId = stdComm.Id, UserId = bobId, Role = ResourceRole.Member, JoinedAt = now },
                new CommunityMemberEntity { CommunityId = offComm.Id, UserId = aliceId, Role = ResourceRole.Owner, JoinedAt = now },
                new CommunityMemberEntity { CommunityId = offComm.Id, UserId = bobId, Role = ResourceRole.Member, JoinedAt = now }
            );
            await db.OrganizationMemberships
                .Where(m => m.OrganizationId == orgAlphaId && m.Id == aliceId)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.TrustLevel, TrustLevel.Verified));

            var posts = Enumerable.Range(1, 90).Select(i => new ChannelPostEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgAlphaId,
                ConversationId = stdFeed.Id,
                AuthorUserId = aliceId,
                Body = $"Feed post #{i}",
                IsPinned = i == 1,
                PinnedAt = i == 1 ? now : null,
                PinnedByUserId = i == 1 ? aliceId : null,
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now.AddMinutes(-i),
                DeletedAt = i == 56 ? now : null
            }).ToArray();
            oldPostId = posts[54].Id;     // #55 (beyond first page of 50, with 34 older posts below)
            deletedPostId = posts[55].Id; // #56 (deleted)
            db.ChannelPosts.AddRange(posts);

            var offPost = new ChannelPostEntity
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgAlphaId,
                ConversationId = offFeed.Id,
                AuthorUserId = aliceId,
                Body = "Official only post",
                CreatedAt = now,
                UpdatedAt = now
            };
            officialPostId = offPost.Id;
            db.ChannelPosts.Add(offPost);
            await db.SaveChangesAsync();
        }

        var oldMsgToken = ContentLinkCodec.Encode("m", null, groupId, oldMsgId);
        var hiddenMsgToken = ContentLinkCodec.Encode("m", null, groupId, hiddenMsgId);
        var deletedMsgToken = ContentLinkCodec.Encode("m", null, groupId, deletedMsgId);
        var oldPostToken = ContentLinkCodec.Encode("p", orgAlphaId, standardFeedId, oldPostId);
        var deletedPostToken = ContentLinkCodec.Encode("p", orgAlphaId, standardFeedId, deletedPostId);
        var channelToken = ContentLinkCodec.Encode("c", orgAlphaId, standardFeedId);
        var officialPostToken = ContentLinkCodec.Encode("p", orgAlphaId, officialFeedId, officialPostId);

        // 1. Authorized recipient (Bob) resolves older Personal message and loads surrounding context <= 50.
        var resolvedMsg = await GetAsync(bob, $"/api/links/resolve?target={Uri.EscapeDataString(oldMsgToken)}");
        Assert.Equal("message", resolvedMsg.GetProperty("kind").GetString());
        Assert.Equal(groupId, resolvedMsg.GetProperty("conversationId").GetGuid());
        Assert.Equal(oldMsgId, resolvedMsg.GetProperty("messageId").GetGuid());
        Assert.Equal(20L, resolvedMsg.GetProperty("seq").GetInt64());

        var visibleReplyLink = ContentLinkCodec.Encode("m", null, groupId, visibleReplyId);
        var resolvedReply = await GetAsync(bob, $"/api/links/resolve?target={Uri.EscapeDataString(visibleReplyLink)}");
        Assert.Equal(visibleReplyId, resolvedReply.GetProperty("messageId").GetGuid());
        Assert.Equal(oldMsgId, resolvedReply.GetProperty("parentMessageId").GetGuid());
        Assert.Equal(20L, resolvedReply.GetProperty("parentSeq").GetInt64());

        var hiddenReplyLink = ContentLinkCodec.Encode("m", null, groupId, hiddenReplyId);
        using var hiddenReplyResponse = await bob.GetAsync($"/api/links/resolve?target={Uri.EscapeDataString(hiddenReplyLink)}");
        Assert.Equal(HttpStatusCode.NotFound, hiddenReplyResponse.StatusCode);

        var sharedFiles = await GetAsync(bob, $"/api/personal/conversations/{groupId}/shared?tab=files");
        Assert.Empty(sharedFiles.GetProperty("items").EnumerateArray());
        using var download = await bob.GetAsync($"/api/personal/files/{hiddenAttachmentId}/download");
        Assert.Equal(HttpStatusCode.Forbidden, download.StatusCode);

        var aroundMsgPage = await GetAsync(bob, $"/api/personal/conversations/{groupId}/messages?aroundSeq=20&limit=50");
        var aroundMsgs = aroundMsgPage.GetProperty("items").EnumerateArray().ToList();
        Assert.InRange(aroundMsgs.Count, 1, 50);
        Assert.Contains(aroundMsgs, m => m.GetProperty("id").GetGuid() == oldMsgId);
        Assert.All(aroundMsgs, m => Assert.True(m.GetProperty("seq").GetInt64() > 10)); // VisibleFromSeq enforced!
        Assert.True(aroundMsgPage.GetProperty("hasNewer").GetBoolean());
        var nextAfterSeq = aroundMsgPage.GetProperty("nextAfterSeq").GetInt64();
        var newerPage = await GetAsync(bob, $"/api/personal/conversations/{groupId}/messages?afterSeq={nextAfterSeq}&limit=50");
        Assert.NotEmpty(newerPage.GetProperty("items").EnumerateArray());

        // 2. Authorized recipient (Bob) resolves older Org Alpha post & channel and loads surrounding feed context <= 50.
        var resolvedPost = await GetAsync(bob, $"/api/links/resolve?target={Uri.EscapeDataString(oldPostToken)}");
        Assert.Equal("post", resolvedPost.GetProperty("kind").GetString());
        Assert.Equal(orgAlphaId, resolvedPost.GetProperty("organizationId").GetGuid());
        Assert.Equal(standardCommId, resolvedPost.GetProperty("communityId").GetGuid());
        Assert.Equal(standardFeedId, resolvedPost.GetProperty("conversationId").GetGuid());
        Assert.Equal(oldPostId, resolvedPost.GetProperty("postId").GetGuid());

        var aroundPostPage = await GetAsync(bob, $"{alphaPath}/channels/{standardFeedId}/posts?aroundPostId={oldPostId}&limit=50");
        var aroundPosts = aroundPostPage.GetProperty("items").EnumerateArray().ToList();
        Assert.InRange(aroundPosts.Count, 1, 50);
        Assert.Contains(aroundPosts, p => p.GetProperty("id").GetGuid() == oldPostId);
        Assert.True(aroundPostPage.GetProperty("hasNewer").GetBoolean());
        Assert.True(aroundPostPage.GetProperty("hasMore").GetBoolean());

        var resolvedChannel = await GetAsync(bob, $"/api/links/resolve?target={Uri.EscapeDataString(channelToken)}");
        Assert.Equal("channel", resolvedChannel.GetProperty("kind").GetString());
        Assert.Equal(standardCommId, resolvedChannel.GetProperty("communityId").GetGuid());

        // 3. Non-disclosing 404 for malformed, deleted, before VisibleFromSeq, SEC-20b Unverified Official, and cross-org Eve.
        async Task AssertLinkUnavailableAsync(HttpClient client, string token)
        {
            using var res = await client.GetAsync($"/api/links/resolve?target={Uri.EscapeDataString(token)}");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("link_unavailable", body.GetProperty("code").GetString());
        }

        await AssertLinkUnavailableAsync(bob, "not-valid-token!!!");
        await AssertLinkUnavailableAsync(bob, hiddenMsgToken);    // SEC-18 before VisibleFromSeq
        await AssertLinkUnavailableAsync(bob, deletedMsgToken);   // deleted message
        await AssertLinkUnavailableAsync(bob, deletedPostToken);  // deleted post
        await AssertLinkUnavailableAsync(bob, officialPostToken); // SEC-20b Unverified in Official community
        await AssertLinkUnavailableAsync(eve, oldMsgToken);       // outsider in Org Beta
        await AssertLinkUnavailableAsync(eve, oldPostToken);      // cross-org outsider
        await AssertLinkUnavailableAsync(eve, channelToken);      // cross-org outsider

        // Verified Alice CAN resolve the Official community post.
        var aliceOfficial = await GetAsync(alice, $"/api/links/resolve?target={Uri.EscapeDataString(officialPostToken)}");
        Assert.Equal(officialPostId, aliceOfficial.GetProperty("postId").GetGuid());
        Assert.Equal(officialCommId, aliceOfficial.GetProperty("communityId").GetGuid());

        // 4. After revoking Bob from Org Alpha, previously valid channel & post links become 404 link_unavailable.
        await ProtectedAsync(alice, $"{alphaPath}/members/{bobId}", null, HttpStatusCode.NoContent, HttpMethod.Delete);
        await AssertLinkUnavailableAsync(bob, oldPostToken);
        await AssertLinkUnavailableAsync(bob, channelToken);
        Assert.NotEqual(Guid.Empty, orgBetaId);
        Assert.NotEqual(Guid.Empty, eveId);
    }

    [Fact]
    public async Task SidebarOrganizationPersistsEnforcesScopePreviewsAndRevocation()
    {
        await using var app = NexusHost.Build([
            "--urls", "http://127.0.0.1:0", "--NexusDatabase:RequireTls=false",
            "--NexusSecurity:RateLimitPermitsPerMinute=600", $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        ], additionalLogSink: new ErrorSink(output));
        await app.StartAsync();
        var baseUri = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        var suffix = Guid.NewGuid().ToString("N")[..6];

        using var alice = Client(baseUri, out _);
        using var bob = Client(baseUri, out _);
        using var eve = Client(baseUri, out _);
        var aliceId = await RegisterAsync(alice, "s26alice_" + suffix);
        var bobId = await RegisterAsync(bob, "s26bob_" + suffix);
        await RegisterAsync(eve, "s26eve_" + suffix);

        var orgId = (await ProtectedAsync(alice, "/api/organizations", new { name = "Sidebar Org " + suffix }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var orgPath = $"/api/organizations/{orgId}";
        var inviteToken = (await ProtectedAsync(alice, $"{orgPath}/invitations", new { }, HttpStatusCode.OK)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, await RedeemAsync(bob, inviteToken));

        async Task<JsonElement> PostIdempotentAsync(HttpClient client, string path, object body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            return await res.Content.ReadFromJsonAsync<JsonElement>();
        }

        // Create two group chats and a community with a post.
        var g1 = (await PostIdempotentAsync(alice, "/api/personal/conversations/groups", new { title = "Priority Chat " + suffix, memberUserIds = new[] { bobId } })).GetProperty("id").GetGuid();
        var g2 = (await PostIdempotentAsync(alice, "/api/personal/conversations/groups", new { title = "Archive Chat " + suffix, memberUserIds = new[] { bobId } })).GetProperty("id").GetGuid();
        await PostAsync(alice, $"/api/personal/conversations/{g1}/messages", new { body = "First visible message", clientMsgId = "s26-m1-" + suffix }, HttpStatusCode.Created);

        // Set Bob's VisibleFromSeq on g2 after message #1 so message #1 never leaks in g2's preview (SEC-18).
        await PostAsync(alice, $"/api/personal/conversations/{g2}/messages", new { body = "Pre-rejoin secret", clientMsgId = "s26-m2-" + suffix }, HttpStatusCode.Created);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.ConversationMembers
                .Where(m => m.ConversationId == g2 && m.UserId == bobId)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.VisibleFromSeq, 1L));
        }

        // 1. Last-message previews honor VisibleFromSeq and unreadOnly filters by live read state.
        var bobConvs = (await GetAsync(bob, "/api/personal/conversations?limit=50")).GetProperty("items").EnumerateArray().ToList();
        var bobG1 = bobConvs.Single(c => c.GetProperty("id").GetGuid() == g1);
        var bobG2 = bobConvs.Single(c => c.GetProperty("id").GetGuid() == g2);
        Assert.Equal("First visible message", bobG1.GetProperty("lastMessagePreview").GetProperty("bodyPreview").GetString());
        Assert.Equal(JsonValueKind.Null, bobG2.GetProperty("lastMessagePreview").ValueKind);

        var unreadBefore = (await GetAsync(bob, "/api/personal/conversations?unreadOnly=true&limit=50")).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(unreadBefore);
        Assert.Equal(g1, unreadBefore[0].GetProperty("id").GetGuid());

        using (var markRead = await bob.PutAsJsonAsync($"/api/personal/conversations/{g1}/read", new { lastReadSeq = 1 }))
            Assert.Equal(HttpStatusCode.OK, markRead.StatusCode);

        var unreadAfter = (await GetAsync(bob, "/api/personal/conversations?unreadOnly=true&limit=50")).GetProperty("items").EnumerateArray().ToList();
        Assert.Empty(unreadAfter);

        // 2. Chat sidebar sections, favorites, reorder, and hiding (preserving access, history, and notification preferences).
        var sec = await ProtectedAsync(bob, "/api/sidebar/sections", new { surface = "chat", name = "Workstreams" }, HttpStatusCode.Created);
        var sectionId = sec.GetProperty("id").GetGuid();

        // Duplicate section name rejected with 409.
        await ProtectedAsync(bob, "/api/sidebar/sections", new { surface = "chat", name = "workstreams" }, HttpStatusCode.Conflict);

        // Rename section.
        var renamed = await ProtectedAsync(bob, $"/api/sidebar/sections/{sectionId}", new { name = "Active Workstreams" }, HttpStatusCode.OK, HttpMethod.Put);
        Assert.Equal("Active Workstreams", renamed.GetProperty("name").GetString());

        // Set notification preference on g2 before hiding it, then hide g2 and favorite g1.
        await ProtectedAsync(bob, $"/api/notifications/conversations/{g2}/preference", new { mode = "mentions" }, HttpStatusCode.NoContent, HttpMethod.Put);
        await ProtectedAsync(bob, $"/api/sidebar/items/{g1}", new { surface = "chat", isFavorite = true, sectionId }, HttpStatusCode.OK, HttpMethod.Put);
        await ProtectedAsync(bob, $"/api/sidebar/items/{g2}", new { surface = "chat", isHidden = true }, HttpStatusCode.OK, HttpMethod.Put);
        await ProtectedAsync(bob, "/api/sidebar/reorder", new { surface = "chat", orderedTargetIds = new[] { g2, g1 } }, HttpStatusCode.NoContent, HttpMethod.Put);

        var bobSidebar = await GetAsync(bob, "/api/sidebar?surface=chat");
        var sbItems = bobSidebar.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, sbItems.Count);
        Assert.Equal(g2, sbItems[0].GetProperty("targetId").GetGuid());
        Assert.True(sbItems[0].GetProperty("isHidden").GetBoolean());
        Assert.Equal(g1, sbItems[1].GetProperty("targetId").GetGuid());
        Assert.True(sbItems[1].GetProperty("isFavorite").GetBoolean());
        Assert.Equal(sectionId, sbItems[1].GetProperty("sectionId").GetGuid());

        // Hiding g2 preserves conversation history and notification preference!
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/personal/conversations/{g2}/messages")).StatusCode);
        var g2Pref = await GetAsync(bob, $"/api/notifications/conversations/{g2}/preference");
        Assert.Equal("mentions", g2Pref.GetProperty("mode").GetString());

        // Unauthorized Eve cannot pin/organize g1.
        using (var eveReq = await ProtectedMessageAsync(eve, $"/api/sidebar/items/{g1}", new { surface = "chat", isFavorite = true }, HttpMethod.Put))
        using (var eveRes = await eve.SendAsync(eveReq))
            Assert.Equal(HttpStatusCode.Forbidden, eveRes.StatusCode);

        // 3. Community sidebar organization, latest post preview (member vs discover), and revocation cleanup.
        var commResp = await PostIdempotentAsync(alice, $"{orgPath}/communities", new { name = "Engineering Hub " + suffix, visibility = "Public" });
        var commId = commResp.GetProperty("id").GetGuid();
        var mainFeedId = commResp.GetProperty("mainFeedConversationId").GetGuid();
        await PostAsync(alice, $"{orgPath}/channels/{mainFeedId}/posts", new { title = "Launch Plan", body = "Ship M6.26" }, HttpStatusCode.Created);

        // Before Bob joins, Discover must NOT leak latestPostPreview.
        var discoverList = (await GetAsync(bob, $"{orgPath}/communities?discover=true&limit=50")).GetProperty("items").EnumerateArray().ToList();
        var discoverComm = discoverList.Single(c => c.GetProperty("id").GetGuid() == commId);
        Assert.Equal(JsonValueKind.Null, discoverComm.GetProperty("latestPostPreview").ValueKind);

        // After Bob joins, member list shows latestPostPreview and Bob can favorite it.
        await PostAsync(bob, $"{orgPath}/communities/{commId}/join", new { }, HttpStatusCode.OK);
        var memberComms = (await GetAsync(bob, $"{orgPath}/communities?limit=50")).GetProperty("items").EnumerateArray().ToList();
        var joinedComm = memberComms.Single(c => c.GetProperty("id").GetGuid() == commId);
        Assert.Equal("Launch Plan", joinedComm.GetProperty("latestPostPreview").GetProperty("bodyPreview").GetString());

        var commSec = await ProtectedAsync(bob, "/api/sidebar/sections", new { surface = "communities", name = "Core Teams" }, HttpStatusCode.Created);
        var commSecId = commSec.GetProperty("id").GetGuid();
        await ProtectedAsync(bob, $"/api/sidebar/items/{commId}", new { surface = "communities", isFavorite = true, sectionId = commSecId }, HttpStatusCode.OK, HttpMethod.Put);

        var commSidebarBefore = await GetAsync(bob, "/api/sidebar?surface=communities");
        Assert.Single(commSidebarBefore.GetProperty("items").EnumerateArray());

        // Removing Bob from g1 and revoking Bob from Org immediately removes items from both sidebars!
        using (var remG1 = await alice.DeleteAsync($"/api/personal/conversations/{g1}/members/{bobId}"))
            Assert.Equal(HttpStatusCode.NoContent, remG1.StatusCode);
        await ProtectedAsync(alice, $"{orgPath}/members/{bobId}", null, HttpStatusCode.NoContent, HttpMethod.Delete);

        var bobSidebarAfter = await GetAsync(bob, "/api/sidebar?surface=chat");
        Assert.Single(bobSidebarAfter.GetProperty("items").EnumerateArray());
        Assert.Equal(g2, bobSidebarAfter.GetProperty("items").EnumerateArray().Single().GetProperty("targetId").GetGuid());

        var commSidebarAfter = await GetAsync(bob, "/api/sidebar?surface=communities");
        Assert.Empty(commSidebarAfter.GetProperty("items").EnumerateArray());

        // Deleting section succeeds and clears cleanly.
        await ProtectedAsync(bob, $"/api/sidebar/sections/{sectionId}", null, HttpStatusCode.NoContent, HttpMethod.Delete);
        Assert.NotEqual(Guid.Empty, aliceId);
    }

    private static async Task<List<Guid>> CollectCursorIdsAsync(
        HttpClient client,
        string basePath,
        string cursorParam,
        string nextProp,
        string idProp)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        var guard = 0;
        do
        {
            Assert.True(++guard <= 20, "Pagination exceeded expected page count.");
            var url = cursor is null
                ? basePath
                : $"{basePath}{(basePath.Contains('?') ? '&' : '?')}{cursorParam}={Uri.EscapeDataString(cursor)}";
            var page = await GetAsync(client, url);
            var items = page.GetProperty("items").EnumerateArray().ToList();
            Assert.InRange(items.Count, 0, PaginationGuard.MaxPageSize);
            ids.AddRange(items.Select(i => i.GetProperty(idProp).GetGuid()));
            cursor = page.TryGetProperty(nextProp, out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        } while (!string.IsNullOrEmpty(cursor));
        return ids;
    }

    private static HttpClient Client(Uri uri, out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        return new HttpClient(new HttpClientHandler { CookieContainer = cookies, CheckCertificateRevocationList = true }) { BaseAddress = uri };
    }

    private static async Task<Guid> RegisterAsync(HttpClient client, string username) =>
        (await PostAsync(client, "/api/auth/register", new { username, displayName = username,
            password = "Chat-scope-password-2026!", termsAccepted = true }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body, HttpStatusCode expected)
    {
        using var res = await client.PostAsJsonAsync(path, body);
        Assert.Equal(expected, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpStatusCode> RedeemAsync(HttpClient client, string token)
    {
        using var request = await ProtectedMessageAsync(client, "/api/organizations/invitations/redeem", new { token }, HttpMethod.Post);
        using var result = await client.SendAsync(request);
        return result.StatusCode;
    }

    private static async Task<JsonElement> ProtectedAsync(HttpClient client, string path, object? body, HttpStatusCode expected, HttpMethod? method = null)
    {
        using var request = await ProtectedMessageAsync(client, path, body, method ?? HttpMethod.Post);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return expected == HttpStatusCode.NoContent || expected == HttpStatusCode.Accepted ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpRequestMessage> ProtectedMessageAsync(HttpClient client, string path, object? body, HttpMethod method)
    {
        var token = (await GetAsync(client, "/api/auth/csrf")).GetProperty("token").GetString();
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return request;
    }

    private sealed class ErrorSink(ITestOutputHelper output) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Exception is not null) output.WriteLine(logEvent.Exception.ToString());
        }
    }

    private sealed class RecordingPushHandler : HttpMessageHandler
    {
        public System.Threading.Channels.Channel<RecordedPush> Requests { get; } = System.Threading.Channels.Channel.CreateUnbounded<RecordedPush>();
        public HttpStatusCode ResponseCode { get; set; } = HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = ResponseCode;
            await Requests.Writer.WriteAsync(new RecordedPush(request.RequestUri!.AbsoluteUri,
                Assert.Single(request.Headers.GetValues("Authorization")), Assert.Single(request.Headers.GetValues("Topic")),
                Assert.Single(request.Headers.GetValues("TTL")), await request.Content!.ReadAsByteArrayAsync(cancellationToken)), cancellationToken);
            return new HttpResponseMessage(status);
        }
    }

    private sealed record RecordedPush(string Endpoint, string Authorization, string Topic, string Ttl, byte[] Body);

    private sealed class RecordingDigestTransport : IEmailDigestTransport
    {
        public bool IsEnabled => true;
        public TimeSpan UnreadAge => TimeSpan.FromHours(1);
        public System.Threading.Channels.Channel<(string Recipient, int Count)> Deliveries { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<(string Recipient, int Count)>();

        public Task SendAsync(string recipient, Guid deliveryId, int unreadCount, CancellationToken ct) =>
            Deliveries.Writer.WriteAsync((recipient, unreadCount), ct).AsTask();
    }
}
