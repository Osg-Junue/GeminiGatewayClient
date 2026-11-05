using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
    internal const int MaxSseLineBytes = 64 * 1024 * 1024;
    private const int MaxNonSseBytes = GeminiErrorParser.MaxErrorBodyChars * 4;
    private static readonly SearchValues<byte> s_newlineSearchValues = SearchValues.Create([(byte)'\r', (byte)'\n']);
    private static readonly SearchValues<byte> s_whitespaceSearchValues = SearchValues.Create(" \t\r\n"u8);

    /// <summary>
    /// Reads chunks from <paramref name="stream"/> in the specified <paramref name="format"/>.
    /// </summary>
    public static IAsyncEnumerable<GenerateContentResponse> ReadAsync(
        Stream stream,
        GeminiStreamingFormat format,
        HttpStatusCode statusCode,
        StreamIdleWatchdog watchdog,
        int maxSseEventBytes = MaxSseLineBytes) =>
        format == GeminiStreamingFormat.ServerSentEvents
            ? ReadServerSentEventsAsync(stream, statusCode, watchdog, maxSseEventBytes)
            : ReadJsonArrayAsync(stream, statusCode, watchdog, maxSseEventBytes);

    private static async IAsyncEnumerable<GenerateContentResponse> ReadServerSentEventsAsync(
        Stream stream,
        HttpStatusCode statusCode,
        StreamIdleWatchdog watchdog,
        int maxSseEventBytes)
    {
        PooledByteBufferWriter? dataBuffer = null;
        PooledByteBufferWriter? nonSseBuffer = null;
        var sawSseEvent = false;
        var isFirstLine = true;
        var skipLeadingLf = false;

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(InitialReadBufferBytes, Math.Max(1, maxSseEventBytes)));
        var start = 0;
        var searchStart = 0;
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
                    searchStart = Math.Max(0, searchStart - start);
                    start = 0;
                }

                if (buffered >= maxSseEventBytes + 1)
                {
                    throw new GeminiApiException("An SSE line exceeded the maximum allowed length.", statusCode);
                }

                if (buffered == buffer.Length)
                {
                    var newCapacity = (int)Math.Min((long)buffer.Length * 2, (long)maxSseEventBytes + 1);
                    var larger = ArrayPool<byte>.Shared.Rent(newCapacity);
                    buffer.AsSpan(0, buffered).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                var maxBytesToRead = Math.Min(buffer.Length - buffered, (maxSseEventBytes + 1) - buffered);
                int bytesRead;
                watchdog.BeforeRead();
                var readSucceeded = false;
                try
                {
                    bytesRead = await stream.ReadAsync(buffer.AsMemory(buffered, maxBytesToRead), watchdog.Token).ConfigureAwait(false);
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
                        if (tailLine.Length > maxSseEventBytes)
                        {
                            throw new GeminiApiException("An SSE line exceeded the maximum allowed length.", statusCode);
                        }

                        if (!tailLine.IsEmpty)
                        {
                            ProcessSseLine(tailLine, statusCode, maxSseEventBytes, ref isFirstLine, ref sawSseEvent, ref dataBuffer, ref nonSseBuffer);
                        }
                    }

                    if (dataBuffer is { WrittenCount: > 0 })
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
                    else if (isFirstLine)
                    {
                        throw new GeminiApiException("Received an empty Gemini streaming response body.", statusCode);
                    }

                    yield break;
                }

                buffered += bytesRead;

                if (skipLeadingLf && start < buffered)
                {
                    if (buffer[start] == (byte)'\n')
                    {
                        start++;
                        if (searchStart < start)
                        {
                            searchStart = start;
                        }
                    }

                    skipLeadingLf = false;
                }

                while (TryReadBufferedSseChunk(
                    buffer,
                    buffered,
                    ref start,
                    ref searchStart,
                    ref skipLeadingLf,
                    ref isFirstLine,
                    ref sawSseEvent,
                    ref dataBuffer,
                    ref nonSseBuffer,
                    statusCode,
                    maxSseEventBytes,
                    out var response))
                {
                    yield return response;
                }
            }
        }
        finally
        {
            dataBuffer?.Dispose();
            nonSseBuffer?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryReadBufferedSseChunk(
        byte[] buffer,
        int buffered,
        ref int start,
        ref int searchStart,
        ref bool skipLeadingLf,
        ref bool isFirstLine,
        ref bool sawSseEvent,
        ref PooledByteBufferWriter? dataBuffer,
        ref PooledByteBufferWriter? nonSseBuffer,
        HttpStatusCode statusCode,
        int maxSseEventBytes,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GenerateContentResponse? response)
    {
        while (searchStart < buffered)
        {
            var unsearched = buffer.AsSpan(searchStart, buffered - searchStart);
            var relativeNewlineIdx = unsearched.IndexOfAny(s_newlineSearchValues);
            if (relativeNewlineIdx < 0)
            {
                if (buffered - start > maxSseEventBytes)
                {
                    throw new GeminiApiException("An SSE line exceeded the maximum allowed length.", statusCode);
                }

                searchStart = buffered;
                break;
            }

            var newlineIdx = searchStart + relativeNewlineIdx;
            ReadOnlySpan<byte> line = buffer.AsSpan(start, newlineIdx - start);
            if (line.Length > maxSseEventBytes)
            {
                throw new GeminiApiException("An SSE line exceeded the maximum allowed length.", statusCode);
            }

            var delimiter = buffer[newlineIdx];
            start = newlineIdx + 1;

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

            searchStart = start;

            if (ProcessSseLine(line, statusCode, maxSseEventBytes, ref isFirstLine, ref sawSseEvent, ref dataBuffer, ref nonSseBuffer))
            {
                response = DeserializeUtf8Chunk(dataBuffer!.WrittenSpan, statusCode);
                dataBuffer.Clear();
                return true;
            }
        }

        response = null;
        return false;
    }

    /// <summary>
    /// Processes a single UTF-8 SSE line (without trailing CR/LF). Returns <c>true</c> when a blank line completed a buffered event.
    /// </summary>
    private static bool ProcessSseLine(
        ReadOnlySpan<byte> line,
        HttpStatusCode statusCode,
        int maxSseEventBytes,
        ref bool isFirstLine,
        ref bool sawSseEvent,
        ref PooledByteBufferWriter? dataBuffer,
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

        if (line.StartsWith("data:"u8))
        {
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

            var currentWritten = dataBuffer?.WrittenCount ?? 0;
            if (payload.IsEmpty && currentWritten == 0)
            {
                return false;
            }

            var separatorBytes = currentWritten > 0 ? 1 : 0;
            if ((long)currentWritten + separatorBytes + payload.Length > maxSseEventBytes)
            {
                throw new GeminiApiException("An SSE event exceeded the maximum allowed length.", statusCode);
            }

            dataBuffer ??= new PooledByteBufferWriter(1024);
            if (separatorBytes > 0)
            {
                dataBuffer.GetSpan(1)[0] = (byte)'\n';
                dataBuffer.Advance(1);
            }

            payload.CopyTo(dataBuffer.GetSpan(payload.Length));
            dataBuffer.Advance(payload.Length);
            return false;
        }

        if (line.IsEmpty)
        {
            if (dataBuffer is { WrittenCount: > 0 })
            {
                sawSseEvent = true;
                return true;
            }

            return false;
        }

        if (line[0] == (byte)':')
        {
            // Comment / keep-alive line: ignore without buffering into nonSseBuffer,
            // but do not mark sawSseEvent until an actual data field is seen so raw proxy error bodies after a keep-alive are still caught.
            return false;
        }

        if (line.SequenceEqual("data"u8))
        {
            sawSseEvent = true;
            return false;
        }

        if (line.StartsWith("event:"u8) || line.SequenceEqual("event"u8) ||
            line.StartsWith("id:"u8) || line.SequenceEqual("id"u8) ||
            line.StartsWith("retry:"u8) || line.SequenceEqual("retry"u8))
        {
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

    internal static GenerateContentResponse DeserializeUtf8Response(
        ReadOnlySpan<byte> utf8Json,
        HttpStatusCode statusCode,
        string emptyMessage,
        string parseFailureMessage,
        string? errorStreamMessage = null) =>
        DeserializeUtf8Response(
            utf8Json,
            GeminiJsonContext.Default.GenerateContentResponse,
            static r => r.ExtensionData,
            statusCode,
            emptyMessage,
            parseFailureMessage,
            errorStreamMessage);

    internal static TResponse DeserializeUtf8Response<TResponse>(
        ReadOnlySpan<byte> utf8Json,
        JsonTypeInfo<TResponse> typeInfo,
        Func<TResponse, IDictionary<string, JsonElement>?> extensionDataSelector,
        HttpStatusCode statusCode,
        string emptyMessage,
        string parseFailureMessage,
        string? errorStreamMessage = null)
        where TResponse : class
    {
        if (utf8Json.StartsWith("\uFEFF"u8))
        {
            utf8Json = utf8Json[3..];
        }

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

            var response = JsonSerializer.Deserialize(utf8Json, typeInfo)
                           ?? throw new GeminiApiException(emptyMessage, statusCode);

            if (extensionDataSelector(response) is { } extensionData &&
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
        var idx = utf8Json.IndexOfAnyExcept(s_whitespaceSearchValues);
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
        StreamIdleWatchdog watchdog,
        int maxSseEventBytes)
    {
        using var recordingStream = new PrefixRecordingStream(stream, MaxNonSseBytes, maxSseEventBytes, statusCode, watchdog);
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
                    recordingStream.OnElementYielded();
                    current = ToResponse(enumerator.Current, statusCode);
                }
                catch (JsonException ex)
                {
                    if (!yieldedAny && !recordingStream.WrittenSpan.IsEmpty)
                    {
                        await recordingStream.DrainToMaxRecordedBytesAsync().ConfigureAwait(false);
                        var captured = recordingStream.WrittenSpan;
                        current = DeserializeUtf8Chunk(captured, statusCode);
                        stopAfterCurrent = true;
                    }
                    else
                    {
                        throw new GeminiApiException(
                            "Failed to deserialize a Gemini streaming chunk.",
                            statusCode,
                            ExtractBoundedString(recordingStream.WrittenSpan),
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
    /// rejects the root token, while also bounding the bytes read per element. The bound is approximate: it counts read
    /// buffers between yields rather than tracking element boundaries, so it can differ from an element's size by up to one read.
    /// </summary>
    private sealed class PrefixRecordingStream(
        Stream inner,
        int maxRecordedBytes,
        int maxElementBytes,
        HttpStatusCode statusCode,
        StreamIdleWatchdog watchdog) : Stream
    {
        private PooledByteBufferWriter? _recorded = new(256);
        private long _currentElementBytes;

        public ReadOnlySpan<byte> WrittenSpan => _recorded is not null ? _recorded.WrittenSpan : ReadOnlySpan<byte>.Empty;

        public void OnElementYielded()
        {
            _currentElementBytes = 0;
            StopRecording();
        }

        public void StopRecording()
        {
            _recorded?.Dispose();
            _recorded = null;
        }

        public async ValueTask DrainToMaxRecordedBytesAsync()
        {
            while (_recorded is { } recorded && recorded.WrittenCount < maxRecordedBytes)
            {
                var remaining = maxRecordedBytes - recorded.WrittenCount;
                var destination = recorded.GetMemory(Math.Min(InitialReadBufferBytes, remaining));
                if (destination.Length > remaining)
                {
                    destination = destination[..remaining];
                }

                int read;
                watchdog.BeforeRead();
                var readSucceeded = false;
                try
                {
                    read = await inner.ReadAsync(destination, watchdog.Token).ConfigureAwait(false);
                    readSucceeded = true;
                }
                finally
                {
                    watchdog.AfterRead(readSucceeded);
                }

                if (read == 0)
                {
                    break;
                }

                recorded.Advance(read);
            }
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

            if (read > 0)
            {
                if (_recorded is { } recorded)
                {
                    var remaining = maxRecordedBytes - recorded.WrittenCount;
                    if (remaining > 0)
                    {
                        var toCopy = Math.Min(read, remaining);
                        buffer.Span[..toCopy].CopyTo(recorded.GetSpan(toCopy));
                        recorded.Advance(toCopy);
                    }
                }

                _currentElementBytes += read;
                if (_currentElementBytes > maxElementBytes)
                {
                    throw new GeminiApiException("A JSON stream chunk exceeded the maximum allowed length.", statusCode);
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
