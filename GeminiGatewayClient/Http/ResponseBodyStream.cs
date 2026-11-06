using System.Net;
using System.Text;

namespace GeminiGatewayClient.Http;

/// <summary>
/// Read-only view of a streaming response body. It fails a read that stays idle for too long, because
/// <see cref="HttpClient.Timeout"/> stops applying once the response headers have arrived, and it reports a lost
/// connection the same way <see cref="HttpClient"/> reports one lost before the headers. The body always ends with a
/// blank line, because an event stream discards a final event that lacks one.
/// </summary>
internal sealed class ResponseBodyStream(Stream inner, TimeSpan? idleTimeout, HttpStatusCode statusCode) : Stream
{
    private readonly MemoryStream _head = new();
    private ReadOnlyMemory<byte> _unread;
    private bool _ended;

    /// <summary>
    /// The start of the body as received, without a byte order mark. It shows what a response without events really was.
    /// </summary>
    public string Head => Encoding.UTF8.GetString(_head.GetBuffer(), 0, (int)_head.Length).TrimStart('\uFEFF').TrimEnd();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Returns the first byte of content, skipping any byte order mark and whitespace, or <c>null</c> for an empty body.
    /// </summary>
    public async ValueTask<byte?> PeekAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        var length = 0;
        while (true)
        {
            var read = await ReadBodyAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            length += read;

            var content = buffer.AsSpan(0, length);
            var preamble = Encoding.UTF8.Preamble;
            if (read > 0 && content.Length < preamble.Length && preamble.StartsWith(content))
            {
                // The rest of a byte order mark is still on its way.
                continue;
            }

            content = content.StartsWith(preamble) ? content[preamble.Length..] : content;
            content = content.TrimStart(" \t\r\n"u8);
            if (read == 0 || !content.IsEmpty)
            {
                _unread = buffer.AsMemory(0, length);
                return content.IsEmpty ? null : content[0];
            }

            length = 0;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_unread.IsEmpty)
        {
            var read = await ReadBodyAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0 || _ended)
            {
                return read;
            }

            _ended = true;
            _unread = "\n\n"u8.ToArray();
        }

        var count = Math.Min(_unread.Length, buffer.Length);
        _unread[..count].CopyTo(buffer);
        _unread = _unread[count..];
        return count;
    }

    private async ValueTask<int> ReadBodyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (idleTimeout is { } timeout)
        {
            idle.CancelAfter(timeout);
        }

        try
        {
            var read = await inner.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            _head.Write(buffer.Span[..Math.Min(read, GeminiErrorParser.MaxErrorBodyChars - (int)_head.Length)]);
            return read;
        }
        catch (OperationCanceledException ex) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No data was received from the Gemini streaming response within {idleTimeout} (GeminiClientOptions.StreamIdleTimeout).",
                ex);
        }
        catch (IOException ex)
        {
            throw new HttpRequestException(
                ex is HttpIOException { HttpRequestError: var error } ? error : HttpRequestError.ResponseEnded,
                "Failed to read the Gemini response body.",
                ex,
                statusCode);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
