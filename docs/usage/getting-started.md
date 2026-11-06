# Getting Started

## Requirements

- .NET 10 or later. The package targets `net10.0` only.
- A Gemini API key, or a gateway that accepts your own credential.

## Install

```bash
dotnet add package GeminiGatewayClient
```

## First Request

```csharp
using GeminiGatewayClient;

using var client = new GeminiClient(
    apiKey: Environment.GetEnvironmentVariable("GEMINI_API_KEY")!,
    model: "gemini-flash-latest");

var response = await client.GenerateContentAsync("Explain quantum computing in three sentences.");
Console.WriteLine(response.Text);
```

Through a gateway, pass options instead. The gateway's own credential replaces `x-goog-api-key`:

```csharp
using GeminiGatewayClient;

using var client = new GeminiClient(new GeminiClientOptions
{
    Endpoint = new Uri("https://contoso.azure-api.net/gemini/v1beta"),
    Model = "gemini-flash-latest",
    ApiKey = Environment.GetEnvironmentVariable("GATEWAY_KEY"),
    ApiKeyHeader = "Ocp-Apim-Subscription-Key"
});
```

See [Configuration](configuration.md) for every option and more gateways.

## Two APIs

The library offers two ways to call Gemini. They use the same connection, so you can mix them.

| | `GeminiClient` (`IGeminiClient`) | `IChatClient` |
|---|---|---|
| **Types** | Gemini's own: `GenerateContentRequest`, `Part`, `Tool` | `Microsoft.Extensions.AI`: `ChatMessage`, `ChatOptions`, `AIFunction` |
| **Reach** | Every field in the [compatibility matrix](../API_COMPATIBILITY.md) | Common options; the rest through `RawRepresentationFactory` |
| **Function calling** | You run the loop: [by hand](function-calling.md#building-function-calls-with-geminiclient) | `UseFunctionInvocation()` runs it: [automatic](function-calling.md#automatic-function-calling-with-ichatclient) |
| **Choose it when** | You want full control or are writing Gemini-specific code | You want provider-neutral code or the `Microsoft.Extensions.AI` middleware |

```csharp
using GeminiGatewayClient;
using Microsoft.Extensions.AI;

IChatClient chatClient = client.AsIChatClient();

var chatResponse = await chatClient.GetResponseAsync("Explain Native AOT in one sentence.");
Console.WriteLine(chatResponse.Text);
```

`AsIChatClient()` and `AsIEmbeddingGenerator()` do not own the client: dispose the `GeminiClient` yourself.

## What the Library Leaves to You

- **Input is your responsibility.** A request is not checked before it is sent. An invalid one reaches the API and comes back as HTTP 400 in a [`GeminiApiException`](errors.md).
- **Retries are yours.** Nothing is retried automatically. See [Resilience](configuration.md#resilience).
- **Which settings a model accepts is yours to know.** The library never looks at the model name to change a request.

## Out of Scope

Only stateless generation and embeddings are covered: `:generateContent`, `:streamGenerateContent`, `:embedContent`, and `:batchEmbedContents`.

The Files API, cached content management, batches, tuning, `countTokens`, model listing, and the Live API are not implemented. You can still use their results: pass an uploaded file's URI to `Part.FromUri`, a tuned model as `tunedModels/{id}`, and a cache name as `GenerateContentRequest.CachedContent`.
