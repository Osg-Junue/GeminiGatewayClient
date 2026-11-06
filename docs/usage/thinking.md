# Thinking

Thinking models reason before they answer. `ThinkingConfig` has three settings:

| Property | Models | Meaning |
|---|---|---|
| `ThinkingLevel` | Gemini 3 and later | `Minimal`, `Low`, `Medium`, or `High` |
| `ThinkingBudget` | Gemini 2.5 | Token budget: `-1` for dynamic, `0` to disable where supported |
| `IncludeThoughts` | Both | Whether thought summaries come back in the response |

`ThinkingLevel` and `ThinkingBudget` cannot be set together. Which values a model accepts is yours to decide, and a model that does not accept one answers HTTP 400.

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
    foreach (var part in modelContent.Parts.Where(p => p.Thought == true))
    {
        Console.WriteLine($"[Thought] {part.Text}");
    }
}

Console.WriteLine($"[Answer] {response.Text}");
Console.WriteLine($"[Thinking tokens] {response.UsageMetadata?.ThoughtsTokenCount}");
```

`response.Text` leaves thoughts out.

## With `IChatClient`

`ChatOptions.Reasoning` maps to `ThinkingConfig` without looking at the model:

| `ReasoningOptions` | Sent as |
|---|---|
| `Effort = None` | `thinkingLevel: MINIMAL` |
| `Effort = Low`, `Medium`, `High` | `LOW`, `MEDIUM`, `HIGH` |
| `Effort = ExtraHigh` | `HIGH` |
| `Output = None` | `includeThoughts: false` |
| `Output = Summary`, `Full` | `includeThoughts: true` |

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

Models change, so treat this as an example.

### Gemini 2.5 and Other Settings

`Reasoning` never sends `thinkingBudget`. For that, set `ThinkingConfig` through `RawRepresentationFactory`, which wins over `Reasoning` and must return a new request on each call:

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
| A text with its signature moved onto merged text | Accepted |
| An altered signature | HTTP 400 |

Only the function call case breaks a conversation today, but resending parts as they arrived is what Google's guide asks for, and the safe choice.

- With `GeminiClient`, resend the `Content` from `TryGetModelContent` or `ToModelContent`.
- With `IChatClient`, resend the messages. See [Persisting History](chat-client.md#persisting-history).

## Output Limits

Thinking tokens count against `MaxOutputTokens`. If they use it up, the candidate can end with `FinishReason.MaxTokens` and no parts. `response.Text` is then `null` and `TryGetModelContent` returns `false`.

Thinking also takes time. Check your [timeouts](configuration.md#resilience).
