# GeminiAPIWrapper

[![NuGet](https://img.shields.io/nuget/v/GeminiAPIWrapper.svg)](https://www.nuget.org/packages/GeminiAPIWrapper)
[![CI](https://github.com/Osg-Junue/GeminiAPIWrapper/actions/workflows/ci.yml/badge.svg)](https://github.com/Osg-Junue/GeminiAPIWrapper/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.txt)

A lightweight, NativeAOT-compatible .NET client for the **Gemini (Generative Language) API** — built to run cleanly behind API gateways and proxies, with built-in **`Microsoft.Extensions.AI` (`IChatClient`)** and **`IHttpClientFactory`** support.

---

## Why GeminiAPIWrapper?

Google's official `Google.GenAI` SDK is designed around calling Google endpoints directly, sending the API key in the `x-goog-api-key` header.

In production, LLM traffic often passes through an **API gateway, reverse proxy, or BFF** (Azure API Management, Cloudflare AI Gateway, Kong, Envoy, or an internal gateway) where:

1. **The client does not hold the real Gemini API key** — it authenticates to the gateway using a different header such as `Ocp-Apim-Subscription-Key` or `Authorization: Bearer <token>`, and the gateway injects `x-goog-api-key` upstream.
2. **Both a gateway header and a Gemini key are required** — for example, an authenticated Cloudflare AI Gateway requires `cf-aig-authorization: Bearer <token>` in addition to `x-goog-api-key`.
3. **You want a gateway-first `HttpClient` lifecycle** — headers applied per `HttpRequestMessage` (never mutating `HttpClient.DefaultRequestHeaders`), first-class `services.AddGeminiService(options)` registration with `IHttpClientFactory`, and source-generated `System.Text.Json` serialization (`<IsAotCompatible>true</IsAotCompatible>`).

| Capability | `GeminiAPIWrapper` | Official `Google.GenAI` |
|---|---|---|
| Replace the API key header name (`KeyHeader`) | Yes (`x-goog-api-key` is not sent when overridden) | Fixed to `x-goog-api-key` |
| Key value prefix (`KeyPrefix`, e.g. `"Bearer "`) | Built-in option | Via custom headers |
| Omit key header when gateway injects key (`ApiKey = null`) | Built-in option | Via custom auth/headers |
| Extra gateway headers (`AdditionalHeaders`) | Yes | Yes (`HttpOptions.Headers`) |
| `Microsoft.Extensions.AI` (`IChatClient`) | Implemented directly on `GeminiService` | Yes (`AsIChatClient()`) |
| `IHttpClientFactory` DI registration (`AddGeminiService`) | Built-in (`IHttpClientBuilder`) | Manual registration |

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
using GeminiAPIWrapper.Extensions;
using GeminiAPIWrapper.Options;

var options = new GenerativeLanguageOptions
{
    EndpointBase = "https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview",
    ApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
};

using var service = GeminiServiceBuilder.Build(options);

var response = await service.GenerateAsync("Explain quantum computing in three sentences.");
Console.WriteLine(response.GetText());
```

> [!IMPORTANT]
> Do **not** include `:generateContent` or `:streamGenerateContent` at the end of `EndpointBase`. The client appends the appropriate operation automatically while preserving any query string (such as `?api-version=...`).

---

## Using Behind an API Gateway or Proxy

### 1. Azure API Management (APIM)

When APIM holds the real Gemini key in a Named Value and clients authenticate with an APIM subscription key:

```csharp
var options = new GenerativeLanguageOptions
{
    EndpointBase = "https://contoso.azure-api.net/gemini/v1beta/models/gemini-2.5-flash",
    ApiKey = "<apim-subscription-key>",
    KeyHeader = "Ocp-Apim-Subscription-Key" // Replaces x-goog-api-key completely
};
```

### 2. Cloudflare AI Gateway (Authenticated)

Send both the Gemini API key and the Cloudflare AI Gateway authentication header:

```csharp
var options = new GenerativeLanguageOptions
{
    EndpointBase = "https://gateway.ai.cloudflare.com/v1/<account-id>/<gateway-name>/google-ai-studio/v1beta/models/gemini-2.5-flash",
    ApiKey = "<gemini-api-key>",
    AdditionalHeaders = new Dictionary<string, string>
    {
        ["cf-aig-authorization"] = "Bearer <cloudflare-gateway-token>"
    }
};
```

*(If Cloudflare AI Gateway injects the Gemini key via Unified Billing / BYOK, set `ApiKey = null` so no `x-goog-api-key` header is sent from the client.)*

### 3. Custom Reverse Proxy / BFF with Bearer Token

```csharp
var options = new GenerativeLanguageOptions
{
    EndpointBase = "https://llm-proxy.internal/gemini/v1beta/models/gemini-2.5-flash",
    ApiKey = "<proxy-access-token>",
    KeyHeader = "Authorization",
    KeyPrefix = "Bearer "
};
```

---

## Dependency Injection & `IHttpClientFactory`

Register `GeminiService` and `IChatClient` with `IServiceCollection.AddGeminiService(...)`. This returns an `IHttpClientBuilder` so you can chain standard resilience pipelines, custom handlers, or logging:

```csharp
using GeminiAPIWrapper;
using GeminiAPIWrapper.DependencyInjection;
using GeminiAPIWrapper.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddGeminiService(new GenerativeLanguageOptions
{
    EndpointBase = "https://contoso.azure-api.net/gemini/v1beta/models/gemini-2.5-flash",
    ApiKey = builder.Configuration["Gemini:SubscriptionKey"],
    KeyHeader = "Ocp-Apim-Subscription-Key"
});
// Optional: chain .AddStandardResilienceHandler() from Microsoft.Extensions.Http.Resilience

await using var provider = services.BuildServiceProvider();

// Resolve either GeminiService or Microsoft.Extensions.AI.IChatClient
var gemini = provider.GetRequiredService<GeminiService>();
var chatClient = provider.GetRequiredService<IChatClient>();
```

You can also pass an existing `HttpClient` directly to `GeminiServiceBuilder.Build(options, httpClient)`. Headers are attached per `HttpRequestMessage` rather than mutating `HttpClient.DefaultRequestHeaders`, and disposing `GeminiService` will **not** dispose a caller-supplied `HttpClient`:

```csharp
using var service = GeminiServiceBuilder.Build(options, httpClient);
```

---

## `Microsoft.Extensions.AI` (`IChatClient`) Support

`GeminiService` implements `Microsoft.Extensions.AI.IChatClient` directly, supporting multi-turn chat, streaming, tool calling (`FunctionInvokingChatClient`), structured output (`ChatResponseFormat.ForJsonSchema`), per-request model overrides (`ChatOptions.ModelId`), and reasoning configuration (`ChatOptions.Reasoning`):

```csharp
using GeminiAPIWrapper;
using GeminiAPIWrapper.Options;
using Microsoft.Extensions.AI;

using IChatClient chatClient = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
{
    EndpointBase = "https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview",
    ApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
});

ChatResponse response = await chatClient.GetResponseAsync(
[
    new ChatMessage(ChatRole.System, "You are a concise assistant."),
    new ChatMessage(ChatRole.User, "What is NativeAOT in .NET?")
], new ChatOptions
{
    Temperature = 0.3f,
    MaxOutputTokens = 512,
    // Optional: override the model in EndpointBase for this call
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

By default, streaming requests use Server-Sent Events (`?alt=sse`), which flushes chunks immediately through API gateways and reverse proxies. If a custom proxy requires the legacy JSON array stream format, set `StreamingFormat = GeminiStreamingFormat.JsonArray` on `GenerativeLanguageOptions`.

```csharp
await foreach (var chunk in service.StreamGenerateAsync("Write a short story about a lighthouse."))
{
    Console.Write(chunk.GetText());
}
```

### Thinking / Reasoning Models

- **Gemini 3 series models** use `ThinkingConfig.ThinkingLevel` (`Minimal`, `Low`, `Medium`, `High`) or `ChatOptions.Reasoning`.
- **Gemini 2.5 series models** use `ThinkingConfig.ThinkingBudget` (`-1` for dynamic thinking, `0` to disable where supported, or a positive token cap).
- `ThinkingLevel` and `ThinkingBudget` are mutually exclusive.

```csharp
using GeminiAPIWrapper.Configurations;

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
using GeminiAPIWrapper.Configurations;

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

var request = new GeminiRequest
{
    Contents =
    [
        new Content
        {
            Role = Role.User,
            Parts = [new Part { Text = "List three planets with their average distance from the Sun in AU." }]
        }
    ],
    GenerationConfig = new GenerationConfig
    {
        ResponseMimeType = ResponseMimeType.ApplicationJson,
        ResponseJsonSchema = schemaDoc.RootElement.Clone()
    }
};

var response = await service.GenerateAsync(request);
Console.WriteLine(response.GetText());
```

### Inspecting Safety Blocks & Handling Errors

When `response.GetText()` returns `null`, inspect `response.GetBlockReason()` (prompt-level block in `PromptFeedback`) or `response.GetFinishReason()` (candidate-level stop reason such as `FinishReason.Safety`):

```csharp
using GeminiAPIWrapper.ApiException;
using GeminiAPIWrapper.Extensions;

try
{
    var response = await service.GenerateAsync(prompt);
    if (response.GetBlockReason() is { } blockReason)
    {
        Console.WriteLine($"Prompt was blocked: {blockReason}");
    }
    else
    {
        Console.WriteLine(response.GetText());
    }
}
catch (GeminiApiException ex)
{
    Console.Error.WriteLine($"HTTP {(int)ex.StatusCode} ({ex.ErrorStatus}): {ex.ErrorMessage}");
    if (ex.RetryAfter is { } delay)
    {
        Console.Error.WriteLine($"Retry after {delay.TotalSeconds:F0}s");
    }
}
```

### Multimodal Inputs & Unit Testing Tips

- **Multimodal media**: Use `MimeTypes` constants (e.g., `MimeTypes.ImagePng`, `MimeTypes.ImageHeic`, `MimeTypes.AudioFlac`, `MimeTypes.Pdf`) or any custom MIME string on `InlineData.MimeType` / `FileData.MimeType`. For large audio or video payloads (> 20 MB), prefer uploading via the Files API and passing `FileData` (`fileUri`) rather than Base64 `InlineData`.
- **Vertex AI-only fields**: `GeminiRequest.Labels` and `GenerationConfig.AudioTimestamp` are supported by Vertex AI endpoints only; sending them to `generativelanguage.googleapis.com` returns HTTP 400.
- **Unit testing**: Supply an `HttpClient` backed by a custom `HttpMessageHandler` to `GeminiServiceBuilder.Build(options, httpClient)` (or `.ConfigurePrimaryHttpMessageHandler(...)` on `AddGeminiService`), or mock `IChatClient`.

---

## `GenerativeLanguageOptions` Reference

`GenerativeLanguageOptions.ToString()` automatically redacts `ApiKey` and `AdditionalHeaders` values so options can be logged safely.

| Property | Type | Default | Description |
|---|---|---|---|
| `EndpointBase` | `required string` | — | Model endpoint URL without `:generateContent` or `:streamGenerateContent`. Query strings are preserved. |
| `ApiKey` | `string?` | `null` | API key or gateway credential. When `null` or empty, no key header is sent. |
| `KeyHeader` | `string` | `"x-goog-api-key"` | Header name used to send `ApiKey`. |
| `KeyPrefix` | `string?` | `null` | Prefix prepended to `ApiKey` (for example `"Bearer "`). |
| `AdditionalHeaders` | `IReadOnlyDictionary<string, string>?` | `null` | Extra request headers sent on every call. Validated at construction time. |
| `Timeout` | `TimeSpan?` | 5 minutes | Request timeout applied to internally created `HttpClient` instances (ignored when an external `HttpClient` is supplied). |
| `AllowInsecureHttp` | `bool` | `false` | Allows plain `http://` for non-loopback hosts when `true` (`http://localhost` is always allowed). |
| `StreamingFormat` | `GeminiStreamingFormat` | `ServerSentEvents` | Streaming wire format: `ServerSentEvents` (`?alt=sse`) or `JsonArray`. |
| `JsonSerializerOptions` | `JsonSerializerOptions?` | `null` | Serializer options used by `IChatClient` when serializing custom POCO tool results. |

---

## License & Resources

- Released under the [MIT License](LICENSE.txt).
- Issues and pull requests are welcome at [Osg-Junue/GeminiAPIWrapper](https://github.com/Osg-Junue/GeminiAPIWrapper).
- Japanese article (日本語): [GeminiAPIWrapper 紹介記事](https://osg.junue.net/articles/geminiapiwrapper/)
