using HuntReplay.Core.Localization;

namespace HuntReplay.Core.Media;

/// <summary>
/// Compressed RAM-only buffer, per-stream FIFO, GOP-aligned eviction. Snapshots share immutable
/// payloads rather than copying megabytes. Leases retain an in-flight sequence across the normal
/// time horizon; the hard byte cap takes precedence and truncation is reported, never hidden.
/// </summary>
public sealed class PacketRingBuffer
{
    private readonly object gate = new();
    private readonly Dictionary<int, LinkedList<EncodedPacket>> streams = new();
    private readonly Dictionary<Guid, long> pins = new();
    private readonly long durationUs;
    private readonly long byteLimit;
    private long bytes;
    private long latestVideoEnd;
    private long evictedGops;
    private bool pressure;

    public PacketRingBuffer(int seconds, long byteLimit)
    {
        if (seconds <= 0 || byteLimit <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        durationUs = seconds * 1_000_000L;
        this.byteLimit = byteLimit;
        streams[0] = new();
    }

    /// <summary>Takes ownership of the packet, including its arena chunk reference, whether it is kept or not.</summary>
    public void Add(EncodedPacket packet)
    {
        bool kept = false;
        try
        {
            if (packet.StreamIndex is < 0 or > 2 || packet.Payload.Length == 0 || packet.DurationUs < 0 ||
                packet.PtsUs < -1_000_000 || packet.DtsUs < -1_000_000 ||
                packet.PtsUs > long.MaxValue - packet.DurationUs)
                throw new InvalidDataException(Text.Get("Buffer.InvalidPacket"));
            lock (gate)
            {
                var video = streams[0];
                if (video.Count == 0 && !(packet.IsVideo && packet.IsKeyFrame)) return;
                if (!streams.TryGetValue(packet.StreamIndex, out var queue)) streams[packet.StreamIndex] = queue = new();
                if (queue.Last is { } last && packet.DtsUs <= last.Value.DtsUs)
                    throw new InvalidDataException(Text.Get("Buffer.DtsBackwards"));
                if (!packet.IsVideo && packet.DtsUs < video.First!.Value.DtsUs) return;
                queue.AddLast(packet);
                kept = true;
                bytes += packet.Payload.Length;
                if (packet.IsVideo) latestVideoEnd = Math.Max(latestVideoEnd, packet.EndUs);
                Evict();
            }
        }
        finally
        {
            if (!kept) packet.Chunk?.Release(); // A rejected packet must not pin its chunk forever.
        }
    }

    public Guid Pin(long desiredStartUs)
    {
        lock (gate)
        {
            var id = Guid.NewGuid();
            pins[id] = desiredStartUs;
            return id;
        }
    }

    public void UpdatePin(Guid id, long desiredStartUs)
    {
        lock (gate)
        {
            if (pins.ContainsKey(id)) pins[id] = Math.Min(pins[id], desiredStartUs);
        }
    }

    public void Unpin(Guid id)
    {
        lock (gate) { pins.Remove(id); Evict(); }
    }

    public BufferStatus Status
    {
        get
        {
            lock (gate)
            {
                var first = streams[0].First?.Value.PtsUs ?? 0;
                return new(bytes, streams[0].Count == 0 ? 0 : (latestVideoEnd - first) / 1_000_000d,
                    first, latestVideoEnd, evictedGops, pressure);
            }
        }
    }

    public ReplaySnapshot? Snapshot(long desiredStartUs, long desiredEndUs)
    {
        lock (gate)
        {
            if (desiredEndUs <= desiredStartUs) return null;
            var video = streams[0];
            EncodedPacket? key = null;
            foreach (var packet in video)
            {
                if (packet.PtsUs >= desiredEndUs) break;
                if (!packet.IsKeyFrame) continue;
                if (key is null || packet.PtsUs <= desiredStartUs) key = packet;
                if (packet.PtsUs > desiredStartUs) break;
            }
            if (key is null) return null;
            var packets = video.Where(p => p.DtsUs >= key.DtsUs && p.PtsUs < desiredEndUs).ToList();
            if (packets.Count == 0) return null;
            var end = packets.Max(p => p.EndUs);
            foreach (var (id, queue) in streams)
            {
                if (id == 0) continue;
                packets.AddRange(queue.Where(p => p.DtsUs >= key.DtsUs && p.PtsUs < end));
            }
            // The snapshot outlives eviction: it keeps its chunks alive until it is disposed after the export.
            foreach (var packet in packets) packet.Chunk?.AddReference();
            // Stable ordering puts the IDR before an audio packet with the same DTS.
            packets.Sort((a, b) => a.DtsUs != b.DtsUs ? a.DtsUs.CompareTo(b.DtsUs) : a.StreamIndex.CompareTo(b.StreamIndex));
            return new(packets, key.PtsUs, end, key.PtsUs > Math.Max(desiredStartUs, 0),
                end + 50_000 < desiredEndUs);
        }
    }

    private void Evict()
    {
        var video = streams[0];
        if (video.Count == 0) return;
        var cutoff = latestVideoEnd - durationUs;
        if (pins.Count > 0) cutoff = Math.Min(cutoff, pins.Values.Min());
        pressure = bytes > byteLimit;
        while (video.First is not null)
        {
            var nextKey = video.First.Next;
            while (nextKey is not null && !nextKey.Value.IsKeyFrame) nextKey = nextKey.Next;
            if (nextKey is null)
            {
                if (bytes <= byteLimit) break;
                // Even one GOP can exceed a configured cap. Drop it and wait for the next IDR.
                foreach (var queue in streams.Values)
                {
                    foreach (var dropped in queue) dropped.Chunk?.Release();
                    queue.Clear();
                }
                bytes = 0;
                latestVideoEnd = 0;
                evictedGops++;
                break;
            }
            if (nextKey.Value.PtsUs > cutoff && bytes <= byteLimit) break;
            var keepDts = nextKey.Value.DtsUs;
            foreach (var queue in streams.Values)
            {
                while (queue.First is { } head && head.Value.DtsUs < keepDts)
                {
                    bytes -= head.Value.Payload.Length;
                    head.Value.Chunk?.Release();
                    queue.RemoveFirst();
                }
            }
            evictedGops++;
        }
    }
}
