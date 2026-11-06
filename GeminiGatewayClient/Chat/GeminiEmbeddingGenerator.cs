using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using System.Net;

namespace GeminiGatewayClient;

/// <summary>
/// <c>Microsoft.Extensions.AI</c> <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> implementation backed by an <see cref="IGeminiClient"/>.
/// Every call uses <c>:batchEmbedContents</c>. An <see cref="EmbedContentRequest"/> returned by
/// <see cref="EmbeddingGenerationOptions.RawRepresentationFactory"/> supplies the settings shared by all inputs.
/// <para>
/// Create instances with <see cref="GeminiClientExtensions.AsIEmbeddingGenerator(IGeminiClient, string?, int?)"/>.
/// This type does not own the underlying <see cref="IGeminiClient"/>; disposing it is a no-op.
/// </para>
/// </summary>
internal sealed class GeminiEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
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
    public GeminiEmbeddingGenerator(IGeminiClient client, string? defaultModelId = null, int? defaultModelDimensions = null)
    {
        ArgumentNullException.ThrowIfNull(client);

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

        var inputs = values.ToList();
        var template = options?.RawRepresentationFactory?.Invoke(this) as EmbedContentRequest;
        var dimensions = options?.Dimensions ?? template?.OutputDimensionality ?? _metadata.DefaultModelDimensions;
        var model = new[] { options?.ModelId, template?.Model, _metadata.DefaultModelId }.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))?.Trim();
        var request = new BatchEmbedContentsRequest
        {
            Requests = [.. inputs.Select(text => new EmbedContentRequest
            {
                Content = new Content { Parts = [Part.FromText(text)] },
                TaskType = template?.TaskType,
                Title = template?.Title,
                OutputDimensionality = dimensions,
                ExtensionData = template?.ExtensionData
            })]
        };

        var response = await _client.BatchEmbedContentsAsync(request, model, cancellationToken).ConfigureAwait(false);
        if (response.Embeddings.Count != inputs.Count)
        {
            throw new GeminiApiException(
                $"The Gemini batch embedding endpoint returned {response.Embeddings.Count} embeddings for {inputs.Count} input values.",
                HttpStatusCode.OK);
        }

        var generated = new GeneratedEmbeddings<Embedding<float>>(inputs.Count);
        foreach (var embedding in response.Embeddings)
        {
            if (embedding is not { Values.Count: > 0 })
            {
                throw new GeminiApiException($"The Gemini batch embedding endpoint returned an empty embedding at index {generated.Count}.", HttpStatusCode.OK);
            }

            generated.Add(new Embedding<float>(embedding.Values.ToArray()) { ModelId = model });
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
}
