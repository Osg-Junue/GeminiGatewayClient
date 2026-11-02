using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.Models;

namespace GeminiGatewayClient.Serialization;

/// <summary>
/// Source-generated (reflection-free, NativeAOT-safe) serialization metadata for the Gemini wire format.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GenerateContentRequest))]
[JsonSerializable(typeof(GenerateContentResponse))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class GeminiJsonContext : JsonSerializerContext
{
}
