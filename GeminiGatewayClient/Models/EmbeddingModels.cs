using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Request payload for the Gemini <c>:embedContent</c> endpoint.
/// See <see href="https://ai.google.dev/api/embeddings#request-body"/>.
/// </summary>
public sealed class EmbedContentRequest
{
    /// <summary>
    /// Optional on <c>:embedContent</c>; populated automatically by <see cref="GeminiClient.BatchEmbedContentsAsync"/>
    /// when omitted on items in <see cref="BatchEmbedContentsRequest.Requests"/>.
    /// Resource name of the model (<c>models/{model}</c>).
    /// </summary>
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    /// <summary>
    /// Required. The content to embed. Only <see cref="Part.Text"/> fields are processed by the embedding endpoint.
    /// </summary>
    [JsonPropertyName("content")]
    public Content Content { get; set; } = null!;

    /// <summary>
    /// Optional. Downstream task type for which the embedding will be used.
    /// </summary>
    [JsonPropertyName("taskType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskType? TaskType { get; set; }

    /// <summary>
    /// Optional. Title for the text. Only applicable when <see cref="TaskType"/> is <see cref="Models.TaskType.RetrievalDocument"/>.
    /// </summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    /// <summary>
    /// Optional. Reduced dimension for the output embedding. When set, must be a positive integer supported by the model.
    /// </summary>
    [JsonPropertyName("outputDimensionality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputDimensionality { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Creates an <see cref="EmbedContentRequest"/> for a single text string.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <param name="taskType">Optional task type hint.</param>
    /// <param name="title">Optional document title (only valid when <paramref name="taskType"/> is <see cref="Models.TaskType.RetrievalDocument"/>).</param>
    /// <param name="outputDimensionality">Optional reduced output dimension.</param>
    /// <returns>A new <see cref="EmbedContentRequest"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <c>null</c>.</exception>
    public static EmbedContentRequest FromText(
        string text,
        TaskType? taskType = null,
        string? title = null,
        int? outputDimensionality = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new EmbedContentRequest
        {
            Content = new Content { Parts = [Part.FromText(text)] },
            TaskType = taskType,
            Title = title,
            OutputDimensionality = outputDimensionality
        };
    }

    internal EmbedContentRequest WithModel(string model)
    {
        var copy = (EmbedContentRequest)MemberwiseClone();
        copy.Model = model;
        return copy;
    }

    internal void Validate(string paramName = "request", int? batchIndex = null)
    {
        if (GetValidationError() is { } error)
        {
            throw new ArgumentException($"{FormatPath(batchIndex)}{error}", paramName);
        }

        if (OutputDimensionality is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                OutputDimensionality,
                $"{FormatPath(batchIndex)}.OutputDimensionality must be greater than zero.");
        }
    }

    private string? GetValidationError()
    {
        if (Content is null)
        {
            return ".Content cannot be null.";
        }

        if (Content.GetValidationError() is { } contentError)
        {
            return $".Content{contentError}";
        }

        if (TaskType == Models.TaskType.Unknown)
        {
            return ".TaskType cannot be TaskType.Unknown.";
        }

        return !string.IsNullOrEmpty(Title) && TaskType is not null and not Models.TaskType.RetrievalDocument
            ? ".Title can only be specified when TaskType is TaskType.RetrievalDocument."
            : null;
    }

    private static string FormatPath(int? batchIndex) =>
        batchIndex is { } index ? $"BatchEmbedContentsRequest.Requests[{index}]" : "EmbedContentRequest";
}

/// <summary>
/// Request payload for the Gemini <c>:batchEmbedContents</c> endpoint.
/// See <see href="https://ai.google.dev/api/embeddings#method:-models.batchembedcontents"/>.
/// </summary>
public sealed class BatchEmbedContentsRequest
{
    /// <summary>
    /// Required. Individual embed requests for the batch.
    /// </summary>
    [JsonPropertyName("requests")]
    public IList<EmbedContentRequest> Requests { get => field ??= []; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    internal void Validate(string paramName = "request")
    {
        if (Requests is null || Requests.Count == 0)
        {
            throw new ArgumentException("BatchEmbedContentsRequest.Requests must contain at least one EmbedContentRequest item.", paramName);
        }

        for (var i = 0; i < Requests.Count; i++)
        {
            var item = Requests[i];
            if (item is null)
            {
                throw new ArgumentException($"BatchEmbedContentsRequest.Requests[{i}] cannot be null.", paramName);
            }

            item.Validate(paramName, i);
        }
    }
}

/// <summary>
/// Response payload returned by the Gemini <c>:embedContent</c> endpoint.
/// See <see href="https://ai.google.dev/api/embeddings#response-body"/>.
/// </summary>
public sealed class EmbedContentResponse
{
    /// <summary>
    /// The embedding generated from the input content.
    /// </summary>
    [JsonPropertyName("embedding")]
    public ContentEmbedding? Embedding { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Response payload returned by the Gemini <c>:batchEmbedContents</c> endpoint.
/// See <see href="https://ai.google.dev/api/embeddings#response-body_1"/>.
/// </summary>
public sealed class BatchEmbedContentsResponse
{
    /// <summary>
    /// The embeddings for each request, in the same order as provided in the batch request.
    /// </summary>
    [JsonPropertyName("embeddings")]
    public IList<ContentEmbedding> Embeddings { get => field ??= []; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// A single dense vector embedding returned by the Gemini embedding endpoints.
/// See <see href="https://ai.google.dev/api/embeddings#contentembedding"/>.
/// </summary>
public sealed class ContentEmbedding
{
    /// <summary>
    /// Floating-point vector values representing the embedding.
    /// </summary>
    [JsonPropertyName("values")]
    public IList<float> Values { get => field ??= []; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Downstream task type for which an embedding will be used.
/// See <see href="https://ai.google.dev/api/embeddings#tasktype"/>.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<TaskType>))]
public enum TaskType
{
    /// <summary>
    /// Unset value, which defaults to one of the other task types on the server (<c>TASK_TYPE_UNSPECIFIED</c>).
    /// </summary>
    [JsonStringEnumMemberName("TASK_TYPE_UNSPECIFIED")]
    Unspecified = 0,

    /// <summary>
    /// The given text is a query in a search/retrieval setting (<c>RETRIEVAL_QUERY</c>).
    /// </summary>
    [JsonStringEnumMemberName("RETRIEVAL_QUERY")]
    RetrievalQuery = 1,

    /// <summary>
    /// The given text is a document from the corpus being searched (<c>RETRIEVAL_DOCUMENT</c>).
    /// </summary>
    [JsonStringEnumMemberName("RETRIEVAL_DOCUMENT")]
    RetrievalDocument = 2,

    /// <summary>
    /// The given text will be used for semantic textual similarity (STS) (<c>SEMANTIC_SIMILARITY</c>).
    /// </summary>
    [JsonStringEnumMemberName("SEMANTIC_SIMILARITY")]
    SemanticSimilarity = 3,

    /// <summary>
    /// The given text will be classified (<c>CLASSIFICATION</c>).
    /// </summary>
    [JsonStringEnumMemberName("CLASSIFICATION")]
    Classification = 4,

    /// <summary>
    /// The embeddings will be used for clustering (<c>CLUSTERING</c>).
    /// </summary>
    [JsonStringEnumMemberName("CLUSTERING")]
    Clustering = 5,

    /// <summary>
    /// The given text will be used for question answering (<c>QUESTION_ANSWERING</c>).
    /// </summary>
    [JsonStringEnumMemberName("QUESTION_ANSWERING")]
    QuestionAnswering = 6,

    /// <summary>
    /// The given text will be used for fact verification (<c>FACT_VERIFICATION</c>).
    /// </summary>
    [JsonStringEnumMemberName("FACT_VERIFICATION")]
    FactVerification = 7,

    /// <summary>
    /// The given text is a natural-language query for code retrieval (<c>CODE_RETRIEVAL_QUERY</c>).
    /// </summary>
    [JsonStringEnumMemberName("CODE_RETRIEVAL_QUERY")]
    CodeRetrievalQuery = 8,

    /// <summary>
    /// Fallback value used when the server returns a task type not yet defined in this version of the library.
    /// </summary>
    Unknown = -1
}

