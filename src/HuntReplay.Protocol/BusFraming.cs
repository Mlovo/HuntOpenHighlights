using System.Buffers.Binary;
using Google.Protobuf;
using HuntReplay.Protocol.Generated;

namespace HuntReplay.Protocol;

public static class BusFraming
{
    public const int MaximumMessageBytes = 4 * 1024 * 1024;

    public static async ValueTask<BusMessage?> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        // An idle game may have no events for a long time. Only a started frame gets a deadline.
        if (await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false) == 0) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > MaximumMessageBytes) throw new InvalidDataException("Invalid bus frame length.");
        var payload = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
        return BusMessage.Parser.ParseFrom(payload);
    }

    public static async ValueTask WriteAsync(Stream stream, BusMessage message, CancellationToken token)
    {
        var payload = message.ToByteArray();
        if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("Bus response too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        await stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
    }
}
