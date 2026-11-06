# Grounding and Built-in Tools

Gemini runs these tools itself, so there is no loop on your side.

| Tool | `GeminiClient` | `IChatClient` |
|---|---|---|
| Google Search | `new Tool { GoogleSearch = new GoogleSearchTool() }` | `new HostedWebSearchTool()` |
| Code execution | `new Tool { CodeExecution = new CodeExecutionTool() }` | `new HostedCodeInterpreterTool()` |
| URL Context | `new Tool { UrlContext = new UrlContextTool() }` | `Tools` on a request from `RawRepresentationFactory` |

Any other tool type in `ChatOptions.Tools` throws `NotSupportedException`. To mix these with your functions, see [Combining with Built-in Tools](function-calling.md#combining-with-built-in-tools).

## Google Search with `IChatClient`

Sources become `CitationAnnotation` items, with spans converted from Gemini's UTF-8 byte offsets to `string` indices, so they work on Japanese text:

```csharp
using GeminiGatewayClient;
using Microsoft.Extensions.AI;

ChatResponse response = await chatClient.GetResponseAsync(
    "When was .NET 10 released?",
    new ChatOptions { Tools = [new HostedWebSearchTool()] });

var text = response.Text;
Console.WriteLine(text);

foreach (var citation in response.Messages
    .SelectMany(m => m.Contents)
    .SelectMany(c => c.Annotations ?? [])
    .OfType<CitationAnnotation>())
{
    Console.WriteLine($"Source: {citation.Title} ({citation.Url})");

    foreach (var region in (citation.AnnotatedRegions ?? []).OfType<TextSpanAnnotatedRegion>())
    {
        Console.WriteLine($"  \"{text[region.StartIndex!.Value..region.EndIndex!.Value]}\"");
    }
}

if (response.AdditionalProperties?.TryGetValue(GeminiChatPropertyNames.WebSearchQueries, out var value) == true &&
    value is IEnumerable<string> queries)
{
    Console.WriteLine($"Searched: {string.Join(", ", queries)}");
}
```

### Where Annotations Live

| Response | Annotations are on |
|---|---|
| Complete, one text | That `TextContent` |
| Complete, several texts | An annotation-only `AIContent`, with regions over the whole text |
| Streamed | An annotation-only `AIContent` in the update of the chunk that carried them, with regions over the whole text |

So find them as above, across all contents, and not only on `TextContent`. A delivered update is never changed afterwards.

In a stream, Gemini sends each grounding chunk once and counts indices across all responses. `IChatClient` resolves this for you. If you read `GroundingMetadata` from raw chunks, gather `GroundingChunks` across them yourself.

## Google Search with `GeminiClient`

```csharp
using GeminiGatewayClient.Models;

var response = await client.GenerateContentAsync(new GenerateContentRequest
{
    Contents = [Content.FromText("When was .NET 10 released?")],
    Tools = [new Tool { GoogleSearch = new GoogleSearchTool() }]
});

if (response.Candidates.FirstOrDefault()?.GroundingMetadata is { } grounding)
{
    Console.WriteLine(string.Join(", ", grounding.WebSearchQueries));

    foreach (var chunk in grounding.GroundingChunks)
    {
        Console.WriteLine($"{chunk.Web?.Title}: {chunk.Web?.Uri}");
    }

    foreach (var support in grounding.GroundingSupports)
    {
        Console.WriteLine($"{support.Segment?.Text} <- {string.Join(",", support.GroundingChunkIndices)}");
    }
}
```

`GroundingSegment.StartIndex` and `EndIndex` are UTF-8 byte offsets, not `string` indices. Convert them yourself here, or use `IChatClient`.

## URL Context

The model reads the URLs named in the prompt:

```csharp
using GeminiGatewayClient.Models;

var response = await client.GenerateContentAsync(new GenerateContentRequest
{
    Contents = [Content.FromText("Summarize https://ai.google.dev/gemini-api/docs/url-context in two sentences.")],
    Tools = [new Tool { UrlContext = new UrlContextTool() }]
});

if (response.Candidates.FirstOrDefault()?.UrlContextMetadata is { } urlContext)
{
    foreach (var item in urlContext.UrlMetadata)
    {
        Console.WriteLine($"{item.RetrievedUrl}: {item.UrlRetrievalStatus}");
    }
}
```

With `IChatClient`, there is no `ChatOptions` counterpart:

```csharp
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

var options = new ChatOptions
{
    RawRepresentationFactory = _ => new GenerateContentRequest
    {
        Tools = [new Tool { UrlContext = new UrlContextTool() }]
    }
};
```

## Code Execution

The model writes Python and runs it in Google's sandbox:

```csharp
using GeminiGatewayClient.Models;

var response = await client.GenerateContentAsync(new GenerateContentRequest
{
    Contents = [Content.FromText("Run Python to sum the multiples of 7 from 1 to 100.")],
    Tools = [new Tool { CodeExecution = new CodeExecutionTool() }]
});

foreach (var part in response.Candidates.FirstOrDefault()?.Content?.Parts ?? [])
{
    if (part.ExecutableCode is { } code)
    {
        Console.WriteLine($"[{code.Language}] {code.Code}");
    }
    else if (part.CodeExecutionResult is { } result)
    {
        Console.WriteLine($"[{result.Outcome}] {result.Output}");
    }
}

Console.WriteLine(response.Text);
```

With `IChatClient`, these parts arrive as plain `AIContent` items. Read them through `AdditionalProperties`:

```csharp
using GeminiGatewayClient;
using Microsoft.Extensions.AI;

var response = await chatClient.GetResponseAsync(
    "Run Python to sum the multiples of 7 from 1 to 100.",
    new ChatOptions { Tools = [new HostedCodeInterpreterTool()] });

foreach (var content in response.Messages.SelectMany(m => m.Contents))
{
    if (content.AdditionalProperties?.TryGetValue(GeminiChatPropertyNames.ExecutableCode, out var code) == true)
    {
        Console.WriteLine(code);
    }

    if (content.AdditionalProperties?.TryGetValue(GeminiChatPropertyNames.CodeExecutionOutput, out var output) == true)
    {
        Console.WriteLine(output);
    }
}
```
