using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Configurations;

/// <summary>
/// A single conversation turn containing a <see cref="Role"/> and a list of <see cref="Part"/> items.
/// See <see href="https://ai.google.dev/api/caching#Content"/>.
/// </summary>
public sealed class Content
{
    /// <summary>
    /// Producer of the content (<see cref="Role.User"/> or <see cref="Role.Model"/>).
    /// </summary>
    [JsonPropertyName("role")]
    public Role Role { get; set; } = Role.User;

    /// <summary>
    /// Ordered parts that make up this conversation turn.
    /// </summary>
    [JsonPropertyName("parts")]
    public List<Part> Parts { get; set; } = [];
}
