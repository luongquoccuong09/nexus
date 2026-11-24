using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Nexus.Monitoring;

namespace Nexus.LoadRunner;

[SuppressMessage("Security", "CA5394", Justification = "Deterministic workload pacing and synthetic choices require no security entropy.")]
internal sealed class VirtualUser : IAsyncDisposable
{
    private readonly int _index;
    private readonly SandboxTarget _target;
    private readonly RunMeasurements _run;
    private readonly Random _random;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _client;
    private HubConnection? _hub;
    private Guid _latestPost;
    private readonly Dictionary<Guid, long> _syncCursors = new();
    private bool _joined;

    public VirtualUser(int index, SandboxTarget target, RunMeasurements run)
    {
        _index = index;
        _target = target;
        _run = run;
        _random = new Random(unchecked(run.Configuration.Users * 8_191 + index * 131_071));
        _client = RunnerHttp.Client(target.Url, TimeSpan.FromSeconds(8), _cookies);
        _latestPost = target.Fixture.SeedPostId;
    }

    private LoadConfiguration Config => _run.Configuration;
    private string Org => $"/api/organizations/{_target.Fixture.OrganizationId}";
    private string Feed => $"{Org}/channels/{_target.Fixture.ChannelId}/posts";
    private Guid Direct => _target.Fixture.DirectIds[_index];
    private Guid Group => _target.Fixture.GroupId;
    private static string Conversation(Guid id) => $"/api/personal/conversations/{id}";

