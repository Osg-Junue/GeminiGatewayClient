using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CachedContent { get; set; }

    /// <summary>
    /// Required. The conversation turns sent to the model.
    /// Each <see cref="Content"/> item specifies a <see cref="Content.Role"/> and one or more <see cref="Content.Parts"/>.
    /// </summary>
    [JsonPropertyName("contents")]
    public IList<Content> Contents { get; set; } = [];

    /// <summary>
    /// Optional. Developer-provided system instructions for the model. Only <see cref="Content.Parts"/> is used;
    /// <see cref="Content.Role"/> is ignored by the API.
    /// </summary>
    [JsonPropertyName("systemInstruction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Content? SystemInstruction { get; set; }

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
    public IList<Tool>? Tools { get; set; }

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
    public IList<SafetySetting>? SafetySettings { get; set; }

    /// <summary>
    /// Optional. <b>Vertex AI only</b> (sending this to the Gemini Developer API <c>generativelanguage.googleapis.com</c> returns HTTP 400).
    /// User-defined key/value metadata labels for billing and filtering on Vertex AI endpoints.
    /// </summary>
    [JsonPropertyName("labels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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

    /// <summary>
    /// Creates a shallow copy of this request that uses <paramref name="contents"/> as its conversation turns.
    /// Every other member (including members added in future versions) is copied automatically.
    /// </summary>
    internal GenerateContentRequest CloneWithContents(IList<Content> contents)
    {
        var clone = (GenerateContentRequest)MemberwiseClone();
        clone.Contents = contents;
        return clone;
    }

    /// <summary>
    /// Validates client-side invariants that the API would otherwise reject with HTTP 400.
    /// </summary>
    /// <exception cref="ArgumentException">The request is invalid.</exception>
    internal void Validate()
    {
        if (Contents is null || Contents.Count == 0)
        {
            throw new ArgumentException("GenerateContentRequest.Contents must contain at least one Content item.", "request");
        }

        for (var i = 0; i < Contents.Count; i++)
        {
            var content = Contents[i];
            if (content is null)
            {
                throw new ArgumentException($"GenerateContentRequest.Contents[{i}] cannot be null.", "request");
            }

            if ((content.Parts is null || content.Parts.Count == 0) &&
                (content.ExtensionData is null || content.ExtensionData.Count == 0))
            {
                throw new ArgumentException($"GenerateContentRequest.Contents[{i}].Parts must contain at least one Part item.", "request");
            }
        }

        GenerationConfig?.Validate();

        if (Tools is null)
        {
            return;
        }

        foreach (var tool in Tools)
        {
            if (tool?.FunctionDeclarations is null)
            {
                continue;
            }

            foreach (var declaration in tool.FunctionDeclarations)
            {
                if (declaration is null)
                {
                    throw new ArgumentException("Tool.FunctionDeclarations cannot contain null elements.", "request");
                }

                declaration.Validate();
            }
        }
    }
}
