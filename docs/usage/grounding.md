# Grounding and Built-in Tools

Gemini runs these tools itself, so there is no loop on your side.

| Tool | `GeminiClient` | `IChatClient` |
|---|---|---|
| Google Search | `new Tool { GoogleSearch = new GoogleSearchTool() }` | `new HostedWebSearchTool()` |
| Code execution | `new Tool { CodeExecution = new CodeExecutionTool() }` | `new HostedCodeInterpreterTool()` |
| URL Context | `new Tool { UrlContext = new UrlContextTool() }` | `Tools` on a request from `RawRepresentationFactory` |

`ChatOptions.Tools` also accepts functions. Any other `AITool` throws `NotSupportedException` when the method is called, even for streaming. To mix these with your functions, see [Combining with Built-in Tools](function-calling.md#combining-with-built-in-tools).

## Google Search with `IChatClient`

Sources become `CitationAnnotation` items, with spans converted from Gemini's UTF-8 byte offsets to `string` indices, so they work on Japanese text. `Title`, `Url`, and the regions can each be missing:

```csharp
using GeminiGatewayClient;
using Microsoft.Extensions.AI;

ChatResponse response = await chatClient.GetResponseAsync(
    "When was .NET 10 released?",
    new ChatOptions { Tools = [new HostedWebSearchTool()] });

Console.WriteLine(response.Text);

foreach (var message in response.Messages)
{
    foreach (var citation in message.Contents.SelectMany(c => c.Annotations ?? []).OfType<CitationAnnotation>())
    {
        Console.WriteLine($"Source: {citation.Title} ({citation.Url})");

        foreach (var region in (citation.AnnotatedRegions ?? []).OfType<TextSpanAnnotatedRegion>())
        {
            Console.WriteLine($"  \"{message.Text[region.StartIndex!.Value..region.EndIndex!.Value]}\"");
        }
    }
}

if (response.AdditionalProperties?.TryGetValue(GeminiChatPropertyNames.WebSearchQueries, out var value) == true &&
    value is IEnumerable<string> queries)
{
    Console.WriteLine($"Searched: {string.Join(", ", queries)}");
}
```

Regions refer to the text of the message that holds them. With `UseFunctionInvocation()`, a response has several messages and `response.Text` joins them, so cut the text from `message.Text` as above.

### Where Annotations Live

| Response | Annotations are on |
|---|---|
| Complete, one text | That `TextContent` |
| Complete, zero or several texts | An annotation-only `AIContent`, with regions over the whole text |
| Streamed | An annotation-only `AIContent` in the update of the chunk that carried them, with regions over the whole text |

So find them as above, across all contents, and not only on `TextContent`.

An update that was already delivered is never changed, so a citation never appears later on an earlier `TextContent`. When streaming:

- Regions refer to all the text so far, not to `update.Text`. Merge the updates with `ToChatResponse()`, or collect the text yourself, before you cut it.
- The same source can appear in several annotations.
- `WebSearchQueries` is on each update, and merging keeps only the last value.

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

`GroundingSegment.StartIndex` and `EndIndex` are UTF-8 byte offsets into the text of the whole response, not `string` indices. Either can be `null`: a missing start means the beginning, and a missing end means the end. Convert them yourself here, or use `IChatClient`.

`GroundingMetadata.SearchEntryPoint` holds the search suggestions. With `IChatClient`, read it through `RawRepresentation`.

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

A URL that cannot be read does not throw. Its `UrlRetrievalStatus` says why, such as `Error`, `Paywall`, or `Unsafe`.

With `IChatClient`, there is no `ChatOptions` counterpart. Return a new request on each call, because the client adds to it, and read the result through `RawRepresentation`:

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

var response = await chatClient.GetResponseAsync("Summarize https://ai.google.dev/gemini-api/docs/url-context.", options);

if (response.RawRepresentation is GenerateContentResponse raw)
{
    var urlContext = raw.Candidates.FirstOrDefault()?.UrlContextMetadata;
}
```

`ToChatResponse()` leaves `RawRepresentation` as `null`, so when streaming, read it from each update.

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

With `IChatClient`, these parts arrive as plain `AIContent` items. Read them through `AdditionalProperties`, where `CodeExecutionOutcome` tells a failed run from a successful one. `ExecutableCodeLanguage` holds the language:

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

    if (content.AdditionalProperties?.TryGetValue(GeminiChatPropertyNames.CodeExecutionOutcome, out var outcome) == true)
    {
        Console.WriteLine($"Outcome: {outcome}");
    }

    if (content.AdditionalProperties?.TryGetValue(GeminiChatPropertyNames.CodeExecutionOutput, out var output) == true)
    {
        Console.WriteLine(output);
    }
}

Console.WriteLine(response.Text);
```
