using System.Diagnostics.Metrics;

namespace Nexus.Web.Infrastructure.Observability;

internal static class NexusMeters
{
    public const string Name = "Nexus.Application";
    private static readonly Meter Meter = new(Name);
    private static readonly Counter<long> Messages = Meter.CreateCounter<long>("nexus.messages.created");
    private static readonly Counter<long> Posts = Meter.CreateCounter<long>("nexus.posts.created");
    private static readonly Counter<long> Comments = Meter.CreateCounter<long>("nexus.comments.created");
    private static readonly Counter<long> Reactions = Meter.CreateCounter<long>("nexus.reactions.created");
    private static readonly Counter<long> Uploads = Meter.CreateCounter<long>("nexus.uploads.created");
    private static readonly Histogram<double> Fanout = Meter.CreateHistogram<double>("nexus.messaging.fanout", "ms");

    public static void MessageCreated() => Messages.Add(1);
    public static void PostCreated() => Posts.Add(1);
    public static void CommentCreated() => Comments.Add(1);
    public static void ReactionCreated() => Reactions.Add(1);
    public static void UploadCreated() => Uploads.Add(1);
    public static void RecordFanout(double milliseconds) => Fanout.Record(milliseconds);
}
