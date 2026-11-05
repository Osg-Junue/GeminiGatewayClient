using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Configuration parameters that control model generation behavior and output formatting.
/// See <see href="https://ai.google.dev/api/generate-content#generationconfig"/>.
/// </summary>
public sealed class GenerationConfig
{
    /// <summary>
    /// Optional. Controls the randomness of token selection (typically in the range <c>0.0</c> to <c>2.0</c>).
    /// Lower values produce more deterministic outputs; higher values produce more diverse or creative outputs.
    /// </summary>
    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    /// <summary>
    /// Optional. Nucleus sampling threshold (<c>0.0</c> to <c>1.0</c>).
    /// The model considers the smallest set of tokens whose cumulative probability reaches <see cref="TopP"/>.
    /// </summary>
    [JsonPropertyName("topP")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    /// <summary>
    /// Optional. Maximum number of tokens to generate in the response candidate (including thinking tokens where applicable).
    /// </summary>
    [JsonPropertyName("maxOutputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// Optional. Top-K sampling parameter: limits token selection at each step to the <c>K</c> highest-probability tokens.
    /// </summary>
    [JsonPropertyName("topK")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TopK { get; set; }

    /// <summary>
    /// Optional. Number of response candidates to generate (only supported by non-streaming <c>generateContent</c>).
    /// </summary>
    [JsonPropertyName("candidateCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CandidateCount { get; set; }

    /// <summary>
    /// Optional. Character sequences that will stop output generation when encountered (case-sensitive).
    /// </summary>
    [JsonPropertyName("stopSequences")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? StopSequences { get; set; }

    /// <summary>
    /// Optional. Penalty applied to tokens that have already appeared in the generated text, encouraging new topics.
    /// Range: <c>-2.0</c> to <c>2.0</c>.
    /// </summary>
    [JsonPropertyName("presencePenalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PresencePenalty { get; set; }

    /// <summary>
    /// Optional. Penalty applied to tokens proportional to how frequently they have appeared in the generated text,
    /// discouraging repetition. Range: <c>-2.0</c> to <c>2.0</c>.
    /// </summary>
    [JsonPropertyName("frequencyPenalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FrequencyPenalty { get; set; }

    /// <summary>
    /// Optional. MIME type of the generated candidate text, for example <see cref="MimeTypes.TextPlain"/>,
    /// <see cref="MimeTypes.ApplicationJson"/>, or <see cref="MimeTypes.TextXEnum"/>.
    /// </summary>
    [JsonPropertyName("responseMimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseMimeType { get; set; }

    /// <summary>
    /// Optional. OpenAPI 3.03 output schema for structured responses when <see cref="ResponseMimeType"/> is not <see cref="MimeTypes.TextPlain"/>.
    /// Mutually exclusive with <see cref="ResponseJsonSchema"/>.
    /// See <see href="https://ai.google.dev/gemini-api/docs/structured-output"/>.
    /// </summary>
    [JsonPropertyName("responseSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Schema? ResponseSchema { get; set; }

    /// <summary>
    /// Optional. Standard JSON Schema object for structured output when <see cref="ResponseMimeType"/> is <see cref="MimeTypes.ApplicationJson"/>.
    /// Preserves full JSON Schema keywords (<c>$defs</c>, <c>$ref</c>, <c>anyOf</c>, etc.) without lossy conversion.
    /// Mutually exclusive with <see cref="ResponseSchema"/>.
    /// </summary>
    [JsonPropertyName("responseJsonSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ResponseJsonSchema { get; set; }

    /// <summary>
    /// Optional. Thinking/reasoning configuration for models that support thinking.
    /// See <see href="https://ai.google.dev/gemini-api/docs/thinking"/>.
    /// </summary>
    [JsonPropertyName("thinkingConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkingConfig? ThinkingConfig { get; set; }

    /// <summary>
    /// Optional. Seed used for best-effort deterministic decoding.
    /// </summary>
    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seed { get; set; }

    /// <summary>
    /// Optional. When <c>true</c>, exports the log probabilities of the tokens chosen at each generation step.
    /// </summary>
    [JsonPropertyName("responseLogprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ResponseLogprobs { get; set; }

    /// <summary>
    /// Optional. Number of top candidate tokens to return with log probabilities at each generation step (<c>1</c> to <c>20</c>).
    /// Requires <see cref="ResponseLogprobs"/> to be <c>true</c>.
    /// </summary>
    [JsonPropertyName("logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Logprobs { get; set; }

    /// <summary>
    /// Optional. Enables enhanced civic answers when supported by the model.
    /// </summary>
    [JsonPropertyName("enableEnhancedCivicAnswers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableEnhancedCivicAnswers { get; set; }

    /// <summary>
    /// Optional. <b>Vertex AI only</b> (sending this to the Gemini Developer API <c>generativelanguage.googleapis.com</c> returns HTTP 400).
    /// Enables timestamp understanding for audio-only files on supported Vertex AI models.
    /// </summary>
    [JsonPropertyName("audioTimestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AudioTimestamp { get; set; }

    /// <summary>
    /// Optional. Requested modalities of the response (for example <c>"TEXT"</c>, <c>"IMAGE"</c>, <c>"AUDIO"</c>).
    /// </summary>
    [JsonPropertyName("responseModalities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? ResponseModalities { get; set; }

    /// <summary>
    /// Optional. Media resolution used to process input images, videos, and PDF documents.
    /// </summary>
    [JsonPropertyName("mediaResolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaResolution? MediaResolution { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Creates a copy of this configuration with nested mutable objects cloned.
    /// </summary>
    internal GenerationConfig Clone()
    {
        var clone = (GenerationConfig)MemberwiseClone();
        clone.ThinkingConfig = ThinkingConfig?.Clone();
        clone.ResponseSchema = ResponseSchema?.Clone();
        if (StopSequences is not null)
        {
            clone.StopSequences = [.. StopSequences];
        }

        if (ResponseModalities is not null)
        {
            clone.ResponseModalities = [.. ResponseModalities];
        }

        if (ExtensionData is not null)
        {
            clone.ExtensionData = new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal);
        }

        return clone;
    }

    /// <summary>
    /// Validates mutually exclusive settings and enum values.
    /// </summary>
    /// <param name="paramName">The parameter name to report in thrown <see cref="ArgumentException"/> instances.</param>
    /// <exception cref="ArgumentException">Mutually exclusive members are both set or an enum is <c>Unknown</c>.</exception>
    internal void Validate(string paramName = "request")
    {
        if (ResponseSchema is not null && ResponseJsonSchema is not null)
        {
            throw new ArgumentException(
                "GenerationConfig.ResponseSchema and GenerationConfig.ResponseJsonSchema are mutually exclusive and cannot both be set.",
                paramName);
        }

        if (ResponseJsonSchema is { ValueKind: JsonValueKind.Undefined })
        {
            throw new ArgumentException("GenerationConfig.ResponseJsonSchema cannot be JsonValueKind.Undefined.", paramName);
        }

        if (ResponseSchema?.GetValidationError() is { } schemaError)
        {
            throw new ArgumentException($"GenerationConfig.ResponseSchema{schemaError}", paramName);
        }

        if (MediaResolution == Models.MediaResolution.Unknown)
        {
            throw new ArgumentException("GenerationConfig.MediaResolution cannot be MediaResolution.Unknown.", paramName);
        }

        ThinkingConfig?.Validate(paramName);
    }
}

/// <summary>
/// Media resolution used to process input media in a <see cref="GenerationConfig"/>.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<MediaResolution>))]
public enum MediaResolution
{
    /// <summary>
    /// Media resolution is unspecified.
    /// </summary>
    [JsonStringEnumMemberName("MEDIA_RESOLUTION_UNSPECIFIED")]
    Unspecified = 0,

    /// <summary>
    /// Low media resolution (64 tokens per image/frame).
    /// </summary>
    [JsonStringEnumMemberName("MEDIA_RESOLUTION_LOW")]
    Low = 1,

    /// <summary>
    /// Medium media resolution (256 tokens per image/frame).
    /// </summary>
    [JsonStringEnumMemberName("MEDIA_RESOLUTION_MEDIUM")]
    Medium = 2,

    /// <summary>
    /// High media resolution (zoomed reframing with 256 tokens).
    /// </summary>
    [JsonStringEnumMemberName("MEDIA_RESOLUTION_HIGH")]
    High = 3,

    /// <summary>
    /// Fallback value used when the server returns a media resolution not yet defined in this version of the library.
    /// </summary>
    Unknown = -1
}
