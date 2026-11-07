# Getting Started

## Requirements

- .NET 10 or later. The package targets `net10.0` only.
- A Gemini API key, or a gateway that accepts your own credential or adds the key for you.

## Install

```bash
dotnet add package GeminiGatewayClient
```

`IChatClient` works with this package alone. `ChatClientBuilder`, `UseFunctionInvocation()`, and other middleware need one more:

```bash
dotnet add package Microsoft.Extensions.AI
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

`Text` is `null` when no text was generated, for example when the prompt is blocked. That is not an exception. See [Not Exceptions](errors.md#not-exceptions).

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

When `ApiKey` is `null` or empty, no credential header is sent, so make sure `GATEWAY_KEY` is set.

See [Configuration](configuration.md) for every option and more gateways.

## Two APIs

The library offers two ways to call Gemini. `AsIChatClient()` wraps the same `GeminiClient`, so both share its endpoint, credential, and `HttpClient`, and you can mix them.

| | `GeminiClient` (`IGeminiClient`) | `IChatClient` |
|---|---|---|
| **Types** | Gemini's own, in `GeminiGatewayClient.Models`: `GenerateContentRequest`, `Part`, `Tool` | `Microsoft.Extensions.AI`: `ChatMessage`, `ChatOptions`, `AIFunction` |
| **Reach** | Every field in the [compatibility matrix](../API_COMPATIBILITY.md) | Common options; the rest through `RawRepresentationFactory` |
| **Function calling** | You run the loop: [by hand](function-calling.md#building-function-calls-with-geminiclient) | `UseFunctionInvocation()` on `ChatClientBuilder` runs it: [automatic](function-calling.md#automatic-function-calling-with-ichatclient) |
| **Choose it when** | You want full control or are writing Gemini-specific code | You want provider-neutral code or the `Microsoft.Extensions.AI` middleware |

Continuing with the `client` created above:

```csharp
using GeminiGatewayClient;
using Microsoft.Extensions.AI;

IChatClient chatClient = client.AsIChatClient();

var chatResponse = await chatClient.GetResponseAsync("Explain Native AOT in one sentence.");
Console.WriteLine(chatResponse.Text);
```

`AsIChatClient()` and [`AsIEmbeddingGenerator()`](embeddings.md) do not own the client: dispose a `GeminiClient` you created yourself. With [`AddGeminiClient`](configuration.md#dependency-injection), the container disposes it.

## What the Library Leaves to You

- **Input is your responsibility.** The content of a request is not validated: an empty `Contents` or an out-of-range value is sent as it is, and the API's rejection comes back as a [`GeminiApiException`](errors.md), usually HTTP 400. Only a few arguments that cannot be sent or mapped throw earlier. See [Before the Request Is Sent](errors.md#before-the-request-is-sent).
- **Retries are yours.** Nothing is retried automatically. See [Resilience](configuration.md#resilience).
- **Which settings a model accepts is yours to know.** The library never looks at the model name to change a request.

## Out of Scope

Only stateless generation and embeddings are covered: `:generateContent`, `:streamGenerateContent`, `:embedContent`, and `:batchEmbedContents`.

The Files API, cached content management, the Batch API, tuning, `countTokens`, model listing, and the Live API are not implemented. You can still use their results:

- Pass an uploaded file's URI to `Part.FromUri`, or to `UriContent` with `IChatClient`.
- Use a tuned model as the model name, in the form `tunedModels/{id}`.
- Set a cache name on `GenerateContentRequest.CachedContent`. With `IChatClient`, set it through `RawRepresentationFactory`.

## Next Steps

Start with [Text Generation](text-generation.md), or see the [Usage Guide](README.md) for every topic.
