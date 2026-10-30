using System.Buffers.Binary;

namespace LinkGate.Control;

public static class FrameCodec
{
    public const int MaxBytes = 65536;

    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        int first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > MaxBytes) throw new InvalidDataException("Invalid control frame.");
        var body = new byte[(int)length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return body;
    }

    public static async Task WriteAsync(Stream stream, byte[] body, CancellationToken cancellationToken)
    {
        if (body.Length == 0 || body.Length > MaxBytes) throw new InvalidDataException("Invalid control frame.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
