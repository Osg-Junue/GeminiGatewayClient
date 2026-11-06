using GeminiGatewayClient.Serialization;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

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
    Unspecified = 0,

    /// <summary>
    /// Harassment content.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_HARASSMENT")]
    Harassment = 7,

    /// <summary>
    /// Hate speech and content that promotes discrimination or disparagement.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_HATE_SPEECH")]
    HateSpeech = 8,

    /// <summary>
    /// Sexually explicit content.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_SEXUALLY_EXPLICIT")]
    SexuallyExplicit = 9,

    /// <summary>
    /// Dangerous content that promotes harm, illegal acts, or self-harm.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_DANGEROUS_CONTENT")]
    DangerousContent = 10,

    /// <summary>
    /// Content that may harm civic integrity.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_CIVIC_INTEGRITY")]
    CivicIntegrity = 11,

    /// <summary>
    /// Jailbreak or prompt-injection attempt.
    /// </summary>
    [JsonStringEnumMemberName("HARM_CATEGORY_JAILBREAK")]
    Jailbreak = 12,

    /// <summary>
    /// Fallback value used when the server returns a harm category not yet defined in this version of the library.
    /// </summary>
    Unknown = -1
}
