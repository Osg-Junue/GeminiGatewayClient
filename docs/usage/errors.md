# Errors and Exceptions

## Exception List

| Exception | Meaning | When |
|---|---|---|
| [`GeminiApiException`](#geminiapiexception) | The server or gateway reported a failure, or sent a response that cannot be used | During the call, or while reading a stream |
| `HttpRequestException` | The connection failed or was lost, or a header could not be sent | During the call, or while reading a stream |
| `TaskCanceledException` | Your token was canceled, or `HttpClient.Timeout` elapsed | During the call |
| `OperationCanceledException` | Your token was canceled | While reading a stream |
| `TimeoutException` | A stream sent nothing for `StreamIdleTimeout` | While reading a stream |
| `ArgumentException` | An option, argument, or history cannot work | When the client is created, or before sending |
| `ArgumentNullException` | A required argument is `null` | Before sending |
| `InvalidOperationException` | No model was set, or a tool value has no JSON metadata | Before sending |
| `NotSupportedException` | A chat tool or content cannot be sent to Gemini | Before sending |
| `OverflowException` | `ChatOptions.Seed` is outside the 32-bit range | Before sending |
| `ObjectDisposedException` | The `GeminiClient` was disposed | Before sending |
| `OptionsValidationException` | Options from dependency injection are invalid | At startup, or on first use |

## Handling Them

```csharp
using GeminiGatewayClient;

try
{
    await foreach (var chunk in client.StreamGenerateContentAsync("Hello", cancellationToken: ct))
    {
        Console.Write(chunk.Text);
    }
}
catch (GeminiApiException ex)
{
    Console.Error.WriteLine($"HTTP {(int)ex.StatusCode} {ex.ErrorStatus}: {ex.ErrorMessage}");

    if (ex.RetryAfter is { } delay)
    {
        Console.Error.WriteLine($"Retry after {delay.TotalSeconds:F0}s");
    }
}
catch (TimeoutException)
{
    Console.Error.WriteLine("The stream stalled.");
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Connection failed: {ex.HttpRequestError}");
}
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    Console.Error.WriteLine("HttpClient.Timeout elapsed.");
}
```

## `GeminiApiException`

| Property | Value |
|---|---|
| `StatusCode` | HTTP status. See [Status of an Error in a 2xx Response](#status-of-an-error-in-a-2xx-response) |
| `ErrorStatus` | Google's status name, such as `RESOURCE_EXHAUSTED`, or `null` |
| `ErrorCode` | Numeric `error.code`, or `null` |
| `ErrorMessage` | `error.message`, or the text of an `error` sent as a string or array, or `null` |
| `ErrorContent` | Raw body, up to 64 KB, or `null` |
| `RetryAfter` | From `RetryInfo.retryDelay`, otherwise the `Retry-After` header, or `null` |
| `Message` | A summary such as `API request failed (StatusCode: 429 RESOURCE_EXHAUSTED): Quota exceeded`, with the detail cut at 512 characters |

### Common Statuses

These come from Gemini or your gateway, and the library passes them on unchanged.

| Status | Typical `ErrorStatus` | Typical cause | Retry? |
|---|---|---|---|
| 400 | `INVALID_ARGUMENT` | Invalid request, a value the model rejects, a batch over 100 items, a function call without its signature | No |
| 401, 403 | `UNAUTHENTICATED`, `PERMISSION_DENIED` | Wrong or missing credential | No |
| 404 | `NOT_FOUND` | Wrong model name, or a model no longer offered to you | No |
| 429 | `RESOURCE_EXHAUSTED` | Rate limit or quota | Yes, honoring `RetryAfter` |
| 500 | `INTERNAL` | Server failure | Yes, with backoff |
| 503 | `UNAVAILABLE` | Overloaded | Yes, with backoff |
| 504 | `DEADLINE_EXCEEDED` | The server timed out | Yes, with backoff |

Retry decisions are yours: the exception has no transient flag.

## When Each Failure Happens

### When the Client Is Created

| Case | Exception |
|---|---|
| `new GeminiClient(apiKey, model)` with a `null`, empty, or whitespace value | `ArgumentException` |
| `options` is `null` | `ArgumentNullException` |
| `http://` to a non-loopback host without `AllowInsecureHttp` | `ArgumentException` |
| Both `ApiKey` and `ApiKeyProvider` set | `ArgumentException` |
| A header value with a line break or NUL | `ArgumentException` |
| The same header twice, including `ApiKeyHeader` in `AdditionalHeaders` | `ArgumentException` |
| A header name .NET refuses, or a content header such as `Content-Type` | `ArgumentException` |
| `Model` or `EmbeddingModel` with a `.` or `..` segment | `ArgumentException` |

With `AddGeminiClient`, the `ArgumentException` cases surface as `OptionsValidationException`.

**Not checked.** Input is the caller's responsibility, so these fail where they are used, with whatever .NET throws:

| Case | Result |
|---|---|
| A relative `Endpoint` | `InvalidOperationException` when the client is created, also through `AddGeminiClient` |
| `Endpoint` or `AdditionalHeaders` set to `null` | `NullReferenceException` when the client is created |
| An `Endpoint` scheme other than `http` or `https` | Fails when sending |
| A negative or very large `StreamIdleTimeout` | `ArgumentOutOfRangeException` when reading a stream |
| A non-ASCII header value | `HttpRequestException` when sending |

### Before the Request Is Sent

`GeminiClient` methods throw these directly from the call, so the exception does not wait for `await`. For a stream, they come from the method call, not from the first enumeration.

| Case | Exception |
|---|---|
| The client was disposed | `ObjectDisposedException` |
| `request` is `null` | `ArgumentNullException` |
| No model on the call or in the options | `InvalidOperationException` |
| A model with a `.` or `..` segment | `ArgumentException` |
| An empty or whitespace `prompt` | `ArgumentException` |

From the `IChatClient` adapter:

| Case | Exception |
|---|---|
| `messages` is `null` | `ArgumentNullException` |
| A `null` item in `ChatOptions.Tools` | `ArgumentException` |
| A tool other than a function, `HostedWebSearchTool`, or `HostedCodeInterpreterTool` | `NotSupportedException` |
| `HostedFileContent` in a message | `NotSupportedException` |
| A `FunctionResultContent` without its `FunctionCallContent` | `ArgumentException` |
| `ChatOptions.Seed` outside the 32-bit range | `OverflowException` |
| A tool value with no JSON metadata | `InvalidOperationException` |

From the helpers:

| Case | Exception |
|---|---|
| `Part.FromFunctionResponse` with a non-object result, or an empty name | `ArgumentException` |
| `Part.FromBytes` or `Part.FromUri` with an empty MIME type or URI | `ArgumentException` |
| `Part.FromText` or `EmbedContentRequest.FromText` with `null` | `ArgumentNullException` |

### While Sending

| Case | Exception |
|---|---|
| `ApiKeyProvider` returns `null`, empty, or whitespace | `ArgumentException` |
| `ApiKeyProvider` returns a value with a line break or NUL | `ArgumentException` |
| `ApiKeyProvider` itself throws | That exception, unchanged |
| DNS, socket, or TLS failure | `HttpRequestException`, with `HttpRequestError` |
| Your token is canceled | `TaskCanceledException` |
| `HttpClient.Timeout` elapses | `TaskCanceledException` |

### Responses from the Server

| Case | Exception |
|---|---|
| Any non-2xx status, including a redirect, which is not followed | `GeminiApiException` with that status |
| The same, with Google's error body | `GeminiApiException` with `ErrorStatus`, `ErrorCode`, `ErrorMessage`, and `RetryAfter` |
| The same, with an HTML or plain text body | `GeminiApiException` with only `ErrorContent` |
| The connection is lost while reading a complete response | `HttpRequestException`, without `StatusCode` |
| The body exceeds `HttpClient.MaxResponseContentBufferSize` | `HttpRequestException` with `ConfigurationLimitExceeded` |

### Status of an Error in a 2xx Response

A gateway may report a failure with HTTP 200. Any `error` value other than `null` then fails the call, with `StatusCode` taken from `error.code` when it is between 400 and 599, and 500 otherwise. A 2xx status is never reported for such an error.

This includes values that look empty, such as `false`, `""`, and `{}`, even next to a usable answer in `candidates`.

### Responses That Cannot Be Used

These throw `GeminiApiException` with the 2xx status. Any body that arrived is in `ErrorContent`, which is `null` for an empty one:

| Case | Message starts with |
|---|---|
| An empty body, or one that is not valid JSON, such as an HTML page | `Failed to deserialize the Gemini API response` |
| A body of `null` | `The Gemini API returned an empty (null) response` |
| A stream without any response | `The Gemini API returned an empty streaming response` |
| An `error` event without an `error` value | `The stream reported an error event` |
| `IEmbeddingGenerator`: a different number of embeddings than inputs | `The Gemini batch embedding endpoint returned` |
| `IEmbeddingGenerator`: an empty embedding | `The Gemini batch embedding endpoint returned an empty embedding` |

### While Reading a Stream

These surface during `await foreach`, after chunks may already have arrived. Discard partial output before you retry.

| Case | Exception |
|---|---|
| Non-2xx status | `GeminiApiException`, on the first `MoveNextAsync` |
| An `{"error": ...}` payload after HTTP 200 | `GeminiApiException` |
| An `error` event | `GeminiApiException` |
| A chunk that is not valid JSON | `GeminiApiException` |
| Nothing received for `StreamIdleTimeout` | `TimeoutException` |
| The connection is lost | `HttpRequestException`, with `StatusCode` set and `HttpRequestError` when known |
| Your token is canceled | `OperationCanceledException` |
| `HttpClient.Timeout` before the headers arrive | `TaskCanceledException` |

`HttpClient.Timeout` stops applying after the headers. A slow reader does not trigger `StreamIdleTimeout`, and keep-alive comments reset it.

On a non-2xx stream, reading the error body is limited by `StreamIdleTimeout` and 64 KB. A body that is longer or arrives too late leaves `ErrorContent`, `ErrorStatus`, `ErrorCode`, and `ErrorMessage` empty. `StatusCode` stays.

## Not Exceptions

These responses succeed, so check for them:

| Case | How it looks | Check |
|---|---|---|
| The prompt was blocked | No candidates | `response.BlockReason`, `response.IsBlocked` |
| The answer was blocked | `FinishReason.Safety`, `Recitation`, and others | `response.IsBlocked` |
| The output limit was reached | `FinishReason.MaxTokens`, with partial or no text | `response.FinishReason` |
| Thinking used up the output limit | No parts, `response.Text` is `null` | `TryGetModelContent` returns `false` |
| A function call failed | `FinishReason.MalformedFunctionCall` and others | `response.FinishReason`; `ChatResponse.FinishReason` is `null` |
| A stream closed cleanly before the end | Partial text, and no chunk has a finish reason | [The last finish reason](#streams-that-end-early) |
| A plain JSON error line in an event stream | Ignored, because it is not an event | [The last finish reason](#streams-that-end-early) |
| A value the library does not know | `Unknown` | The enum value |
| A field the library does not model | Nothing is lost | `ExtensionData` |

With `IChatClient`, a block shows as `ChatFinishReason.ContentFilter`. See [Finish Reasons](chat-client.md#finish-reasons).

### Streams That End Early

A stream that is cut inside an event, or whose connection is lost, fails with an exception. A stream that a gateway closes cleanly between events does not, so partial text can look like a complete answer. The same goes for a line such as `{"error": ...}` sent without `data:`, which an event stream ignores. If more events follow that line, the stream can even finish normally.

To detect an early end, keep the last finish reason:

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

FinishReason? finishReason = null;
BlockReason? blockReason = null;

await foreach (var chunk in client.StreamGenerateContentAsync("Hello", cancellationToken: ct))
{
    Console.Write(chunk.Text);
    finishReason = chunk.FinishReason ?? finishReason;
    blockReason = chunk.BlockReason ?? blockReason;
}

if (finishReason is null && blockReason is null)
{
    Console.Error.WriteLine("The stream ended without a finish reason, so the answer may be incomplete.");
}
```

A blocked prompt has no candidates and so no finish reason, which is why the sample also checks `BlockReason`.

With `IChatClient`, `FinishReason` can also be `null` for a failed function call or a reason with no mapping, so `null` alone does not prove an early end. Read the `GenerateContentResponse` chunks from `ChatResponseUpdate.RawRepresentation` to tell them apart.

## Telling Cancellation from a Timeout

Both are `TaskCanceledException`. Check your own token:

```csharp
using GeminiGatewayClient;

try
{
    var response = await client.GenerateContentAsync("Hello", cancellationToken: ct);
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    Console.Error.WriteLine("Canceled by the caller.");
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("HttpClient.Timeout elapsed.");
}
```

Do not compare `ex.CancellationToken` with your token: the client links tokens, so the exception may carry another one.

## Retrying

The library never retries. `ApiKeyProvider` runs once per client call, so a retry inside an HTTP handler resends the same value, while calling the client again gets a fresh one.

```csharp
using System.Net;
using GeminiGatewayClient;

for (var attempt = 1; ; attempt++)
{
    try
    {
        var response = await client.GenerateContentAsync("Hello", cancellationToken: ct);
        Console.WriteLine(response.Text);
        break;
    }
    catch (GeminiApiException ex) when (attempt < 3 && ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
    {
        await Task.Delay(ex.RetryAfter ?? TimeSpan.FromSeconds(attempt * 2), ct);
    }
}
```

For retries on the HTTP pipeline, see [Resilience](configuration.md#resilience). They cannot see failures after a stream's headers.
