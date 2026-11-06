using System.Text;
using GeminiGatewayClient.ExploratoryTests.Infrastructure;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests;

/// <summary>
/// Checks against the real API that citation regions point at the text Gemini says they support.
/// </summary>
public sealed class GroundingOffsetsExploratoryTests(ExploratoryTestFixture fixture, ITestOutputHelper output)
    : ExploratoryTestBase(fixture, output)
{
    private const string Prompt = "Google検索を使って、東京都の人口と面積を調べ、3文の日本語で答えてください。";

    private static ChatOptions SearchOptions() => new() { Temperature = 0.0f, Tools = [new HostedWebSearchTool()] };

    private void LogGrounding(string label, GenerateContentResponse response)
    {
        var candidate = response.Candidates.FirstOrDefault();
        var parts = candidate?.Content?.Parts ?? [];
        Output.WriteLine($"{label}: parts=[{string.Join(", ", parts.Select(p => Encoding.UTF8.GetByteCount(p.Text ?? string.Empty)))}] bytes");
        foreach (var support in candidate?.GroundingMetadata?.GroundingSupports ?? [])
        {
            var segment = support.Segment;
            Output.WriteLine($"  support partIndex={segment?.PartIndex?.ToString() ?? "-"} bytes={segment?.StartIndex}..{segment?.EndIndex} text='{segment?.Text}'");
        }
    }

    [ExploratoryFact]
    public async Task NonStreaming_RegionsMatchTheSegmentText()
    {
        LogSection("Grounding offsets (non-streaming)");
        using var client = CreateGeminiClient();

        var response = await client.AsIChatClient().GetResponseAsync(Prompt, SearchOptions());

        var raw = Assert.IsType<GenerateContentResponse>(response.RawRepresentation);
        LogGrounding("response", raw);
        AssertRegions(response.Messages.SelectMany(m => m.Contents).OfType<TextContent>().Select(t => (t.Text, (AIContent)t)), [raw]);
    }

    [ExploratoryFact]
    public async Task Streaming_RegionsMatchTheSegmentText()
    {
        LogSection("Grounding offsets (streaming)");
        using var client = CreateGeminiClient();
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in client.AsIChatClient().GetStreamingResponseAsync(Prompt, SearchOptions()))
        {
            updates.Add(update);
            if (update.RawRepresentation is GenerateContentResponse chunk)
            {
                LogGrounding($"chunk {updates.Count}", chunk);
            }
        }

        var fullText = string.Concat(updates.SelectMany(u => u.Contents).OfType<TextContent>().Select(t => t.Text));
        var chunks = updates.Select(u => u.RawRepresentation).OfType<GenerateContentResponse>();
        AssertRegions(updates.SelectMany(u => u.Contents).Where(c => c.Annotations is { Count: > 0 }).Select(c => (fullText, c)), chunks);
    }

    [ExploratoryFact]
    public async Task ToolModeWithoutFunctionDeclarations_IsAccepted()
    {
        LogSection("Tool mode without function declarations");
        using var client = CreateGeminiClient();

        var response = await client.AsIChatClient().GetResponseAsync("こんにちは", new ChatOptions { ToolMode = ChatToolMode.Auto });

        Assert.False(string.IsNullOrEmpty(response.Text));
    }

    private void AssertRegions(IEnumerable<(string Text, AIContent Content)> targets, IEnumerable<GenerateContentResponse> responses)
    {
        var segments = responses
            .SelectMany(r => r.Candidates.FirstOrDefault()?.GroundingMetadata?.GroundingSupports ?? [])
            .Select(s => s.Segment?.Text)
            .ToHashSet();
        var checkedRegions = 0;
        var mismatches = 0;
        foreach (var (text, content) in targets)
        {
            foreach (var annotation in content.Annotations?.OfType<CitationAnnotation>() ?? [])
            {
                foreach (var region in annotation.AnnotatedRegions?.OfType<TextSpanAnnotatedRegion>() ?? [])
                {
                    var slice = text[region.StartIndex!.Value..region.EndIndex!.Value];
                    Output.WriteLine($"  region {region.StartIndex}..{region.EndIndex}: '{slice}'");
                    checkedRegions++;
                    mismatches += segments.Contains(slice) ? 0 : 1;
                }
            }
        }

        Output.WriteLine($"checked {checkedRegions} regions, {mismatches} differ from every segment");
        Assert.True(checkedRegions > 0, "The response carried no regions.");
        Assert.Equal(0, mismatches);
    }
}
