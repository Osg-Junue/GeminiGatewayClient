using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// Tool specification containing one or more <see cref="FunctionDeclaration"/> definitions or built-in server tools that the model may invoke.
/// See <see href="https://ai.google.dev/api/caching#Tool"/>.
/// </summary>
public sealed class Tool
{
    /// <summary>
    /// Optional. Function declarations exposed to the model for tool calling.
    /// </summary>
    [JsonPropertyName("functionDeclarations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<FunctionDeclaration>? FunctionDeclarations { get; set; }

    /// <summary>
    /// Optional. Enables Google Search grounding for the model.
    /// </summary>
    [JsonPropertyName("googleSearch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GoogleSearchTool? GoogleSearch { get; set; }

    /// <summary>
    /// Optional. Enables the model to generate and execute Python code in a sandboxed environment.
    /// </summary>
    [JsonPropertyName("codeExecution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodeExecutionTool? CodeExecution { get; set; }

    /// <summary>
    /// Optional. Enables the model to fetch and ground responses on web URLs provided in the prompt.
    /// </summary>
    [JsonPropertyName("urlContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UrlContextTool? UrlContext { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Returns a copy that also declares <paramref name="declarations"/>, because the caller may reuse this instance.
    /// </summary>
    internal Tool WithDeclarations(IEnumerable<FunctionDeclaration> declarations) => new()
    {
        FunctionDeclarations = [.. FunctionDeclarations ?? [], .. declarations],
        GoogleSearch = GoogleSearch,
        CodeExecution = CodeExecution,
        UrlContext = UrlContext,
        ExtensionData = ExtensionData
    };
}

/// <summary>
/// Configuration for Google Search grounding (<c>googleSearch</c>).
/// </summary>
public sealed class GoogleSearchTool
{
    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Configuration for server-side Python code execution (<c>codeExecution</c>).
/// </summary>
public sealed class CodeExecutionTool
{
    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Configuration for URL context retrieval (<c>urlContext</c>).
/// </summary>
public sealed class UrlContextTool
{
    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
