# `IChatClient`

`client.AsIChatClient()` adapts the client to `Microsoft.Extensions.AI`, so the middleware and provider-neutral code of that library work with Gemini. `ChatClientBuilder` and the middleware are in the `Microsoft.Extensions.AI` package, so [add it](getting-started.md#install) to use them.

```csharp
using GeminiGatewayClient;
using Microsoft.Extensions.AI;

IChatClient chatClient = client.AsIChatClient();

ChatResponse response = await chatClient.GetResponseAsync(
    [
        new ChatMessage(ChatRole.System, "You are a concise assistant."),
        new ChatMessage(ChatRole.User, "What is the capital of Japan?")
    ],
    new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 512 });

Console.WriteLine(response.Text);
```

| Parameter of `AsIChatClient` | Purpose |
|---|---|
| `defaultModelId` | Model when `ChatOptions.ModelId` is not set. Defaults to `IGeminiClient.DefaultModel`. With neither, the call throws `InvalidOperationException` |
| `toolSerializerOptions` | Serializer options for function arguments and results in the history. Defaults to `AIJsonUtilities.DefaultOptions`. See [Native AOT](native-aot.md#tools-with-ichatclient) |

Disposing the chat client does nothing, because it does not own the `GeminiClient`.

[`AddGeminiClient`](configuration.md#dependency-injection) registers an `IChatClient` with the default parameters. API failures arrive as a [`GeminiApiException`](errors.md).

## `ChatOptions`

| Option | Sent as |
|---|---|
| `ModelId` | The model in the URL |
| `Instructions` | `systemInstruction`, before any system messages |
| `Temperature`, `TopP`, `TopK`, `MaxOutputTokens` | `generationConfig` fields |
| `PresencePenalty`, `FrequencyPenalty`, `StopSequences` | `generationConfig` fields |
| `Seed` | `generationConfig.seed`. A value outside the 32-bit range throws `OverflowException` |
| `ResponseFormat` | [Structured output](structured-output.md#with-ichatclient) |
| `Reasoning` | [`thinkingConfig`](thinking.md#with-ichatclient) |
| `Tools` | [Functions](function-calling.md#automatic-function-calling-with-ichatclient) and [built-in tools](grounding.md). Any other tool throws `NotSupportedException` |
| `ToolMode` | [`toolConfig.functionCallingConfig`](function-calling.md#automatic-function-calling-with-ichatclient) |
| `RawRepresentationFactory` | See below |

`ChatOptions.AdditionalProperties`, `AllowMultipleToolCalls`, `AllowBackgroundResponses`, and `ContinuationToken` are not read.

### Gemini-Specific Settings

Anything without a `ChatOptions` counterpart goes through `RawRepresentationFactory`. Return a `GenerateContentRequest`. Any other type is ignored. Values you set win over `ChatOptions`, field by field. `Tools` are added to the ones from `ChatOptions`, and messages are added after `Contents`:

```csharp
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

var options = new ChatOptions
{
    Temperature = 0.5f,
    RawRepresentationFactory = _ => new GenerateContentRequest
    {
        SafetySettings = [new SafetySetting { Category = HarmCategory.Harassment, Threshold = HarmBlockThreshold.BlockOnlyHigh }],
        CachedContent = "cachedContents/abc123"
    }
};
```

> [!IMPORTANT]
> Return a **new** instance on each call. The client adds to it, so a shared instance collects messages from every call. `UseFunctionInvocation()` calls the factory on each round.

## Messages

| `ChatRole` | Sent as |
|---|---|
| `System` | Text joins `systemInstruction`, wherever the message is in the history. Other content is ignored. If `RawRepresentationFactory` sets `SystemInstruction`, neither `Instructions` nor system messages are sent |
| `Assistant` | `model` |
| `User`, `Tool`, and any other | `user` |

Consecutive messages of one role merge into one content, such as parallel tool results. `Tool` and `User` stay separate, so a user's follow-up does not mix into a `functionResponse` turn. A message that produces no parts is skipped.

| Content | Sent as |
|---|---|
| `TextContent` | Text. Empty text is skipped |
| `TextReasoningContent` | A thought part, or a signature-only part, with its signature |
| `DataContent`, `UriContent` | [Media](multimodal.md#with-ichatclient) |
| `FunctionCallContent`, `FunctionResultContent` | [Function calling](function-calling.md#running-the-loop-yourself) |
| `HostedFileContent` | Throws `NotSupportedException` |
| Other kinds | Resent from `RawRepresentation` or the `gemini.*` properties, otherwise dropped |

## Responses

`ChatResponse.Messages` holds one assistant message built from the first candidate, and with no candidate it has no contents. The message's `RawRepresentation` is the `Candidate`.

| Gemini | `ChatResponse` |
|---|---|
| `responseId` | `ResponseId` |
| `modelVersion` | `ModelId`, from the candidate's or the response's value, falling back to the requested model |
| The whole response | `RawRepresentation`, a `GenerateContentResponse` |
| `usageMetadata` | `Usage` |

`UsageDetails` count thoughts as output and tool prompts as input, whereas Gemini reports them apart. `ReasoningTokenCount` and `CachedInputTokenCount` are filled in when Gemini reports them.

### Finish Reasons

The first matching row applies:

| Condition | `ChatFinishReason` |
|---|---|
| `MalformedFunctionCall`, `UnexpectedToolCall`, `TooManyToolCalls` | `null` |
| The response has a function call | `ToolCalls` |
| The prompt was blocked | `ContentFilter` |
| `Stop` | `Stop` |
| `MaxTokens` | `Length` |
| `Safety`, `Recitation`, `BlockList`, `ProhibitedContent`, `Spii`, `ImageSafety`, `ImageProhibitedContent`, `ImageRecitation` | `ContentFilter` |
| Anything else | `null` |

A blocked prompt also sets `AdditionalProperties[GeminiChatPropertyNames.BlockReason]`, as the enum name such as `Safety`. For a finish reason that maps to `null`, read `GenerateContentResponse.FinishReason` from `ChatResponse.RawRepresentation`. A value this library does not know reads as `Unknown`, and its original text is not kept.

## Streaming

```csharp
using Microsoft.Extensions.AI;

var updates = new List<ChatResponseUpdate>();

await foreach (var update in chatClient.GetStreamingResponseAsync("Tell me a short story."))
{
    updates.Add(update);
    Console.Write(update.Text);
}

ChatResponse response = updates.ToChatResponse();
```

- On the Gemini chat client itself, arguments and options are checked when the method is called, not on the first enumeration. Middleware such as `UseFunctionInvocation()` can move that to enumeration.
- Token usage comes once, as a trailing update with a `UsageContent`, because Gemini repeats cumulative counts on many chunks and consumers add up every `UsageContent`. It follows the update with the finish reason, so enumerate to the end. If Gemini reports no usage, there is no such update.
- `ToChatResponse()` leaves `RawRepresentation` as `null`. Read it from each update, where it is the chunk.
- Citations travel on [annotation-only content](grounding.md#where-annotations-live).

## Persisting History

`RawRepresentation` is not serialized, so Gemini's data also lives where JSON keeps it:

| Stored in | Holds |
|---|---|
| `TextReasoningContent.ProtectedData` | A reasoning `thoughtSignature` |
| `AdditionalProperties[GeminiChatPropertyNames.ThoughtSignature]` | The signature of a text, function call, or other part |
| `AdditionalProperties[GeminiChatPropertyNames.SynthesizedCallId]` | Marks a local `CallId` that is never sent back |
| `AdditionalProperties[GeminiChatPropertyNames.SignatureCompanion]` | A flag: the `ProtectedData` of this content belongs to the preceding streamed text |
| `AdditionalProperties[GeminiChatPropertyNames.ExecutableCode]` and the related keys | Code execution parts |

Keep the whole history, including your own messages, because `response.Messages` holds only the reply. Serialize it with `AIJsonUtilities.DefaultOptions`, then pass the restored messages back:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;

var history = new List<ChatMessage> { new(ChatRole.User, "Hello") };

var response = await chatClient.GetResponseAsync(history);
history.AddRange(response.Messages);

string json = JsonSerializer.Serialize(history, AIJsonUtilities.DefaultOptions);

var restored = JsonSerializer.Deserialize<List<ChatMessage>>(json, AIJsonUtilities.DefaultOptions)!;
restored.Add(new ChatMessage(ChatRole.User, "Tell me more."));

var next = await chatClient.GetResponseAsync(restored);
```

Part kinds with no chat mapping, such as `toolCall`, need `RawRepresentation` and do not survive this. Neither do the fields that only `RawRepresentation` holds, such as `videoMetadata`, unknown fields, and the `id` that pairs `executableCode` with its result.

When trimming a history, remove a `FunctionCallContent` together with its `FunctionResultContent`.

## Histories From Other Providers

A `ChatMessage` history can move between `IChatClient` implementations, and text, function calls, and function results carry over. Two things need care, and the library does not change the history for you.

**Remove reasoning before the history changes provider.** `TextReasoningContent.ProtectedData` is only valid for the provider that made it. Gemini answers another provider's value with a 400 `Invalid thought signature`, and other providers can reject a signature from Gemini too. A history can hold one even if thoughts were never requested, because streamed text with a signature adds a `TextReasoningContent` with empty text.

```csharp
using Microsoft.Extensions.AI;

static List<ChatMessage> WithoutReasoning(IEnumerable<ChatMessage> history)
{
    var result = new List<ChatMessage>();
    foreach (var message in history)
    {
        var contents = message.Contents.Where(c => c is not TextReasoningContent).ToList();
        if (contents.Count > 0)
        {
            var copy = message.Clone();
            copy.Contents = contents;
            result.Add(copy);
        }
    }

    return result;
}
```

**Move to Gemini between turns, not during a tool call.** A function call made by another provider has no `thoughtSignature`. If the history ends with its result, Gemini answers with a 400 that says the function call is missing a `thought_signature`. The same history is accepted once a new user message follows.

## `GetService`

`chatClient.GetService<ChatClientMetadata>()` returns the provider name `gemini`, the endpoint, and the default model. `GetService<IGeminiClient>()` returns the underlying client.

## Known Limits

- Only the first candidate is used.
- `ConversationId` is not used: send the whole history on each call.
