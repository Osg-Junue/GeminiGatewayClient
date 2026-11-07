# Configuration

## `GeminiClientOptions`

| Property | Type | Default | Description |
|---|---|---|---|
| `Endpoint` | `Uri` | `https://generativelanguage.googleapis.com/v1beta` | API root of Gemini or the gateway. Its query string is kept on every request. |
| `Model` | `string?` | `null` | Default model, used when a call names none. With neither, the call throws `InvalidOperationException`. |
| `EmbeddingModel` | `string?` | `null` | Default embedding model. Falls back to `Model`. |
| `ApiKey` | `string?` | `null` | Credential sent in `ApiKeyHeader`. When `null` or empty and there is no provider, no credential header is sent. |
| `ApiKeyProvider` | `Func<CancellationToken, ValueTask<string>>?` | `null` | Supplies the credential for each client call, and for each enumeration of a stream. Cannot be combined with `ApiKey`. |
| `ApiKeyHeader` | `string` | `x-goog-api-key` | Header that carries the credential. |
| `ApiKeyPrefix` | `string?` | `null` | Text before the credential, such as `"Bearer "`. |
| `AdditionalHeaders` | `IDictionary<string, string>` | Empty | Extra headers on every call. Content headers such as `Content-Type` cannot be set here. |
| `StreamIdleTimeout` | `TimeSpan?` | 2 minutes | Longest wait for more data on a stream, after the response headers arrive. Exceeding it throws `TimeoutException`. `null` or `Timeout.InfiniteTimeSpan` disables it. |
| `UseServerSentEvents` | `bool` | `true` | Whether streaming asks for `alt=sse`. See [Streaming Format](#streaming-format). |
| `AllowInsecureHttp` | `bool` | `false` | Allows `http://` to hosts other than loopback. |

`GeminiClient` takes a snapshot when it is created, so later changes to the options have no effect on it. `ToString()` hides the key, header values, and the endpoint's query string, so options can be logged. `ApiKeyPrefix` and header names are shown.

Option values are not range-checked. For example, a negative `StreamIdleTimeout` fails on the first read. See [what is checked](errors.md#when-the-client-is-created).

## Endpoints and Models

The client builds `{Endpoint}/{resource}:{operation}`, keeping any query string from `Endpoint`. Do not put `:generateContent` in `Endpoint`.

| `Model` value | Resource used |
|---|---|
| `gemini-flash-latest` | `models/gemini-flash-latest` |
| `models/gemini-flash-latest` | Used as it is |
| `tunedModels/my-model` | Used as it is |

Surrounding whitespace and slashes are removed first, and a name that still contains `/` is used as it is. A `.` or `..` segment throws `ArgumentException`, because it would move the request and its credential to another path of the gateway.

An alias such as `gemini-flash-latest` follows Google's current model. Use a version ID when the model must not change.

Any call can name another model:

```csharp
var response = await client.GenerateContentAsync("Hello", model: "gemini-3.8-flash");
```

With `IChatClient`, set `ChatOptions.ModelId`. With `IEmbeddingGenerator`, set `EmbeddingGenerationOptions.ModelId`.

## Credentials and Gateways

### Azure API Management

APIM stores the Gemini key, and callers authenticate with a subscription key. Only `Ocp-Apim-Subscription-Key` is sent:

```csharp
var options = new GeminiClientOptions
{
    Endpoint = new Uri("https://contoso.azure-api.net/gemini/v1beta"),
    Model = "gemini-flash-latest",
    ApiKey = "<apim-subscription-key>",
    ApiKeyHeader = "Ocp-Apim-Subscription-Key"
};
```

### Cloudflare AI Gateway

Send the Gemini key and the gateway token. If Cloudflare injects the Gemini key itself, set `ApiKey = null`:

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

### Reverse Proxy with Short-Lived Tokens

`ApiKeyProvider` runs once per client call, and for a stream once per enumeration, so cache tokens inside it. A retry inside an HTTP handler resends the same value. In the samples below, `tokenProvider` stands for your own token service:

```csharp
var options = new GeminiClientOptions
{
    Endpoint = new Uri("https://llm-proxy.internal/gemini/v1beta"),
    Model = "gemini-flash-latest",
    ApiKeyHeader = "Authorization",
    ApiKeyPrefix = "Bearer ",
    ApiKeyProvider = async ct => await tokenProvider.GetAccessTokenAsync(ct)
};
```

### Gateway Authenticated by Other Means

With neither `ApiKey` nor `ApiKeyProvider`, no credential header is sent. Use this when the gateway adds the key and identifies callers another way, such as mutual TLS or `AdditionalHeaders`:

```csharp
var options = new GeminiClientOptions
{
    Endpoint = new Uri("https://llm-proxy.internal/gemini/v1beta"),
    Model = "gemini-flash-latest",
    AdditionalHeaders = new Dictionary<string, string> { ["x-tenant-id"] = "contoso" }
};
```

### Vertex AI

Point `Endpoint` at the publisher path. The same client then resolves `models/{model}` under it. Vertex AI is not covered by this project's live tests.

```csharp
var options = new GeminiClientOptions
{
    Endpoint = new Uri("https://aiplatform.googleapis.com/v1/projects/<project>/locations/global/publishers/google"),
    Model = "gemini-flash-latest",
    ApiKeyHeader = "Authorization",
    ApiKeyPrefix = "Bearer ",
    ApiKeyProvider = async ct => await tokenProvider.GetAccessTokenAsync(ct)
};
```

`GenerateContentRequest.Labels` and `GenerationConfig.AudioTimestamp` are for Vertex AI. The Gemini Developer API rejects them with HTTP 400.

### Header Rules

- A value with a line break or NUL throws `ArgumentException`, because .NET would send it as it is.
- The same header set twice throws `ArgumentException`, for example `ApiKeyHeader` also listed in `AdditionalHeaders` when a credential is set.
- A non-ASCII value is not checked here and fails with `HttpRequestException` when it is sent.

## Streaming Format

By default, streaming asks for Server-Sent Events with `?alt=sse` and `Accept: text/event-stream`.

- A gateway that drops the query string makes Gemini answer with a JSON array. The client reads that as well, though a complete chunk may wait for the data after it.
- A gateway that rejects the query string fails with its own status. Set `UseServerSentEvents = false`, and the client adds neither.

The body decides how a stream is read, not its `Content-Type`.

## Plain HTTP

`http://` is accepted only for loopback hosts. Anything else throws `ArgumentException` unless `AllowInsecureHttp` is `true`, so a credential is not sent in cleartext by accident.

## Using Your Own `HttpClient`

`new GeminiClient(options, httpClient)` neither changes nor disposes your client. Set `httpClient.Timeout` yourself: the .NET default of 100 seconds can be too short for thinking models.

.NET keeps custom credential headers when it follows a redirect to another host, so build your client on `CreateDefaultHandler()`. It follows no redirects and stores no cookies:

```csharp
using var httpClient = new HttpClient(GeminiClient.CreateDefaultHandler())
{
    Timeout = TimeSpan.FromMinutes(10)
};

using var client = new GeminiClient(new GeminiClientOptions { ApiKey = "<key>", Model = "gemini-flash-latest" }, httpClient);
```

With a handler of your own, set `AllowAutoRedirect = false` and `UseCookies = false`.

A client the library creates itself has a 5-minute timeout and uses that same handler.

## Dependency Injection

`AddGeminiClient` registers `GeminiClient`, `IGeminiClient`, `IChatClient`, and `IEmbeddingGenerator<string, Embedding<float>>` as singletons, on a named `HttpClient` from `IHttpClientFactory`. Its name is `GeminiServiceCollectionExtensions.HttpClientName`:

```csharp
using GeminiGatewayClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddGeminiClient(options =>
{
    options.Endpoint = new Uri("https://contoso.azure-api.net/gemini/v1beta");
    options.Model = "gemini-flash-latest";
    options.EmbeddingModel = "gemini-embedding-001";
    options.ApiKey = Environment.GetEnvironmentVariable("GATEWAY_KEY");
    options.ApiKeyHeader = "Ocp-Apim-Subscription-Key";
});

await using var provider = services.BuildServiceProvider();

var geminiClient = provider.GetRequiredService<IGeminiClient>();
var injectedChatClient = provider.GetRequiredService<IChatClient>();
```

Use the overload with `IServiceProvider` when the options need a service:

```csharp
services.AddGeminiClient((serviceProvider, options) =>
{
    var tokens = serviceProvider.GetRequiredService<ITokenProvider>();
    options.ApiKeyProvider = async ct => await tokens.GetAccessTokenAsync(ct);
});
```

Options can also be bound from configuration. This needs no extra package, but `IConfiguration` must be registered. A host does that for you. With a plain `ServiceCollection`, add it yourself:

```csharp
services.AddOptions<GeminiClientOptions>().BindConfiguration("Gemini");
services.AddGeminiClient();
```

Good to know:

- The checks that throw `ArgumentException` in the constructor surface as `OptionsValidationException`, at host startup or when the client is first resolved outside a host. Other failures keep their own type, such as `InvalidOperationException` for a relative `Endpoint`. See [what is checked](errors.md#when-the-client-is-created).
- The returned `IHttpClientBuilder` configures the `HttpClient`, for example `.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMinutes(3))`. The default is 5 minutes, unless a resilience handler changes it. See [Resilience](#resilience).
- A primary handler you attach must also disable redirects and cookies. `GeminiClient.CreateDefaultHandler()` creates one.
- The registered `IChatClient` uses the default tool serializer options. For others, register your own `client.AsIChatClient(toolSerializerOptions: ...)`.
- One client is registered. A second `AddGeminiClient` call adds to the same options rather than creating another client. For a second endpoint or key, create another `GeminiClient` yourself.

## Resilience

The library does not retry. Add a pipeline on the builder that `AddGeminiClient` returns.

`AddStandardResilienceHandler()` from `Microsoft.Extensions.Http.Resilience` defaults to a 10-second attempt and a 30-second total timeout, which is too short for thinking models. Raise them:

```csharp
services.AddGeminiClient(options => options.Model = "gemini-flash-latest")
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(3);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(6);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(6); // At least twice AttemptTimeout
    });
```

Before you add it, note three things. They were measured with version 10.0.0:

- `AddStandardResilienceHandler()` sets `HttpClient.Timeout` to infinite, which also drops a `ConfigureHttpClient` timeout set before it. To keep a timeout, call `.ConfigureHttpClient(c => c.Timeout = ...)` after the handler.
- Its timeouts only cover the wait for the response headers. Without `HttpClient.Timeout`, a response body that stalls waits until you cancel.
- It retries POST requests, so a generation request can be sent, and billed, more than once.

An HTTP handler only sees a stream up to its response headers. A failure after that, such as an in-stream error or `TimeoutException`, reaches your code while you enumerate. Retry it yourself and discard any partial output.
