using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Developer-provided system instructions that guide model behavior across the conversation.
/// See <see href="https://ai.google.dev/gemini-api/docs/text-generation#system-instructions"/>.
/// </summary>
public class SystemInstruction
{
    /// <summary>
    /// Required. Content parts that make up the system instruction (typically text parts).
    /// </summary>
    [JsonPropertyName("parts")]
    public required List<Part> Parts { get; init; }
}
