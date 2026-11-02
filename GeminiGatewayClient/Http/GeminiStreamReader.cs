using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using GeminiGatewayClient.Models;
using GeminiGatewayClient.Serialization;

namespace GeminiGatewayClient.Http;

/// <summary>
/// Reads <c>:streamGenerateContent</c> response bodies in either Server-Sent Events or chunked JSON array format,
/// surfacing in-stream error payloads as <see cref="GeminiApiException"/>.
/// </summary>
internal static class GeminiStreamReader
{
    private const int InitialReadBufferBytes = 4096;
    private const int MaxSseLineBytes = 64 * 1024 * 1024;
    private const int MaxNonSseBytes = GeminiErrorParser.MaxErrorBodyChars * 4;

    /// <summary>
    /// Reads chunks from <paramref name="stream"/> in the specified <paramref name="format"/>.
    /// </summary>
    public static IAsyncEnumerable<GenerateContentResponse> ReadAsync(
        Stream stream,
        GeminiStreamingFormat format,
        HttpStatusCode statusCode,
        StreamIdleWatchdog watchdog) =>
        format == GeminiStreamingFormat.ServerSentEvents
            ? ReadServerSentEventsAsync(stream, statusCode, watchdog)
            : ReadJsonArrayAsync(stream, statusCode, watchdog);

