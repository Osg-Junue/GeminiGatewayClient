using System.Text.Json.Serialization;
using GeminiAPIWrapper.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Category of potential harm evaluated by Gemini safety filters.
/// See <see href="https://ai.google.dev/api/generate-content#harmcategory"/>.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<HarmCategory>))]
public enum HarmCategory
{
    /// <summary>
    /// Category is unspecified.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_UNSPECIFIED")]
    Unspecified,

    /// <summary>
    /// Harassment content.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_HARASSMENT")]
    Harassment,

    /// <summary>
    /// Hate speech and content that promotes discrimination or disparagement.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_HATE_SPEECH")]
    HateSpeech,

    /// <summary>
    /// Sexually explicit content.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_SEXUALLY_EXPLICIT")]
    SexuallyExplicit,

    /// <summary>
    /// Dangerous content that promotes harm, illegal acts, or self-harm.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_DANGEROUS_CONTENT")]
    DangerousContent,

    /// <summary>
    /// Content that may harm civic integrity.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_CIVIC_INTEGRITY")]
    CivicIntegrity,

    /// <summary>
    /// Fallback value used when the server returns a harm category not yet defined in this version of the library.
    /// </summary>
    Unknown
}
