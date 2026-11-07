# Text Generation

## A Single Prompt

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

var response = await client.GenerateContentAsync(
    prompt: "What is the capital of Japan? Answer in one sentence.",
    systemInstruction: "You are a concise geography assistant.",
    generationConfig: new GenerationConfig { Temperature = 0.1, MaxOutputTokens = 1024 });

Console.WriteLine(response.Text);
```

`prompt` must not be `null`, empty, or whitespace, or the call throws `ArgumentException`.

To use another model for one call, pass `model:`. It is the second parameter, so always name `systemInstruction`:

```csharp
using GeminiGatewayClient;

var response = await client.GenerateContentAsync("Hello", model: "gemini-3.8-flash");
```

With no model here or in the options, the call throws `InvalidOperationException`.

## A Full Request

`GenerateContentRequest` exposes every field this library models, and `GenerateContentRequest.FromPrompt` builds a single-turn one:

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
| `CandidateCount` | Number of candidates, for non-streaming calls. All 8 current models checked on 2026-11-06 rejected more than one with HTTP 400 |
| `ResponseMimeType`, `ResponseSchema`, `ResponseJsonSchema` | [Structured output](structured-output.md) |
| `ThinkingConfig` | [Thinking](thinking.md) |
| `ResponseLogprobs`, `Logprobs` | Log probabilities, on `Candidate.LogprobsResult`. `Logprobs` needs `ResponseLogprobs = true` and takes 1 to 20 |
| `ResponseModalities`, `MediaResolution` | [Output kinds](multimodal.md#media-in-a-response) and input media resolution |
| `EnableEnhancedCivicAnswers` | Enhanced civic answers |
| `AudioTimestamp` | Audio timestamps, for Vertex AI only |

A field this library does not model can go in `ExtensionData`, an `IDictionary<string, JsonElement>` that nearly every model type has, except `VideoOffset`. Its entries are written at the level of that object.

## Streaming

Chunks arrive as they are generated. With [`UseServerSentEvents = false`](configuration.md#streaming-format), a complete chunk may wait for the data after it:

```csharp
using GeminiGatewayClient;

await foreach (var chunk in client.StreamGenerateContentAsync("List three features of C#."))
{
    Console.Write(chunk.Text);
}
```

The request overload works the same way. Arguments, the model, and a disposed client are checked when the method is called. Nothing is sent until you enumerate, and everything else fails then. See [While Reading a Stream](errors.md#while-reading-a-stream).

`HttpClient.Timeout` stops applying once the headers arrive, so [`StreamIdleTimeout`](configuration.md#geminiclientoptions), 2 minutes by default, guards a stream that stalls and throws `TimeoutException`. Any received data resets it, including keep-alive comments from a gateway. A slow reader does not trigger it: it only measures the wait for the server.

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

`TryGetModelContent` returns `false` when no usable part exists, for example when a thinking model spends all of `MaxOutputTokens` before answering. Do not go on as if the model had answered: retry the turn or stop. It leaves out empty placeholder parts and does not change the response.

It can also return `true` with `Text` as `null`, when the only parts are thoughts or signatures. To know whether there is an answer, read `Text` and `FinishReason`.

For a stream, collect the chunks:

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

var request = new GenerateContentRequest { Contents = [Content.FromText("Tell me a short story.")] };

Content? streamedContent = await client.StreamGenerateContentAsync(request).ToModelContentAsync();
```

`ToModelContentAsync` returns `null` when there is nothing to add, so check before you add it to the history. It returns only the content, and blocked streams do not throw. To read `FinishReason`, `IsBlocked`, or `UsageMetadata`, or to show the text while you collect it, gather the chunks yourself and call `chunks.ToModelContent()`. See [Streams That End Early](errors.md#streams-that-end-early).

Do not merge or edit the parts, so that each [`thoughtSignature`](thinking.md#thought-signatures) goes back on the part that carried it.

## Safety Settings

Set a blocking threshold for each harm category:

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
| `FinishReason` | Why the first candidate stopped, or `null`, as on most stream chunks |
| `BlockReason` | Why the prompt was blocked, or `null` |
| `IsBlocked` | Whether the prompt or the first candidate was blocked by a filter. Other finish reasons, such as `Language` or `MalformedFunctionCall`, are not included |
| `Candidates` | Every candidate, with its safety ratings, citations, and grounding. Never `null`, but empty when the prompt is blocked |
| `UsageMetadata` | Token counts, or `null` |
| `ModelVersion` | The model that served the request |
| `ResponseId` | Identifier of the response |
| `PromptFeedback` | Prompt-level safety details, or `null` |

A blocked or truncated response is not an exception, so check for it:

```csharp
using GeminiGatewayClient;
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
else if (response.FinishReason == FinishReason.Stop)
{
    Console.WriteLine(response.Text);
}
else
{
    Console.WriteLine($"Stopped: {response.FinishReason}");
}
```

An enum value this library does not know yet reads as `Unknown` instead of failing, and unknown fields stay in `ExtensionData`.
