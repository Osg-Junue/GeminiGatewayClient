using System.Text.Json.Serialization;
using GeminiAPIWrapper.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// Reason why an input prompt was blocked before candidate generation.
/// See <see href="https://ai.google.dev/api/generate-content#BlockReason"/>.
/// </summary>
[JsonConverter(typeof(LenientStringEnumConverter<BlockReason>))]
public enum BlockReason
{
    /// <summary>
    /// Default value when the block reason is unspecified (<c>BLOCK_REASON_UNSPECIFIED</c>).
    /// </summary>
    [JsonStringEnumMemberName("BLOCK_REASON_UNSPECIFIED")]
    Unspecified,

    /// <summary>
    /// Prompt was blocked for safety reasons. Inspect <see cref="PromptFeedback.SafetyRatings"/> for details.
    /// </summary>
    [JsonStringEnumMemberName("SAFETY")]
    Safety,

    /// <summary>
    /// Prompt was blocked for unknown or other platform reasons.
    /// </summary>
    [JsonStringEnumMemberName("OTHER")]
    Other,

    /// <summary>
    /// Prompt was blocked because it contained terms from a terminology blocklist.
    /// </summary>
    [JsonStringEnumMemberName("BLOCKLIST")]
    BlockList,

    /// <summary>
    /// Prompt was blocked for potentially containing prohibited content.
    /// </summary>
    [JsonStringEnumMemberName("PROHIBITED_CONTENT")]
    ProhibitedContent,

    /// <summary>
    /// Prompt was blocked because candidates would violate image generation safety policies.
    /// </summary>
    [JsonStringEnumMemberName("IMAGE_SAFETY")]
    ImageSafety,

    /// <summary>
    /// Fallback value used when the server returns a block reason not yet defined in this version of the library.
    /// </summary>
    Unknown
}
