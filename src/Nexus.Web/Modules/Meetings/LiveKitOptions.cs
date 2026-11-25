using System.Text;

namespace Nexus.Web.Modules.Meetings;

internal sealed class LiveKitOptions
{
    internal const string SectionName = "NexusMeetings:LiveKit";

    public bool Enabled { get; set; }
    public string ServerUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;

    internal Uri ServerOrigin => new(ServerUrl, UriKind.Absolute);

    internal bool IsValid(bool development) => !Enabled ||
        (Uri.TryCreate(ServerUrl, UriKind.Absolute, out var server) &&
         (server.Scheme == Uri.UriSchemeHttps || development && server.IsLoopback && server.Scheme == Uri.UriSchemeHttp) &&
         server.PathAndQuery == "/" && server.Fragment.Length == 0 && server.UserInfo.Length == 0 &&
         !string.IsNullOrWhiteSpace(ApiKey) && Encoding.UTF8.GetByteCount(ApiSecret) >= 32);
}
