using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Token usage statistics for a generation request and its response.
/// See <see href="https://ai.google.dev/api/generate-content#usagemetadata"/>.
/// </summary>
public sealed class UsageMetadata
{
    /// <summary>
    /// Number of tokens in the input prompt.
    /// </summary>
    [JsonPropertyName("promptTokenCount")]
    public int PromptTokenCount { get; set; }

    /// <summary>
    /// Total number of tokens across all generated response candidates (excludes internal thought tokens).
    /// </summary>
    [JsonPropertyName("candidatesTokenCount")]
    public int CandidatesTokenCount { get; set; }

    /// <summary>
    /// Total token count for the generation request (prompt + candidates + thoughts + tool-use prompt tokens where applicable).
    /// </summary>
    [JsonPropertyName("totalTokenCount")]
    public int TotalTokenCount { get; set; }

    /// <summary>
    /// Optional. Number of tokens consumed by the model's internal thinking process on thinking-enabled models.
    /// These tokens are billed as output tokens.
    /// </summary>
    [JsonPropertyName("thoughtsTokenCount")]
    public int? ThoughtsTokenCount { get; set; }

    /// <summary>
    /// Optional. Number of tokens in the prompt that were served from context cache (<see cref="GeminiRequest.CachedContent"/>).
    /// </summary>
    [JsonPropertyName("cachedContentTokenCount")]
    public int? CachedContentTokenCount { get; set; }

    /// <summary>
    /// Optional. Number of tokens in results from tool execution that were included in the prompt.
    /// </summary>
    [JsonPropertyName("toolUsePromptTokenCount")]
    public int? ToolUsePromptTokenCount { get; set; }
}
