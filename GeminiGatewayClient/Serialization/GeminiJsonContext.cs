using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using GeminiGatewayClient.Models;

namespace GeminiGatewayClient.Serialization;

/// <summary>
/// Source-generated (reflection-free, NativeAOT-safe) serialization metadata for the Gemini wire format.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GenerateContentRequest))]
[JsonSerializable(typeof(GenerateContentResponse))]
[JsonSerializable(typeof(EmbedContentRequest))]
[JsonSerializable(typeof(EmbedContentResponse))]
[JsonSerializable(typeof(BatchEmbedContentsRequest))]
[JsonSerializable(typeof(BatchEmbedContentsResponse))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class GeminiJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Metadata for serializing request bodies. The default encoder writes non-ASCII characters as six-byte <c>\uXXXX</c>
    /// escapes, doubling CJK payloads that are re-sent on each turn; this one keeps Basic Multilingual Plane text as UTF-8
    /// while still escaping HTML-sensitive ASCII such as <c>&lt;</c> and <c>&amp;</c>. Supplementary characters such as
    /// emoji remain escaped.
    /// </summary>
    internal static GeminiJsonContext Wire => field ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    });
}
