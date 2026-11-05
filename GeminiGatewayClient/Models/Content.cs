using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiGatewayClient.Models;

/// <summary>
/// A single conversation turn containing a <see cref="Role"/> and a list of <see cref="Part"/> items.
/// See <see href="https://ai.google.dev/api/caching#Content"/>.
/// </summary>
public sealed class Content
{
    private IList<Part>? _parts;

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
    public IList<Part> Parts
    {
        get => _parts ??= [];
        set => _parts = value;
    }

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
    public static Content FromParts(Role role, params ReadOnlySpan<Part> parts)
    {
        var list = new List<Part>(parts.Length);
        foreach (var part in parts)
        {
            ArgumentNullException.ThrowIfNull(part, nameof(parts));
            list.Add(part);
        }

        return new Content { Role = role, Parts = list };
    }

    /// <summary>
    /// Returns why this content is invalid as a message suffix for the caller's path, or <c>null</c> when it is valid.
    /// </summary>
    internal string? GetValidationError()
    {
        if (Role is { } role && (role == Models.Role.Unknown || !Enum.IsDefined(role)))
        {
            return ".Role cannot be Role.Unknown.";
        }

        if (_parts is not { Count: > 0 } parts)
        {
            return ExtensionData is { Count: > 0 } ? null : ".Parts must contain at least one Part item.";
        }

        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not { } part)
            {
                return $".Parts[{i}] cannot be null.";
            }

            if (part.GetValidationError() is { } error)
            {
                return $".Parts[{i}]{error}";
            }
        }

        return null;
    }

    internal Content Clone()
    {
        // Null elements are kept so request validation reports them instead of the clone silently dropping them.
        List<Part>? clonedParts = null;
        if (_parts is { Count: > 0 } sourceParts)
        {
            clonedParts = new List<Part>(sourceParts.Count);
            for (var i = 0; i < sourceParts.Count; i++)
            {
                clonedParts.Add(sourceParts[i]?.Clone()!);
            }
        }

        return new Content
        {
            Role = Role,
            _parts = clonedParts,
            ExtensionData = ExtensionData is not null ? new Dictionary<string, JsonElement>(ExtensionData, StringComparer.Ordinal) : null
        };
    }
}
