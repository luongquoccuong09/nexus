using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Nexus.Web.Modules.Meetings;

internal sealed class LiveKitProvider(
    IHttpClientFactory clients, IOptions<LiveKitOptions> settings, LiveKitTokens tokens, MediaSignalConnections connections)
    : IMeetingProvider
{
    // Let the provider's explicit leave reason reach the SDK before closing signaling.
    internal static readonly TimeSpan SignalCloseGrace = TimeSpan.FromMilliseconds(500);

    public bool Enabled => settings.Value.Enabled;

    public MeetingCredentials Issue(string roomName, Guid grantId, string displayName) =>
        new(MediaSignalGateway.SignalPath, tokens.Join(roomName, grantId, displayName));

    public async Task RemoveParticipantAsync(string roomName, Guid grantId, CancellationToken ct)
    {
        var removed = false;
        try
        {
            removed = Enabled && await SendAsync("RemoveParticipant", tokens.Remove(roomName),
                new { room = roomName, identity = grantId.ToString("N") }, ct);
        }
        finally
        {
            connections.Disconnect(grantId, removed ? SignalCloseGrace : TimeSpan.Zero);
        }
    }

    public async Task EndRoomAsync(string roomName, CancellationToken ct)
    {
        var ended = false;
        try
        {
            ended = Enabled && await SendAsync("DeleteRoom", tokens.Delete(), new { room = roomName }, ct);
        }
        finally
        {
            connections.DisconnectRoom(roomName, ended ? SignalCloseGrace : TimeSpan.Zero);
        }
    }

    private async Task<bool> SendAsync<T>(string method, string credential, T body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/twirp/livekit.RoomService/{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Content = JsonContent.Create(body);

        using var client = clients.CreateClient(nameof(LiveKitProvider));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    internal static SocketsHttpHandler CreatePrivateTransport() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        SslOptions = new SslClientAuthenticationOptions { CertificateRevocationCheckMode = X509RevocationMode.Online }
    };
}
