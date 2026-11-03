using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests.Infrastructure;

/// <summary>
/// Base class for live Gemini exploratory tests providing client factories and structured diagnostic output.
/// </summary>
[Collection(ExploratoryTestCollection.CollectionName)]
[Trait("Category", "Exploratory")]
public abstract class ExploratoryTestBase(ExploratoryTestFixture fixture, ITestOutputHelper output)
{
    protected ExploratoryTestFixture Fixture { get; } = fixture;

    protected ITestOutputHelper Output { get; } = output;

    protected string DefaultModel => Fixture.SelectedModel;

    protected string DefaultEmbeddingModel => "gemini-embedding-001";

    protected GeminiClient CreateGeminiClient(Action<GeminiClientOptions>? configure = null)
    {
        var options = new GeminiClientOptions
        {
            ApiKey = ExploratoryTestSettings.GetRequiredApiKey(),
            Model = DefaultModel,
            EmbeddingModel = DefaultEmbeddingModel,
            Timeout = TimeSpan.FromSeconds(60),
            StreamIdleTimeout = TimeSpan.FromSeconds(30)
        };
        configure?.Invoke(options);

        // GeminiClientOptions.ToString() redacts ApiKey and AdditionalHeaders values automatically.
        Output.WriteLine($"[Client Created] Source={ExploratoryTestSettings.ApiKeySource}, Options={options}");
        return new GeminiClient(options);
    }

    protected IChatClient CreateChatClient(Action<GeminiClientOptions>? configure = null)
    {
        var client = CreateGeminiClient(configure);
        return client.AsIChatClient();
    }

    protected void LogSection(string title)
    {
        Output.WriteLine(string.Empty);
        Output.WriteLine($"=== {title} ===");
    }

    protected void LogResponseSummary(GenerateContentResponse response, TimeSpan? elapsed = null)
    {
        var elapsedText = elapsed is { } e ? $" ({e.TotalMilliseconds:F0} ms)" : string.Empty;
        Output.WriteLine($"[Response{elapsedText}] ModelVersion={response.ModelVersion ?? "<null>"}, ResponseId={response.ResponseId ?? "<null>"}, FinishReason={response.FinishReason}");

        if (response.BlockReason is { } blockReason)
        {
            Output.WriteLine($"[Blocked] BlockReason={blockReason}");
        }

        if (response.UsageMetadata is { } usage)
        {
            Output.WriteLine(
                $"[Usage] Prompt={usage.PromptTokenCount}, Candidates={usage.CandidatesTokenCount}, " +
                $"Thoughts={usage.ThoughtsTokenCount ?? 0}, ToolUsePrompt={usage.ToolUsePromptTokenCount ?? 0}, Total={usage.TotalTokenCount}");
        }

        if (response.Candidates is { Count: > 0 } candidates && candidates[0] is { } candidate)
        {
            if (candidate.Content?.Parts is { } parts)
            {
                for (var i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    if (part.Thought == true)
                    {
                        Output.WriteLine($"  Part[{i}] (Thought, sig={!string.IsNullOrEmpty(part.ThoughtSignature)}): {part.Text}");
                    }
                    else if (part.FunctionCall is { } call)
                    {
                        Output.WriteLine($"  Part[{i}] (FunctionCall, id={call.Id ?? "<none>"}, sig={!string.IsNullOrEmpty(part.ThoughtSignature)}): {call.Name}({call.Args})");
                    }
                    else if (!string.IsNullOrEmpty(part.Text))
                    {
                        Output.WriteLine($"  Part[{i}] (Text, sig={!string.IsNullOrEmpty(part.ThoughtSignature)}): {part.Text}");
                    }
                    else if (!string.IsNullOrEmpty(part.ThoughtSignature))
                    {
                        Output.WriteLine($"  Part[{i}] (SignatureOnly, sigLen={part.ThoughtSignature.Length})");
                    }
                }
            }

            if (candidate.GroundingMetadata is { } grounding)
            {
                Output.WriteLine(
                    $"[GroundingMetadata] Queries=[{string.Join(", ", grounding.WebSearchQueries)}], " +
                    $"Chunks={grounding.GroundingChunks.Count}, Supports={grounding.GroundingSupports.Count}, " +
                    $"HasSearchEntryPoint={grounding.SearchEntryPoint is not null}");
                for (var i = 0; i < grounding.GroundingChunks.Count; i++)
                {
                    var chunk = grounding.GroundingChunks[i];
                    if (chunk.Web is { } web)
                    {
                        Output.WriteLine($"  Chunk[{i}] (Web): title={web.Title ?? "<null>"}, domain={web.Domain ?? "<null>"}, uri={web.Uri ?? "<null>"}");
                    }
                    else if (chunk.RetrievedContext is { } rc)
                    {
                        Output.WriteLine($"  Chunk[{i}] (RetrievedContext): title={rc.Title ?? "<null>"}, uri={rc.Uri ?? "<null>"}");
                    }
                }
            }

            if (candidate.UrlContextMetadata is { } urlContext)
            {
                Output.WriteLine($"[UrlContextMetadata] UrlCount={urlContext.UrlMetadata.Count}");
                for (var i = 0; i < urlContext.UrlMetadata.Count; i++)
                {
                    var meta = urlContext.UrlMetadata[i];
                    Output.WriteLine($"  Url[{i}]: status={meta.UrlRetrievalStatus}, url={meta.RetrievedUrl}");
                }
            }
        }
    }

