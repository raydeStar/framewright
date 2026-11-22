using System.Text.Json;
using System.Threading.Channels;

namespace StoryboardStudio.Api.Services;

/// <summary>Something happened to a library asset: it arrived, a revision of it arrived, or it changed.</summary>
public sealed record AssetChangeEvent(string Type, Guid AssetId, Guid ProjectId, string Kind, Guid? RevisionFamilyId, int? RevisionNumber);

/// <summary>
/// Tells open pages about library changes as they are saved, so a model an
/// agent uploads appears without anyone reloading. Every subscriber gets its
/// own small queue; a page that stops reading loses its oldest events rather
/// than holding up the save that raised them, and the page refreshes the whole
/// list on any event anyway.
/// </summary>
public sealed class AssetEventHub
{
    private readonly Lock gate = new();
    private readonly List<Channel<AssetChangeEvent>> subscribers = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<AssetChangeEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false,
        });
        lock (gate) subscribers.Add(channel);
        return new Subscription(channel.Reader, () =>
        {
            lock (gate) subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
    }

    public void Publish(IEnumerable<AssetChangeEvent> events)
    {
        Channel<AssetChangeEvent>[] targets;
        lock (gate) targets = [.. subscribers];
        foreach (var change in events)
            foreach (var target in targets)
                target.Writer.TryWrite(change);
    }

    public int SubscriberCount { get { lock (gate) return subscribers.Count; } }

    public sealed class Subscription(ChannelReader<AssetChangeEvent> reader, Action release) : IDisposable
    {
        public ChannelReader<AssetChangeEvent> Reader { get; } = reader;
        public void Dispose() => release();
    }
}
