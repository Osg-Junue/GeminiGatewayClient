namespace GeminiGatewayClient.Chat;

/// <summary>
/// The id given to a function call the model sent without one. It carries the function name, so a result can still name
/// its function after the call was trimmed from the history.
/// </summary>
internal static class SynthesizedCallId
{
    private const string Separator = "_call_";

    public static string Create(string functionName) => $"{functionName}{Separator}{Guid.NewGuid():N}";

    /// <summary>
    /// Returns the function name in an id made by <see cref="Create"/>, or <c>null</c> for any other id.
    /// </summary>
    public static string? GetFunctionName(string callId)
    {
        var separator = callId.LastIndexOf(Separator, StringComparison.Ordinal);
        return separator > 0 && Guid.TryParseExact(callId.AsSpan(separator + Separator.Length), "N", out _)
            ? callId[..separator]
            : null;
    }
}
