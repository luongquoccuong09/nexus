namespace Nexus.Web.Modules.Meetings;

public interface IMeetingProvider
{
    bool Enabled { get; }
    MeetingCredentials Issue(string roomName, Guid grantId, string displayName);
    Task RemoveParticipantAsync(string roomName, Guid grantId, CancellationToken ct);
    Task EndRoomAsync(string roomName, CancellationToken ct);
}

public sealed record MeetingCredentials(string SignalPath, string Token)
{
    public override string ToString() => nameof(MeetingCredentials);
}
