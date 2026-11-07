# Native AOT

The package is marked AOT-compatible, and its JSON uses source generation. CI publishes a smoke test with Native AOT on Linux twice, once with the whole library kept (rooted) and once trimmed as in a real application, and runs both with stub HTTP responses.

Set `PublishAot` in your project file, so that `dotnet build` also shows AOT warnings:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

Then publish for the OS you build on. Cross-OS native compilation is not supported, and Linux needs `clang` and `zlib1g-dev`:

```bash
dotnet publish -c Release -r linux-x64
```

## Your Own JSON

Gemini's types need nothing from you when they go through the client. Your own types do, in the same places as in any AOT application. The same goes for a Gemini type you serialize yourself, such as a `Content` you save: add it to your own context, with `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull` so that unset fields are not written.

**Reading structured output or function arguments:**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

using var schema = JsonDocument.Parse("""
    { "type": "object", "properties": { "name": { "type": "string" } }, "required": ["name"] }
    """);

var response = await client.GenerateContentAsync(
    "Name a city.",
    generationConfig: new GenerationConfig
    {
        ResponseMimeType = MimeTypes.ApplicationJson,
        ResponseJsonSchema = schema.RootElement.Clone()
    });

var city = JsonSerializer.Deserialize(response.Text!, AppJsonContext.Default.City);

public sealed record City([property: JsonPropertyName("name")] string Name);

[JsonSerializable(typeof(City))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
```

`FunctionCall.Args` is a `JsonElement`, so read arguments the same way: `call.Args?.Deserialize(AppJsonContext.Default.City)`.

**Building a function response:**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.Models;

var result = JsonSerializer.SerializeToElement(new Forecast("Sunny"), ForecastJsonContext.Default.Forecast);
var part = Part.FromFunctionResponse("get_weather", result, id: "call-1");

public sealed record Forecast(string Sky);

[JsonSerializable(typeof(Forecast))]
internal sealed partial class ForecastJsonContext : JsonSerializerContext;
```

## Tools with `IChatClient`

`toolSerializerOptions` is used for `FunctionCallContent.Arguments` and `FunctionResultContent.Result` in the messages you send. Dictionaries and sequences are walked element by element, and these work without any setup: `string`, `bool`, the built-in integer and floating-point types up to `decimal`, `Guid`, date and time types, `Uri`, and byte arrays.

A custom type needs metadata. Start from the default options, so that the JSON stays the same as without AOT, and combine your context with the default resolver:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GeminiGatewayClient;
using Microsoft.Extensions.AI;

var toolOptions = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
{
    TypeInfoResolver = JsonTypeInfoResolver.Combine(ToolJsonContext.Default, AIJsonUtilities.DefaultOptions.TypeInfoResolver)
};

IChatClient aotChatClient = client.AsIChatClient(toolSerializerOptions: toolOptions);

public sealed record WeatherReport(string City, double TempC);

[JsonSerializable(typeof(WeatherReport))]
internal sealed partial class ToolJsonContext : JsonSerializerContext;
```

For an enum to stay a string, add `[JsonSourceGenerationOptions(UseStringEnumConverter = true)]` to your context.

Without metadata, the call throws `InvalidOperationException` and names the type. This happens only when reflection is disabled, that is, in a Native AOT binary or a project that sets `PublishAot`. So set `PublishAot` in the project file to see it during development. Anonymous types and `ValueTuple` fail the same way, and an anonymous type cannot be listed in a context.

The `IChatClient` that `AddGeminiClient` registers uses the default options. Register your own to change them:

```csharp
services.AddSingleton<IChatClient>(sp =>
    sp.GetRequiredService<IGeminiClient>().AsIChatClient(toolSerializerOptions: toolOptions));
```

### `AIFunctionFactory`

`AIFunctionFactory` is in the `Microsoft.Extensions.AI` namespace, and it does not use `toolSerializerOptions`. For a custom type, pass the options to it as well, or `Create` throws `NotSupportedException`:

```csharp
var tool = AIFunctionFactory.Create(
    (string city) => new WeatherReport(city, 21.5),
    "get_weather",
    serializerOptions: toolOptions);
```

`string` arguments and results need no options. How `AIFunctionFactory` behaves under AOT is that library's concern, so check it in your own application.

### Other Serialization

- `GetResponseAsync<T>` needs the options too: `chatClient.GetResponseAsync<City>("Name a city.", toolOptions)`.
- `JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions)` warns under AOT and throws `NotSupportedException` for a `List<ChatMessage>`. [Persist a history](chat-client.md#persisting-history) through the metadata of `IList<ChatMessage>`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

var info = (JsonTypeInfo<IList<ChatMessage>>)AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IList<ChatMessage>));
string json = JsonSerializer.Serialize(history, info);
```
