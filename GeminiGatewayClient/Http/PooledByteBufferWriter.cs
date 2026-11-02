using System.Buffers;

namespace GeminiGatewayClient.Http;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> backed by <see cref="ArrayPool{T}.Shared"/> to avoid Large Object Heap (LOH)
/// and gen0 allocations when buffering UTF-8 request/response payloads and SSE chunks.
/// </summary>
internal sealed class PooledByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int DefaultInitialBufferSize = 1024;
    private const int MinimumBufferSize = 256;

    private byte[] _buffer;
    private int _index;

    public PooledByteBufferWriter(int initialCapacity = DefaultInitialBufferSize)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, MinimumBufferSize));
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _index);

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _index);

    public int WrittenCount => _index;

    public void Clear() => _index = 0;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_index > _buffer.Length - count)
        {
            throw new InvalidOperationException("Cannot advance past the end of the underlying buffer.");
        }

        _index += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        return _buffer.AsMemory(_index);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        return _buffer.AsSpan(_index);
    }

    public void Dispose()
    {
        var toReturn = _buffer;
        _buffer = [];
        _index = 0;
        if (toReturn.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(toReturn);
        }
    }

    private void CheckAndResizeBuffer(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var minimumSize = sizeHint == 0 ? MinimumBufferSize : sizeHint;
        if (_buffer.Length - _index >= minimumSize)
        {
            return;
        }

        var currentLength = _buffer.Length;
        var growBy = Math.Max(minimumSize, currentLength);
        var newCapacityLong = (long)currentLength + growBy;
        var newCapacity = newCapacityLong > Array.MaxLength
            ? Math.Max(_index + minimumSize, Array.MaxLength)
            : (int)newCapacityLong;

        var next = ArrayPool<byte>.Shared.Rent(newCapacity);
        if (_index > 0)
        {
            _buffer.AsSpan(0, _index).CopyTo(next);
        }

        if (currentLength > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = next;
    }
}

