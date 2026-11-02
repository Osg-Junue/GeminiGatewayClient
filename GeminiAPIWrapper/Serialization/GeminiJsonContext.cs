using System.Text.Json.Serialization;
using GeminiAPIWrapper.Configurations;

namespace GeminiAPIWrapper;

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GeminiRequest))]
[JsonSerializable(typeof(Schema))]
internal partial class GeminiRequestJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GeminiResponse))]
internal partial class GeminiResponseJsonContext : JsonSerializerContext
{
}
