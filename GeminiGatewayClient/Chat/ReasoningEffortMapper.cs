using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Maps <see cref="ReasoningEffort"/> onto the thinking control understood by the target model family:
/// <c>thinkingBudget</c> for Gemini 2.5 and <c>thinkingLevel</c> for everything else, including aliases such as
/// <c>gemini-flash-latest</c>. The value closest to the requested effort is sent as is; a model that does not accept it,
/// such as a Pro model asked not to think, fails with the server's HTTP 400 instead of quietly thinking more.
/// </summary>
internal static class ReasoningEffortMapper
{
    public static void Apply(ThinkingConfig thinking, ReasoningEffort effort, string? modelId)
    {
        var model = NormalizeModelId(modelId);

        if (model.Length == 0 ||
            model.StartsWith("gemini-1", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("gemini-2.0", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (model.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase))
        {
            thinking.ThinkingBudget = effort switch
            {
                ReasoningEffort.None => 0,
                ReasoningEffort.Low => 1024,
                ReasoningEffort.Medium => 8192,
                ReasoningEffort.High or ReasoningEffort.ExtraHigh => 24576,
                _ => null
            };
            return;
        }

        thinking.ThinkingLevel = effort switch
        {
            ReasoningEffort.None => ThinkingLevel.Minimal,
            ReasoningEffort.Low => ThinkingLevel.Low,
            ReasoningEffort.Medium => ThinkingLevel.Medium,
            ReasoningEffort.High or ReasoningEffort.ExtraHigh => ThinkingLevel.High,
            _ => null
        };
    }

    private static string NormalizeModelId(string? modelId)
    {
        var model = modelId?.Trim() ?? string.Empty;
        return model[(model.LastIndexOf('/') + 1)..];
    }
}
