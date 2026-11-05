# Usage Guide & Recipes

This guide provides practical usage patterns for `GeminiGatewayClient`, modeled after the live scenarios in [`tests/GeminiGatewayClient.ExploratoryTests`](../tests/GeminiGatewayClient.ExploratoryTests).

## Table of Contents

1. [API Gateway & Proxy Configurations](#1-api-gateway--proxy-configurations)
2. [Dependency Injection, `IHttpClientFactory` & Resilience](#2-dependency-injection-ihttpclientfactory--resilience)
3. [Single-Turn Generation & Streaming](#3-single-turn-generation--streaming)
4. [Structured JSON Output (`ResponseJsonSchema`)](#4-structured-json-output-responsejsonschema)
5. [Thinking & Reasoning (`ThinkingConfig` and `ReasoningOptions`)](#5-thinking--reasoning-thinkingconfig-and-reasoningoptions)
6. [Multimodal Inputs (Inline Bytes & File URIs)](#6-multimodal-inputs-inline-bytes--file-uris)
7. [`Microsoft.Extensions.AI` (`IChatClient`) & Automatic Tool Chains](#7-microsoftextensionsai-ichatclient--automatic-tool-chains)
8. [Manual Multi-Step & Parallel Function Calling (`GeminiClient`)](#8-manual-multi-step--parallel-function-calling-geminiclient)
9. [Google Search Grounding, URL Context & Embeddings (`IEmbeddingGenerator`)](#9-google-search-grounding-url-context--embeddings-iembeddinggenerator)
10. [Multi-Turn Conversations & Database Persistence](#10-multi-turn-conversations--database-persistence)
11. [Safety Blocks, Timeouts & Error Handling](#11-safety-blocks-timeouts--error-handling)
12. [`GeminiClientOptions` Reference](#12-geminiclientoptions-reference)

---

## 1. API Gateway & Proxy Configurations

> [!IMPORTANT]
> `Endpoint` is the API root URI (default: `https://generativelanguage.googleapis.com/v1beta`). Do **not** append `:generateContent` or `:streamGenerateContent` to `Endpoint`. The client builds `{Endpoint}/models/{Model}:{operation}` (or `{Endpoint}/{Model}:{operation}` when `Model` contains `/`, such as `tunedModels/...`) while preserving any query string on `Endpoint` (e.g., `?api-version=...`).

### Azure API Management (APIM)

When APIM stores the upstream Gemini API key in a Named Value and your client authenticates with an APIM subscription key:

```csharp
var options = new GeminiClientOptions
{
    Endpoint = new Uri("https://contoso.azure-api.net/gemini/v1beta"),
    Model = "gemini-3-flash-preview",
    ApiKey = "<apim-subscription-key>",
    ApiKeyHeader = "Ocp-Apim-Subscription-Key" // Replaces x-goog-api-key completely
};
```

### Cloudflare AI Gateway (Authenticated)

Send both the Gemini API key and the Cloudflare AI Gateway authentication header:

```csharp
var options = new GeminiClientOptions
{
    Endpoint = new Uri("https://gateway.ai.cloudflare.com/v1/<account-id>/<gateway-name>/google-ai-studio/v1beta"),
    Model = "gemini-3-flash-preview",
    ApiKey = "<gemini-api-key>",
    AdditionalHeaders = new Dictionary<string, string>
    {
        ["cf-aig-authorization"] = "Bearer <cloudflare-gateway-token>"
    }
};
```

*(If Cloudflare AI Gateway injects the upstream Gemini key via Unified Billing / BYOK, set `ApiKey = null` so no `x-goog-api-key` header is sent from the client.)*

### Custom Reverse Proxy / BFF with Rotating Bearer Token

```csharp
var options = new GeminiClientOptions
{
    Endpoint = new Uri("https://llm-proxy.internal/gemini/v1beta"),
    Model = "gemini-3-flash-preview",
    ApiKeyHeader = "Authorization",
    ApiKeyPrefix = "Bearer ",
    // Invoked before each HTTP request so short-lived OAuth / JWT tokens rotate cleanly:
    ApiKeyProvider = async ct => await tokenProvider.GetAccessTokenAsync(ct)
};
```

---

## 2. Dependency Injection, `IHttpClientFactory` & Resilience

`services.AddGeminiClient(...)` registers `GeminiClient`, `IGeminiClient`, `IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>`, and `IValidateOptions<GeminiClientOptions>`, returning an `IHttpClientBuilder`:

```csharp
using GeminiGatewayClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddGeminiClient(options =>
{
    options.Endpoint = new Uri("https://contoso.azure-api.net/gemini/v1beta");
    options.Model = "gemini-3-flash-preview";
    options.ApiKeyProvider = async ct => await tokenService.GetTokenAsync(ct);
    options.AdditionalHeaders["x-client-scenario"] = "order-assistant";
    options.Timeout = TimeSpan.FromMinutes(3);
});

await using var provider = services.BuildServiceProvider();

var geminiClient = provider.GetRequiredService<IGeminiClient>();
var chatClient = provider.GetRequiredService<IChatClient>();
```

### Tuning `AddStandardResilienceHandler` for Reasoning Models

`Microsoft.Extensions.Http.Resilience`'s `AddStandardResilienceHandler()` defaults to a **10-second per-attempt** and **30-second total** timeout, which is too short for thinking models. Configure timeouts explicitly when chaining it:

```csharp
services.AddGeminiClient(options => { /* ... */ })
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(3);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(6);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(6); // Must be >= 2x AttemptTimeout
    });
```

> **Note (Streaming & Post-Header Retry Boundary):** `HttpMessageHandler` resilience pipelines (such as `AddStandardResilienceHandler`) operate at the `SendAsync` header boundary (`HttpCompletionOption.ResponseHeadersRead` for streaming). They automatically retry transient transport errors and non-2xx HTTP status codes (e.g., `429` / `503`) before response headers arrive, whereas mid-stream failures after an HTTP 200 header is received—such as an in-stream `{"error": ...}` payload (`GeminiApiException` with `IsTransient == true`) or a stalled stream (`TimeoutException` from `StreamIdleTimeout`)—surface to the caller during enumeration.

---

## 3. Single-Turn Generation & Streaming

### Single-Turn Prompt with System Instruction & `GenerationConfig`

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

using var client = new GeminiClient(
    apiKey: Environment.GetEnvironmentVariable("GEMINI_API_KEY")!,
    model: "gemini-3-flash-preview");

var response = await client.GenerateContentAsync(
    prompt: "What is the capital of Japan and which region is it in? Answer in one sentence.",
    systemInstruction: "You are a concise geography assistant.",
    generationConfig: new GenerationConfig
    {
        Temperature = 0.1,
        MaxOutputTokens = 1024
    });

Console.WriteLine(response.Text);
Console.WriteLine($"Tokens: prompt={response.UsageMetadata?.PromptTokenCount}, total={response.UsageMetadata?.TotalTokenCount}");
```

### Real-Time Streaming (`ServerSentEvents` & `JsonArray`)

By default, `StreamGenerateContentAsync` uses Server-Sent Events (`?alt=sse`), which flushes chunks immediately through proxies. If your gateway requires the legacy JSON array stream format, set `options.StreamingFormat = GeminiStreamingFormat.JsonArray`.

```csharp
var chunks = new List<GenerateContentResponse>();

await foreach (var chunk in client.StreamGenerateContentAsync(
    prompt: "List three key features of C# in one line each.",
    systemInstruction: "Be concise."))
{
    chunks.Add(chunk);
    Console.Write(chunk.Text);
}

// Aggregate streamed chunks (coalescing thought parts, text parts, and trailing thoughtSignature)
// into a single Content item for multi-turn conversation history:
if (chunks.ToModelContent() is { } assistantContent)
{
    history.Add(assistantContent);
}
```

---

## 4. Structured JSON Output (`ResponseJsonSchema`)

Enforce a strict JSON Schema on the model output using `ResponseMimeType = MimeTypes.ApplicationJson` and `ResponseJsonSchema` (standard JSON Schema as a `JsonElement`) or `ResponseSchema` (strongly typed OpenAPI `Schema` model):

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

using var schemaDoc = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "languages": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": { "type": "string" },
              "paradigm": { "type": "string" },
              "isStaticallyTyped": { "type": "boolean" }
            },
            "required": ["name", "paradigm", "isStaticallyTyped"]
          }
        }
      },
      "required": ["languages"]
    }
    """);

var request = GenerateContentRequest.FromPrompt(
    prompt: "Provide the name, primary paradigm, and static typing status for C#, Python, and Rust.",
    generationConfig: new GenerationConfig
    {
        Temperature = 0.0,
        ResponseMimeType = MimeTypes.ApplicationJson,
        ResponseJsonSchema = schemaDoc.RootElement.Clone()
    });

var response = await client.GenerateContentAsync(request);
var catalog = JsonSerializer.Deserialize<LanguageCatalog>(response.Text!);

public sealed record LanguageCatalog(
    [property: JsonPropertyName("languages")] IReadOnlyList<LanguageEntry> Languages);

public sealed record LanguageEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("paradigm")] string Paradigm,
    [property: JsonPropertyName("isStaticallyTyped")] bool IsStaticallyTyped);
```

---

## 5. Thinking & Reasoning (`ThinkingConfig` and `ReasoningOptions`)

- **Gemini 3+ models** use `ThinkingConfig.ThinkingLevel` (`Minimal`, `Low`, `Medium`, `High`).
- **Gemini 2.5 models** use `ThinkingConfig.ThinkingBudget` (`-1` for dynamic, `0` to disable where supported, or a positive token count).
- `ThinkingLevel` and `ThinkingBudget` are mutually exclusive.
- When a thinking model exhausts `MaxOutputTokens` during internal reasoning (`FinishReason.MaxTokens`), `Candidates[0].Content` may be returned with an empty `Parts` collection. Use `response.TryGetModelContent(out var modelContent)` before appending to multi-turn history.

### Direct `GeminiClient` (`ThinkingConfig`)

```csharp
var request = GenerateContentRequest.FromPrompt(
    prompt: "Solve this logic puzzle step by step...",
    generationConfig: new GenerationConfig
    {
        ThinkingConfig = new ThinkingConfig
        {
            ThinkingLevel = ThinkingLevel.Low, // Use ThinkingBudget = 1024 on Gemini 2.5
            IncludeThoughts = true
        }
    });

var response = await client.GenerateContentAsync(request);

if (response.TryGetModelContent(out var modelContent))
{
    foreach (var part in modelContent.Parts)
    {
        if (part.Thought == true)
        {
            Console.WriteLine($"[Thought Summary] {part.Text}");
        }
    }
}

Console.WriteLine($"[Answer] {response.Text}");
Console.WriteLine($"[Thoughts Tokens] {response.UsageMetadata?.ThoughtsTokenCount}");
```

### Via `IChatClient` (`ReasoningOptions`)

When using `IChatClient`, `ChatOptions.Reasoning.Effort` is automatically mapped according to the target model (`gemini-3*` -> `thinkingLevel`, `gemini-2.5*` -> `thinkingBudget`):

```csharp
using Microsoft.Extensions.AI;

IChatClient chatClient = client.AsIChatClient();

var response = await chatClient.GetResponseAsync(
    "Calculate 2^10 - 2^5.",
    new ChatOptions
    {
        Reasoning = new ReasoningOptions
        {
            Effort = ReasoningEffort.Low,
            Output = ReasoningOutput.Summary
        }
    });

// Filter out empty signature-carrier TextReasoningContent items when displaying thought summaries:
foreach (var reasoning in response.Messages
    .SelectMany(m => m.Contents)
    .OfType<TextReasoningContent>()
    .Where(r => !string.IsNullOrEmpty(r.Text)))
{
    Console.WriteLine($"[Reasoning] {reasoning.Text}");
}
Console.WriteLine(response.Text);
```

| `ReasoningEffort` | Gemini 2.5 (`thinkingBudget`) | Gemini 3+ (`thinkingLevel`) |
|---|---|---|
| `None` | `0` (Pro: `128`, the minimum) | `Minimal` (Pro: `Low`) |
| `Low` | `1024` | `Low` |
| `Medium` | `8192` | `Medium` (`gemini-3-pro*`: `High`) |
| `High` / `ExtraHigh` | `24576` | `High` |

---

## 6. Multimodal Inputs (Inline Bytes & File URIs)

Use `Part.FromBytes(byte[], mimeType)` for inline payloads (`Blob.Data` is serialized as Base64 automatically) or `Part.FromUri(fileUri, mimeType)` for files uploaded via the Files API or Cloud Storage:

```csharp
using GeminiGatewayClient.Models;

byte[] imageBytes = await File.ReadAllBytesAsync("diagram.png");

var request = new GenerateContentRequest
{
    Contents =
    [
        Content.FromParts(
            Role.User,
            Part.FromBytes(imageBytes, MimeTypes.ImagePng),
            Part.FromText("Describe the architecture shown in this diagram."))
    ]
};

var response = await client.GenerateContentAsync(request);
Console.WriteLine(response.Text);
```

---

## 7. `Microsoft.Extensions.AI` (`IChatClient`) & Automatic Tool Chains

Combine `client.AsIChatClient()` with `ChatClientBuilder.UseFunctionInvocation()` to automatically execute multi-step C# function chains (both non-streaming and streaming):

```csharp
using System.ComponentModel;
using GeminiGatewayClient;
using Microsoft.Extensions.AI;

[Description("Looks up the customer ID and membership tier (Gold / Silver / Standard) by email.")]
static object LookupCustomerTier([Description("Customer email address")] string email) =>
    new { customerId = "CUST-901", email, tier = "Gold" };

[Description("Returns unit price and subtotal in JPY for a product SKU and quantity.")]
static object GetProductQuote([Description("Product SKU")] string sku, [Description("Quantity")] int quantity) =>
    new { sku, quantity, unitPriceJpy = 20000, subtotalJpy = 20000 * quantity };

[Description("Calculates tier discount and final discounted amount in JPY.")]
static object ComputeTierDiscount([Description("Membership tier")] string tier, [Description("Subtotal in JPY")] int subtotalJpy)
{
    var discount = string.Equals(tier, "Gold", StringComparison.OrdinalIgnoreCase) ? (int)(subtotalJpy * 0.15) : 0;
    return new { tier, discountJpy = discount, discountedTotalJpy = subtotalJpy - discount };
}

IChatClient pipeline = new ChatClientBuilder(client.AsIChatClient())
    .UseFunctionInvocation()
    .Build();

var options = new ChatOptions
{
    Temperature = 0.0f,
    Tools =
    [
        AIFunctionFactory.Create(LookupCustomerTier, "lookup_customer_tier"),
        AIFunctionFactory.Create(GetProductQuote, "get_product_quote"),
        AIFunctionFactory.Create(ComputeTierDiscount, "compute_tier_discount")
    ]
};

ChatResponse response = await pipeline.GetResponseAsync(
    "Check the tier for kenji.tanaka@example.jp and calculate the final discounted price for 3 units of SKU-NVME-4TB.",
    options);

Console.WriteLine(response.Text);
```

---

## 8. Manual Multi-Step & Parallel Function Calling (`GeminiClient`)

When managing the tool-execution loop directly with `GeminiClient`, use `response.TryGetModelContent(out var modelContent)` to append the model's `Content` to `Contents` before appending `Part.FromFunctionResponse(...)`. This preserves the `ThoughtSignature` on `functionCall` parts required by Gemini 3 models while guarding against empty `Parts` when `MaxOutputTokens` is exhausted:

```csharp
using System.Text.Json;
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

var contents = new List<Content>
{
    Content.FromText("Check the weather in Sapporo and Fukuoka for 2026-11-10 and pick the sunny city.", Role.User)
};

var tools = new Tool
{
    FunctionDeclarations =
    [
        new FunctionDeclaration
        {
            Name = "get_branch_weather",
            Description = "Gets the weather forecast for a city and date.",
            ParametersJsonSchema = JsonDocument.Parse("""
                {
                  "type": "object",
                  "properties": {
                    "city": { "type": "string" },
                    "date": { "type": "string" }
                  },
                  "required": ["city", "date"]
                }
                """).RootElement.Clone()
        }
    ]
};

while (true)
{
    var response = await client.GenerateContentAsync(new GenerateContentRequest
    {
        Contents = contents,
        Tools = [tools],
        // Optional: Force a specific tool call using ToolConfig
        // ToolConfig = new ToolConfig
        // {
        //     FunctionCallingConfig = new FunctionCallingConfig
        //     {
        //         Mode = FunctionCallingMode.Any,
        //         AllowedFunctionNames = ["get_branch_weather"]
        //     }
        // }
    });

    // 1. Preserve the exact model Content (including ThoughtSignature) when non-empty
    if (response.TryGetModelContent(out var modelContent))
    {
        contents.Add(modelContent);
    }

    if (response.FunctionCalls.Count == 0)
    {
        Console.WriteLine(response.Text);
        break;
    }

    // 2. Execute all requested function calls (supports parallel calls in a single turn)
    var responseParts = new List<Part>(response.FunctionCalls.Count);
    foreach (var call in response.FunctionCalls)
    {
        JsonElement result = ExecuteMyTool(call.Name, call.Args);
        responseParts.Add(Part.FromFunctionResponse(call.Name, result, call.Id));
    }

    contents.Add(new Content { Role = Role.User, Parts = responseParts });
}
```

---

## 9. Google Search Grounding, URL Context & Embeddings (`IEmbeddingGenerator`)

### Google Search Grounding & Citation Spans via `IChatClient`

Pass `new HostedWebSearchTool()` in `ChatOptions.Tools` to enable Google Search grounding. `GeminiChatClient` maps `Candidate.GroundingMetadata` and `CitationMetadata` onto `TextContent.Annotations` (`CitationAnnotation` with UTF-16 `TextSpanAnnotatedRegion` offsets) and exposes executed search queries under `response.AdditionalProperties[GeminiChatPropertyNames.WebSearchQueries]`:

```csharp
using GeminiGatewayClient;
using Microsoft.Extensions.AI;

IChatClient chatClient = client.AsIChatClient();

ChatResponse response = await chatClient.GetResponseAsync(
    "When was .NET 10 released and is it an LTS or STS release?",
    new ChatOptions
    {
        Temperature = 0.0f,
        Tools = [new HostedWebSearchTool()]
    });

Console.WriteLine(response.Text);

if (response.AdditionalProperties?.TryGetValue(GeminiChatPropertyNames.WebSearchQueries, out var queriesObj) == true &&
    queriesObj is IEnumerable<string> queries)
{
    Console.WriteLine($"Search queries: {string.Join(", ", queries)}");
}

foreach (var textContent in response.Messages.SelectMany(m => m.Contents).OfType<TextContent>())
{
    foreach (var citation in (textContent.Annotations ?? []).OfType<CitationAnnotation>())
    {
        Console.WriteLine($"Source: {citation.Title} ({citation.Url})");
        foreach (var region in (citation.AnnotatedRegions ?? []).OfType<TextSpanAnnotatedRegion>())
        {
            var spanText = textContent.Text[region.StartIndex!.Value..region.EndIndex!.Value];
            Console.WriteLine($"  [{region.StartIndex}..{region.EndIndex}] \"{spanText}\"");
        }
    }
}
```

### URL Context & Grounding via `GeminiClient`

With `GeminiClient`, enable `GoogleSearchTool` or `UrlContextTool` on `GenerateContentRequest.Tools` and inspect `Candidate.GroundingMetadata` or `Candidate.UrlContextMetadata`:

```csharp
using GeminiGatewayClient.Models;

var response = await client.GenerateContentAsync(new GenerateContentRequest
{
    Contents = [Content.FromText("Summarize https://ai.google.dev/gemini-api/docs/url-context in two sentences.")],
    Tools = [new Tool { UrlContext = new UrlContextTool() }]
});

var candidate = response.Candidates[0];
if (candidate.UrlContextMetadata is { } urlContext)
{
    foreach (var item in urlContext.UrlMetadata)
    {
        Console.WriteLine($"{item.RetrievedUrl}: {item.UrlRetrievalStatus}");
    }
}
```

### Embeddings (`IEmbeddingGenerator<string, Embedding<float>>` & `GeminiClient`)

Use `client.AsIEmbeddingGenerator()` (or inject `IEmbeddingGenerator<string, Embedding<float>>` from DI). Single-item calls use `:embedContent`; multi-item calls use `:batchEmbedContents`:

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

IEmbeddingGenerator<string, Embedding<float>> generator =
    client.AsIEmbeddingGenerator(defaultModelId: "gemini-embedding-001", defaultModelDimensions: 256);

// Single input -> :embedContent (with optional TaskType/Title via RawRepresentationFactory)
var queryEmbeddings = await generator.GenerateAsync(
    ["How to stream SSE responses in C#"],
    new EmbeddingGenerationOptions
    {
        RawRepresentationFactory = _ => new EmbedContentRequest
        {
            TaskType = TaskType.RetrievalQuery
        }
    });
ReadOnlyMemory<float> queryVector = queryEmbeddings[0].Vector;

// Multiple inputs -> :batchEmbedContents
var docEmbeddings = await generator.GenerateAsync(
    [
        "Use IAsyncEnumerable<T> and yield return for streaming.",
        "Configure SocketsHttpHandler with AllowAutoRedirect = false."
    ]);
```

---

## 10. Multi-Turn Conversations & Database Persistence

When persisting `Microsoft.Extensions.AI` `ChatMessage` history to a database or JSON cache, `AIContent.RawRepresentation` is not serialized. `GeminiChatClient` automatically stores critical Gemini metadata in `AdditionalProperties` and `ProtectedData` so rehydrated messages continue to work on Gemini 3+ models:

- `TextReasoningContent.ProtectedData`: Stores the reasoning `thoughtSignature` (including trailing signature-only companion items with empty `Text`).
- `TextContent.AdditionalProperties[GeminiChatPropertyNames.ThoughtSignature]`: Stores any trailing text `thoughtSignature`.
- `FunctionCallContent.AdditionalProperties[GeminiChatPropertyNames.ThoughtSignature]`: Stores the function-call `thoughtSignature`.
- `FunctionCallContent.AdditionalProperties[GeminiChatPropertyNames.SynthesizedCallId]`: Marks locally generated call IDs so they are not echoed back to the wire if Gemini did not issue an `id`.

Simply serialize and deserialize `ChatMessage.Contents` (including `AdditionalProperties` and `ProtectedData`) and pass the restored `IList<ChatMessage>` to `chatClient.GetResponseAsync(history, options)`.

---

## 11. Safety Blocks, Timeouts & Error Handling

### Exception & Failure Modes Matrix

`GeminiClient` and `GeminiChatClient` distinguish between validation errors, upstream API errors, idle stream stalls, and request cancellation:

| Exception | When It Is Thrown | Key Properties & Recovery Guidance |
|---|---|---|
| `GeminiApiException` | • Non-2xx HTTP status from Gemini or the gateway<br>• In-stream `{"error": ...}` payload arriving after an HTTP 200 header<br>• Empty or malformed JSON response body | Inspect `StatusCode`, `ErrorStatus` (e.g., `RESOURCE_EXHAUSTED`, `UNAVAILABLE`), `ErrorCode`, `ErrorMessage`, and `ErrorContent` (up to 64 KB). Retry when `IsTransient == true`, honoring `RetryAfter` when present. |
| `TimeoutException` | No response body or streaming chunk bytes arrive within `GeminiClientOptions.StreamIdleTimeout` (default: **2 minutes**). | Indicates a stalled stream or hung upstream connection after headers were received. Safe to retry if the operation is idempotent. |
| `OperationCanceledException` / `TaskCanceledException` | • The caller's `CancellationToken` was canceled (`ex.CancellationToken == ct`)<br>• `HttpClient.Timeout` (`GeminiClientOptions.Timeout`, default: **5 minutes**) elapsed before response headers arrived | Distinguish caller cancellation (`ct.IsCancellationRequested`) from initial header timeout (`!ct.IsCancellationRequested`). Increase `GeminiClientOptions.Timeout` for deep thinking requests. |
| `HttpRequestException` | Transport-level DNS, socket, or TLS failure, including the connection being lost while reading a successful response body or stream. | Check gateway reachability, TLS certificates, or proxy settings. `HttpRequestError` gives the failure category when known, and `StatusCode` is set when the failure came after the response headers. |
| `InvalidOperationException` | • Neither per-call `model` nor `GeminiClientOptions.Model` was set<br>• `ApiKeyProvider` returned an empty, whitespace-only, or newline-containing token | Fix client configuration or token provider implementation. |
| `ArgumentException` / `ArgumentNullException` | Invalid `GenerateContentRequest` or `GeminiClientOptions` (thrown **eagerly** before network I/O, including when calling `StreamGenerateContentAsync` before enumeration starts). | Fix request parameters (e.g., empty `Contents`, conflicting `ThinkingLevel` and `ThinkingBudget`, or `Unknown` wire enum values). |

### Handling `GeminiApiException`, `TimeoutException`, and Safety Blocks

```csharp
using GeminiGatewayClient;

try
{
    var response = await client.GenerateContentAsync(prompt, cancellationToken: ct);

    if (response.BlockReason is { } blockReason)
    {
        Console.WriteLine($"Prompt blocked by safety filter: {blockReason}");
    }
    else
    {
        Console.WriteLine(response.Text);
    }
}
catch (GeminiApiException ex)
{
    Console.Error.WriteLine(
        $"HTTP {(int)ex.StatusCode} (status={ex.ErrorStatus}, code={ex.ErrorCode}, transient={ex.IsTransient}): {ex.ErrorMessage}");

    if (ex.RetryAfter is { } retryAfter)
    {
        Console.Error.WriteLine($"Retry after {retryAfter.TotalSeconds:F0}s");
    }
}
catch (TimeoutException ex)
{
    // StreamIdleTimeout elapsed while reading the response or streaming chunks
    Console.Error.WriteLine($"Response stream stalled: {ex.Message}");
}
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    // HttpClient.Timeout (GeminiClientOptions.Timeout) elapsed before headers arrived
    Console.Error.WriteLine("Request timed out waiting for response headers.");
}
```

### Operational Note: Caller-Supplied `HttpClient` Instances

When you pass your own `HttpClient` to `new GeminiClient(options, httpClient)`, `GeminiClient` does **not** mutate or dispose that instance:
- `GeminiClientOptions.Timeout` is not applied to a caller-supplied `HttpClient`—configure `httpClient.Timeout` directly (or via `IHttpClientBuilder`).
- Configure `SocketsHttpHandler.AllowAutoRedirect = false` on your custom handler so credentials in custom headers (`x-goog-api-key`, `Ocp-Apim-Subscription-Key`) are never forwarded across cross-host redirects (`services.AddGeminiClient(...)` and internally created clients configure this automatically).

---

## 12. `GeminiClientOptions` Reference

`GeminiClientOptions.ToString()` automatically redacts `ApiKey`, `ApiKeyProvider`, and `AdditionalHeaders` values so options can be logged safely.

| Property | Type | Default | Description |
|---|---|---|---|
| `Endpoint` | `Uri` | `https://generativelanguage.googleapis.com/v1beta` | Base URI of the Gemini API or gateway (without `:generateContent`). Query strings are preserved. |
| `Model` | `string?` | `null` | Default model ID (`"gemini-3-flash-preview"`) or resource path (`"models/..."`, `"tunedModels/..."`). |
| `EmbeddingModel` | `string?` | `null` | Default embedding model ID (`"gemini-embedding-001"`) used by `EmbedContentAsync`, `BatchEmbedContentsAsync`, and `IEmbeddingGenerator`. Falls back to `Model` when `null`. |
| `ApiKey` | `string?` | `null` | Static API key or gateway credential. Mutually exclusive with `ApiKeyProvider`. |
| `ApiKeyProvider` | `Func<CancellationToken, ValueTask<string>>?` | `null` | Async callback invoked per request to obtain a fresh API key or bearer token. |
| `ApiKeyHeader` | `string` | `"x-goog-api-key"` | Header name used to send `ApiKey` or the value returned by `ApiKeyProvider`. |
| `ApiKeyPrefix` | `string?` | `null` | Prefix prepended to the key value (for example `"Bearer "`). |
| `AdditionalHeaders` | `IDictionary<string, string>` | Empty | Extra request headers sent on every call. Validated against CRLF injection and duplicates. |
| `Timeout` | `TimeSpan` | 5 minutes | Request timeout applied to internally created and DI-managed `HttpClient` instances. |
| `StreamIdleTimeout` | `TimeSpan?` | 2 minutes | Maximum idle time between streaming chunks before throwing `TimeoutException` (`null` or `Timeout.InfiniteTimeSpan` disables). |
| `AllowInsecureHttp` | `bool` | `false` | Allows plain `http://` for non-loopback hosts when `true` (`http://localhost` is always allowed). |
| `StreamingFormat` | `GeminiStreamingFormat` | `ServerSentEvents` | Streaming wire format: `ServerSentEvents` (`?alt=sse`) or `JsonArray`. |
| `ToolSerializerOptions` | `JsonSerializerOptions?` | `null` | Optional `JsonSerializerOptions` (e.g., custom `JsonSerializerContext`) for serializing custom POCO tool results in `IChatClient`. |
