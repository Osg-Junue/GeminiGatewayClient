using System.Net;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient;

/// <summary>
/// <c>Microsoft.Extensions.AI</c> <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> implementation backed by an <see cref="IGeminiClient"/>.
/// Uses <c>:embedContent</c> for single-input calls and <c>:batchEmbedContents</c> when multiple inputs are supplied.
/// <para>
/// Create instances with <see cref="GeminiClientExtensions.AsIEmbeddingGenerator(IGeminiClient, string?, int?)"/>.
/// This type does not own the underlying <see cref="IGeminiClient"/>; disposing it is a no-op.
/// </para>
/// </summary>
public sealed class GeminiEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IGeminiClient _client;
    private readonly EmbeddingGeneratorMetadata _metadata;

    /// <summary>
    /// Initializes a new <see cref="GeminiEmbeddingGenerator"/>.
    /// </summary>
    /// <param name="client">The underlying Gemini client.</param>
    /// <param name="defaultModelId">
    /// The model used when <see cref="EmbeddingGenerationOptions.ModelId"/> is not set; defaults to <see cref="IGeminiClient.DefaultEmbeddingModel"/>.
    /// </param>
    /// <param name="defaultModelDimensions">
    /// Optional default output dimensionality used when <see cref="EmbeddingGenerationOptions.Dimensions"/> is not set.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="defaultModelDimensions"/> is less than or equal to zero.</exception>
    public GeminiEmbeddingGenerator(IGeminiClient client, string? defaultModelId = null, int? defaultModelDimensions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (defaultModelDimensions is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultModelDimensions),
                defaultModelDimensions,
                "defaultModelDimensions must be greater than zero.");
        }

        _client = client;
        _metadata = new EmbeddingGeneratorMetadata(
            "gemini",
            client.Endpoint,
            string.IsNullOrWhiteSpace(defaultModelId) ? client.DefaultEmbeddingModel : defaultModelId.Trim(),
            defaultModelDimensions);
    }

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var inputList = MaterializeInputs(values);
        var dimensions = options?.Dimensions ?? _metadata.DefaultModelDimensions;
        var rawTemplate = options?.RawRepresentationFactory?.Invoke(this);
        var model = ResolveModel(options, rawTemplate);

        if (inputList.Count == 1 && rawTemplate is not BatchEmbedContentsRequest)
        {
            var singleRequest = BuildSingleRequest(inputList[0], dimensions, rawTemplate as EmbedContentRequest);
            var response = await _client.EmbedContentAsync(singleRequest, model, cancellationToken).ConfigureAwait(false);
            if (response.Embedding is not { RawValues: { Count: > 0 } } embedding)
            {
                throw new GeminiApiException(
                    "The Gemini embedding endpoint returned an empty or missing embedding vector.",
                    HttpStatusCode.OK);
            }

            return new GeneratedEmbeddings<Embedding<float>>(1)
            {
                ToEmbedding(embedding, model)
            };
        }

        var batchRequest = BuildBatchRequest(inputList, dimensions, rawTemplate);
        var batchResponse = await _client.BatchEmbedContentsAsync(batchRequest, model, cancellationToken).ConfigureAwait(false);
        var embeddings = batchResponse.RawEmbeddings;
        if (embeddings is null || embeddings.Count != inputList.Count)
        {
            throw new GeminiApiException(
                $"The Gemini batch embedding endpoint returned {embeddings?.Count ?? 0} embeddings for {inputList.Count} input values.",
                HttpStatusCode.OK);
        }

        var generated = new GeneratedEmbeddings<Embedding<float>>(embeddings.Count);
        for (var i = 0; i < embeddings.Count; i++)
        {
            if (embeddings[i] is not { RawValues: { Count: > 0 } } item)
            {
                throw new GeminiApiException(
                    $"The Gemini batch embedding endpoint returned an empty or null embedding at index {i}.",
                    HttpStatusCode.OK);
            }

            generated.Add(ToEmbedding(item, model));
        }

        return generated;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return _metadata;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return serviceType.IsInstanceOfType(_client) ? _client : null;
    }

    /// <summary>
    /// No-op: the underlying <see cref="IGeminiClient"/> is not owned by this instance.
    /// </summary>
    public void Dispose()
    {
    }

    private static IReadOnlyList<string> MaterializeInputs(IEnumerable<string> values)
    {
        if (values is IReadOnlyList<string> readOnlyList)
        {
            if (readOnlyList.Count == 0)
            {
                throw new ArgumentException("values must contain at least one element.", nameof(values));
            }

            for (var i = 0; i < readOnlyList.Count; i++)
            {
                if (readOnlyList[i] is null)
                {
                    throw new ArgumentException($"values[{i}] cannot be null.", nameof(values));
                }
            }

            return readOnlyList;
        }

        var list = new List<string>();
        var index = 0;
        foreach (var value in values)
        {
            if (value is null)
            {
                throw new ArgumentException($"values[{index}] cannot be null.", nameof(values));
            }

            list.Add(value);
            index++;
        }

        if (list.Count == 0)
        {
            throw new ArgumentException("values must contain at least one element.", nameof(values));
        }

        return list;
    }

    private static EmbedContentRequest BuildSingleRequest(
        string text,
        int? dimensions,
        EmbedContentRequest? template) => new()
        {
            Model = template?.Model,
            Content = new Content { Parts = [Part.FromText(text)] },
            TaskType = template?.TaskType,
            Title = template?.Title,
            OutputDimensionality = dimensions ?? template?.OutputDimensionality,
            ExtensionData = template?.ExtensionData is not null
                ? new Dictionary<string, System.Text.Json.JsonElement>(template.ExtensionData, StringComparer.Ordinal)
                : null
        };

    private static BatchEmbedContentsRequest BuildBatchRequest(
        IReadOnlyList<string> inputs,
        int? dimensions,
        object? rawTemplate)
    {
        var singleTemplate = rawTemplate as EmbedContentRequest;
        var batchTemplate = rawTemplate as BatchEmbedContentsRequest;
        var requests = new List<EmbedContentRequest>(inputs.Count);

        for (var i = 0; i < inputs.Count; i++)
        {
            var itemTemplate = batchTemplate?.RawRequests is { } batchItems && i < batchItems.Count
                ? batchItems[i]
                : singleTemplate;
            requests.Add(BuildSingleRequest(inputs[i], dimensions, itemTemplate));
        }

        return new BatchEmbedContentsRequest
        {
            Requests = requests,
            ExtensionData = batchTemplate?.ExtensionData is not null
                ? new Dictionary<string, System.Text.Json.JsonElement>(batchTemplate.ExtensionData, StringComparer.Ordinal)
                : null
        };
    }

    private static Embedding<float> ToEmbedding(ContentEmbedding embedding, string? modelId)
    {
        ReadOnlyMemory<float> vector = embedding.RawValues switch
        {
            float[] array => array,
            List<float> list => list.ToArray(),
            { Count: > 0 } values => values.ToArray(),
            _ => ReadOnlyMemory<float>.Empty
        };

        return new Embedding<float>(vector)
        {
            ModelId = modelId
        };
    }

    private string? ResolveModel(EmbeddingGenerationOptions? options, object? rawTemplate)
    {
        if (!string.IsNullOrWhiteSpace(options?.ModelId))
        {
            return options.ModelId.Trim();
        }

        if (rawTemplate is EmbedContentRequest { Model: { } singleModel } && !string.IsNullOrWhiteSpace(singleModel))
        {
            return singleModel.Trim();
        }

        if (rawTemplate is BatchEmbedContentsRequest { RawRequests: { Count: > 0 } batchItems })
        {
            for (var i = 0; i < batchItems.Count; i++)
            {
                if (batchItems[i] is { Model: { } itemModel } && !string.IsNullOrWhiteSpace(itemModel))
                {
                    return itemModel.Trim();
                }
            }
        }

        return _metadata.DefaultModelId;
    }
}
