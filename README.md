# GeminiAPIWrapper

[![NuGet](https://img.shields.io/nuget/v/GeminiAPIWrapper.svg)](https://www.nuget.org/packages/GeminiAPIWrapper)
[![CI](https://github.com/Osg-Junue/GeminiAPIWrapper/actions/workflows/ci.yml/badge.svg)](https://github.com/Osg-Junue/GeminiAPIWrapper/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.txt)

A lightweight, NativeAOT-compatible .NET 10 client for the **Gemini (Generative Language) API** — built to run cleanly behind API gateways and proxies, with built-in **`Microsoft.Extensions.AI` (`IChatClient`)**, **`IOptions<GeminiClientOptions>`**, and **`IHttpClientFactory`** support.

---

## Why GeminiAPIWrapper?

Google's official `Google.GenAI` SDK is designed around calling Google endpoints directly, sending the API key in the `x-goog-api-key` header.

In production, LLM traffic often passes through an **API gateway, reverse proxy, or BFF** (Azure API Management, Cloudflare AI Gateway, Kong, Envoy, or an internal gateway) where:

1. **The client does not hold the real Gemini API key** — it authenticates to the gateway using a different header such as `Ocp-Apim-Subscription-Key` or `Authorization: Bearer <token>`, and the gateway injects `x-goog-api-key` upstream.
2. **Credentials rotate per request** — short-lived OAuth or JWT tokens can be supplied dynamically via `ApiKeyProvider`.
3. **Both a gateway header and a Gemini key are required** — for example, an authenticated Cloudflare AI Gateway requires `cf-aig-authorization: Bearer <token>` in addition to `x-goog-api-key`.
4. **You want a gateway-first `HttpClient` lifecycle** — headers applied per `HttpRequestMessage` (never mutating `HttpClient.DefaultRequestHeaders`), first-class `services.AddGeminiClient(...)` registration with `IHttpClientFactory` and `IOptions<GeminiClientOptions>`, and source-generated `System.Text.Json` serialization (`<IsAotCompatible>true</IsAotCompatible>`).

| Capability | `GeminiAPIWrapper` | Official `Google.GenAI` |
|---|---|---|
| Replace the API key header name (`ApiKeyHeader`) | Yes (`x-goog-api-key` is not sent when overridden) | Possible via `HttpOptions` (manual header wiring) |
| Key value prefix (`ApiKeyPrefix`, e.g. `"Bearer "`) | Built-in option | Via custom headers |
| Per-request dynamic token callback (`ApiKeyProvider`) | Built-in (`Func<CancellationToken, ValueTask<string>>`) | Manual handler |
| Omit key header when gateway injects key (`ApiKey = null`) | Built-in option | Via custom auth/headers |
| Extra gateway headers (`AdditionalHeaders`) | Yes | Yes (`HttpOptions.Headers`) |
| `Microsoft.Extensions.AI` (`IChatClient`) | Built-in (`client.AsIChatClient()` / `GeminiChatClient`) | Yes (`AsIChatClient()`) |
| `IHttpClientFactory` & `IOptions` DI (`AddGeminiClient`) | Built-in (`IHttpClientBuilder`) | Manual registration |

---

## Installation

```bash
dotnet add package GeminiAPIWrapper
```

Targets **.NET 10** (`net10.0`).

---

## Quick Start

```csharp
using GeminiAPIWrapper;

using var client = new GeminiClient(
    apiKey: Environment.GetEnvironmentVariable("GEMINI_API_KEY")!,
    model: "gemini-2.5-flash");

var response = await client.GenerateContentAsync("Explain quantum computing in three sentences.");
Console.WriteLine(response.Text);
```

Or configure `GeminiClientOptions` for custom endpoints, headers, and timeouts:

```csharp
using GeminiAPIWrapper;

using var client = new GeminiClient(new GeminiClientOptions
{
    Endpoint = new Uri("https://generativelanguage.googleapis.com/v1beta"),
    Model = "gemini-3-flash-preview",
    ApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
});

var response = await client.GenerateContentAsync("Explain quantum computing in three sentences.");
Console.WriteLine(response.Text);
```

> [!IMPORTANT]
> `Endpoint` is the API root URI (default: `https://generativelanguage.googleapis.com/v1beta`). Do **not** include `:generateContent` or `:streamGenerateContent` in `Endpoint`. The client composes `{Endpoint}/models/{Model}:{operation}` (or `{Endpoint}/{Model}:{operation}` when `Model` contains `/`, such as `tunedModels/...`) while preserving any query string on `Endpoint` (such as `?api-version=...`).

---

## Using Behind an API Gateway or Proxy

### 1. Azure API Management (APIM)

When APIM holds the real Gemini key in a Named Value and clients authenticate with an APIM subscription key:

```csharp
var options = new GeminiClientOptions
{
    Endpoint = new Uri("https://contoso.azure-api.net/gemini/v1beta"),
    Model = "gemini-2.5-flash",
    ApiKey = "<apim-subscription-key>",
    ApiKeyHeader = "Ocp-Apim-Subscription-Key" // Replaces x-goog-api-key completely
};
```

### 2. Cloudflare AI Gateway (Authenticated)

Send both the Gemini API key and the Cloudflare AI Gateway authentication header:

```csharp
var options = new GeminiClientOptions
{
    Endpoint = new Uri("https://gateway.ai.cloudflare.com/v1/<account-id>/<gateway-name>/google-ai-studio/v1beta"),
    Model = "gemini-2.5-flash",
    ApiKey = "<gemini-api-key>",
    AdditionalHeaders = new Dictionary<string, string>
    {
        ["cf-aig-authorization"] = "Bearer <cloudflare-gateway-token>"
    }
};
```

*(If Cloudflare AI Gateway injects the Gemini key via Unified Billing / BYOK, leave `ApiKey = null` so no `x-goog-api-key` header is sent from the client.)*

### 3. Custom Reverse Proxy / BFF with Static or Rotating Bearer Token

```csharp
var options = new GeminiClientOptions
{
    Endpoint = new Uri("https://llm-proxy.internal/gemini/v1beta"),
    Model = "gemini-2.5-flash",
    ApiKeyHeader = "Authorization",
    ApiKeyPrefix = "Bearer ",
    // Invoked before each HTTP request so short-lived OAuth / JWT tokens rotate cleanly:
    ApiKeyProvider = async ct => await tokenProvider.GetAccessTokenAsync(ct)
};
```

---

## Dependency Injection, `IOptions<GeminiClientOptions>` & `IHttpClientFactory`

Register `GeminiClient`, `IGeminiClient`, and `IChatClient` with `IServiceCollection.AddGeminiClient(...)`. It integrates with `Microsoft.Extensions.Options` (`IOptions<GeminiClientOptions>` and `IValidateOptions<GeminiClientOptions>`) and returns an `IHttpClientBuilder` so you can chain standard resilience pipelines, custom handlers, or logging:

```csharp
using GeminiAPIWrapper;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

// Bind from IConfiguration or configure via delegate:
services.AddGeminiClient(options =>
{
    options.Endpoint = new Uri("https://contoso.azure-api.net/gemini/v1beta");
    options.Model = "gemini-2.5-flash";
    options.ApiKey = configuration["Gemini:SubscriptionKey"];
    options.ApiKeyHeader = "Ocp-Apim-Subscription-Key";
});

await using var provider = services.BuildServiceProvider();

// Resolve IGeminiClient, GeminiClient, or Microsoft.Extensions.AI.IChatClient
var gemini = provider.GetRequiredService<IGeminiClient>();
var chatClient = provider.GetRequiredService<IChatClient>();
```

You can also pass an existing `HttpClient` directly to `new GeminiClient(options, httpClient)`. Headers are attached per `HttpRequestMessage` rather than mutating `HttpClient.DefaultRequestHeaders`, and disposing `GeminiClient` will **not** dispose a caller-supplied `HttpClient`:

```csharp
using var client = new GeminiClient(options, httpClient);
```

---

## Timeouts, Resilience & Redirects

- **Request timeout (`Timeout`).** `GeminiClientOptions.Timeout` (default **5 minutes**) is applied to internally created `HttpClient` instances as well as the `HttpClient` configured by `AddGeminiClient()`. If you construct `new GeminiClient(options, httpClient)` with your own `HttpClient`, that instance's `Timeout` applies.
- **Stream idle timeout (`StreamIdleTimeout`).** Because `HttpClient.Timeout` only covers the time until response headers arrive (`ResponseHeadersRead`), `GeminiClientOptions.StreamIdleTimeout` (default **2 minutes**) arms a per-read watchdog during `StreamGenerateContentAsync` and throws `TimeoutException` if the stream stalls between chunks. Set `StreamIdleTimeout = Timeout.InfiniteTimeSpan` to disable.
- **Resilience handlers.** `AddStandardResilienceHandler()` defaults to a **10 s per-attempt** and **30 s total** timeout, which will cancel most thinking-model calls. Configure it explicitly:

  ```csharp
  services.AddGeminiClient(options => { ... })
      .AddStandardResilienceHandler(o =>
      {
          o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(3);
          o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(6);
          o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(6); // must be >= 2x AttemptTimeout
      });
  ```
- **Redirects.** Both the internally created `HttpClient` and the primary handler configured by `AddGeminiClient` set `AllowAutoRedirect = false`: credentials are sent in custom headers (such as `x-goog-api-key`), and .NET only strips `Authorization` on cross-host redirects.

---

## `Microsoft.Extensions.AI` (`IChatClient`) Support

Adapt any `IGeminiClient` to `Microsoft.Extensions.AI.IChatClient` via `client.AsIChatClient()` (or resolve `IChatClient` directly from DI after calling `services.AddGeminiClient(...)`). Supports multi-turn chat, streaming, tool calling (`FunctionInvokingChatClient`), structured output (`ChatResponseFormat.ForJsonSchema`), per-request model overrides (`ChatOptions.ModelId`), and reasoning configuration (`ChatOptions.Reasoning`):

```csharp
using GeminiAPIWrapper;
using Microsoft.Extensions.AI;

using var gemini = new GeminiClient(
    apiKey: Environment.GetEnvironmentVariable("GEMINI_API_KEY")!,
    model: "gemini-3-flash-preview");

IChatClient chatClient = gemini.AsIChatClient();

ChatResponse response = await chatClient.GetResponseAsync(
[
    new ChatMessage(ChatRole.System, "You are a concise assistant."),
    new ChatMessage(ChatRole.User, "What is NativeAOT in .NET?")
], new ChatOptions
{
    Temperature = 0.3f,
    MaxOutputTokens = 512,
    // Optional: override the default model for this call
    ModelId = "gemini-3.1-pro-preview"
});

Console.WriteLine(response.Text);

// Streaming via IChatClient (uses Server-Sent Events by default)
await foreach (ChatResponseUpdate update in chatClient.GetStreamingResponseAsync("Write a haiku about C#."))
{
    Console.Write(update.Text);
}
```

---

## Streaming, Thinking, Structured Output & Error Handling

### Streaming (`IAsyncEnumerable`)

By default, streaming requests use Server-Sent Events (`?alt=sse`), which flushes chunks immediately through API gateways and reverse proxies. If a custom proxy requires the legacy JSON array stream format, set `StreamingFormat = GeminiStreamingFormat.JsonArray` on `GeminiClientOptions`.

- An `{"error": {...}}` payload that arrives *inside* a stream (after HTTP 200) is surfaced as a `GeminiApiException` (with `ErrorStatus`/`ErrorMessage`/`RetryAfter`/`IsTransient`) instead of an empty chunk.
- `StreamGenerateContentAsync` / `GetStreamingResponseAsync` validate their arguments when called, not on first enumeration.
- Through `IChatClient`, token usage is reported **once**, as a single trailing `UsageContent` carrying the final cumulative counts (Gemini repeats cumulative `usageMetadata` on streamed chunks, so emitting it per chunk would over-count when updates are aggregated).
- `ChatOptions.Tools` supports `AIFunctionDeclaration`, `HostedWebSearchTool` (mapped to `googleSearch`), and `HostedCodeInterpreterTool` (mapped to `codeExecution`); other `AITool` types throw `NotSupportedException` instead of being silently dropped.
- Function-call `thoughtSignature` and locally generated call IDs are kept in `FunctionCallContent.AdditionalProperties` (`GeminiChatPropertyNames.ThoughtSignature`, `GeminiChatPropertyNames.SynthesizedCallId`), so persisted chat history can be replayed on Gemini 3 models.

```csharp
await foreach (var chunk in client.StreamGenerateContentAsync("Write a short story about a lighthouse."))
{
    Console.Write(chunk.Text);
}
```

### Thinking / Reasoning Models

- **Gemini 3 series models** use `ThinkingConfig.ThinkingLevel` (`Minimal`, `Low`, `Medium`, `High`) or `ChatOptions.Reasoning`.
- **Gemini 2.5 series models** use `ThinkingConfig.ThinkingBudget` (`-1` for dynamic thinking, `0` to disable where supported, or a positive token cap).
- `ThinkingLevel` and `ThinkingBudget` are mutually exclusive.
- **`ChatOptions.Reasoning.Effort` is mapped per model family** (taken from `ChatOptions.ModelId` or `GeminiClientOptions.Model`):

  | Effort | Gemini 2.5 (`thinkingBudget`) | Gemini 3+ (`thinkingLevel`) |
  |---|---|---|
  | `None` | `0` (Pro: `128`, the minimum) | `Minimal` (Pro models: `Low`) |
  | `Low` | `1024` | `Low` |
  | `Medium` | `8192` | `Medium` (`gemini-3-pro*`: `High`) |
  | `High` / `ExtraHigh` | `24576` | `High` |

  For exact control, set `GenerationConfig.ThinkingConfig` yourself via `ChatOptions.RawRepresentationFactory` (it takes precedence).

```csharp
using GeminiAPIWrapper.Models;

// Gemini 3 series: qualitative thinking level
var config = new GenerationConfig
{
    ThinkingConfig = new ThinkingConfig
    {
        ThinkingLevel = ThinkingLevel.Low,
        IncludeThoughts = true
    }
};
```

### Structured Output (`ResponseJsonSchema` / `ResponseSchema`)

Pass a standard JSON Schema (`JsonElement`) via `GenerationConfig.ResponseJsonSchema` (or `ChatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(...)`), or use the strongly typed OpenAPI `Schema` class via `GenerationConfig.ResponseSchema`:

```csharp
using System.Text.Json;
using GeminiAPIWrapper;
using GeminiAPIWrapper.Models;

using var schemaDoc = JsonDocument.Parse("""
    {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "name": { "type": "string" },
          "distanceAu": { "type": "number" }
        },
        "required": ["name", "distanceAu"]
      }
    }
    """);

var request = GenerateContentRequest.FromPrompt(
    prompt: "List three planets with their average distance from the Sun in AU.",
    generationConfig: new GenerationConfig
    {
        ResponseMimeType = MimeTypes.ApplicationJson,
        ResponseJsonSchema = schemaDoc.RootElement.Clone()
    });

var response = await client.GenerateContentAsync(request);
Console.WriteLine(response.Text);
```

### Inspecting Safety Blocks & Handling Errors

When `response.Text` returns `null`, inspect `response.BlockReason` (prompt-level block in `PromptFeedback`) or `response.FinishReason` (candidate-level stop reason such as `FinishReason.Safety`):

```csharp
using GeminiAPIWrapper;

try
{
    var response = await client.GenerateContentAsync(prompt);
    if (response.BlockReason is { } blockReason)
    {
        Console.WriteLine($"Prompt was blocked: {blockReason}");
    }
    else
    {
        Console.WriteLine(response.Text);
    }
}
catch (GeminiApiException ex)
{
    Console.Error.WriteLine($"HTTP {(int)ex.StatusCode} ({ex.ErrorStatus}, transient={ex.IsTransient}): {ex.ErrorMessage}");
    if (ex.RetryAfter is { } delay)
    {
        Console.Error.WriteLine($"Retry after {delay.TotalSeconds:F0}s");
    }
}
```

### Multimodal Inputs & Unit Testing Tips

- **Multimodal media**: Use `Part.FromBytes(bytes, MimeTypes.ImagePng)` or `Part.FromUri(fileUri, MimeTypes.Pdf)` to construct inline (`Blob`) or file (`FileData`) parts cleanly. `Blob.Data` is strongly typed as `byte[]` and automatically Base64-encoded by `System.Text.Json`. For large audio or video payloads (> 20 MB), prefer uploading via the Files API and passing `Part.FromUri(...)` rather than inline bytes.
- **Vertex AI-only fields**: `GenerateContentRequest.Labels` and `GenerationConfig.AudioTimestamp` are supported by Vertex AI endpoints only; sending them to `generativelanguage.googleapis.com` returns HTTP 400.
- **Unit testing**: Mock `IGeminiClient` directly, or pass an `HttpClient` backed by a custom `HttpMessageHandler` to `new GeminiClient(options, httpClient)` (or `.ConfigurePrimaryHttpMessageHandler(...)` on `AddGeminiClient`).

---

## `GeminiClientOptions` Reference

`GeminiClientOptions.ToString()` automatically redacts `ApiKey`, `ApiKeyProvider`, and `AdditionalHeaders` values so options can be logged safely.

| Property | Type | Default | Description |
|---|---|---|---|
| `Endpoint` | `Uri` | `https://generativelanguage.googleapis.com/v1beta` | Base URI of the Gemini API or gateway (without `:generateContent`). Query strings are preserved. |
| `Model` | `string?` | `null` | Default model ID (`"gemini-2.5-flash"`) or resource name (`"models/..."`, `"tunedModels/..."`). Can be overridden per call. |
| `ApiKey` | `string?` | `null` | Static API key or gateway credential. Mutually exclusive with `ApiKeyProvider`. |
| `ApiKeyProvider` | `Func<CancellationToken, ValueTask<string>>?` | `null` | Async callback invoked per request to obtain a fresh API key or bearer token. |
| `ApiKeyHeader` | `string` | `"x-goog-api-key"` | Header name used to send `ApiKey` or the value returned by `ApiKeyProvider`. |
| `ApiKeyPrefix` | `string?` | `null` | Prefix prepended to the key value (for example `"Bearer "`). |
| `AdditionalHeaders` | `IDictionary<string, string>` | Empty | Extra request headers sent on every call. Validated against CRLF injection and duplicates. |
| `Timeout` | `TimeSpan` | 5 minutes | Request timeout applied to internally created and DI-managed `HttpClient` instances. |
| `StreamIdleTimeout` | `TimeSpan` | 2 minutes | Maximum idle time between streaming chunks before throwing `TimeoutException`. |
| `AllowInsecureHttp` | `bool` | `false` | Allows plain `http://` for non-loopback hosts when `true` (`http://localhost` is always allowed). |
| `StreamingFormat` | `GeminiStreamingFormat` | `ServerSentEvents` | Streaming wire format: `ServerSentEvents` (`?alt=sse`) or `JsonArray`. |

---

## License & Resources

- Released under the [MIT License](LICENSE.txt).
- Issues and pull requests are welcome at [Osg-Junue/GeminiAPIWrapper](https://github.com/Osg-Junue/GeminiAPIWrapper).
- Japanese article (日本語): [GeminiAPIWrapper 紹介記事](https://osg.junue.net/articles/geminiapiwrapper/)
