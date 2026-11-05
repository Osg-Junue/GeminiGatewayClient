namespace GeminiGatewayClient.Benchmarks;

/// <summary>
/// Read-only stream that never returns more than <c>packetSize</c> bytes per read, approximating TCP packet fragmentation.
/// </summary>
internal sealed class ChunkedMemoryStream(ReadOnlyMemory<byte> payload, int packetSize) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => payload.Length;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= payload.Length)
        {
            return 0;
        }

        var available = payload.Length - _position;
        var toRead = Math.Min(Math.Min(buffer.Length, available), packetSize);
        payload.Span.Slice(_position, toRead).CopyTo(buffer);
        _position += toRead;
        return toRead;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
