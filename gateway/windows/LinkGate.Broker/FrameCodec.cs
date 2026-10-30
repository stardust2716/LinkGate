namespace LinkGate.Broker;

public static class FrameCodec
{
    public const int MaxBytes = LinkGate.Control.FrameCodec.MaxBytes;
    public static Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        LinkGate.Control.FrameCodec.ReadAsync(stream, cancellationToken);
    public static Task WriteAsync(Stream stream, byte[] body, CancellationToken cancellationToken) =>
        LinkGate.Control.FrameCodec.WriteAsync(stream, body, cancellationToken);
}
