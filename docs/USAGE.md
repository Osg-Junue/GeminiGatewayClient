# Usage Guide & Recipes

This guide provides practical usage patterns for `GeminiGatewayClient`, modeled after the live scenarios in [`tests/GeminiGatewayClient.ExploratoryTests`](../tests/GeminiGatewayClient.ExploratoryTests).

## Table of Contents

1. [API Gateway & Proxy Configurations](#1-api-gateway--proxy-configurations)
2. [Dependency Injection, `IHttpClientFactory` & Resilience](#2-dependency-injection-ihttpclientfactory--resilience)
3. [Single-Turn Generation & Streaming](#3-single-turn-generation--streaming)
4. [Structured JSON Output (`ResponseJsonSchema`)](#4-structured-json-output-responsejsonschema)
5. [Thinking (`ThinkingConfig`)](#5-thinking-thinkingconfig)
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
    Model = "gemini-flash-latest",
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
    Model = "gemini-flash-latest",
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
    Model = "gemini-flash-latest",
    ApiKeyHeader = "Authorization",
    ApiKeyPrefix = "Bearer ",
    // Invoked once per client call, so short-lived OAuth / JWT tokens rotate cleanly (HTTP handler retries resend the same value):
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
    options.Model = "gemini-flash-latest";
    options.ApiKeyProvider = async ct => await tokenService.GetTokenAsync(ct);
    options.AdditionalHeaders["x-client-scenario"] = "order-assistant";
});

await using var provider = services.BuildServiceProvider();

var geminiClient = provider.GetRequiredService<IGeminiClient>();
var chatClient = provider.GetRequiredService<IChatClient>();
```

The named `HttpClient` has a 5-minute timeout. Change it on the returned builder: `.ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(3))`. The registered `IChatClient` uses the default tool serializer options; for others, register your own `client.AsIChatClient(toolSerializerOptions: ...)`.

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

> **Note (Streaming & Post-Header Retry Boundary):** `HttpMessageHandler` resilience pipelines (such as `AddStandardResilienceHandler`) operate at the `SendAsync` header boundary (`HttpCompletionOption.ResponseHeadersRead` for streaming). They automatically retry transient transport errors and non-2xx HTTP status codes (e.g., `429` / `503`) before response headers arrive, whereas mid-stream failures after an HTTP 200 header is received—such as an in-stream `{"error": ...}` payload (`GeminiApiException`) or a stalled stream (`TimeoutException` from `StreamIdleTimeout`)—surface to the caller during enumeration.

---

## 3. Single-Turn Generation & Streaming

### Single-Turn Prompt with System Instruction & `GenerationConfig`

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

using var client = new GeminiClient(
    apiKey: Environment.GetEnvironmentVariable("GEMINI_API_KEY")!,
    model: "gemini-flash-latest");

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

### Real-Time Streaming

`StreamGenerateContentAsync` asks for Server-Sent Events (`?alt=sse`), which flushes chunks immediately through proxies. A gateway that does not forward the query string makes Gemini answer with a JSON array instead, and the client reads that as it arrives too, though a complete chunk may wait for the data after it. A gateway that rejects the query string fails with its own status; set `UseServerSentEvents = false` to send neither `alt=sse` nor the `Accept: text/event-stream` header.

```csharp
var chunks = new List<GenerateContentResponse>();

await foreach (var chunk in client.StreamGenerateContentAsync(
    prompt: "List three key features of C# in one line each.",
    systemInstruction: "Be concise."))
{
    chunks.Add(chunk);
    Console.Write(chunk.Text);
}

// Collect the streamed parts into a single Content item for multi-turn conversation history.
// Parts keep their boundaries because Gemini forbids merging a part that carries a thoughtSignature.
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

// Source-generated metadata keeps this working under trimming and Native AOT.
var catalog = JsonSerializer.Deserialize(response.Text!, CatalogJsonContext.Default.LanguageCatalog);

public sealed record LanguageCatalog(
    [property: JsonPropertyName("languages")] IReadOnlyList<LanguageEntry> Languages);

public sealed record LanguageEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("paradigm")] string Paradigm,
    [property: JsonPropertyName("isStaticallyTyped")] bool IsStaticallyTyped);

