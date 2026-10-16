namespace HuntReplay.Core.Media;

// Payload ownership transfers to the buffer; it must never be mutated after publication.
// All timestamps use one capture epoch in microseconds. Video is encoded with B-frames disabled.
// Chunk is set when the payload lives in a PayloadArena; the replay buffer then releases it on eviction.
public sealed record EncodedPacket(int StreamIndex, ReadOnlyMemory<byte> Payload, long PtsUs, long DtsUs,
    long DurationUs, bool IsKeyFrame)
{
    public PayloadChunk? Chunk { get; init; }
    public bool IsVideo => StreamIndex == 0;
    public long EndUs => PtsUs + Math.Max(DurationUs, 1);
}

/// <summary>
/// Packets of one clip. A snapshot holds a reference on every arena chunk it reads, so the data cannot be recycled
/// while the clip is being written; dispose it once the export is finished (or abandoned).
/// </summary>
public sealed record ReplaySnapshot(IReadOnlyList<EncodedPacket> Packets, long StartUs, long EndUs,
    bool TruncatedStart, bool TruncatedEnd) : IDisposable
{
    private int disposed;
    public long Bytes => Packets.Sum(x => (long)x.Payload.Length);
    public double DurationSeconds => (EndUs - StartUs) / 1_000_000d;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var packet in Packets) packet.Chunk?.Release();
    }
}

public readonly record struct BufferStatus(long Bytes, double Seconds, long FirstUs, long LatestUs,
    long EvictedGops, bool MemoryPressure);
