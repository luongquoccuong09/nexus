using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Nexus.Web.Modules.Meetings;

internal sealed class LiveKitSignalTransport : HttpMessageInvoker
{
    public LiveKitSignalTransport() : base(LiveKitProvider.CreatePrivateTransport()) { }
}

internal static class MediaSignalGateway
{
    internal const string SignalPath = "/api/media";
    private const string TokenQueryKey = "access_token";

    internal static WebApplicationBuilder AddLiveKitProvider(this WebApplicationBuilder builder)
    {
        var development = builder.Environment.IsDevelopment();
        builder.Services.AddOptions<LiveKitOptions>().BindConfiguration(LiveKitOptions.SectionName)
            .Validate(options => options.IsValid(development), "LiveKit requires a private HTTPS origin and a configured API key and secret.")
            .ValidateOnStart();
        builder.Services.AddSingleton<LiveKitTokens>();
        builder.Services.AddSingleton<MediaSignalConnections>();
        builder.Services.AddSingleton<LiveKitSignalTransport>();
        builder.Services.AddScoped<IMeetingProvider, LiveKitProvider>();
        builder.Services.AddHttpClient(nameof(LiveKitProvider), (services, client) =>
        {
            var options = services.GetRequiredService<IOptions<LiveKitOptions>>().Value;
            if (options.Enabled) client.BaseAddress = options.ServerOrigin;
            client.Timeout = TimeSpan.FromSeconds(8);
        }).ConfigurePrimaryHttpMessageHandler(LiveKitProvider.CreatePrivateTransport).RemoveAllLoggers();
        return builder;
    }

    internal static void MapMediaSignalGateway(this IEndpointRouteBuilder app)
    {
        var signal = app.MapGroup(SignalPath).RequireAuthorization();
        signal.MapGet("/rtc", ProxyAsync);
        signal.MapGet("/rtc/v1", ProxyAsync);
        signal.MapGet("/rtc/validate", ValidateAsync);
        signal.MapGet("/rtc/v1/validate", ValidateAsync);
    }

    private static async Task<IResult> ValidateAsync(HttpContext context, LiveKitTokens tokens, MeetingsAccess access)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Headers.ContainsKey("Origin") && !IsSameOrigin(context.Request)) return Results.StatusCode(403);
        if (!TryAuthorizeToken(context.Request, tokens, out _, out var grantId, out var roomName))
            return Results.Unauthorized();
        return await access.CanConnectSignalAsync(grantId, roomName, context.User, context.RequestAborted)
            ? Results.Ok()
            : Results.Unauthorized();
    }

    private static async Task ProxyAsync(HttpContext context, LiveKitTokens tokens, MeetingsAccess access,
        IOptions<LiveKitOptions> settings, MediaSignalConnections connections, IMeetingProvider provider,
        LiveKitSignalTransport transport)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!IsSameOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var aborted = context.RequestAborted;
        if (!TryAuthorizeToken(context.Request, tokens, out var token, out var grantId, out var roomName) ||
            !await access.CanConnectSignalAsync(grantId, roomName, context.User, aborted))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        using var upstream = new ClientWebSocket();
        upstream.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        connections.Register(grantId, roomName, lifetime);
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await upstream.ConnectAsync(GetProviderSocket(settings.Value, context.Request), transport, timeout.Token);
            }

            if (!await access.CanConnectSignalAsync(grantId, roomName, context.User, lifetime.Token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await RemoveAsync(provider, roomName, grantId);
                return;
            }

            using var client = await context.WebSockets.AcceptWebSocketAsync();
            var clientToProvider = PumpAsync(client, upstream, lifetime.Token);
            var providerToClient = PumpAsync(upstream, client, lifetime.Token);
            var revalidate = RevalidateAsync(context, access, provider, roomName, grantId, lifetime.Token);
            await Task.WhenAny(clientToProvider, providerToClient, revalidate);
            await lifetime.CancelAsync();
            await Task.WhenAll(clientToProvider, providerToClient, revalidate);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or HttpRequestException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
        finally
        {
            connections.Unregister(grantId, lifetime);
        }
    }

    private static async Task RevalidateAsync(HttpContext context, MeetingsAccess access, IMeetingProvider provider,
        string roomName, Guid grantId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (await access.CanConnectSignalAsync(grantId, roomName, context.User, ct)) continue;
            await RemoveAsync(provider, roomName, grantId);
            await Task.Delay(LiveKitProvider.SignalCloseGrace, ct);
            return;
        }
    }

    private static async Task RemoveAsync(IMeetingProvider provider, string roomName, Guid grantId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await provider.RemoveParticipantAsync(roomName, grantId, timeout.Token); }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { }
    }

    private static bool TryAuthorizeToken(HttpRequest request, LiveKitTokens tokens, out string token,
        out Guid grantId, out string roomName)
    {
        token = string.Empty;
        grantId = Guid.Empty;
        roomName = string.Empty;
        if (!request.Query.TryGetValue(TokenQueryKey, out var values) || values.Count != 1 || values[0] is not { } value)
            return false;
        token = value;
        return tokens.TryReadJoin(token, out grantId, out roomName);
    }

    private static bool IsSameOrigin(HttpRequest request) =>
        request.Headers.Origin is { Count: 1 } origins &&
        Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) && origin.PathAndQuery == "/" &&
        origin.Fragment.Length == 0 && origin.UserInfo.Length == 0 &&
        Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var expected) &&
        string.Equals(origin.GetLeftPart(UriPartial.Authority), expected.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private static Uri GetProviderSocket(LiveKitOptions options, HttpRequest request)
    {
        var path = request.Path.Value![SignalPath.Length..].ToLowerInvariant();
        var parameters = request.Query.Where(pair => !pair.Key.Equals(TokenQueryKey, StringComparison.OrdinalIgnoreCase));
        var builder = new UriBuilder(new Uri(options.ServerOrigin, QueryHelpers.AddQueryString(path, parameters)))
        {
            Scheme = options.ServerOrigin.Scheme == Uri.UriSchemeHttps ? "wss" : "ws"
        };
        return builder.Uri;
    }

    private static async Task PumpAsync(WebSocket source, WebSocket destination, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
            {
                var result = await source.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
