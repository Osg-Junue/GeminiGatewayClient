using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using GeminiGatewayClient.Http;

namespace GeminiGatewayClient.Benchmarks;

[MemoryDiagnoser]
public class StreamReaderBenchmarks
{
    private byte[] _ssePayload = null!;
    private byte[] _jsonArrayPayload = null!;

    [GlobalSetup]
    public void Setup()
    {
        var sseBuilder = new StringBuilder();
        var jsonBuilder = new StringBuilder("[");
        for (var i = 0; i < 16; i++)
        {
            var chunk = $$$"""{"responseId":"resp-stream","modelVersion":"gemini-2.5-flash","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"Chunk {{{i}}} content "}]},"finishReason":null}]}""";
            sseBuilder.Append("data: ").Append(chunk).Append("\r\n\r\n");
            if (i > 0)
            {
                jsonBuilder.Append(',');
            }

            jsonBuilder.Append(chunk);
        }

        sseBuilder.Append("data: [DONE]\r\n\r\n");
        jsonBuilder.Append(']');

        _ssePayload = Encoding.UTF8.GetBytes(sseBuilder.ToString());
        _jsonArrayPayload = Encoding.UTF8.GetBytes(jsonBuilder.ToString());
    }

    [Benchmark]
    public async Task<int> ReadServerSentEvents_16Chunks()
    {
        using var stream = new MemoryStream(_ssePayload, writable: false);
        using var watchdog = new StreamIdleWatchdog(idleTimeout: null, CancellationToken.None);
        var count = 0;
        await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.ServerSentEvents, HttpStatusCode.OK, watchdog))
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> ReadJsonArray_16Chunks()
    {
        using var stream = new MemoryStream(_jsonArrayPayload, writable: false);
        using var watchdog = new StreamIdleWatchdog(idleTimeout: null, CancellationToken.None);
        var count = 0;
        await foreach (var _ in GeminiStreamReader.ReadAsync(stream, GeminiStreamingFormat.JsonArray, HttpStatusCode.OK, watchdog))
        {
            count++;
        }

        return count;
    }
}
