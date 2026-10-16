namespace HuntReplay.Core.Media;

/// <summary>
/// Storage for encoded packet payloads during a capture session. Instead of one new array per packet (at 40+ Mbps
/// most video frames exceed the 85 KB large-object threshold, so every frame became a large-object-heap allocation),
/// payloads are bump-allocated into reusable fixed-size chunks.
/// <para>
/// Each chunk counts its references: one per packet still held by the replay buffer, plus one per packet in every
/// snapshot that is being exported. A chunk is recycled only when that count is zero and it is no longer the chunk
/// being filled, so data that a buffer or an export can still read is never overwritten.
/// </para>
/// </summary>
public sealed class PayloadArena(int chunkBytes = PayloadArena.DefaultChunkBytes, int maxIdleChunks = 4)
{
    public const int DefaultChunkBytes = 16 * 1024 * 1024;
    private readonly object gate = new();
    private readonly Stack<PayloadChunk> idle = new();
    private PayloadChunk? current;

    /// <summary>Chunks allocated over the arena's lifetime; recycling keeps this near limit / chunk size.</summary>
    public int AllocatedChunks { get; private set; }

    /// <summary>
    /// Reserves space for one payload. The returned chunk carries one reference for the caller, released through
    /// <see cref="PayloadChunk.Release"/> (the replay buffer does this when it drops or evicts the packet).
    /// </summary>
    public (PayloadChunk Chunk, ArraySegment<byte> Segment) Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        lock (gate)
        {
            if (length > chunkBytes)
            {
                // A packet larger than a chunk (a huge keyframe) gets a one-off chunk that is never pooled.
                var oversized = new PayloadChunk(this, new byte[length], pooled: false);
                AllocatedChunks++;
                return (oversized.Take(length), new ArraySegment<byte>(oversized.Data, 0, length));
            }
            if (current is null || current.Used + length > current.Data.Length)
            {
                var retired = current;
                current = idle.Count > 0 ? idle.Pop() : NewChunk();
                if (retired is not null && retired.References == 0) Recycle(retired);
            }
            int offset = current.Used;
            return (current.Take(length), new ArraySegment<byte>(current.Data, offset, length));
        }
    }

    internal void AddReference(PayloadChunk chunk)
    {
        lock (gate) chunk.References++;
    }

    internal void Release(PayloadChunk chunk)
    {
        lock (gate)
        {
            if (chunk.References <= 0) throw new InvalidOperationException("Payload chunk released more often than referenced.");
            if (--chunk.References == 0 && chunk != current) Recycle(chunk);
        }
    }

    private PayloadChunk NewChunk()
    {
        AllocatedChunks++;
        return new PayloadChunk(this, GC.AllocateUninitializedArray<byte>(chunkBytes), pooled: true);
    }

    private void Recycle(PayloadChunk chunk)
    {
        if (!chunk.Pooled || idle.Count >= maxIdleChunks) return; // Surplus chunks go back to the GC.
        chunk.Used = 0;
        idle.Push(chunk);
    }
}

/// <summary>A block of payload memory owned by a <see cref="PayloadArena"/>; see the arena for the lifetime rules.</summary>
public sealed class PayloadChunk
{
    private readonly PayloadArena arena;
    internal PayloadChunk(PayloadArena arena, byte[] data, bool pooled)
    {
        this.arena = arena;
        Data = data;
        Pooled = pooled;
    }
    internal byte[] Data { get; }
    internal bool Pooled { get; }
    internal int Used { get; set; }
    internal int References { get; set; }

    internal PayloadChunk Take(int length)
    {
        Used += length;
        References++;
        return this;
    }

    public void AddReference() => arena.AddReference(this);
    public void Release() => arena.Release(this);
}
