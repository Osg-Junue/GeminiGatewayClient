# Text Generation

## A Single Prompt

```csharp
using GeminiGatewayClient.Models;

var response = await client.GenerateContentAsync(
    prompt: "What is the capital of Japan? Answer in one sentence.",
    systemInstruction: "You are a concise geography assistant.",
    generationConfig: new GenerationConfig { Temperature = 0.1, MaxOutputTokens = 1024 });

Console.WriteLine(response.Text);
```

`prompt` must not be empty or whitespace.

## A Full Request

`GenerateContentRequest` exposes every field, and `FromPrompt` builds a single-turn one:

```csharp
using GeminiGatewayClient.Models;

var request = new GenerateContentRequest
{
    SystemInstruction = new Content { Parts = [Part.FromText("Answer in Japanese.")] },
    Contents = [Content.FromText("What is the tallest mountain in Japan?")],
    GenerationConfig = new GenerationConfig { Temperature = 0.2, MaxOutputTokens = 512 }
};

var response = await client.GenerateContentAsync(request);
```

### `GenerationConfig`

| Property | Purpose |
|---|---|
| `Temperature`, `TopP`, `TopK` | Sampling |
| `MaxOutputTokens` | Output limit, which thinking tokens also use |
| `StopSequences` | Text that ends generation |
| `PresencePenalty`, `FrequencyPenalty` | Repetition control |
| `Seed` | Best-effort deterministic output |
| `CandidateCount` | Number of candidates. All 8 current models checked on 2026-11-06 rejected more than one with HTTP 400 |
| `ResponseMimeType`, `ResponseSchema`, `ResponseJsonSchema` | [Structured output](structured-output.md) |
| `ThinkingConfig` | [Thinking](thinking.md) |
| `ResponseLogprobs`, `Logprobs` | Log probabilities, on `Candidate.LogprobsResult` |
| `ResponseModalities`, `MediaResolution` | Output kinds and input media resolution |

A field this library does not model can go in `ExtensionData`, which every model type has.

## Streaming

Chunks arrive as they are generated:

```csharp
await foreach (var chunk in client.StreamGenerateContentAsync("List three features of C#."))
{
    Console.Write(chunk.Text);
}
```

The request overload works the same way. Arguments are checked when the method is called, and everything else fails during enumeration. See [Streaming Failures](errors.md#while-reading-a-stream).

`HttpClient.Timeout` stops applying once the headers arrive, so `StreamIdleTimeout` guards a stream that stalls. Any received data resets it, including keep-alive comments from a gateway. A slow reader does not trigger it: it only measures the wait for the server.

## Multi-Turn History

You own the history. Append the model's content, then the next user turn.

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

var contents = new List<Content> { Content.FromText("My name is Aoi.") };

var first = await client.GenerateContentAsync(new GenerateContentRequest { Contents = contents });
if (first.TryGetModelContent(out var modelContent))
{
    contents.Add(modelContent);
}

contents.Add(Content.FromText("What is my name?"));
var second = await client.GenerateContentAsync(new GenerateContentRequest { Contents = contents });
Console.WriteLine(second.Text);
```

`TryGetModelContent` returns `false` when there is nothing to add, for example when a thinking model spends all of `MaxOutputTokens` before answering. It leaves out empty placeholder parts and does not change the response.

For a stream, collect the chunks:

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

var request = new GenerateContentRequest { Contents = [Content.FromText("Tell me a short story.")] };

Content? streamedContent = await client.StreamGenerateContentAsync(request).ToModelContentAsync();
```

To show the text while you collect it, gather the chunks yourself and call `chunks.ToModelContent()`. Keep the parts as they are: each `thoughtSignature` must go back unchanged, and Gemini forbids merging a signed part with an unsigned one.

## Safety Settings

```csharp
using GeminiGatewayClient.Models;

var request = new GenerateContentRequest
{
    Contents = [Content.FromText("Hello")],
    SafetySettings =
    [
        new SafetySetting { Category = HarmCategory.Harassment, Threshold = HarmBlockThreshold.BlockOnlyHigh }
    ]
};
```

## Reading a Response

| Member | Value |
|---|---|
| `Text` | All non-thought text of the first candidate, or `null` |
| `FunctionCalls` | Function calls of the first candidate, never `null` |
| `FinishReason` | Why the first candidate stopped |
| `BlockReason` | Why the prompt was blocked, or `null` |
| `IsBlocked` | Whether the prompt or the first candidate was blocked |
| `Candidates` | Every candidate, with its safety ratings, citations, and grounding |
| `UsageMetadata` | Token counts |
| `ModelVersion`, `ResponseId` | What served the request |
| `PromptFeedback` | Prompt-level safety details |

A blocked or truncated response is not an exception, so check for it:

```csharp
using GeminiGatewayClient.Models;

var response = await client.GenerateContentAsync("Hello");

if (response.IsBlocked)
{
    Console.WriteLine($"Blocked: {response.BlockReason} {response.FinishReason}");
}
else if (response.FinishReason == FinishReason.MaxTokens)
{
    Console.WriteLine($"Truncated: {response.Text}");
}
else
{
    Console.WriteLine(response.Text);
}
```

A value this library does not know yet reads as `Unknown` instead of failing, and unknown fields stay in `ExtensionData`.
