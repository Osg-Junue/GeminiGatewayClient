using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Tool specification containing one or more <see cref="FunctionDeclaration"/> definitions that the model may invoke.
/// See <see href="https://ai.google.dev/api/caching#Tool"/>.
/// </summary>
public sealed class Tool
{
    /// <summary>
    /// Function declarations exposed to the model for tool calling.
    /// </summary>
    [JsonPropertyName("functionDeclarations")]
    public List<FunctionDeclaration> FunctionDeclarations { get; init; } = [];
}
