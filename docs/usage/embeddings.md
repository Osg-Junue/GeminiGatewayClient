# Embeddings

The model comes from the call, then `EmbedContentRequest.Model`, then `GeminiClientOptions.EmbeddingModel`, then `GeminiClientOptions.Model`.

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

`title` needs `TaskType.RetrievalDocument`. With any other task type, or none, the API answers HTTP 400. For other content, pass an `EmbedContentRequest`.

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

- Results keep the order of the requests.
- The URL uses the `model` argument, then the first item's `Model`, then the default.
- An item without `Model` gets the same model, written as `models/{model}`, without changing your request.
- An item that sets its own `Model` keeps it. The API did not reject one that differed from the URL when checked on 2026-11-06, so make sure they match yourself.
- The API accepts 1 to 100 items, and answers HTTP 400 otherwise. Split larger batches yourself.

## `IEmbeddingGenerator`

```csharp
using GeminiGatewayClient;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

IEmbeddingGenerator<string, Embedding<float>> generator =
    client.AsIEmbeddingGenerator(defaultModelId: "gemini-embedding-001", defaultModelDimensions: 256);

var embeddings = await generator.GenerateAsync(["First document", "Second document"]);
ReadOnlyMemory<float> vector = embeddings[0].Vector;
```

Every call is one `:batchEmbedContents` request, so more than 100 inputs fail with HTTP 400. Split them yourself. An empty input is sent as it is, and fails the same way.

An `EmbedContentRequest` from `RawRepresentationFactory` supplies the settings shared by all inputs:

```csharp
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

var options = new EmbeddingGenerationOptions
{
    RawRepresentationFactory = _ => new EmbedContentRequest { TaskType = TaskType.RetrievalQuery }
};
```

| Setting | Taken from, in order |
|---|---|
| Model | `EmbeddingGenerationOptions.ModelId`, the factory's `Model`, the generator's default |
| Dimensions | `EmbeddingGenerationOptions.Dimensions`, the factory's `OutputDimensionality`, the generator's default |
| `TaskType`, `Title`, `ExtensionData` | The factory |

A `BatchEmbedContentsRequest` from the factory is ignored.

The generator checks what comes back: a different number of embeddings, or an empty one, throws [`GeminiApiException`](errors.md#responses-that-cannot-be-used) with status 200.

`AsIEmbeddingGenerator()` does not own the client, and disposing it does nothing.
