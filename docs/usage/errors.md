# Errors and Exceptions

## Exception List

| Exception | Meaning | When |
|---|---|---|
| [`GeminiApiException`](#geminiapiexception) | The server or gateway reported a failure, or sent a response that cannot be used | During the call, or while reading a stream |
| `HttpRequestException` | The connection failed or was lost, or a header could not be sent | During the call, or while reading a stream |
| `TaskCanceledException` | Your token was canceled, or `HttpClient.Timeout` elapsed | During the call, or while reading a stream |
| `OperationCanceledException` | Your token was canceled | While reading a stream |
| `TimeoutException` | A stream sent nothing for `StreamIdleTimeout` | While reading a stream |
| `ArgumentException` | An option, argument, or history cannot work | When the client is created, before sending, or while sending |
| `ArgumentNullException` | A required argument is `null` | When the client is created, or before sending |
| `InvalidOperationException` | No model was set, or a tool value has no JSON metadata | Before sending |
| `NotSupportedException` | A chat tool or content cannot be sent to Gemini, or the `Endpoint` scheme is not supported | Before sending, or while sending |
| `OverflowException` | `ChatOptions.Seed` is outside the 32-bit range, or a `TimeSpan` is too large for `VideoOffset.FromTimeSpan` | Before sending |
| `ObjectDisposedException` | The `GeminiClient` or your `HttpClient` was disposed | Before sending, or while sending |
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

Your own cancellation is not caught here, so it reaches the caller as usual.

## `GeminiApiException`

| Property | Value |
|---|---|
| `StatusCode` | HTTP status. See [Status of an Error in a 2xx Response](#status-of-an-error-in-a-2xx-response) |
| `ErrorStatus` | Google's status name, such as `RESOURCE_EXHAUSTED`, or `null` |
| `ErrorCode` | Numeric `error.code`, or `null` |
| `ErrorMessage` | `error.message`, or the text of an `error` sent as a string or array, or `null` |
| `ErrorContent` | Raw body, up to 64 KB, or `null`. An empty non-2xx body gives an empty string. In a stream, it can hold only the error event or the `error` value |
| `RetryAfter` | From `retryDelay` in the error details, otherwise the `Retry-After` header of a non-2xx response, or `null` |
| `Message` | A summary such as `API request failed (StatusCode: 429 RESOURCE_EXHAUSTED): Quota exceeded`, with the detail cut at 512 characters |

### Common Statuses

These come from Gemini or your gateway, and the library passes them on unchanged. The exception is an `error` in a 2xx response, where [the library sets the status](#status-of-an-error-in-a-2xx-response).

| Status | Typical `ErrorStatus` | Typical cause | Retry? |
|---|---|---|---|
| 400 | `INVALID_ARGUMENT` | Invalid request, a value the model rejects, a batch over 100 items, a function call without its signature | No |
| 401, 403 | `UNAUTHENTICATED`, `PERMISSION_DENIED` | Wrong or missing credential. Gemini answered a wrong API key with 400 `INVALID_ARGUMENT` when checked on 2026-11-07 | No |
| 404 | `NOT_FOUND` | Wrong model name, or a model no longer offered to you | No |
| 429 | `RESOURCE_EXHAUSTED` | Rate limit or quota | Yes, honoring `RetryAfter` |
| 500 | `INTERNAL` | Server failure | Yes, with backoff |
| 503 | `UNAVAILABLE` | Overloaded | Yes, with backoff |
| 504 | `DEADLINE_EXCEEDED` | The server timed out | Yes, with backoff |

Retry decisions are yours: the exception has no transient flag. Do not decide on a 500 alone, because it can stand for an `error` in a 2xx response that has no status of its own. Check `ErrorCode` and `ErrorStatus` too.

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
| An `Endpoint` scheme other than `http` or `https` | `NotSupportedException` when sending, with the default handler |
| A negative or very large `StreamIdleTimeout` | `ArgumentOutOfRangeException` when reading a stream |
| A `StreamIdleTimeout` of `TimeSpan.Zero` | `TimeoutException` when reading a stream |
| A non-ASCII header value | `HttpRequestException` when sending |

### Before the Request Is Sent

`GeminiClient` methods throw these directly from the call, so the exception does not wait for `await`. For a stream, they come from the method call, not from the first enumeration.

The adapters differ: `GetResponseAsync` and `GenerateAsync` report these through the returned `Task`, while `GetStreamingResponseAsync` throws them directly from the call.

| Case | Exception |
|---|---|
| The client was disposed. A stream created earlier fails when you enumerate it | `ObjectDisposedException` |
| `request` is `null` | `ArgumentNullException` |
| No model on the call or in the options | `InvalidOperationException` |
| A model with a `.` or `..` segment | `ArgumentException` |
| A `null`, empty, or whitespace `prompt` | `ArgumentException`, or `ArgumentNullException` for `null` |

From the `IChatClient` adapter:

| Case | Exception |
|---|---|
| `messages` is `null` | `ArgumentNullException` |
| A `null` item in `ChatOptions.Tools` | `ArgumentException` |
| A tool other than a function, `HostedWebSearchTool`, or `HostedCodeInterpreterTool` | `NotSupportedException` |
| `HostedFileContent` in a message other than `System`, where it is dropped | `NotSupportedException` |
| A `FunctionResultContent` without its `FunctionCallContent` | `ArgumentException` |
| `ChatOptions.Seed` outside the 32-bit range | `OverflowException` |
| A tool value with no JSON metadata | `InvalidOperationException` |

From the helpers:

| Case | Exception |
|---|---|
| `Part.FromFunctionResponse` with a non-object result, or an empty name | `ArgumentException` |
| `Part.FromBytes` with an empty MIME type, or `Part.FromUri` with an empty URI | `ArgumentException` |
| `Part.FromText` or `EmbedContentRequest.FromText` with `null` | `ArgumentNullException` |

### While Sending

| Case | Exception |
|---|---|
| `ApiKeyProvider` returns `null`, empty, or whitespace. For a stream, this and the next two rows surface during enumeration | `ArgumentException` |
| `ApiKeyProvider` returns a value with a line break or NUL | `ArgumentException` |
| `ApiKeyProvider` itself throws | That exception, unchanged |
| DNS, socket, or TLS failure | `HttpRequestException`, with `HttpRequestError` |
| Your token is canceled | `TaskCanceledException` |
| `HttpClient.Timeout` elapses | `TaskCanceledException` |

### Responses from the Server

| Case | Exception |
|---|---|
| Any non-2xx status, including a redirect | `GeminiApiException` with that status |
| The same, with Google's error body | `GeminiApiException` with `ErrorStatus`, `ErrorCode`, `ErrorMessage`, and `RetryAfter` |
| The same, with an HTML or plain text body | `GeminiApiException` with `ErrorContent`, and `RetryAfter` from the header if present |
| The connection is lost while reading a complete response, even one with a non-2xx status | `HttpRequestException`, without `StatusCode` |
| The body exceeds `HttpClient.MaxResponseContentBufferSize`, even with a non-2xx status | `HttpRequestException` with `ConfigurationLimitExceeded` |

A redirect is not followed by the client the library creates, or by `GeminiClient.CreateDefaultHandler()`. An `HttpClient` of your own follows it by default, and custom credential headers go to the new host, so [build yours on that handler](configuration.md#using-your-own-httpclient).

### Status of an Error in a 2xx Response

A gateway may report a failure with HTTP 200. Any `error` value other than `null` then fails the call, with `StatusCode` taken from `error.code` when it is between 400 and 599, and 500 otherwise. A 2xx status is never reported for such an error.

This includes values that look empty, such as `false`, `""`, and `{}`, even next to a usable answer in `candidates`.

### Responses That Cannot Be Used

These throw `GeminiApiException` with the 2xx status, and any body that arrived is in `ErrorContent`, which is `null` for an empty one. A body of `null` leaves `ErrorContent` as `null`. The two `IEmbeddingGenerator` rows always have status 200 and no `ErrorContent`:

| Case | Message starts with |
|---|---|
| An empty body, or one that cannot be read as a response, such as an HTML page or `[]` | `Failed to deserialize the Gemini API response` |
| A body of `null` | `The Gemini API returned an empty (null) response` |
| A stream without any response | `The Gemini API returned an empty streaming response` |
| An `error` event with `data:` but no `error` value | `The stream reported an error event` |
| `IEmbeddingGenerator`: a different number of embeddings than inputs | `The Gemini batch embedding endpoint returned` |
| `IEmbeddingGenerator`: an empty embedding | `The Gemini batch embedding endpoint returned an empty embedding` |

### While Reading a Stream

These surface during `await foreach`, after chunks may already have arrived. Discard partial output before you retry.

| Case | Exception |
|---|---|
| Non-2xx status | `GeminiApiException`, on the first `MoveNextAsync` |
| An `{"error": ...}` payload after HTTP 200 | `GeminiApiException` |
| An `error` event with a `data:` line | `GeminiApiException` |
| A chunk that is not valid JSON | `GeminiApiException` |
| Nothing received for `StreamIdleTimeout` after the headers | `TimeoutException` |
| The connection is lost while reading a 2xx body | `HttpRequestException`, with `StatusCode` set and `HttpRequestError` when known |
| Your token is canceled | `OperationCanceledException` |
| `HttpClient.Timeout` before the headers arrive | `TaskCanceledException` |

`HttpClient.Timeout` stops applying after the headers, and `StreamIdleTimeout` starts there, so only `HttpClient.Timeout` limits the wait for the headers. A slow reader does not trigger `StreamIdleTimeout`, and keep-alive comments reset it.

On a non-2xx stream, reading the error body is limited to 64 KB, and `StreamIdleTimeout` is the deadline for the whole body, which data does not reset. With `StreamIdleTimeout = null` there is no deadline, so a stalled body waits until you cancel. A body that is longer, arrives too late, or loses its connection still gives a `GeminiApiException`, with `ErrorContent`, `ErrorStatus`, `ErrorCode`, and `ErrorMessage` empty. `StatusCode` and a `Retry-After` header stay.

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
| A plain JSON error line after chunks of an event stream | Ignored, because it is not an event | [The last finish reason](#streams-that-end-early) |
| An empty response object, `{}` | No candidates and no `BlockReason` | `response.Candidates` |
| `BatchEmbedContentsAsync` returned too few or empty embeddings | The response as it is | The count and the values. Only `IEmbeddingGenerator` checks them |
| A value the library does not know | `Unknown` | The enum value |
| A field the library does not model | Nothing is lost | `ExtensionData` |

With `IChatClient`, a block shows as `ChatFinishReason.ContentFilter`. See [Finish Reasons](chat-client.md#finish-reasons).

### Streams That End Early

A stream that is cut in the middle of a chunk's JSON fails with an exception, and so does a lost connection on a chunked response. A stream cut anywhere else may not, such as one that a gateway closes cleanly between events, or a response with no length that just closes. Partial text can then look like a complete answer. The same goes for a line such as `{"error": ...}` sent without `data:` after chunks have arrived, which an event stream ignores. If more events follow that line, the stream can even finish normally.

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

With `IChatClient`, `FinishReason` can also be `null` for a failed function call or a reason with no mapping, so `null` alone does not prove an early end. Read the `GenerateContentResponse` chunks from `ChatResponseUpdate.RawRepresentation` to tell them apart. The trailing usage update has no `RawRepresentation`.

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

For retries on the HTTP pipeline, see [Resilience](configuration.md#resilience). They cannot see failures after a stream's headers, or an `error` in an HTTP 200 response.
