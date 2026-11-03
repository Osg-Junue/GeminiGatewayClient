using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Converts <see cref="GenerateContentResponse"/> objects into <c>Microsoft.Extensions.AI</c> responses and streaming updates.
/// </summary>
internal static class ChatResponseMapper
{
    public static ChatResponse ToChatResponse(GenerateContentResponse response, string? modelId)
    {
        var candidate = response.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
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
        var candidate = chunk.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
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
        if (candidate?.Content?.RawParts is not { Count: > 0 } parts)
        {
            return [];
        }

        var contents = new List<AIContent>(parts.Count);
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
                case { ThoughtSignature: { Length: > 0 }, Text: null or "", InlineData: null, FileData: null, ExecutableCode: null, CodeExecutionResult: null, FunctionResponse: null }:
                    contents.Add(new TextReasoningContent(part.Text ?? string.Empty)
                    {
                        ProtectedData = part.ThoughtSignature,
                        RawRepresentation = part
                    });
                    break;

                case { Text: { } text }:
                    var textContent = new TextContent(text) { RawRepresentation = part };
                    AttachThoughtSignature(textContent, part.ThoughtSignature);
                    contents.Add(textContent);
                    break;

                case { InlineData: { Data.Length: > 0 } blob }:
                    var dataContent = new DataContent(blob.Data, string.IsNullOrWhiteSpace(blob.MimeType) ? MimeTypes.ApplicationOctetStream : blob.MimeType)
                    {
                        RawRepresentation = part
                    };
                    AttachThoughtSignature(dataContent, part.ThoughtSignature);
                    contents.Add(dataContent);
                    break;

                case { FileData: { FileUri.Length: > 0 } file } when Uri.TryCreate(file.FileUri, UriKind.RelativeOrAbsolute, out var fileUri):
                    var uriContent = new UriContent(fileUri, string.IsNullOrWhiteSpace(file.MimeType) ? MimeTypes.ApplicationOctetStream : file.MimeType)
                    {
                        RawRepresentation = part
                    };
                    AttachThoughtSignature(uriContent, part.ThoughtSignature);
                    contents.Add(uriContent);
                    break;

                case { ExecutableCode: not null } or { CodeExecutionResult: not null }:
                    var codeContent = new AIContent { RawRepresentation = part };
                    AttachCodeExecutionProperties(codeContent, part);
                    AttachThoughtSignature(codeContent, part.ThoughtSignature);
                    contents.Add(codeContent);
                    break;
            }
        }

        return contents;
    }

    private static void AttachCodeExecutionProperties(AIContent content, Part part)
    {
        var properties = content.AdditionalProperties ??= new AdditionalPropertiesDictionary();

        if (part.ExecutableCode is { } exec)
        {
            if (exec.Language is { } language)
            {
                properties[GeminiChatPropertyNames.ExecutableCodeLanguage] = language.ToString();
            }

            if (exec.Code is not null)
            {
                properties[GeminiChatPropertyNames.ExecutableCode] = exec.Code;
            }
        }

        if (part.CodeExecutionResult is { } result)
        {
            if (result.Outcome is { } outcome)
            {
                properties[GeminiChatPropertyNames.CodeExecutionOutcome] = outcome.ToString();
            }

            if (result.Output is not null)
            {
                properties[GeminiChatPropertyNames.CodeExecutionOutput] = result.Output;
            }
        }
    }

    private static void AttachThoughtSignature(AIContent content, string? thoughtSignature)
    {
        if (!string.IsNullOrEmpty(thoughtSignature))
        {
            (content.AdditionalProperties ??= new AdditionalPropertiesDictionary())[GeminiChatPropertyNames.ThoughtSignature] = thoughtSignature;
        }
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
                or FinishReason.ProhibitedContent or FinishReason.Spii or FinishReason.ImageSafety
                or FinishReason.ImageProhibitedContent or FinishReason.ImageRecitation => ChatFinishReason.ContentFilter,
            FinishReason.MalformedFunctionCall or FinishReason.UnexpectedToolCall or FinishReason.TooManyToolCalls => ChatFinishReason.ToolCalls,
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