    public async Task RunAsync(CancellationToken ct)
    {
        var offset = TimeSpan.FromMinutes(Config.RampMinutes).TotalSeconds * _index / Config.Users;
        await Task.Delay(TimeSpan.FromSeconds(offset), ct);
        using var login = await SendAsync("login", HttpMethod.Post, "/api/auth/login",
            new { username = $"sandbox_{_index:D4}", password = _target.Password }, ct);
        if (login is not { IsSuccessStatusCode: true }) return;
        if (Config.SignalR)
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(new Uri(_target.Url, "/hubs/personal/messaging"), options =>
                {
                    options.Cookies = _cookies;
                    options.Transports = HttpTransportType.WebSockets;
                    options.UseDefaultCredentials = false;
                })
                .WithAutomaticReconnect([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)])
                .Build();
            _hub.On<JsonElement>("ConversationChanged", HandleMessage);
            _hub.Reconnecting += error =>
            {
                _run.Connected.TryRemove(_index, out _);
                return Task.CompletedTask;
            };
            _hub.Reconnected += _ =>
            {
                _run.Connected.TryAdd(_index, this);
                _run.Reconnected();
                return Task.CompletedTask;
            };
            _hub.Closed += error =>
            {
                _run.Connected.TryRemove(_index, out _);
                return Task.CompletedTask;
            };
            if (!await ConnectAsync(ct)) return;
        }

        _run.Joined();
        _joined = true;
        var schedule = BuildSchedule();
        while (!ct.IsCancellationRequested)
        {
            var next = schedule.MinBy(item => item.Due);
            if (next is null)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return;
            }
            var now = _run.Elapsed.TotalSeconds;
            if (next.Due > now) await Task.Delay(TimeSpan.FromSeconds(next.Due - now), ct);
            await next.Action(ct);
            next.Due = _run.Elapsed.TotalSeconds + next.Interval * (.8 + _random.NextDouble() * .4);
        }
    }

    private List<PacedOperation> BuildSchedule()
    {
        var operations = new List<PacedOperation>();
        if (Config.DirectMessages || Config.GroupChat)
        {
            Add(Config.MessagesPerMinute, 60, SendMessageAsync);
            if (Config.Uploads) Add(1, 120, UploadAsync);
        }
        Add(Config.ReadsPerMinute, 60, ReadAsync);
        if (Config.CommunityPosts) Add(Config.PostsPerHour, 3600, PostAsync);
        if (Config.Comments) Add(Config.CommentsPerHour, 3600, CommentAsync);
        if (Config.Reactions) Add(2, 60, ReactAsync);
        if (Config.Reconnect || Config.OfflineSync)
        {
            var steadySeconds = (Config.DurationMinutes - Config.RampMinutes) * 60;
            var burst = Config.Reconnect ? Config.RampMinutes * 60 + Math.Min(60, steadySeconds / 2) + _random.NextDouble() * 3 : (double?)null;
            Add(1, 180, ReconnectAsync, burst);
        }
        return operations;

        void Add(int rate, int interval, Func<CancellationToken, Task> action, double? due = null)
        {
            if (rate <= 0) return;
            var seconds = interval / (double)rate;
            operations.Add(new PacedOperation(action, seconds, due ?? _run.Elapsed.TotalSeconds + _random.NextDouble() * seconds));
        }
    }

    private Task SendMessageAsync(CancellationToken ct) =>
        SendMessageAsync(ChooseConversation(), Guid.NewGuid(), null, false, ct);

    private Guid ChooseConversation() => Config.GroupChat && (!Config.DirectMessages || _random.Next(2) == 0) ? Group : Direct;

    private async Task SendMessageAsync(Guid id, Guid key, Guid? attachment, bool replay, CancellationToken ct)
    {
        var operation = id == Group ? "group" : "direct";
        if (Config.Typing && _hub?.State == HubConnectionState.Connected)
        {
            try { await _hub.InvokeAsync("SendTyping", id, ct); }
            catch (Exception ex) when (ex is HubException or InvalidOperationException or HttpRequestException) { }
        }

        var recipients = id == Group
            ? _run.Connected.Keys.Where(i => i != _index)
            : _run.Connected.Keys.Where(i => i == (_index + 1) % _target.Fixture.UserIds.Length);
        _run.Deliveries.Begin(key, id, recipients, _run.Steady);
        var body = new { clientMsgId = key.ToString("N"), body = "Synthetic test message",
            attachmentIds = attachment is { } value ? new[] { value } : null, preserveReadState = true };
        using var response = await SendAsync(operation, HttpMethod.Post, $"{Conversation(id)}/messages", body, ct);
        var message = await ReadJsonAsync<MessageReference>(response, ct);
        _run.Deliveries.End(key, message?.Seq);
        if (!replay || message is null) return;
        using var retry = await SendAsync("sync", HttpMethod.Post, $"{Conversation(id)}/messages", body, ct);
        var existing = await ReadJsonAsync<MessageReference>(retry, ct);
        if (existing is null) _run.SyncFailed();
        else if (existing.Id != message.Id) _run.DuplicateMessage();
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        var useFeed = Config.CommunityPosts && (!(Config.DirectMessages || Config.GroupChat) || _random.Next(4) == 0);
        var path = useFeed ? $"{Feed}?limit=15" : Config.DirectMessages || Config.GroupChat
            ? $"{Conversation(ChooseConversation())}/messages?limit=15" : "/api/personal/conversations?limit=15";
        using var response = await SendAsync("read", HttpMethod.Get, path, null, ct);
    }

    private async Task PostAsync(CancellationToken ct)
    {
        using var response = await SendAsync("post", HttpMethod.Post, Feed,
            new { title = "Sandbox post", body = "Synthetic discussion" }, ct);
        if (await ReadJsonAsync<CreatedReference>(response, ct) is { } post) _latestPost = post.Id;
    }

    private async Task CommentAsync(CancellationToken ct)
    {
        using var response = await SendAsync("comment", HttpMethod.Post, $"{Org}/posts/{_latestPost}/comments",
            new { body = "Synthetic reply" }, ct);
    }

    private async Task ReactAsync(CancellationToken ct)
    {
        var emoji = Uri.EscapeDataString(_random.Next(2) == 0 ? "👍" : "❤️");
        using var response = await SendAsync("reaction", HttpMethod.Put, $"{Org}/posts/{_latestPost}/reactions/{emoji}", null, ct);
    }

    private async Task UploadAsync(CancellationToken ct)
    {
        var id = ChooseConversation();
        // 1x1 synthetic PNG. No local user files are read by the workload.
        using var image = new ByteArrayContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGMwCljwHwAD/AIipj6yyQAAAABJRU5ErkJggg=="));
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var upload = new MultipartFormDataContent();
        upload.Add(image, "file", "sandbox.png");
        upload.Add(new StringContent(Guid.NewGuid().ToString("N")), "clientUploadId");
        using var response = await SendAsync("upload", HttpMethod.Post, $"{Conversation(id)}/attachments", upload, ct);
        if (await ReadJsonAsync<UploadReference>(response, ct) is { } result)
            await SendMessageAsync(id, Guid.NewGuid(), result.Attachment.Id, false, ct);
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        if (_hub is null) return;
        var queued = Guid.NewGuid();
        await _hub.StopAsync(ct);
        await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(400, 1100)), ct);
        if (!await ConnectAsync(ct)) { _run.SyncFailed(); return; }
        _run.Reconnected();
        using var reconciliation = await SendAsync("sync", HttpMethod.Get, "/api/personal/sync/reconcile?limit=50", null, ct);
        if (reconciliation is not { IsSuccessStatusCode: true }) _run.SyncFailed();
        if (!Config.OfflineSync) return;
        var conversation = ChooseConversation();
        var cursor = _syncCursors.GetValueOrDefault(conversation);
        for (var page = 0; page < 10; page++)
        {
            using var result = await SendAsync("sync", HttpMethod.Get, $"{Conversation(conversation)}/changes?limit=50&after={cursor}", null, ct);
            if (result?.StatusCode == HttpStatusCode.Gone)
            {
                using var reload = await SendAsync("sync", HttpMethod.Get, $"{Conversation(conversation)}/messages?limit=50", null, ct);
                if (reload is not { IsSuccessStatusCode: true }) _run.SyncFailed();
                break;
            }
            var delta = await ReadJsonAsync<DeltaPage>(result, ct);
            if (delta is null) { _run.SyncFailed(); break; }
            cursor = delta.NextAfter;
            _syncCursors[conversation] = cursor;
            if (!delta.HasMore) break;
            if (page == 9) _run.SyncFailed();
        }
        await SendMessageAsync(conversation, queued, null, true, ct);
    }

    private async Task<bool> ConnectAsync(CancellationToken ct)
    {
        if (_hub is null) return false;
        var begin = Stopwatch.GetTimestamp();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await _hub.StartAsync(deadline.Token);
            _run.Connected.TryAdd(_index, this);
            if (_run.Steady) _run.Latency("signalr", Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
            return true;
        }
        catch (Exception ex) when (RunnerHttp.IsTransient(ex, ct) ||
            ex is InvalidOperationException or HubException or System.Net.WebSockets.WebSocketException)
        {
            return false;
        }
    }

    private void HandleMessage(JsonElement change)
    {
        if (change.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("clientMsgId", out var key) && Guid.TryParseExact(key.GetString(), "N", out var parsed))
            _run.Deliveries.Receive(parsed, _index);
    }

    public async Task<bool> HasMessageAsync(Guid conversationId, long sequence, Guid key, CancellationToken ct)
    {
        using var response = await SendAsync("sync", HttpMethod.Get,
            $"{Conversation(conversationId)}/messages?afterSeq={sequence - 1}&limit=1", null, ct);
        var page = await ReadJsonAsync<MessagePage>(response, ct);
        return page?.Items.Any(item => item.Seq == sequence && item.ClientMsgId == key.ToString("N")) == true;
    }

    private async Task<HttpResponseMessage?> SendAsync(string operation, HttpMethod method, string path, object? data, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Content = data switch { HttpContent content => content, null => null, _ => JsonContent.Create(data) };
        var begin = Stopwatch.GetTimestamp();
        var steady = _run.Steady;
        try
        {
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            _run.Request(operation, response.StatusCode, Stopwatch.GetElapsedTime(begin).TotalMilliseconds, steady);
            return response;
        }
        catch (Exception ex) when (RunnerHttp.IsTransient(ex, ct))
        {
            _run.Request(operation, null, Stopwatch.GetElapsedTime(begin).TotalMilliseconds, steady);
            return null;
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage? response, CancellationToken ct)
    {
        if (response is not { IsSuccessStatusCode: true }) return default;
        try { return await response.Content.ReadFromJsonAsync<T>(ct); }
        catch (JsonException) { return default; }
    }

    public async ValueTask DisposeAsync()
    {
        _run.Connected.TryRemove(_index, out _);
        if (_joined) _run.Left();
        if (_hub is not null) await _hub.DisposeAsync();
        _client.Dispose();
    }

    private sealed record CreatedReference(Guid Id);
    private sealed record UploadReference(CreatedReference Attachment);
    private sealed record MessageReference(Guid Id, long Seq, string ClientMsgId);
    private sealed record MessagePage(MessageReference[] Items);
    private sealed record DeltaPage(long NextAfter, bool HasMore);
    private sealed record PacedOperation(Func<CancellationToken, Task> Action, double Interval, double Initial)
    {
        public double Due { get; set; } = Initial;
    }
}
