# Native AOT

The package is marked AOT-compatible, and its JSON uses source generation. CI publishes a smoke test with Native AOT on Linux, both with the library rooted and trimmed as in a real application, and runs both with stub HTTP responses.

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

## Your Own JSON

Gemini's types need nothing from you. Your own types do, in the same places as in any AOT application.

**Reading structured output or function arguments:**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.Models;

var response = await client.GenerateContentAsync("Give me a city as JSON.");
var city = JsonSerializer.Deserialize(response.Text!, AppJsonContext.Default.City);

public sealed record City(string Name);

[JsonSerializable(typeof(City))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
```

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

Function arguments and results go through `toolSerializerOptions`. Dictionaries and sequences are walked element by element, and plain values such as `string`, numbers, `Guid`, and dates work without any setup.

A custom type needs metadata. Combine your context with the default resolver:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GeminiGatewayClient;
using Microsoft.Extensions.AI;

var toolOptions = new JsonSerializerOptions
{
    TypeInfoResolver = JsonTypeInfoResolver.Combine(ToolJsonContext.Default, AIJsonUtilities.DefaultOptions.TypeInfoResolver)
};

IChatClient aotChatClient = client.AsIChatClient(toolSerializerOptions: toolOptions);

public sealed record WeatherReport(string City, double TempC);

[JsonSerializable(typeof(WeatherReport))]
internal sealed partial class ToolJsonContext : JsonSerializerContext;
```

Without metadata, the call throws `InvalidOperationException` and names the type.

The `IChatClient` that `AddGeminiClient` registers uses the default options. Register your own to change them.

`AIFunctionFactory` comes from `Microsoft.Extensions.AI`, and how it behaves under AOT is that library's concern. Check it in your own application.
