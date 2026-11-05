using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using GeminiGatewayClient.Http;

namespace GeminiGatewayClient.Benchmarks;

[MemoryDiagnoser]
public class StreamReaderBenchmarks
{
    [Params(1, 16, 64)]
    public int ChunkCount { get; set; }

    private byte[] _sseSimpleTextPayload = null!;
    private byte[] _sseThinkingTextPayload = null!;
    private byte[] _sseToolCallingPayload = null!;
    private byte[] _sseSearchGroundedPayload = null!;
    private byte[] _jsonArraySimpleTextPayload = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sseSimpleTextPayload = BuildSsePayload(ChunkCount, CreateSimpleTextChunk);
        _sseThinkingTextPayload = BuildSsePayload(ChunkCount, CreateThinkingAndTextChunk);
        _sseToolCallingPayload = BuildSsePayload(ChunkCount, CreateToolCallingChunk);
        _sseSearchGroundedPayload = BuildSsePayload(ChunkCount, CreateSearchGroundedChunk);
        _jsonArraySimpleTextPayload = BuildJsonArrayPayload(ChunkCount, CreateSimpleTextChunk);
    }

    private static string CreateSimpleTextChunk(int index, int total)
    {
        var finishReason = index == total - 1 ? "\"STOP\"" : "null";
        var markdownText = $"### Section {index + 1}\n\nProgressive markdown stream content token delivery for chunk {index}.\n";
        var jsonText = JsonEncodedText(markdownText);
        return $$"""{"responseId":"resp-stream","modelVersion":"gemini-2.5-flash","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"{{jsonText}}"}]},"finishReason":{{finishReason}}}]}""";
    }

    private static string CreateThinkingAndTextChunk(int index, int total)
    {
        var finishReason = index == total - 1 ? "\"STOP\"" : "null";
        if (index < total / 2)
        {
            var thought = $"Analyzing intermediate reasoning step {index + 1} of {total}...";
            var thoughtSig = $"EhwKBFRPVE8SBk1PREVMMBoCYXMiDHRob3VnaHQtc2lnLTAxMjM0NTY3ODkt{index:D3}";
            return $$"""{"responseId":"resp-stream","modelVersion":"gemini-2.5-flash","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"{{thought}}","thought":true,"thoughtSignature":"{{thoughtSig}}"}]},"finishReason":{{finishReason}}}]}""";
        }
        else
        {
            var text = $"Synthesized conclusion for chunk {index}: evaluation complete with validated constraints.";
            return $$"""{"responseId":"resp-stream","modelVersion":"gemini-2.5-flash","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"{{text}}"}]},"finishReason":{{finishReason}}}]}""";
        }
    }

    private static string CreateToolCallingChunk(int index, int total)
    {
        var finishReason = index == total - 1 ? "\"STOP\"" : "null";
        var callId = $"call-{index:D3}";
        var thoughtSig = $"EhwKBFRPVE8SBk1PREVMMBoCYXMiDHRvb2wtc2lnLTAxMjM0NTY3ODkt{index:D3}";
        var sourceCluster = $"cluster-{index % 4}";
        return $$$$"""{"responseId":"resp-stream","modelVersion":"gemini-2.5-flash","candidates":[{"index":0,"content":{"role":"model","parts":[{"functionCall":{"id":"{{{{callId}}}}","name":"query_metrics","args":{"source":"{{{{sourceCluster}}}}","metric":"cpu_usage","threshold":0.85}},"thoughtSignature":"{{{{thoughtSig}}}}"}]},"finishReason":{{{{finishReason}}}}}]}""";
    }

    private static string CreateSearchGroundedChunk(int index, int total)
    {
        var finishReason = index == total - 1 ? "\"STOP\"" : "null";
        var uri = $"https://weather.example.com/item/{index}";
        var title = $"Weather Report {index}";
        return $$$$"""{"responseId":"resp-stream","modelVersion":"gemini-2.5-flash","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Tokyo and Kyoto weather update {{{{index}}}}: temperature is 18C."}]},"finishReason":{{{{finishReason}}}},"groundingMetadata":{"webSearchQueries":["tokyo kyoto weather {{{{index}}}}"],"groundingChunks":[{"web":{"uri":"{{{{uri}}}}","title":"{{{{title}}}}"}}],"groundingSupports":[{"segment":{"startIndex":0,"endIndex":30,"text":"Tokyo and Kyoto weather update"},"groundingChunkIndices":[0]}]}}]}""";
    }

    private static string JsonEncodedText(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static byte[] BuildSsePayload(int chunkCount, Func<int, int, string> chunkFactory)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < chunkCount; i++)
        {
            var chunk = chunkFactory(i, chunkCount);
            builder.Append("data: ").Append(chunk).Append("\r\n\r\n");
        }

        builder.Append("data: [DONE]\r\n\r\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] BuildJsonArrayPayload(int chunkCount, Func<int, int, string> chunkFactory)
    {
        var builder = new StringBuilder("[");
        for (var i = 0; i < chunkCount; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(chunkFactory(i, chunkCount));
        }

        builder.Append(']');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    [Benchmark(Baseline = true)]
    public async Task<int> ReadServerSentEvents_SimpleText()
    {
        using var stream = new MemoryStream(_sseSimpleTextPayload, writable: false);
        using var watchdog = new StreamIdleWatchdog(idleTimeout: null, CancellationToken.None);
        var count = 0;
        await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> ReadServerSentEvents_ThinkingAndText()
    {
        using var stream = new MemoryStream(_sseThinkingTextPayload, writable: false);
        using var watchdog = new StreamIdleWatchdog(idleTimeout: null, CancellationToken.None);
        var count = 0;
        await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> ReadServerSentEvents_ToolCalling()
    {
        using var stream = new MemoryStream(_sseToolCallingPayload, writable: false);
        using var watchdog = new StreamIdleWatchdog(idleTimeout: null, CancellationToken.None);
        var count = 0;
        await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> ReadServerSentEvents_SearchGrounded()
    {
        using var stream = new MemoryStream(_sseSearchGroundedPayload, writable: false);
        using var watchdog = new StreamIdleWatchdog(idleTimeout: null, CancellationToken.None);
        var count = 0;
        await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> ReadServerSentEvents_Fragmented_512B()
    {
        await using var stream = new ChunkedMemoryStream(_sseSimpleTextPayload, 512);
        using var watchdog = new StreamIdleWatchdog(idleTimeout: null, CancellationToken.None);
        var count = 0;
        await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> ReadJsonArray_SimpleText()
    {
        using var stream = new MemoryStream(_jsonArraySimpleTextPayload, writable: false);
        using var watchdog = new StreamIdleWatchdog(idleTimeout: null, CancellationToken.None);
        var count = 0;
        await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.JsonArray, HttpStatusCode.OK, watchdog))
        {
            count++;
        }

        return count;
    }

    private sealed class ChunkedMemoryStream(ReadOnlyMemory<byte> payload, int packetSize) : Stream
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
}
