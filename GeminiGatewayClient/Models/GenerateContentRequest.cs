using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Request payload for the Gemini content generation endpoints (<c>:generateContent</c> and <c>:streamGenerateContent</c>).
/// See <see href="https://ai.google.dev/api/generate-content#request-body"/>.
/// </summary>
public sealed class GenerateContentRequest
{
    /// <summary>
    /// Optional. Resource name of cached context content to prepend to the conversation.
    /// Format: <c>cachedContents/{cachedContent}</c> or <c>projects/{project}/locations/{location}/cachedContents/{cachedContent}</c>.
    /// </summary>
    [JsonPropertyName("cachedContent")]
    public string? CachedContent { get; set; }

    /// <summary>
    /// Required. The conversation turns sent to the model.
    /// Each <see cref="Content"/> item specifies a <see cref="Content.Role"/> and one or more <see cref="Content.Parts"/>.
    /// </summary>
    [JsonPropertyName("contents")]
    public IList<Content> Contents { get => field ??= []; set; }

    /// <summary>
    /// Optional. Developer-provided system instructions for the model. Only <see cref="Content.Parts"/> is used;
    /// <see cref="Content.Role"/> is ignored by the API.
    /// </summary>
    [JsonPropertyName("systemInstruction")]
    public Content? SystemInstruction { get; set; }

    /// <summary>
    /// Optional. Configuration parameters that control generation behavior (temperature, token limits, structured output, etc.).
    /// </summary>
    [JsonPropertyName("generationConfig")]
    public GenerationConfig? GenerationConfig { get; set; }

    /// <summary>
    /// Optional. Tool declarations (such as function declarations) that the model may invoke.
    /// See <see href="https://ai.google.dev/gemini-api/docs/function-calling"/>.
    /// </summary>
    [JsonPropertyName("tools")]
    public IList<Tool>? Tools { get; set; }

    /// <summary>
    /// Optional. Configuration controlling how the model uses declared <see cref="Tools"/>.
    /// </summary>
    [JsonPropertyName("toolConfig")]
    public ToolConfig? ToolConfig { get; set; }

    /// <summary>
    /// Optional. Per-category safety blocking thresholds applied to responses.
    /// </summary>
    [JsonPropertyName("safetySettings")]
    public IList<SafetySetting>? SafetySettings { get; set; }

    /// <summary>
    /// Optional. Controls whether the request and response are stored by the service when conversation logging is supported.
    /// </summary>
    [JsonPropertyName("store")]
    public bool? Store { get; set; }

    /// <summary>
    /// Optional. <b>Vertex AI only</b> (sending this to the Gemini Developer API <c>generativelanguage.googleapis.com</c> returns HTTP 400).
    /// User-defined key/value metadata labels for billing and filtering on Vertex AI endpoints.
    /// </summary>
    [JsonPropertyName("labels")]
    public IDictionary<string, string>? Labels { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Creates a single-turn request from a user <paramref name="prompt"/> and an optional <paramref name="systemInstruction"/>.
    /// </summary>
    /// <param name="prompt">The user prompt text.</param>
    /// <param name="systemInstruction">Optional system instruction text.</param>
    /// <param name="generationConfig">Optional generation parameters.</param>
    /// <returns>A new <see cref="GenerateContentRequest"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="prompt"/> is <c>null</c>, empty, or whitespace.</exception>
    public static GenerateContentRequest FromPrompt(string prompt, string? systemInstruction = null, GenerationConfig? generationConfig = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return new GenerateContentRequest
        {
            Contents = [Content.FromText(prompt)],
            SystemInstruction = string.IsNullOrEmpty(systemInstruction) ? null : new Content { Parts = [Part.FromText(systemInstruction)] },
            GenerationConfig = generationConfig
        };
    }
}
