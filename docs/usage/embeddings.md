# Embeddings

The model comes from the call, then `EmbedContentRequest.Model`, then `GeminiClientOptions.EmbeddingModel`, then `GeminiClientOptions.Model`. If `Model` is a chat model, set `EmbeddingModel`, because the library does not tell them apart. With no model at all, the call throws [`InvalidOperationException`](errors.md#before-the-request-is-sent).

## One Text

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;

var response = await client.EmbedContentAsync(
    "How do I stream responses in C#?",
    model: "gemini-embedding-001",
    taskType: TaskType.RetrievalQuery,
    outputDimensionality: 256);

IList<float>? vector = response.Embedding?.Values;
```

The `title` parameter needs `TaskType.RetrievalDocument`. With any other task type, or none, the API answers HTTP 400, which arrives as a [`GeminiApiException`](errors.md).

To send a `Content` you build yourself, pass an `EmbedContentRequest`. The endpoint reads text: `gemini-embedding-001` answered HTTP 400 to an image part and to an empty text when checked on 2026-11-07.

## Several Texts

`BatchEmbedContentsAsync` sends one request:

```csharp
using GeminiGatewayClient.Models;

var response = await client.BatchEmbedContentsAsync(
    new BatchEmbedContentsRequest
    {
        Requests =
        [
            EmbedContentRequest.FromText("First document", TaskType.RetrievalDocument),
            EmbedContentRequest.FromText("Second document", TaskType.RetrievalDocument)
        ]
    },
    model: "gemini-embedding-001");

foreach (var embedding in response.Embeddings)
{
    Console.WriteLine(embedding.Values.Count);
}
```

- Results are returned in the order the API sends them, which is the order of the requests.
- The URL uses the `model` argument, then the `Model` of the first item that has one, then the default.
- An item without `Model` gets the same model, written as a resource name such as `models/{model}`, without changing your request.
- An item that sets its own `Model` keeps it. The API did not reject one that differed from the URL when checked on 2026-11-06, so make sure they match yourself.
- The API accepts 1 to 100 items, and answers HTTP 400 otherwise. Split larger batches yourself.
- `EmbedContentAsync` and `BatchEmbedContentsAsync` return the response as it is. Check the number of results and that the values are not empty yourself. Only the generator below does this for you.

## `IEmbeddingGenerator`

```csharp
using GeminiGatewayClient;
using Microsoft.Extensions.AI;

IEmbeddingGenerator<string, Embedding<float>> generator =
    client.AsIEmbeddingGenerator(defaultModelId: "gemini-embedding-001", defaultModelDimensions: 256);

var embeddings = await generator.GenerateAsync(["First document", "Second document"]);
ReadOnlyMemory<float> vector = embeddings[0].Vector;
```

Every call is one `:batchEmbedContents` request, so more than 100 inputs fail with HTTP 400. Split them yourself. An empty list is sent as `{"requests":[]}`, and the API answers HTTP 400.

An `EmbedContentRequest` from `RawRepresentationFactory` supplies the settings shared by all inputs:

```csharp
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

var options = new EmbeddingGenerationOptions
{
    RawRepresentationFactory = _ => new EmbedContentRequest { TaskType = TaskType.RetrievalQuery }
};

var embeddings = await generator.GenerateAsync(["First document"], options);
```

| Setting | Taken from, in order |
|---|---|
| Model | `EmbeddingGenerationOptions.ModelId`, the factory's `Model`, the `defaultModelId` of `AsIEmbeddingGenerator`, `IGeminiClient.DefaultEmbeddingModel` |
| Dimensions | `EmbeddingGenerationOptions.Dimensions`, the factory's `OutputDimensionality`, the `defaultModelDimensions` of `AsIEmbeddingGenerator` |
| `TaskType`, `Title`, `ExtensionData` | The factory |

Any other type from the factory is ignored, and so are the factory's `Content` and `EmbeddingGenerationOptions.AdditionalProperties`.

The generator registered by [`AddGeminiClient`](configuration.md#dependency-injection) has no `defaultModelDimensions`. Set `Dimensions` on each call, or register your own.

The generator checks what comes back: a different number of embeddings, or an empty one, throws [`GeminiApiException`](errors.md#responses-that-cannot-be-used) with status 200.

The generator does not own the client. Disposing the generator does nothing.