    private static async IAsyncEnumerable<GenerateContentResponse> ReadServerSentEventsAsync(
        Stream stream,
        HttpStatusCode statusCode,
        StreamIdleWatchdog watchdog)
    {
        using var dataBuffer = new PooledByteBufferWriter(1024);
        PooledByteBufferWriter? nonSseBuffer = null;
        var sawSseEvent = false;
        var isFirstLine = true;
        var skipLeadingLf = false;

        var buffer = ArrayPool<byte>.Shared.Rent(InitialReadBufferBytes);
        var start = 0;
        var buffered = 0;

        try
        {
            while (true)
            {
                if (start > 0)
                {
                    var remaining = buffered - start;
                    if (remaining > 0)
                    {
                        buffer.AsSpan(start, remaining).CopyTo(buffer);
                    }

                    buffered = remaining;
                    start = 0;
                }

                if (buffered == buffer.Length)
                {
                    if (buffer.Length >= MaxSseLineBytes)
                    {
                        throw new GeminiApiException("An SSE line exceeded the maximum allowed length.", statusCode);
                    }

                    var newCapacity = (int)Math.Min((long)buffer.Length * 2, MaxSseLineBytes);
                    var larger = ArrayPool<byte>.Shared.Rent(newCapacity);
                    buffer.AsSpan(0, buffered).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                int bytesRead;
                watchdog.BeforeRead();
                var readSucceeded = false;
                try
                {
                    bytesRead = await stream.ReadAsync(buffer.AsMemory(buffered), watchdog.Token).ConfigureAwait(false);
                    readSucceeded = true;
                }
                finally
                {
                    watchdog.AfterRead(readSucceeded);
                }

                if (bytesRead == 0)
                {
                    if (buffered > start)
                    {
                        var tailLine = buffer.AsSpan(start, buffered - start);
                        if (skipLeadingLf && !tailLine.IsEmpty && tailLine[0] == (byte)'\n')
                        {
                            tailLine = tailLine[1..];
                        }

                        if (!tailLine.IsEmpty)
                        {
                            ProcessSseLine(tailLine, ref isFirstLine, ref sawSseEvent, dataBuffer, ref nonSseBuffer);
                        }
                    }

                    if (dataBuffer.WrittenCount > 0)
                    {
                        sawSseEvent = true;
                        var response = DeserializeUtf8Chunk(dataBuffer.WrittenSpan, statusCode);
                        dataBuffer.Clear();
                        yield return response;
                    }
                    else if (!sawSseEvent && nonSseBuffer is { WrittenCount: > 0 })
                    {
                        yield return DeserializeUtf8Chunk(nonSseBuffer.WrittenSpan, statusCode);
                    }

                    yield break;
                }

                buffered += bytesRead;

                if (skipLeadingLf && start < buffered)
                {
                    if (buffer[start] == (byte)'\n')
                    {
                        start++;
                    }

                    skipLeadingLf = false;
                }

                while (start < buffered)
                {
                    var unread = buffer.AsSpan(start, buffered - start);
                    var newlineIdx = unread.IndexOfAny((byte)'\r', (byte)'\n');
                    if (newlineIdx < 0)
                    {
                        break;
                    }

                    var line = unread[..newlineIdx];
                    var delimiter = unread[newlineIdx];
                    start += newlineIdx + 1;

                    if (delimiter == (byte)'\r')
                    {
                        if (start < buffered)
                        {
                            if (buffer[start] == (byte)'\n')
                            {
                                start++;
                            }
                        }
                        else
                        {
                            skipLeadingLf = true;
                        }
                    }

                    if (ProcessSseLine(line, ref isFirstLine, ref sawSseEvent, dataBuffer, ref nonSseBuffer))
                    {
                        var response = DeserializeUtf8Chunk(dataBuffer.WrittenSpan, statusCode);
                        dataBuffer.Clear();
                        yield return response;
                    }
                }
            }
        }
        finally
        {
            nonSseBuffer?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Processes a single UTF-8 SSE line (without trailing CR/LF). Returns <c>true</c> when a blank line completed a buffered event.
    /// </summary>
    private static bool ProcessSseLine(
        ReadOnlySpan<byte> line,
        ref bool isFirstLine,
        ref bool sawSseEvent,
        PooledByteBufferWriter dataBuffer,
        ref PooledByteBufferWriter? nonSseBuffer)
    {
        if (isFirstLine)
        {
            isFirstLine = false;
            if (line.StartsWith("\uFEFF"u8))
            {
                line = line[3..];
            }
        }

        if (line.IsEmpty)
        {
            if (dataBuffer.WrittenCount > 0)
            {
                sawSseEvent = true;
                return true;
            }

            return false;
        }

        if (line[0] == (byte)':')
        {
            // Comment / keep-alive line.
            sawSseEvent = true;
            return false;
        }

        if (!line.StartsWith("data:"u8))
        {
            if (line.SequenceEqual("data"u8) ||
                line.StartsWith("event:"u8) || line.SequenceEqual("event"u8) ||
                line.StartsWith("id:"u8) || line.SequenceEqual("id"u8) ||
                line.StartsWith("retry:"u8) || line.SequenceEqual("retry"u8))
            {
                sawSseEvent = true;
                return false;
            }

            // A proxy or upstream server may return a raw JSON error payload with HTTP 200 instead of SSE framing.
            // Cap buffered non-SSE bytes at MaxNonSseBytes to prevent unbounded memory growth on unexpected responses.
            if (!sawSseEvent)
            {
                nonSseBuffer ??= new PooledByteBufferWriter(256);
                var remaining = MaxNonSseBytes - nonSseBuffer.WrittenCount;
                if (remaining > 0)
                {
                    var slice = line.Length > remaining ? line[..remaining] : line;
                    slice.CopyTo(nonSseBuffer.GetSpan(slice.Length));
                    nonSseBuffer.Advance(slice.Length);
                }
            }

            return false;
        }

        sawSseEvent = true;
        var payload = line[5..];
        if (!payload.IsEmpty && payload[0] == (byte)' ')
        {
            payload = payload[1..];
        }

        if (payload.SequenceEqual("[DONE]"u8))
        {
            return false;
        }

        if (dataBuffer.WrittenCount > 0)
        {
            dataBuffer.GetSpan(1)[0] = (byte)'\n';
            dataBuffer.Advance(1);
        }

        payload.CopyTo(dataBuffer.GetSpan(payload.Length));
        dataBuffer.Advance(payload.Length);
        return false;
    }

    internal static GenerateContentResponse DeserializeUtf8Response(
        ReadOnlySpan<byte> utf8Json,
        HttpStatusCode statusCode,
        string emptyMessage,
        string parseFailureMessage,
        string? errorStreamMessage = null)
    {
        try
        {
            if (StartsWithJsonArray(utf8Json))
            {
                if (LooksLikeErrorPayload(utf8Json))
                {
                    var docReader = new Utf8JsonReader(utf8Json);
                    using var document = JsonDocument.ParseValue(ref docReader);
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    {
                        root = root[0];
                    }

                    if (GeminiErrorParser.TryCreateFromStreamPayload(root, statusCode, errorStreamMessage) is { } arrayStreamError)
                    {
                        throw arrayStreamError;
                    }
                }
            }

            var response = JsonSerializer.Deserialize(utf8Json, GeminiJsonContext.Default.GenerateContentResponse)
                           ?? throw new GeminiApiException(emptyMessage, statusCode);

            if (response.ExtensionData is { } extensionData &&
                extensionData.TryGetValue("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.Object)
            {
                throw GeminiErrorParser.CreateFromErrorElement(errorElement, ExtractBoundedString(utf8Json), statusCode, errorStreamMessage);
            }

            return response;
        }
        catch (JsonException ex)
        {
            var rawPreview = ExtractBoundedString(utf8Json);
            throw new GeminiApiException(parseFailureMessage, statusCode, rawPreview, ex);
        }
    }

    private static bool StartsWithJsonArray(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.StartsWith("\uFEFF"u8))
        {
            utf8Json = utf8Json[3..];
        }

        var idx = utf8Json.IndexOfAnyExcept(" \t\r\n"u8);
        return idx >= 0 && utf8Json[idx] == (byte)'[';
    }

    private static GenerateContentResponse DeserializeUtf8Chunk(ReadOnlySpan<byte> utf8Json, HttpStatusCode statusCode) =>
        DeserializeUtf8Response(
            utf8Json,
            statusCode,
            "Received an empty (null) Gemini streaming chunk.",
            "Failed to deserialize a Gemini streaming chunk.");

    private static string? ExtractBoundedString(ReadOnlySpan<byte> utf8Bytes)
    {
        if (utf8Bytes.IsEmpty)
        {
            return null;
        }

        var maxBytes = Math.Min(utf8Bytes.Length, GeminiErrorParser.MaxErrorBodyChars * 4);
        var text = Encoding.UTF8.GetString(utf8Bytes[..maxBytes]);
        return text.Length > GeminiErrorParser.MaxErrorBodyChars
            ? text[..GeminiErrorParser.MaxErrorBodyChars]
            : text;
    }

    internal static bool LooksLikeErrorPayload(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        if (!reader.Read())
        {
            return false;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            if (!reader.Read())
            {
                return false;
            }
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return false;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            var isErrorProp = reader.ValueTextEquals("error"u8);
            if (!reader.Read())
            {
                return false;
            }

            if (isErrorProp)
            {
                return reader.TokenType == JsonTokenType.StartObject;
            }

            if (!reader.TrySkip())
            {
                return false;
            }
        }

        return false;
    }

    private static async IAsyncEnumerable<GenerateContentResponse> ReadJsonArrayAsync(
        Stream stream,
        HttpStatusCode statusCode,
        StreamIdleWatchdog watchdog)
    {
        using var recordingStream = new PrefixRecordingStream(stream, MaxNonSseBytes, watchdog);
        var enumerator = JsonSerializer
            .DeserializeAsyncEnumerable(recordingStream, GeminiJsonContext.Default.GenerateContentResponse, watchdog.CallerToken)
            .GetAsyncEnumerator(watchdog.CallerToken);
        try
        {
            var yieldedAny = false;
            var stopAfterCurrent = false;
            while (!stopAfterCurrent)
            {
                GenerateContentResponse current;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }

                    yieldedAny = true;
                    recordingStream.StopRecording();
                    current = ToResponse(enumerator.Current, statusCode);
                }
                catch (JsonException ex)
                {
                    var captured = recordingStream.WrittenSpan;
                    if (!yieldedAny && !captured.IsEmpty)
                    {
                        current = DeserializeUtf8Chunk(captured, statusCode);
                        stopAfterCurrent = true;
                    }
                    else
                    {
                        throw new GeminiApiException(
                            "Failed to deserialize a Gemini streaming chunk.",
                            statusCode,
                            ExtractBoundedString(captured),
                            ex);
                    }
                }

                yield return current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static GenerateContentResponse ToResponse(GenerateContentResponse? response, HttpStatusCode statusCode)
    {
        if (response is null)
        {
            throw new GeminiApiException("Received an empty (null) Gemini streaming chunk.", statusCode);
        }

        if (response.ExtensionData is { Count: > 0 } extensionData &&
            extensionData.TryGetValue("error", out var errorElement) &&
            errorElement.ValueKind == JsonValueKind.Object)
        {
            var raw = errorElement.GetRawText();
            throw GeminiErrorParser.CreateFromErrorElement(errorElement, raw, statusCode);
        }

        return response;
    }

    /// <summary>
    /// Captures the initial bytes read from a JSON-array stream so non-array error objects (<c>{"error": ...}</c>)
    /// or gateway HTML pages returned with HTTP 200 can be diagnosed if <see cref="JsonSerializer.DeserializeAsyncEnumerable{TValue}(Stream, System.Text.Json.Serialization.Metadata.JsonTypeInfo{TValue}, CancellationToken)"/>
    /// rejects the root token.
    /// </summary>
    private sealed class PrefixRecordingStream(Stream inner, int maxRecordedBytes, StreamIdleWatchdog watchdog) : Stream
    {
        private PooledByteBufferWriter? _recorded = new(256);

        public ReadOnlySpan<byte> WrittenSpan => _recorded is not null ? _recorded.WrittenSpan : ReadOnlySpan<byte>.Empty;

        public void StopRecording()
        {
            _recorded?.Dispose();
            _recorded = null;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read;
            watchdog.BeforeRead();
            var readSucceeded = false;
            try
            {
                read = await inner.ReadAsync(buffer, watchdog.Token).ConfigureAwait(false);
                readSucceeded = true;
            }
            finally
            {
                watchdog.AfterRead(readSucceeded);
            }

            if (read > 0 && _recorded is { } recorded)
            {
                var remaining = maxRecordedBytes - recorded.WrittenCount;
                if (remaining > 0)
                {
                    var toCopy = Math.Min(read, remaining);
                    buffer.Span[..toCopy].CopyTo(recorded.GetSpan(toCopy));
                    recorded.Advance(toCopy);
                }
            }

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Synchronous reads are not supported by PrefixRecordingStream.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopRecording();
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
