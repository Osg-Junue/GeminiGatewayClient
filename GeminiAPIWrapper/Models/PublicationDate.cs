using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// Publication date of a cited source.
/// </summary>
public sealed class PublicationDate
{
    /// <summary>
    /// Year of publication (for example <c>2026</c>).
    /// </summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>
    /// Month of publication (<c>1</c> to <c>12</c>).
    /// </summary>
    [JsonPropertyName("month")]
    public int? Month { get; set; }

    /// <summary>
    /// Day of the month (<c>1</c> to <c>31</c>).
    /// </summary>
    [JsonPropertyName("day")]
    public int? Day { get; set; }

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
