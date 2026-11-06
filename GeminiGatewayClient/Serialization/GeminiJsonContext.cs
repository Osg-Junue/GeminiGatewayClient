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
    /// Metadata for request bodies. The default encoder writes non-ASCII text as six-byte <c>\uXXXX</c> escapes, which doubles
    /// Japanese text in a history that is sent again on every turn and makes it unreadable in gateway logs. This one keeps it as
    /// UTF-8, while HTML-sensitive ASCII and characters such as emoji stay escaped.
    /// </summary>
    internal static GeminiJsonContext Wire => field ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    });
}