    protected void LogChatResponseSummary(ChatResponse response, TimeSpan? elapsed = null)
    {
        var elapsedText = elapsed is { } e ? $" ({e.TotalMilliseconds:F0} ms)" : string.Empty;
        Output.WriteLine($"[ChatResponse{elapsedText}] ModelId={response.ModelId ?? "<null>"}, ResponseId={response.ResponseId ?? "<null>"}, FinishReason={response.FinishReason}");

        if (response.Usage is { } usage)
        {
            Output.WriteLine(
                $"[ChatUsage] Input={usage.InputTokenCount}, Output={usage.OutputTokenCount}, " +
                $"Reasoning={usage.ReasoningTokenCount ?? 0}, CachedInput={usage.CachedInputTokenCount ?? 0}, Total={usage.TotalTokenCount}");
        }

        if (response.AdditionalProperties?.TryGetValue(GeminiChatPropertyNames.WebSearchQueries, out var queriesObj) == true &&
            queriesObj is IEnumerable<string> queries)
        {
            Output.WriteLine($"[ChatAdditionalProperties] WebSearchQueries=[{string.Join(", ", queries)}]");
        }

        for (var m = 0; m < response.Messages.Count; m++)
        {
            var msg = response.Messages[m];
            Output.WriteLine($"  Message[{m}] Role={msg.Role}:");
            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent reasoning:
                        Output.WriteLine($"    [Reasoning (hasProtectedData={!string.IsNullOrEmpty(reasoning.ProtectedData)})] {reasoning.Text}");
                        break;
                    case FunctionCallContent fc:
                        var argsJson = fc.Arguments is null
                            ? "{}"
                            : string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
                        Output.WriteLine($"    [FunctionCall id={fc.CallId}] {fc.Name}({argsJson})");
                        break;
                    case FunctionResultContent fr:
                        Output.WriteLine($"    [FunctionResult id={fr.CallId}] {fr.Result}");
                        break;
                    case TextContent tc:
                        Output.WriteLine($"    [Text] {tc.Text}");
                        if (tc.Annotations is { Count: > 0 } annotations)
                        {
                            for (var a = 0; a < annotations.Count; a++)
                            {
                                if (annotations[a] is CitationAnnotation ca)
                                {
                                    var regionsDesc = ca.AnnotatedRegions is { Count: > 0 } regions
                                        ? string.Join(", ", regions.OfType<TextSpanAnnotatedRegion>().Select(r => $"[{r.StartIndex}..{r.EndIndex}]"))
                                        : "<none>";
                                    Output.WriteLine($"      Annotation[{a}] (Citation): title={ca.Title}, tool={ca.ToolName}, regions={regionsDesc}, url={ca.Url}");
                                }
                            }
                        }
                        break;
                }
            }
        }
    }
}
