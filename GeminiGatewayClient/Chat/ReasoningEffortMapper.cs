using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Maps <see cref="ReasoningEffort"/> onto the thinking control understood by the target model family:
/// <c>thinkingBudget</c> for Gemini 2.5 and <c>thinkingLevel</c> for Gemini 3 and later.
/// </summary>
internal static class ReasoningEffortMapper
{
    public static void Apply(ThinkingConfig thinking, ReasoningEffort effort, string? modelId)
    {
        var model = NormalizeModelId(modelId);
        var isPro = IsProModel(model);

        if (model.Length == 0 ||
            model.StartsWith("gemini-1", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("gemini-2.0", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (model.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("gemini-flash", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("gemini-pro", StringComparison.OrdinalIgnoreCase))
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

        // Gemini 3 and later, and gateway or tuned model aliases: thinkingLevel. "minimal" is Flash-only, and
        // Gemini 3 Pro supports only LOW and HIGH ("medium" requires 3.1+).
        var isGemini3Pro = model.StartsWith("gemini-3-pro", StringComparison.OrdinalIgnoreCase) &&
                           (model.Length == "gemini-3-pro".Length || model["gemini-3-pro".Length] is '-' or '.');

        thinking.ThinkingLevel = effort switch
        {
            ReasoningEffort.None => isPro ? ThinkingLevel.Low : ThinkingLevel.Minimal,
            ReasoningEffort.Low => ThinkingLevel.Low,
            ReasoningEffort.Medium => isGemini3Pro ? ThinkingLevel.High : ThinkingLevel.Medium,
            ReasoningEffort.High or ReasoningEffort.ExtraHigh => ThinkingLevel.High,
            _ => null
        };
    }

    private static bool IsProModel(string model) =>
        model.EndsWith("-pro", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("-pro-", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("-pro.", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeModelId(string? modelId)
    {
        var model = modelId?.Trim() ?? string.Empty;
        return model[(model.LastIndexOf('/') + 1)..];
    }
}
