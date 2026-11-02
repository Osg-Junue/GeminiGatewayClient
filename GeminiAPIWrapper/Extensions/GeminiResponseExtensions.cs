using GeminiAPIWrapper.Configurations;

namespace GeminiAPIWrapper.Extensions;

/// <summary>
/// Convenience extension methods for extracting common values from a <see cref="GeminiResponse"/>.
/// </summary>
public static class GeminiResponseExtensions
{
    /// <summary>
    /// Concatenates all non-thought text parts in the first candidate of <paramref name="response"/>.
    /// Returns <c>null</c> when there are no candidates, no parts, or no non-thought text parts.
    /// </summary>
    /// <param name="response">The <see cref="GeminiResponse"/> to read from.</param>
    /// <returns>The concatenated text of the first candidate, or <c>null</c>.</returns>
    public static string? GetText(this GeminiResponse? response)
    {
        var parts = response?.Candidates?.FirstOrDefault()?.Content?.Parts;
        if (parts is null || parts.Count == 0)
        {
            return null;
        }

        var text = string.Concat(parts.Where(p => p.Thought != true && !string.IsNullOrEmpty(p.Text)).Select(p => p.Text));
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Returns the first <see cref="FunctionCall"/> found in the first candidate of <paramref name="response"/>,
    /// or <c>null</c> when none is present.
    /// </summary>
    /// <param name="response">The <see cref="GeminiResponse"/> to read from.</param>
    /// <returns>The first <see cref="FunctionCall"/> in the first candidate, or <c>null</c>.</returns>
    public static FunctionCall? GetFunctionCall(this GeminiResponse? response)
    {
        return response?.Candidates?.FirstOrDefault()?.Content?.Parts?
            .FirstOrDefault(p => p.FunctionCall is not null)?.FunctionCall;
    }

    /// <summary>
    /// Returns the <see cref="BlockReason"/> from <see cref="GeminiResponse.PromptFeedback"/> when the input prompt
    /// was blocked before candidate generation, or <c>null</c> if the prompt was not blocked.
    /// </summary>
    /// <param name="response">The <see cref="GeminiResponse"/> to inspect.</param>
    /// <returns>The prompt <see cref="BlockReason"/>, or <c>null</c>.</returns>
    public static BlockReason? GetBlockReason(this GeminiResponse? response)
        => response?.PromptFeedback?.BlockReason;

    /// <summary>
    /// Returns the <see cref="FinishReason"/> of the first candidate in <paramref name="response"/>,
    /// or <c>null</c> when there are no candidates.
    /// </summary>
    /// <param name="response">The <see cref="GeminiResponse"/> to inspect.</param>
    /// <returns>The first candidate's <see cref="FinishReason"/>, or <c>null</c>.</returns>
    public static FinishReason? GetFinishReason(this GeminiResponse? response)
        => response?.Candidates?.FirstOrDefault()?.FinishReason;
}
