using GeminiAPIWrapper.Models;
using Microsoft.Extensions.AI;

namespace GeminiAPIWrapper.Chat;

/// <summary>
/// Converts <see cref="GenerateContentResponse"/> objects into <c>Microsoft.Extensions.AI</c> responses and streaming updates.
/// </summary>
internal static class ChatResponseMapper
{
    public static ChatResponse ToChatResponse(GenerateContentResponse response, string? modelId)
    {
        var candidate = response.Candidates.FirstOrDefault();
        var contents = MapContents(candidate, out var hasFunctionCall);

        var message = new ChatMessage(ChatRole.Assistant, contents)
        {
            RawRepresentation = candidate,
            MessageId = response.ResponseId
        };

        return new ChatResponse(message)
        {
            RawRepresentation = response,
            ResponseId = response.ResponseId,
            ModelId = response.ModelVersion ?? candidate?.ModelVersion ?? modelId,
            FinishReason = MapFinishReason(candidate?.FinishReason, response.BlockReason, hasFunctionCall),
            Usage = MapUsage(response.UsageMetadata),
            AdditionalProperties = CreateBlockReasonProperties(response.BlockReason)
        };
    }

    /// <summary>
    /// Maps a streamed chunk. Usage is intentionally omitted: Gemini repeats cumulative <c>usageMetadata</c> on many chunks,
    /// and consumers sum every <see cref="UsageContent"/>; see <see cref="ToUsageUpdate"/>.
    /// </summary>
    public static ChatResponseUpdate ToChatResponseUpdate(GenerateContentResponse chunk, string? modelId)
    {
        var candidate = chunk.Candidates.FirstOrDefault();
        var contents = MapContents(candidate, out var hasFunctionCall);

        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            RawRepresentation = chunk,
            ResponseId = chunk.ResponseId,
            MessageId = chunk.ResponseId,
            ModelId = chunk.ModelVersion ?? candidate?.ModelVersion ?? modelId,
            FinishReason = MapFinishReason(candidate?.FinishReason, chunk.BlockReason, hasFunctionCall),
            AdditionalProperties = CreateBlockReasonProperties(chunk.BlockReason)
        };
    }

    /// <summary>
    /// Creates the single trailing update that reports token usage for a streamed response,
    /// or <c>null</c> when the server never reported usage.
    /// </summary>
    public static ChatResponseUpdate? ToUsageUpdate(GenerateContentResponse? lastUsageChunk, string? modelId)
    {
        if (MapUsage(lastUsageChunk?.UsageMetadata) is not { } usage)
        {
            return null;
        }

        return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)])
        {
            ResponseId = lastUsageChunk!.ResponseId,
            MessageId = lastUsageChunk.ResponseId,
            ModelId = lastUsageChunk.ModelVersion ?? modelId
        };
    }

    private static List<AIContent> MapContents(Candidate? candidate, out bool hasFunctionCall)
    {
        hasFunctionCall = false;
        var contents = new List<AIContent>();
        if (candidate?.Content?.Parts is not { } parts)
        {
            return contents;
        }

        var callOrdinal = 0;
        foreach (var part in parts)
        {
            switch (part)
            {
                case { FunctionCall: { } call }:
                    hasFunctionCall = true;
                    contents.Add(MapFunctionCall(part, call, ref callOrdinal));
                    break;

                case { Thought: true }:
                case { ThoughtSignature: { Length: > 0 }, Text: null }:
                    contents.Add(new TextReasoningContent(part.Text ?? string.Empty)
                    {
                        ProtectedData = part.ThoughtSignature,
                        RawRepresentation = part
                    });
                    break;

                case { Text: { } text }:
                    contents.Add(new TextContent(text) { RawRepresentation = part });
                    if (!string.IsNullOrEmpty(part.ThoughtSignature))
                    {
                        // Exposes the signature to consumers that persist history without RawRepresentation.
                        // ChatRequestMapper skips this companion when the preceding part already carries the same signature.
                        contents.Add(new TextReasoningContent(string.Empty) { ProtectedData = part.ThoughtSignature });
                    }

                    break;

                case { InlineData: { Data.Length: > 0 } blob }:
                    contents.Add(new DataContent(blob.Data, string.IsNullOrWhiteSpace(blob.MimeType) ? MimeTypes.ApplicationOctetStream : blob.MimeType)
                    {
                        RawRepresentation = part
                    });
                    break;

                case { FileData: { FileUri.Length: > 0 } file } when Uri.TryCreate(file.FileUri, UriKind.RelativeOrAbsolute, out var fileUri):
                    contents.Add(new UriContent(fileUri, string.IsNullOrWhiteSpace(file.MimeType) ? MimeTypes.ApplicationOctetStream : file.MimeType)
                    {
                        RawRepresentation = part
                    });
                    break;
            }
        }

        return contents;
    }

    private static FunctionCallContent MapFunctionCall(Part part, FunctionCall call, ref int callOrdinal)
    {
        var synthesized = string.IsNullOrEmpty(call.Id);
        var callId = synthesized ? $"{call.Name}_call_{callOrdinal++}_{Guid.NewGuid():N}" : call.Id!;

        var content = new FunctionCallContent(callId, call.Name, ToolJsonConverter.ToDictionary(call.Args))
        {
            RawRepresentation = part
        };

        // Round-trip metadata lives in AdditionalProperties so it survives (de)serialization of chat history,
        // which drops RawRepresentation.
        if (synthesized)
        {
            (content.AdditionalProperties ??= new AdditionalPropertiesDictionary())[GeminiChatPropertyNames.SynthesizedCallId] = true;
        }

        if (!string.IsNullOrEmpty(part.ThoughtSignature))
        {
            (content.AdditionalProperties ??= new AdditionalPropertiesDictionary())[GeminiChatPropertyNames.ThoughtSignature] = part.ThoughtSignature;
        }

        return content;
    }

    private static ChatFinishReason? MapFinishReason(FinishReason? reason, BlockReason? blockReason, bool hasFunctionCall)
    {
        if (hasFunctionCall)
        {
            return ChatFinishReason.ToolCalls;
        }

        if (blockReason is not null and not BlockReason.Unspecified)
        {
            return ChatFinishReason.ContentFilter;
        }

        return reason switch
        {
            FinishReason.Stop => ChatFinishReason.Stop,
            FinishReason.MaxTokens => ChatFinishReason.Length,
            FinishReason.Safety or FinishReason.Recitation or FinishReason.BlockList
                or FinishReason.ProhibitedContent or FinishReason.Spii or FinishReason.ImageSafety => ChatFinishReason.ContentFilter,
            FinishReason.MalformedFunctionCall or FinishReason.UnexpectedToolCall => ChatFinishReason.ToolCalls,
            _ => null
        };
    }

    private static UsageDetails? MapUsage(UsageMetadata? usage) => usage is null
        ? null
        : new UsageDetails
        {
            InputTokenCount = usage.PromptTokenCount + (usage.ToolUsePromptTokenCount ?? 0),
            OutputTokenCount = usage.CandidatesTokenCount + (usage.ThoughtsTokenCount ?? 0),
            TotalTokenCount = usage.TotalTokenCount,
            ReasoningTokenCount = usage.ThoughtsTokenCount,
            CachedInputTokenCount = usage.CachedContentTokenCount
        };

    private static AdditionalPropertiesDictionary? CreateBlockReasonProperties(BlockReason? blockReason) =>
        blockReason is { } reason
            ? new AdditionalPropertiesDictionary { [GeminiChatPropertyNames.BlockReason] = reason.ToString() }
            : null;
}