[JsonSerializable(typeof(LanguageCatalog))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
```

---

## 5. Thinking (`ThinkingConfig`)

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

### Via `IChatClient`

`ChatOptions.Reasoning` sends `thinkingLevel`, which Gemini 3 and later understand. `Effort` maps `None` to `MINIMAL`, because no level turns thinking off, and `ExtraHigh` to `HIGH`. `Output` sets `IncludeThoughts`: `false` for `None`, `true` otherwise. Which values a model accepts is yours to decide, and a model that does not accept one answers HTTP 400. For `ThinkingBudget`, set `ThinkingConfig` through `RawRepresentationFactory`, which must return a new request on each call and wins over `Reasoning`:

```csharp
using Microsoft.Extensions.AI;

IChatClient chatClient = client.AsIChatClient();

var response = await chatClient.GetResponseAsync(
    "Calculate 2^10 - 2^5.",
    new ChatOptions
    {
        Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low, Output = ReasoningOutput.Summary }
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

---

## 6. Multimodal Inputs (Inline Bytes & File URIs)

Use `Part.FromBytes(data, mimeType)` for inline payloads (`Blob.Data` is serialized as Base64 automatically) or `Part.FromUri(fileUri, mimeType)` for files uploaded via the Files API or Cloud Storage:

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

Citations sit on the `TextContent` when a response has a single text. Otherwise, and always when streaming, they arrive on an annotation-only `AIContent` in the update of the chunk that carried them. Regions then count from the start of the full response text.

Pass `new HostedWebSearchTool()` in `ChatOptions.Tools` to enable Google Search grounding. `GeminiChatClient` maps `Candidate.GroundingMetadata` and `CitationMetadata` onto `AIContent.Annotations` (`CitationAnnotation` with UTF-16 `TextSpanAnnotatedRegion` offsets) and exposes executed search queries under `response.AdditionalProperties[GeminiChatPropertyNames.WebSearchQueries]`:

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

var text = response.Text;
foreach (var citation in response.Messages.SelectMany(m => m.Contents).SelectMany(c => c.Annotations ?? []).OfType<CitationAnnotation>())
{
    Console.WriteLine($"Source: {citation.Title} ({citation.Url})");
    foreach (var region in (citation.AnnotatedRegions ?? []).OfType<TextSpanAnnotatedRegion>())
    {
        Console.WriteLine($"  [{region.StartIndex}..{region.EndIndex}] \"{text[region.StartIndex!.Value..region.EndIndex!.Value]}\"");
    }
}
```

### Combining Functions with Google Search or Code Execution

Gemini answers HTTP 400 when `ChatOptions.Tools` mixes functions with `HostedWebSearchTool` or `HostedCodeInterpreterTool`, unless the request sets `toolConfig.includeServerSideToolInvocations`. Set it through `RawRepresentationFactory`, returning a new request for each call:

```csharp
var options = new ChatOptions
{
    Tools = [getWeather, new HostedWebSearchTool()],
    RawRepresentationFactory = _ => new GenerateContentRequest
    {
        ToolConfig = new ToolConfig
        {
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["includeServerSideToolInvocations"] = JsonDocument.Parse("true").RootElement
            }
        }
    }
};
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

Use `client.AsIEmbeddingGenerator()` (or inject `IEmbeddingGenerator<string, Embedding<float>>` from DI). Every call is one `:batchEmbedContents` request, so more than 100 inputs fail with HTTP 400; split them yourself. An `EmbedContentRequest` from `RawRepresentationFactory` supplies the settings shared by all inputs:

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

IEmbeddingGenerator<string, Embedding<float>> generator =
    client.AsIEmbeddingGenerator(defaultModelId: "gemini-embedding-001", defaultModelDimensions: 256);

// Optional TaskType/Title via RawRepresentationFactory
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

// Several inputs in one request
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

When trimming a history, remove a `FunctionCallContent` together with its `FunctionResultContent`. A result without its call in the messages throws `ArgumentException`, because only the call knows the function name.

---

## 11. Safety Blocks, Timeouts & Error Handling

Settings without a `ChatOptions` counterpart go through `ChatOptions.RawRepresentationFactory`, which must return a new `GenerateContentRequest` on each call because the client modifies it. `ChatOptions.AdditionalProperties` are not read.

### Exception & Failure Modes Matrix

Upstream API errors, idle stream stalls, and request cancellation surface as different exceptions. Requests are not checked before sending, so an invalid one comes back as `GeminiApiException` with HTTP 400:

| Exception | When It Is Thrown | Key Properties & Recovery Guidance |
|---|---|---|
| `GeminiApiException` | • Non-2xx HTTP status from Gemini or the gateway<br>• In-stream `{"error": ...}` payload or `error` event with data arriving after an HTTP 200 header<br>• Empty or malformed JSON response body | Inspect `StatusCode`, `ErrorStatus` (e.g., `RESOURCE_EXHAUSTED`, `UNAVAILABLE`), `ErrorCode`, `ErrorMessage`, and `ErrorContent` (up to 64 KB). On a stream, a longer body leaves `ErrorContent`, `ErrorStatus`, `ErrorCode`, and `ErrorMessage` empty; `StatusCode` stays. Decide on retries from `StatusCode` and `ErrorStatus`, honoring `RetryAfter` when present. |
| `TimeoutException` | No data arrives within `GeminiClientOptions.StreamIdleTimeout` (default: **2 minutes**). Keep-alive comments from a gateway count as data. | Indicates a stalled stream or hung upstream connection after headers were received. Safe to retry if the operation is idempotent. |
| `OperationCanceledException` / `TaskCanceledException` | • The caller's `CancellationToken` was canceled<br>• `HttpClient.Timeout` (default: **5 minutes** on clients this library creates, covering a complete response or the headers of a stream) elapsed | Tell them apart with your own token: `ct.IsCancellationRequested` is `true` for caller cancellation and `false` for the timeout. Do not compare `ex.CancellationToken` with `ct`: the client links tokens, so the exception may carry another token. Increase `HttpClient.Timeout` for deep thinking requests. |
| `HttpRequestException` | • Transport-level DNS, socket, or TLS failure, including the connection being lost while reading a response body or stream. `StatusCode` is set only for a stream.<br>• A non-streaming response body is larger than `HttpClient.MaxResponseContentBufferSize` | Check gateway reachability, TLS certificates, or proxy settings. `HttpRequestError` gives the failure category when known (`ConfigurationLimitExceeded` for an oversized body). |
| `InvalidOperationException` | • Neither per-call `model` nor `GeminiClientOptions.Model` was set | Set a model. |
| `ArgumentException` / `ArgumentNullException` | • A `null` argument<br>• A header that cannot be sent: when the client is created, or on each call for a key from `ApiKeyProvider`<br>• A function result without its call | Fix the argument, header, or history. |

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
        $"HTTP {(int)ex.StatusCode} (status={ex.ErrorStatus}, code={ex.ErrorCode}): {ex.ErrorMessage}");

    if (ex.RetryAfter is { } retryAfter)
    {
        Console.Error.WriteLine($"Retry after {retryAfter.TotalSeconds:F0}s");
    }
}
catch (TimeoutException ex)
{
    // StreamIdleTimeout elapsed while reading streaming chunks
    Console.Error.WriteLine($"Response stream stalled: {ex.Message}");
}
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    // HttpClient.Timeout elapsed
    Console.Error.WriteLine("Request timed out.");
}
```

### Operational Note: Caller-Supplied `HttpClient` Instances

When you pass your own `HttpClient` to `new GeminiClient(options, httpClient)`, `GeminiClient` does **not** mutate or dispose that instance:
- Configure `httpClient.Timeout` directly (or via `IHttpClientBuilder`).
- Build it on `GeminiClient.CreateDefaultHandler()`, as in `new HttpClient(GeminiClient.CreateDefaultHandler())`, so credentials in custom headers (`x-goog-api-key`, `Ocp-Apim-Subscription-Key`) are never forwarded across cross-host redirects and cookies never pass between callers. With a handler of your own, set `AllowAutoRedirect = false` and `UseCookies = false`. `services.AddGeminiClient(...)` and internally created clients do this automatically; a primary handler you attach to its `IHttpClientBuilder` needs the same settings.

---

## 12. `GeminiClientOptions` Reference

`GeminiClientOptions.ToString()` automatically redacts `ApiKey`, `ApiKeyProvider`, and `AdditionalHeaders` values so options can be logged safely.

| Property | Type | Default | Description |
|---|---|---|---|
| `Endpoint` | `Uri` | `https://generativelanguage.googleapis.com/v1beta` | Base URI of the Gemini API or gateway (without `:generateContent`). Query strings are preserved. |
| `Model` | `string?` | `null` | Default model ID (`"gemini-flash-latest"`) or resource path (`"models/..."`, `"tunedModels/..."`). An alias such as `gemini-flash-latest` follows the current model; pin a version ID when the model must not change. `GET /v1beta/models` can list a model that still answers 404 to new users. |
| `EmbeddingModel` | `string?` | `null` | Default embedding model ID (`"gemini-embedding-001"`) used by `EmbedContentAsync`, `BatchEmbedContentsAsync`, and `IEmbeddingGenerator`. Falls back to `Model` when `null`. |
| `ApiKey` | `string?` | `null` | Static API key or gateway credential. Mutually exclusive with `ApiKeyProvider`. |
| `ApiKeyProvider` | `Func<CancellationToken, ValueTask<string>>?` | `null` | Async callback invoked once per client call to obtain a fresh API key or bearer token. Retries inside an HTTP handler, such as `AddStandardResilienceHandler`, resend the same value. |
| `ApiKeyHeader` | `string` | `"x-goog-api-key"` | Header name used to send `ApiKey` or the value returned by `ApiKeyProvider`. |
| `ApiKeyPrefix` | `string?` | `null` | Prefix prepended to the key value (for example `"Bearer "`). |
| `AdditionalHeaders` | `IDictionary<string, string>` | Empty | Extra request headers sent on every call. A value with a line break or NUL throws `ArgumentException`. |
| `StreamIdleTimeout` | `TimeSpan?` | 2 minutes | Maximum time without received data on a stream before throwing `TimeoutException`; keep-alive comments reset it (`null` or `Timeout.InfiniteTimeSpan` disables). |
| `UseServerSentEvents` | `bool` | `true` | Whether streaming calls ask for Server-Sent Events with `alt=sse`. With `false`, Gemini answers with a JSON array. |
| `AllowInsecureHttp` | `bool` | `false` | Allows plain `http://` for non-loopback hosts when `true` (`http://localhost` is always allowed). |
