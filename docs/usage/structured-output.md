# Structured Output

Ask for JSON with `ResponseMimeType`, and describe its shape with one of two schema properties. Set only one of them: the library sends what you set, and it does not fill in `ResponseMimeType` for you.

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

`Clone()` keeps the element valid after the document is disposed. Without it, a config that outlives the document throws `ObjectDisposedException` when sent.

`response.Text!` assumes an answer arrived. A blocked or truncated response does not throw, so `Text` can be `null` or hold JSON that stops halfway. Check `IsBlocked` and `FinishReason` before you parse. See [Reading a Response](text-generation.md#reading-a-response).

When streaming, a chunk's `Text` can be a fragment of the JSON. Join the chunks, then parse.

See [Native AOT](native-aot.md) for source-generated JSON.

## Typed `Schema`

```csharp
using GeminiGatewayClient;
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

var response = await client.GenerateContentAsync("Name a film and its year.", generationConfig: config);
```

## Enum Output

For one value out of a fixed list, use `text/x.enum`:

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

var config = new GenerationConfig
{
    ResponseMimeType = MimeTypes.TextXEnum,
    ResponseSchema = new Schema { Type = SchemaType.String, Enum = ["positive", "negative", "neutral"] }
};

var response = await client.GenerateContentAsync("Is the word 'great' positive?", generationConfig: config);
Console.WriteLine(response.Text);
```

## With `IChatClient`

`ChatOptions.ResponseFormat` maps to `ResponseMimeType` and `ResponseJsonSchema`. A JSON format sets `application/json`, and its schema goes in `ResponseJsonSchema`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;

using var schema = JsonDocument.Parse("""{ "type": "object", "properties": { "city": { "type": "string" } } }""");

var response = await chatClient.GetResponseAsync(
    "Name the capital of Japan.",
    new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone()) });

Console.WriteLine(response.Text);
```

- `ChatResponseFormat.Json` asks for JSON without a schema, and `ChatResponseFormat.Text` sets `text/plain`.
- `ForJsonSchema` also takes a name and a description, which are not sent to Gemini. Put a description inside the schema.
- `ForJsonSchema<T>()` builds the schema from a type. `GetResponseAsync<T>` does too, and it needs the `Microsoft.Extensions.AI` package.
- In a blocked response, `Text` is an empty string rather than `null`.

Values from `RawRepresentationFactory` win. If it sets `ResponseMimeType`, `ResponseFormat` is ignored altogether, and if it sets a schema, the schema from `ResponseFormat` is not sent.

A typed `Schema` or enum output has no `ChatResponseFormat`, so set them through the factory. Return a new request on each call:

```csharp
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

var options = new ChatOptions
{
    RawRepresentationFactory = _ => new GenerateContentRequest
    {
        GenerationConfig = new GenerationConfig
        {
            ResponseMimeType = MimeTypes.TextXEnum,
            ResponseSchema = new Schema { Type = SchemaType.String, Enum = ["positive", "negative"] }
        }
    }
};
```
