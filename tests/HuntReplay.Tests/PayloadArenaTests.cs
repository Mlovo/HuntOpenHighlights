using HuntReplay.Core.Media;

namespace HuntReplay.Tests;

public static partial class Program
{
    private static void PayloadArenaTests()
    {
        Test("payload arena recycles chunks once the buffer evicts their packets", () =>
        {
            var arena = new PayloadArena(chunkBytes: 64, maxIdleChunks: 4);
            var ring = new PacketRingBuffer(2, 1_000_000);
            for (int i = 0; i < 1000; i++) ring.Add(ArenaPacket(arena, i, key: true, fill: (byte)i, size: 16));
            // 2 s window of 16-byte packets = 3 packets live: steady state needs a handful of 64-byte chunks, not 250.
            True(arena.AllocatedChunks <= 4);
        });
        Test("payload arena never overwrites data a snapshot still holds", () =>
        {
            var arena = new PayloadArena(chunkBytes: 64, maxIdleChunks: 4);
            var ring = new PacketRingBuffer(2, 1_000_000);
            ring.Add(ArenaPacket(arena, 0, key: true, fill: 0xAB, size: 16));
            using var snapshot = ring.Snapshot(0, 1_000_000)!;
            for (int i = 1; i < 500; i++) ring.Add(ArenaPacket(arena, i, key: true, fill: (byte)i, size: 16));
            True(snapshot.Packets[0].Payload.Span.ToArray().All(b => b == 0xAB));
        });
        Test("payload arena reuses a snapshot's chunk only after it is disposed", () =>
        {
            var arena = new PayloadArena(chunkBytes: 64, maxIdleChunks: 4);
            var ring = new PacketRingBuffer(2, 1_000_000);
            ring.Add(ArenaPacket(arena, 0, key: true, fill: 1, size: 16));
            var snapshot = ring.Snapshot(0, 1_000_000)!;
            for (int i = 1; i < 200; i++) ring.Add(ArenaPacket(arena, i, key: true, fill: 2, size: 16));
            int whileHeld = arena.AllocatedChunks;
            snapshot.Dispose();
            snapshot.Dispose(); // Idempotent: a second dispose must not release twice.
            for (int i = 200; i < 400; i++) ring.Add(ArenaPacket(arena, i, key: true, fill: 3, size: 16));
            True(arena.AllocatedChunks <= whileHeld + 1);
        });
        Test("payload arena chunks of rejected packets are released", () =>
        {
            var arena = new PayloadArena(chunkBytes: 64, maxIdleChunks: 4);
            var ring = new PacketRingBuffer(2, 1_000_000);
            // Audio before the first keyframe is dropped by the buffer; its chunk reference must not leak.
            for (int i = 0; i < 1000; i++)
            {
                var (chunk, segment) = arena.Rent(16);
                ring.Add(new EncodedPacket(1, segment, i, i, 1, false) { Chunk = chunk });
            }
            True(arena.AllocatedChunks <= 2);
        });
        Test("payload arena gives a packet larger than a chunk its own block", () =>
        {
            var arena = new PayloadArena(chunkBytes: 64, maxIdleChunks: 4);
            var (chunk, segment) = arena.Rent(200);
            Equal(200, segment.Count);
            segment.AsSpan().Fill(7);
            chunk.Release();
            Throws<InvalidOperationException>(() => chunk.Release());
        });
    }

    private static EncodedPacket ArenaPacket(PayloadArena arena, int second, bool key, byte fill, int size)
    {
        var (chunk, segment) = arena.Rent(size);
        segment.AsSpan().Fill(fill);
        return new EncodedPacket(0, segment, second * 1_000_000L, second * 1_000_000L, 1_000_000, key) { Chunk = chunk };
    }
}
