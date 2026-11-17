using System.Diagnostics;
using GeminiGatewayClient.ExploratoryTests.Infrastructure;
using GeminiGatewayClient.Models;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests;

/// <summary>
/// Checks over a real connection that the idle timeout of a stream measures the server and not the consumer, and that
/// canceling a stream ends it at once.
/// </summary>
public sealed class StreamTimingExploratoryTests(ExploratoryTestFixture fixture, ITestOutputHelper output)
    : ExploratoryTestBase(fixture, output)
{
    private const string LongPrompt = "Write an essay of about 1000 words on the history of the bicycle.";

    [ExploratoryFact]
    public async Task Stream_ConsumerSlowerThanTheIdleTimeout_StillReceivesTheWholeAnswer()
    {
        LogSection("Consumer slower than the idle timeout");
        var idleTimeout = TimeSpan.FromSeconds(10);
        using var client = CreateGeminiClient(options => options.StreamIdleTimeout = idleTimeout);
        var chunks = 0;
        FinishReason? finishReason = null;

        await foreach (var chunk in client.StreamGenerateContentAsync(LongPrompt))
        {
            finishReason = chunk.FinishReason ?? finishReason;
            if (++chunks == 2)
            {
                // The rest of the answer arrives meanwhile, and no read is waiting for the server.
                await Task.Delay(idleTimeout + TimeSpan.FromSeconds(2));
            }
        }

        Output.WriteLine($"chunks={chunks}, finishReason={finishReason}");
        Assert.True(chunks > 2, $"The answer came in {chunks} chunks, too few to pause between them.");
        Assert.Equal(FinishReason.Stop, finishReason);
    }

    [ExploratoryFact]
    public async Task Stream_CanceledWhileTheAnswerIsArriving_EndsWithOperationCanceledAtOnce()
    {
        LogSection("Cancellation while the answer is arriving");
        using var client = CreateGeminiClient();
        using var cancellation = new CancellationTokenSource();
        var sinceFirstChunk = new Stopwatch();
        var chunks = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamGenerateContentAsync(LongPrompt, cancellationToken: cancellation.Token))
            {
                if (++chunks == 1)
                {
                    sinceFirstChunk.Start();
                    cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
                }
            }
        });

        sinceFirstChunk.Stop();
        Output.WriteLine($"chunks={chunks}, ended {sinceFirstChunk.ElapsedMilliseconds} ms after the first chunk");
        Assert.True(cancellation.IsCancellationRequested);
        Assert.InRange(sinceFirstChunk.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
    }
}
