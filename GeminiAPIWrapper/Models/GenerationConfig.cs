using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

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
    /// Optional. <b>Vertex AI only</b> (sending this to the Gemini Developer API <c>generativelanguage.googleapis.com</c> returns HTTP 400).
    /// Enables timestamp understanding for audio-only files on supported Vertex AI models.
    /// </summary>
    [JsonPropertyName("audioTimestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AudioTimestamp { get; set; }

    /// <summary>
    /// Creates a shallow copy of this configuration.
    /// </summary>
    internal GenerationConfig Clone() => (GenerationConfig)MemberwiseClone();

    /// <summary>
    /// Validates mutually exclusive settings.
    /// </summary>
    /// <exception cref="ArgumentException">Mutually exclusive members are both set.</exception>
    internal void Validate()
    {
        if (ResponseSchema is not null && ResponseJsonSchema is not null)
        {
            throw new ArgumentException(
                "GenerationConfig.ResponseSchema and GenerationConfig.ResponseJsonSchema are mutually exclusive and cannot both be set.",
                "request");
        }

        ThinkingConfig?.Validate();
    }
}
