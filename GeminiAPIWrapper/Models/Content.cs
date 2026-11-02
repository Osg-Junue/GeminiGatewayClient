using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiAPIWrapper.Models;

/// <summary>
/// A single conversation turn containing a <see cref="Role"/> and a list of <see cref="Part"/> items.
/// See <see href="https://ai.google.dev/api/caching#Content"/>.
/// </summary>
public sealed class Content
{
    /// <summary>
    /// Producer of the content (<see cref="Models.Role.User"/> or <see cref="Models.Role.Model"/>).
    /// Leave <c>null</c> for <see cref="GenerateContentRequest.SystemInstruction"/>; for single-turn requests the API treats a missing role as <see cref="Models.Role.User"/>.
    /// </summary>
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Role? Role { get; set; }

    /// <summary>
    /// Ordered parts that make up this conversation turn.
    /// </summary>
    [JsonPropertyName("parts")]
    public IList<Part> Parts { get; set; } = [];

    /// <summary>
    /// Gets or sets additional JSON properties not explicitly modeled by this type.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Creates a conversation turn containing a single text part.
    /// </summary>
    /// <param name="text">The text of the turn.</param>
    /// <param name="role">The producer of the turn. Defaults to <see cref="Models.Role.User"/>.</param>
    /// <returns>A new <see cref="Content"/>.</returns>
    public static Content FromText(string text, Role role = Models.Role.User) => new()
    {
        Role = role,
        Parts = [Part.FromText(text)]
    };

    /// <summary>
    /// Creates a conversation turn from the specified <paramref name="parts"/>.
    /// </summary>
    /// <param name="role">The producer of the turn.</param>
    /// <param name="parts">The parts of the turn.</param>
    /// <returns>A new <see cref="Content"/>.</returns>
    public static Content FromParts(Role role, params Part[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return new Content { Role = role, Parts = [.. parts] };
    }
}
