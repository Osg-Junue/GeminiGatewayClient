using GeminiAPIWrapper.Models;
using Microsoft.Extensions.AI;

namespace GeminiAPIWrapper.Chat;

/// <summary>
/// Maps <see cref="ReasoningEffort"/> onto the thinking control understood by the target model family:
/// <c>thinkingBudget</c> for Gemini 2.5 and <c>thinkingLevel</c> for Gemini 3 and later.
/// </summary>
internal static class ReasoningEffortMapper
{
    public static void Apply(ThinkingConfig thinking, ReasoningEffort effort, string? modelId)
    {
        var model = NormalizeModelId(modelId);
        var isPro = model.Contains("-pro", StringComparison.OrdinalIgnoreCase);

        if (model.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase))
        {
            // Gemini 2.5 only understands thinkingBudget. 2.5 Pro cannot disable thinking (minimum 128 tokens).
            thinking.ThinkingBudget = effort switch
            {
                ReasoningEffort.None => isPro ? 128 : 0,
                ReasoningEffort.Low => 1024,
                ReasoningEffort.Medium => 8192,
                ReasoningEffort.High or ReasoningEffort.ExtraHigh => 24576,
                _ => null
            };
            return;
        }

        // Gemini 3+: thinkingLevel. "minimal" is Flash-only and Gemini 3 Pro supports only LOW and HIGH
        // ("medium" requires 3.1+), so clamp to the nearest supported level for those models.
        var isGemini3Pro = model.StartsWith("gemini-3-pro", StringComparison.OrdinalIgnoreCase);

        thinking.ThinkingLevel = effort switch
        {
            ReasoningEffort.None => isPro ? ThinkingLevel.Low : ThinkingLevel.Minimal,
            ReasoningEffort.Low => ThinkingLevel.Low,
            ReasoningEffort.Medium => isGemini3Pro ? ThinkingLevel.High : ThinkingLevel.Medium,
            ReasoningEffort.High or ReasoningEffort.ExtraHigh => ThinkingLevel.High,
            _ => null
        };
    }

    private static string NormalizeModelId(string? modelId)
    {
        var model = modelId?.Trim() ?? string.Empty;
        var slash = model.LastIndexOf('/');
        return slash >= 0 ? model[(slash + 1)..] : model;
    }
}

