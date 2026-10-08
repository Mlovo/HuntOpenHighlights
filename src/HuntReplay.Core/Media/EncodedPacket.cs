namespace HuntReplay.Core.Media;

// Payload ownership transfers to the buffer; it must never be mutated after publication.
// All timestamps use one capture epoch in microseconds. Video is encoded with B-frames disabled.
public sealed record EncodedPacket(int StreamIndex, byte[] Payload, long PtsUs, long DtsUs,
    long DurationUs, bool IsKeyFrame)
{
    public bool IsVideo => StreamIndex == 0;
    public long EndUs => PtsUs + Math.Max(DurationUs, 1);
}

public sealed record ReplaySnapshot(IReadOnlyList<EncodedPacket> Packets, long StartUs, long EndUs,
    bool TruncatedStart, bool TruncatedEnd)
{
    public long Bytes => Packets.Sum(x => (long)x.Payload.Length);
    public double DurationSeconds => (EndUs - StartUs) / 1_000_000d;
}

public readonly record struct BufferStatus(long Bytes, double Seconds, long FirstUs, long LatestUs,
    long EvictedGops, bool MemoryPressure);
