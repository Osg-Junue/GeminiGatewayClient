# `IChatClient`

`client.AsIChatClient()` adapts the client to `Microsoft.Extensions.AI`, so the middleware and provider-neutral code of that library work with Gemini.

```csharp
using Microsoft.Extensions.AI;

IChatClient chat = client.AsIChatClient();

ChatResponse response = await chat.GetResponseAsync(
    [
        new ChatMessage(ChatRole.System, "You are a concise assistant."),
        new ChatMessage(ChatRole.User, "What is the capital of Japan?")
    ],
    new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 512 });

Console.WriteLine(response.Text);
```

| Parameter of `AsIChatClient` | Purpose |
|---|---|
| `defaultModelId` | Model when `ChatOptions.ModelId` is not set. Defaults to `IGeminiClient.DefaultModel` |
| `toolSerializerOptions` | Serializer options for function arguments and results. See [Native AOT](native-aot.md) |

Disposing the chat client does nothing, because it does not own the `GeminiClient`.

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
| `Tools` | [Functions](function-calling.md#automatic-function-calling-with-ichatclient) and [built-in tools](grounding.md) |
| `ToolMode` | [`toolConfig.functionCallingConfig`](function-calling.md#automatic-function-calling-with-ichatclient) |
| `RawRepresentationFactory` | See below |

`ChatOptions.AdditionalProperties` are not read.

### Gemini-Specific Settings

Anything without a `ChatOptions` counterpart goes through `RawRepresentationFactory`. Return a `GenerateContentRequest`, and its values win over `ChatOptions`:

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
| `System` | Text joins `systemInstruction`, wherever the message is in the history. Other content is ignored |
| `Assistant` | `model` |
| `User`, `Tool`, and any other | `user` |

Consecutive messages of one role merge into one content, such as parallel tool results. `Tool` and `User` stay separate, so a user's follow-up does not mix into a `functionResponse` turn. A message that produces no parts is skipped.

| Content | Sent as |
|---|---|
| `TextContent` | Text. Empty text is skipped |
| `TextReasoningContent` | A thought part with its signature |
| `DataContent`, `UriContent` | [Media](multimodal.md#with-ichatclient) |
| `FunctionCallContent`, `FunctionResultContent` | [Function calling](function-calling.md#running-the-loop-yourself) |
| `HostedFileContent` | Throws `NotSupportedException` |
| Other kinds | Resent from `RawRepresentation` or the `gemini.*` properties, otherwise dropped |

## Responses

`ChatResponse.Messages` holds one assistant message built from the first candidate.

| Gemini | `ChatResponse` |
|---|---|
| `responseId` | `ResponseId` |
| `modelVersion` | `ModelId`, falling back to the requested model |
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

A blocked prompt also sets `AdditionalProperties[GeminiChatPropertyNames.BlockReason]`. Read `RawRepresentation` for the original value.

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

- Arguments and options are checked when the method is called, not on the first enumeration.
- Token usage comes once, as a trailing update with a `UsageContent`, because Gemini repeats cumulative counts on many chunks and consumers add up every `UsageContent`.
- Citations travel on [annotation-only content](grounding.md#where-annotations-live).

## Persisting History

`RawRepresentation` is not serialized, so Gemini's data also lives where JSON keeps it:

| Stored in | Holds |
|---|---|
| `TextReasoningContent.ProtectedData` | A reasoning `thoughtSignature` |
| `AdditionalProperties[GeminiChatPropertyNames.ThoughtSignature]` | The signature of a text, function call, or other part |
| `AdditionalProperties[GeminiChatPropertyNames.SynthesizedCallId]` | Marks a local `CallId` that is never sent back |
| `AdditionalProperties[GeminiChatPropertyNames.SignatureCompanion]` | A signature that belongs to the preceding streamed text |
| `AdditionalProperties[GeminiChatPropertyNames.ExecutableCode]` and the related keys | Code execution parts |

Serialize `ChatMessage` with `AIJsonUtilities.DefaultOptions`, then pass the restored messages back:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;

var response = await chatClient.GetResponseAsync("Hello");

string json = JsonSerializer.Serialize(response.Messages, AIJsonUtilities.DefaultOptions);

var restored = JsonSerializer.Deserialize<List<ChatMessage>>(json, AIJsonUtilities.DefaultOptions)!;
restored.Add(new ChatMessage(ChatRole.User, "Tell me more."));

var next = await chatClient.GetResponseAsync(restored);
```

Part kinds with no chat mapping, such as `toolCall`, need `RawRepresentation` and do not survive this.

When trimming a history, remove a `FunctionCallContent` together with its `FunctionResultContent`.

## `GetService`

`chatClient.GetService<ChatClientMetadata>()` returns the provider name `gemini`, the endpoint, and the default model. `GetService<IGeminiClient>()` returns the underlying client.

## Known Limits

- Only the first candidate is used.
- `ConversationId` is not used: send the whole history on each call.
