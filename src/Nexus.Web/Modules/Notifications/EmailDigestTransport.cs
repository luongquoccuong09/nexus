using System.Net;
using System.Net.Mail;

namespace Nexus.Web.Modules.Notifications;

public interface IEmailDigestTransport
{
    bool IsEnabled { get; }
    TimeSpan UnreadAge { get; }
    Task SendAsync(string recipient, Guid deliveryId, int unreadCount, CancellationToken ct);
}

internal sealed class EmailDigestTransport(IConfiguration configuration) : IEmailDigestTransport
{
    private readonly string? _host = configuration["NexusDigest:Host"];
    private readonly string? _from = configuration["NexusDigest:From"];
    private readonly int _port = configuration.GetValue("NexusDigest:Port", 587);
    private readonly string? _username = configuration["NexusDigest:Username"];
    private readonly string? _password = configuration["NexusDigest:Password"];
    private readonly bool _enabled = configuration.GetValue("NexusDigest:Enabled", false);

    public bool IsEnabled => _enabled && !string.IsNullOrWhiteSpace(_host) && MailAddress.TryCreate(_from, out _) && _port is > 0 and <= 65535;
    public TimeSpan UnreadAge { get; } = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("NexusDigest:DelayHours", 4), 1, 168));

    public async Task SendAsync(string recipient, Guid deliveryId, int unreadCount, CancellationToken ct)
    {
        if (!IsEnabled) throw new InvalidOperationException("Email digest transport is unavailable.");

        using var client = new SmtpClient(_host!, _port) { EnableSsl = true, UseDefaultCredentials = false };
        if (!string.IsNullOrWhiteSpace(_username)) client.Credentials = new NetworkCredential(_username, _password);

        using var message = new MailMessage(_from!, recipient)
        {
            Subject = "Unread activity in Nexus",
            Body = $"You have {unreadCount} unread {(unreadCount == 1 ? "item" : "items")} in Nexus. Open Activity to review."
        };
        message.Headers.Add("Message-ID", $"<nexus-digest-{deliveryId:N}@{message.From!.Host}>");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await client.SendMailAsync(message, timeout.Token);
    }
}
