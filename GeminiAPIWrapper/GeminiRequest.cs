using System.Text.Json.Serialization;
using GeminiAPIWrapper.Configurations;

namespace GeminiAPIWrapper;

/// <summary>
/// Request payload for the Gemini content generation endpoints (<c>:generateContent</c> and <c>:streamGenerateContent</c>).
/// See <see href="https://ai.google.dev/api/generate-content#request-body"/>.
/// </summary>
public class GeminiRequest
{
    /// <summary>
    /// Optional. Resource name of cached context content to prepend to the conversation.
    /// Format: <c>cachedContents/{cachedContent}</c> or <c>projects/{project}/locations/{location}/cachedContents/{cachedContent}</c>.
    /// </summary>
    [JsonPropertyName("cachedContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CachedContent { get; set; }

    /// <summary>
    /// Required. The conversation turns sent to the model.
    /// Each <see cref="Content"/> item specifies a <see cref="Content.Role"/> and one or more <see cref="Content.Parts"/>.
    /// </summary>
    [JsonPropertyName("contents")]
    public required List<Content> Contents { get; init; }

    /// <summary>
    /// Optional. Developer-provided system instructions for the model.
    /// </summary>
    [JsonPropertyName("systemInstruction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SystemInstruction? SystemInstruction { get; set; }

    /// <summary>
    /// Optional. Configuration parameters that control generation behavior (temperature, token limits, structured output, etc.).
    /// </summary>
    [JsonPropertyName("generationConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationConfig? GenerationConfig { get; set; }

    /// <summary>
    /// Optional. Tool declarations (such as function declarations) that the model may invoke.
    /// See <see href="https://ai.google.dev/gemini-api/docs/function-calling"/>.
    /// </summary>
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Tool>? Tools { get; set; }

    /// <summary>
    /// Optional. Configuration controlling how the model uses declared <see cref="Tools"/>.
    /// </summary>
    [JsonPropertyName("toolConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolConfig? ToolConfig { get; set; }

    /// <summary>
    /// Optional. Per-category safety blocking thresholds applied to responses.
    /// </summary>
    [JsonPropertyName("safetySettings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SafetySetting>? SafetySettings { get; set; }

    /// <summary>
    /// Optional. <b>Vertex AI only</b> (sending this to the Gemini Developer API <c>generativelanguage.googleapis.com</c> returns HTTP 400).
    /// User-defined key/value metadata labels for billing and filtering on Vertex AI endpoints.
    /// </summary>
    [JsonPropertyName("labels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Labels { get; set; }
}
