# Structured Output

Ask for JSON with `ResponseMimeType`, and describe its shape with one of two schema properties.

| Property | Format | Use it when |
|---|---|---|
| `ResponseJsonSchema` | Standard JSON Schema as a `JsonElement` | You have a schema, or need keywords such as `$ref` |
| `ResponseSchema` | Typed `Schema` (OpenAPI subset) | You want to build it in C# |

## JSON Schema

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.Models;

using var schema = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "languages": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "name": { "type": "string" },
              "isStaticallyTyped": { "type": "boolean" }
            },
            "required": ["name", "isStaticallyTyped"]
          }
        }
      },
      "required": ["languages"]
    }
    """);

var request = GenerateContentRequest.FromPrompt(
    prompt: "List C#, Python, and Rust with whether each is statically typed.",
    generationConfig: new GenerationConfig
    {
        Temperature = 0.0,
        ResponseMimeType = MimeTypes.ApplicationJson,
        ResponseJsonSchema = schema.RootElement.Clone()
    });

var response = await client.GenerateContentAsync(request);

// Source-generated metadata keeps this working under trimming and Native AOT.
var catalog = JsonSerializer.Deserialize(response.Text!, CatalogJsonContext.Default.LanguageCatalog);

public sealed record LanguageCatalog(
    [property: JsonPropertyName("languages")] IReadOnlyList<LanguageEntry> Languages);

public sealed record LanguageEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isStaticallyTyped")] bool IsStaticallyTyped);

[JsonSerializable(typeof(LanguageCatalog))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
```

`Clone()` keeps the element valid after the document is disposed.

## Typed `Schema`

```csharp
using GeminiGatewayClient.Models;

var config = new GenerationConfig
{
    ResponseMimeType = MimeTypes.ApplicationJson,
    ResponseSchema = new Schema
    {
        Type = SchemaType.Object,
        Properties = new Dictionary<string, Schema>
        {
            ["title"] = new() { Type = SchemaType.String },
            ["year"] = new() { Type = SchemaType.Integer }
        },
        Required = ["title", "year"]
    }
};
```

## Enum Output

For one value out of a fixed list, use `text/x.enum`:

```csharp
using GeminiGatewayClient.Models;

var config = new GenerationConfig
{
    ResponseMimeType = MimeTypes.TextXEnum,
    ResponseSchema = new Schema { Type = SchemaType.String, Enum = ["positive", "negative", "neutral"] }
};
```

## With `IChatClient`

`ChatOptions.ResponseFormat` maps to the same fields. A JSON format with a schema sets `ResponseJsonSchema`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;

using var schema = JsonDocument.Parse("""{ "type": "object", "properties": { "city": { "type": "string" } } }""");

var response = await chatClient.GetResponseAsync(
    "Name the capital of Japan.",
    new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone()) });
```

`ChatResponseFormat.Json` asks for JSON without a schema, and `ChatResponseFormat.Text` sets `text/plain`.
