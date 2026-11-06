using System.Text;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Converts the chunks of one streamed response into <see cref="ChatResponseUpdate"/> instances. Later chunks depend on
/// earlier ones: citation offsets count from the start of the response, and a function call decides the final finish reason.
/// </summary>
/// <remarks>
/// A delivered update is never changed afterwards, so citations travel on an annotation-only <see cref="AIContent"/> in
/// the update of the chunk that carried them, with regions relative to the full response text.
/// </remarks>
internal sealed class ChatStreamMapper(string? modelId)
{
    private readonly StringBuilder _text = new();
    private GenerateContentResponse? _lastUsageChunk;
    private bool _sawFunctionCall;

    /// <summary>
    /// Maps a chunk. Usage is left out because Gemini repeats cumulative <c>usageMetadata</c> on many chunks, whereas consumers
    /// sum every <see cref="UsageContent"/>; see <see cref="ToUsageUpdate"/>.
    /// </summary>
    public ChatResponseUpdate ToUpdate(GenerateContentResponse chunk)
    {
        if (chunk.UsageMetadata is not null)
        {
            _lastUsageChunk = chunk;
        }

        var candidate = ChatResponseMapper.FirstCandidate(chunk);
        var contents = ChatResponseMapper.ToContents(candidate, isStreaming: true);
        _sawFunctionCall |= contents.OfType<FunctionCallContent>().Any();
        foreach (var text in contents.OfType<TextContent>())
        {
            _text.Append(text.Text);
        }

        if (candidate is not null)
        {
            CitationAnnotator.AddAnnotationContent(candidate, contents, _text.ToString());
        }

        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            RawRepresentation = chunk,
            ResponseId = chunk.ResponseId,
            MessageId = chunk.ResponseId,
            ModelId = chunk.ModelVersion ?? candidate?.ModelVersion ?? modelId,
            FinishReason = candidate?.FinishReason is null && chunk.BlockReason is null
                ? null
                : ChatResponseMapper.ToFinishReason(candidate?.FinishReason, chunk.BlockReason, _sawFunctionCall),
            AdditionalProperties = ChatResponseMapper.ToResponseProperties(chunk.BlockReason, candidate?.GroundingMetadata)
        };
    }

    /// <summary>
    /// Creates the single trailing update that reports token usage, or <c>null</c> when the server never reported it.
    /// </summary>
    public ChatResponseUpdate? ToUsageUpdate()
    {
        if (ChatResponseMapper.ToUsage(_lastUsageChunk?.UsageMetadata) is not { } usage)
        {
            return null;
        }

        return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)])
        {
            ResponseId = _lastUsageChunk!.ResponseId,
            MessageId = _lastUsageChunk.ResponseId,
            ModelId = _lastUsageChunk.ModelVersion ?? modelId
        };
    }
}
