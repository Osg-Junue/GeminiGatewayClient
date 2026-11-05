using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Request payload for the Gemini content generation endpoints (<c>:generateContent</c> and <c>:streamGenerateContent</c>).
/// See <see href="https://ai.google.dev/api/generate-content#request-body"/>.
/// </summary>
public sealed class GenerateContentRequest
{
    private IList<Content> _contents = [];

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
    public IList<Content> Contents
    {
        get => _contents;
        set => _contents = value ?? [];
    }


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
    /// Optional. Controls whether the request and response are stored by the service when conversation logging is supported.
    /// </summary>
    [JsonPropertyName("store")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Store { get; set; }

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
    /// Creates a copy of this request that uses <paramref name="contents"/> as its conversation turns
    /// with isolated top-level collection and configuration containers.
    /// </summary>
    internal GenerateContentRequest CloneWithContents(IList<Content> contents)
    {
        var clone = (GenerateContentRequest)MemberwiseClone();
        clone.Contents = contents;
        clone.SystemInstruction = SystemInstruction?.Clone();
        clone.GenerationConfig = GenerationConfig?.Clone();
        clone.ToolConfig = ToolConfig?.Clone();
        if (Tools is not null)
        {
            var clonedTools = new List<Tool>(Tools.Count);
            for (var i = 0; i < Tools.Count; i++)
            {
                if (Tools[i] is { } tool)
                {
                    clonedTools.Add(tool.Clone());
                }
            }

            clone.Tools = clonedTools;
        }

        if (SafetySettings is not null)
        {
            var clonedSettings = new List<SafetySetting>(SafetySettings.Count);
            for (var i = 0; i < SafetySettings.Count; i++)
            {
                if (SafetySettings[i] is { } setting)
                {
                    clonedSettings.Add(setting.Clone());
                }
            }

            clone.SafetySettings = clonedSettings;
        }

        if (Labels is not null)
        {
            clone.Labels = new Dictionary<string, string>(Labels, StringComparer.Ordinal);
        }

        if (ExtensionData is not null)
        {
            clone.ExtensionData = new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal);
        }

        return clone;
    }

    /// <summary>
    /// Validates client-side invariants that the API would otherwise reject with HTTP 400.
    /// </summary>
    /// <param name="paramName">The parameter name to report in thrown <see cref="ArgumentException"/> instances.</param>
    /// <exception cref="ArgumentException">The request is invalid.</exception>
    internal void Validate(string paramName = "request")
    {
        var contents = _contents;
        if (contents is null || contents.Count == 0)
        {
            throw new ArgumentException("GenerateContentRequest.Contents must contain at least one Content item.", paramName);
        }

        for (var i = 0; i < contents.Count; i++)
        {
            var content = contents[i];
            if (content is null)
            {
                throw new ArgumentException($"GenerateContentRequest.Contents[{i}] cannot be null.", paramName);
            }

            ValidateContentParts(content, $"GenerateContentRequest.Contents[{i}]", paramName);
        }

        if (SystemInstruction is not null)
        {
            ValidateContentParts(SystemInstruction, "GenerateContentRequest.SystemInstruction", paramName);
        }

        GenerationConfig?.Validate(paramName);
        ToolConfig?.Validate(paramName);

        if (SafetySettings is not null)
        {
            for (var i = 0; i < SafetySettings.Count; i++)
            {
                var setting = SafetySettings[i];
                if (setting is null)
                {
                    throw new ArgumentException($"GenerateContentRequest.SafetySettings[{i}] cannot be null.", paramName);
                }

                if (setting.Category == HarmCategory.Unknown)
                {
                    throw new ArgumentException($"GenerateContentRequest.SafetySettings[{i}].Category cannot be HarmCategory.Unknown.", paramName);
                }

                if (setting.Threshold == HarmBlockThreshold.Unknown)
                {
                    throw new ArgumentException($"GenerateContentRequest.SafetySettings[{i}].Threshold cannot be HarmBlockThreshold.Unknown.", paramName);
                }

                if (setting.Method == HarmBlockMethod.Unknown)
                {
                    throw new ArgumentException($"GenerateContentRequest.SafetySettings[{i}].Method cannot be HarmBlockMethod.Unknown.", paramName);
                }
            }
        }

        if (Tools is null)
        {
            return;
        }

        for (var i = 0; i < Tools.Count; i++)
        {
            var tool = Tools[i];
            if (tool is null)
            {
                throw new ArgumentException($"GenerateContentRequest.Tools[{i}] cannot be null.", paramName);
            }

            if (tool.FunctionDeclarations is null)
            {
                continue;
            }

            foreach (var declaration in tool.FunctionDeclarations)
            {
                if (declaration is null)
                {
                    throw new ArgumentException("Tool.FunctionDeclarations cannot contain null elements.", paramName);
                }

                declaration.Validate(paramName);
            }
        }
    }

    private static void ValidateContentParts(Content content, string pathPrefix, string paramName)
    {
        if (content.Role is { } role && (role == Role.Unknown || !Enum.IsDefined(role)))
        {
            throw new ArgumentException($"{pathPrefix}.Role cannot be Role.Unknown.", paramName);
        }

        var parts = content.Parts;
        if ((parts is null || parts.Count == 0) &&
            (content.ExtensionData is null || content.ExtensionData.Count == 0))
        {
            throw new ArgumentException($"{pathPrefix}.Parts must contain at least one Part item.", paramName);
        }

        if (parts is null)
        {
            return;
        }

        for (var j = 0; j < parts.Count; j++)
        {
            var part = parts[j];
            if (part is null)
            {
                throw new ArgumentException($"{pathPrefix}.Parts[{j}] cannot be null.", paramName);
            }

            part.Validate($"{pathPrefix}.Parts[{j}]", paramName);
        }
    }
}
