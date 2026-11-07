# Thinking

Thinking models reason before they answer. `ThinkingConfig` has three settings:

| Property | Models | Meaning |
|---|---|---|
| `ThinkingLevel` | Gemini 3 and later | `Minimal`, `Low`, `Medium`, or `High`. `Unknown` only reads a value this library does not know |
| `ThinkingBudget` | Gemini 2.5 | Token budget: `-1` for dynamic, `0` to disable where supported |
| `IncludeThoughts` | Both | Whether thought summaries come back in the response |

The API rejects `ThinkingLevel` and `ThinkingBudget` together with HTTP 400. The library sends what you set and checks neither that rule nor the model. Check which values your model accepts: a rejected request fails with a [`GeminiApiException`](errors.md).

## With `GeminiClient`

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

var request = GenerateContentRequest.FromPrompt(
    prompt: "Solve this logic puzzle step by step...",
    generationConfig: new GenerationConfig
    {
        ThinkingConfig = new ThinkingConfig { ThinkingLevel = ThinkingLevel.Low, IncludeThoughts = true }
    });

var response = await client.GenerateContentAsync(request);

if (response.TryGetModelContent(out var modelContent))
{
    foreach (var part in modelContent.Parts.Where(p => p.Thought == true && !string.IsNullOrEmpty(p.Text)))
    {
        Console.WriteLine($"[Thought] {part.Text}");
    }
}

Console.WriteLine($"[Answer] {response.Text}");
Console.WriteLine($"[Thinking tokens] {response.UsageMetadata?.ThoughtsTokenCount}");
```

`response.Text` leaves thoughts out, and so does `chunk.Text` in a stream.

## With `IChatClient`

`ChatOptions.Reasoning` maps to `ThinkingConfig` without looking at the model:

| `ReasoningOptions` | Sent as |
|---|---|
| `Effort = None` | `thinkingLevel: MINIMAL` |
| `Effort = Low`, `Medium`, `High` | `LOW`, `MEDIUM`, `HIGH` |
| `Effort = ExtraHigh` | `HIGH` |
| `Output = None` | `includeThoughts: false` |
| `Output = Summary`, `Full` | `includeThoughts: true` |
| `Effort` not set | No `thinkingLevel` |
| `Output` not set | No `includeThoughts` |

```csharp
using Microsoft.Extensions.AI;

var response = await chatClient.GetResponseAsync(
    "Calculate 2^10 - 2^5.",
    new ChatOptions
    {
        Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low, Output = ReasoningOutput.Summary }
    });

foreach (var reasoning in response.Messages
    .SelectMany(m => m.Contents)
    .OfType<TextReasoningContent>()
    .Where(r => !string.IsNullOrEmpty(r.Text)))
{
    Console.WriteLine($"[Thought] {reasoning.Text}");
}

Console.WriteLine($"[Answer] {response.Text}");
```

An empty `TextReasoningContent` only carries a signature, so skip it when displaying thoughts and keep it in the history.

Thinking tokens are in `response.Usage?.ReasoningTokenCount`, and `OutputTokenCount` includes them.

When streaming, thoughts arrive in `update.Contents` as `TextReasoningContent`, and `update.Text` leaves them out. Usage comes once, in the last update.

### `None` Does Not Turn Thinking Off

No level turns thinking off, so `None` asks for the lowest one. Several models reject that level. Measured on 2026-11-06:

| Model | `None` (`MINIMAL`) | `Low`, `Medium`, `High` |
|---|---|---|
| `gemini-flash-latest` | HTTP 400 | Accepted |
| `gemini-3.8-flash` | HTTP 400 | Accepted |
| `gemini-pro-latest` | HTTP 400 | Accepted |
| `gemini-3.1-pro-preview` | HTTP 400 | Accepted |
| `gemini-3.5-flash-lite` | Accepted | Accepted |
| `gemini-3-flash-preview` | Accepted | Accepted |

Models change, so treat this as an example. If a model rejects `None`, leave `Effort` unset, which sends no `thinkingLevel`, or use `Low`.

### Gemini 2.5 and `thinkingBudget`

`Reasoning` never sends `thinkingBudget`, and `Effort` sends `thinkingLevel` to Gemini 2.5 too. For a budget, set `ThinkingConfig` through `RawRepresentationFactory`, which must return a new request on each call. Values it sets win over `Reasoning`, value by value: if it sets a level or budget, `Effort` is ignored, and an `includeThoughts` it leaves unset still comes from `Output`:

```csharp
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

var options = new ChatOptions
{
    RawRepresentationFactory = _ => new GenerateContentRequest
    {
        GenerationConfig = new GenerationConfig
        {
            ThinkingConfig = new ThinkingConfig { ThinkingBudget = 1024, IncludeThoughts = true }
        }
    }
};
```

## Thought Signatures

Gemini may return a `thoughtSignature`. Send it back unchanged on the next turn, on the part that carried it. Measured on `gemini-3.8-flash` and `gemini-3-flash-preview` on 2026-11-06:

| Sent back | Result |
|---|---|
| A function call without its signature | HTTP 400 |
| A text without its signature | Accepted |
| Streamed text merged into one part, with the signature moved onto it | Accepted |
| A function call with an altered signature | HTTP 400 |

Of these, only a problem with a function call's signature breaks a conversation today, but resending parts as they arrived is what Google's guide asks for, and the safe choice. A rejected request fails with a `GeminiApiException`, and a response can also end with `FinishReason.MissingThoughtSignature`.

- With `GeminiClient`, resend the `Content` from `TryGetModelContent`, or for a stream from `ToModelContent` or `ToModelContentAsync`.
- With `IChatClient`, resend the messages. See [Persisting History](chat-client.md#persisting-history). `ToChatResponse()` merges neighboring streamed text, and each signature moves onto the merged part, which the table shows is accepted. To keep every part as it arrived, use `GeminiClient`.

## Output Limits

Thinking tokens count against `MaxOutputTokens`. If they use it up, the candidate can end with `FinishReason.MaxTokens` and no parts. `response.Text` is then `null` and `TryGetModelContent` returns `false`. If only thought parts came back, `TryGetModelContent` returns `true` with `Text` still `null`, so read `Text` to know whether there is an answer.

With `IChatClient`, `FinishReason` is `ChatFinishReason.Length` and `Text` is an empty string.

Thinking also takes time. The client's default `HttpClient.Timeout` is 5 minutes, and `StreamIdleTimeout` is 2 minutes. See [Configuration](configuration.md#geminiclientoptions) and [Resilience](configuration.md#resilience).
