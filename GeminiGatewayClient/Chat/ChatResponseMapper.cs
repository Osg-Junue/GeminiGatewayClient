using System.Buffers;
using System.Text;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Converts <see cref="GenerateContentResponse"/> objects into <c>Microsoft.Extensions.AI</c> responses and streaming updates.
/// </summary>
internal static class ChatResponseMapper
{
    private const string GoogleSearchToolName = "google_search";

    public static ChatResponse ToChatResponse(GenerateContentResponse response, string? modelId)
    {
        var candidate = response.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
        var contents = MapContents(candidate, isStreaming: false, out var hasFunctionCall);

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
            AdditionalProperties = CreateResponseProperties(response.BlockReason, candidate?.GroundingMetadata)
        };
    }

    /// <summary>
    /// Maps a streamed chunk. Usage is intentionally omitted: Gemini repeats cumulative <c>usageMetadata</c> on many chunks,
    /// and consumers sum every <see cref="UsageContent"/>; see <see cref="ToUsageUpdate"/>.
    /// </summary>
    public static ChatResponseUpdate ToChatResponseUpdate(GenerateContentResponse chunk, string? modelId)
    {
        var candidate = chunk.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
        var contents = MapContents(candidate, isStreaming: true, out var hasFunctionCall);

        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            RawRepresentation = chunk,
            ResponseId = chunk.ResponseId,
            MessageId = chunk.ResponseId,
            ModelId = chunk.ModelVersion ?? candidate?.ModelVersion ?? modelId,
            FinishReason = MapFinishReason(candidate?.FinishReason, chunk.BlockReason, hasFunctionCall),
            AdditionalProperties = CreateResponseProperties(chunk.BlockReason, candidate?.GroundingMetadata)
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

    private static List<AIContent> MapContents(Candidate? candidate, bool isStreaming, out bool hasFunctionCall)
    {
        hasFunctionCall = false;
        if (candidate is null)
        {
            return [];
        }

        var parts = candidate.Content?.RawParts;
        var hasAnnotations = HasCandidateAnnotations(candidate);
        if (parts is not { Count: > 0 })
        {
            if (!hasAnnotations)
            {
                return [];
            }

            var annotatedOnly = new List<AIContent>(1);
            AttachCandidateAnnotations(candidate, parts, annotatedOnly);
            return annotatedOnly;
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
                    if (!string.IsNullOrEmpty(part.Text) || !string.IsNullOrEmpty(part.ThoughtSignature))
                    {
                        contents.Add(new TextReasoningContent(part.Text ?? string.Empty)
                        {
                            ProtectedData = part.ThoughtSignature,
                            RawRepresentation = part
                        });
                    }
                    break;

                case { ThoughtSignature: { Length: > 0 }, Text: null or "", InlineData: null, FileData: null, ExecutableCode: null, CodeExecutionResult: null, FunctionResponse: null }:
                    contents.Add(new TextReasoningContent(string.Empty)
                    {
                        ProtectedData = part.ThoughtSignature,
                        RawRepresentation = part
                    });
                    break;

                case { Text: { Length: > 0 } text }:
                    var textContent = new TextContent(text) { RawRepresentation = part };
                    AttachThoughtSignature(textContent, part.ThoughtSignature);
                    contents.Add(textContent);
                    if (isStreaming && !string.IsNullOrEmpty(part.ThoughtSignature))
                    {
                        contents.Add(new TextReasoningContent(string.Empty)
                        {
                            ProtectedData = part.ThoughtSignature,
                            RawRepresentation = part
                        });
                    }
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

        if (hasAnnotations)
        {
            AttachCandidateAnnotations(candidate, parts, contents);
        }

        return contents;
    }

    private static bool HasCandidateAnnotations(Candidate candidate) =>
        candidate.CitationMetadata?.RawCitations is { Count: > 0 } ||
        candidate.GroundingMetadata?.RawGroundingChunks is { Count: > 0 } ||
        candidate.GroundingMetadata?.RawGroundingSupports is { Count: > 0 };

    private static void AttachCandidateAnnotations(Candidate candidate, IList<Part>? parts, List<AIContent> contents)
    {
        var primaryText = FindOrCreateTargetTextContent(contents, parts, partIndex: null);

        if (candidate.CitationMetadata?.RawCitations is { Count: > 0 } citations)
        {
            for (var i = 0; i < citations.Count; i++)
            {
                if (citations[i] is not { } citation)
                {
                    continue;
                }

                var annotation = new CitationAnnotation
                {
                    Title = citation.Title,
                    Url = TryCreateUri(citation.Uri),
                    RawRepresentation = citation
                };

                if (TryCreateRegion(primaryText.Text, citation.StartIndex, citation.EndIndex, out var region))
                {
                    annotation.AnnotatedRegions = [region];
                }

                (primaryText.Annotations ??= []).Add(annotation);
            }
        }

        if (candidate.GroundingMetadata is not { } grounding)
        {
            return;
        }

        var chunks = grounding.RawGroundingChunks;
        var supports = grounding.RawGroundingSupports;
        var chunkCount = chunks?.Count ?? 0;

        CitationAnnotation?[]? chunkAnnotations = chunkCount > 0 ? new CitationAnnotation?[chunkCount] : null;
        TextContent?[]? chunkTargets = chunkCount > 0 ? new TextContent?[chunkCount] : null;

        if (supports is { Count: > 0 })
        {
            for (var i = 0; i < supports.Count; i++)
            {
                if (supports[i] is not { } support)
                {
                    continue;
                }

                var segment = support.Segment;
                var target = FindOrCreateTargetTextContent(contents, parts, segment?.PartIndex, primaryText);
                AnnotatedRegion? regionToAdd = segment is not null &&
                                               TryCreateRegion(target.Text, segment.StartIndex, segment.EndIndex, out var segmentRegion)
                    ? segmentRegion
                    : null;

                var indices = support.RawGroundingChunkIndices;
                var attachedToChunk = false;
                if (indices is { Count: > 0 } && chunks is not null)
                {
                    for (var j = 0; j < indices.Count; j++)
                    {
                        var chunkIndex = indices[j];
                        if ((uint)chunkIndex >= (uint)chunkCount || chunks[chunkIndex] is not { } chunk)
                        {
                            continue;
                        }

                        attachedToChunk = true;
                        var annotation = chunkAnnotations![chunkIndex];
                        if (annotation is null || !ReferenceEquals(chunkTargets![chunkIndex], target))
                        {
                            annotation = CreateChunkAnnotation(chunk);
                            chunkAnnotations[chunkIndex] = annotation;
                            chunkTargets![chunkIndex] = target;
                            (target.Annotations ??= []).Add(annotation);
                        }

                        if (annotation.Snippet is null && !string.IsNullOrEmpty(segment?.Text))
                        {
                            annotation.Snippet = segment.Text;
                        }

                        if (regionToAdd is not null)
                        {
                            (annotation.AnnotatedRegions ??= []).Add(regionToAdd);
                        }
                    }
                }

                if (!attachedToChunk && (regionToAdd is not null || !string.IsNullOrEmpty(segment?.Text)))
                {
                    var supportAnnotation = new CitationAnnotation
                    {
                        Snippet = segment?.Text,
                        RawRepresentation = support
                    };

                    if (regionToAdd is not null)
                    {
                        supportAnnotation.AnnotatedRegions = [regionToAdd];
                    }

                    (target.Annotations ??= []).Add(supportAnnotation);
                }
            }
        }

        if (chunks is { Count: > 0 })
        {
            for (var i = 0; i < chunkCount; i++)
            {
                if (chunkAnnotations![i] is not null || chunks[i] is not { } chunk)
                {
                    continue;
                }

                if (chunk.Web is null && chunk.RetrievedContext is null)
                {
                    continue;
                }

                (primaryText.Annotations ??= []).Add(CreateChunkAnnotation(chunk));
            }
        }
    }

    private static CitationAnnotation CreateChunkAnnotation(GroundingChunk chunk)
    {
        if (chunk.Web is { } web)
        {
            return new CitationAnnotation
            {
                Title = web.Title ?? web.Domain,
                Url = TryCreateUri(web.Uri),
                ToolName = GoogleSearchToolName,
                RawRepresentation = chunk
            };
        }

        if (chunk.RetrievedContext is { } retrieved)
        {
            return new CitationAnnotation
            {
                Title = retrieved.Title,
                Url = TryCreateUri(retrieved.Uri),
                Snippet = retrieved.Text,
                RawRepresentation = chunk
            };
        }

        return new CitationAnnotation { RawRepresentation = chunk };
    }

    private static TextContent FindOrCreateTargetTextContent(
        List<AIContent> contents,
        IList<Part>? parts,
        int? partIndex,
        TextContent? fallback = null)
    {
        if (partIndex is >= 0 and var idx && parts is not null && idx < parts.Count && parts[idx] is { } targetPart)
        {
            for (var i = 0; i < contents.Count; i++)
            {
                if (contents[i] is TextContent tc && ReferenceEquals(tc.RawRepresentation, targetPart))
                {
                    return tc;
                }
            }
        }

        if (fallback is not null)
        {
            return fallback;
        }

        for (var i = 0; i < contents.Count; i++)
        {
            if (contents[i] is TextContent tc)
            {
                return tc;
            }
        }

        var created = new TextContent(string.Empty);
        contents.Add(created);
        return created;
    }

    private static Uri? TryCreateUri(string? uriString) =>
        !string.IsNullOrWhiteSpace(uriString) && Uri.TryCreate(uriString, UriKind.RelativeOrAbsolute, out var uri)
            ? uri
            : null;

    /// <summary>
    /// Converts Gemini UTF-8 byte offsets (<paramref name="startByte"/>..<paramref name="endByte"/>) into a UTF-16
    /// <see cref="TextSpanAnnotatedRegion"/> without allocating intermediate byte arrays.
    /// </summary>
    private static bool TryCreateRegion(
        string? text,
        int? startByte,
        int? endByte,
        out TextSpanAnnotatedRegion region)
    {
        if (startByte is null && endByte is null)
        {
            region = null!;
            return false;
        }

        var start = startByte ?? 0;
        if (start < 0 || (endByte is { } eb && eb < start))
        {
            region = null!;
            return false;
        }

        if (string.IsNullOrEmpty(text))
        {
            if (start == 0 && (endByte is null or 0))
            {
                region = new TextSpanAnnotatedRegion { StartIndex = 0, EndIndex = 0 };
                return true;
            }

            region = null!;
            return false;
        }

        if (Ascii.IsValid(text))
        {
            if (start > text.Length)
            {
                region = null!;
                return false;
            }

            var end = endByte is { } asciiEnd ? Math.Min(asciiEnd, text.Length) : text.Length;
            region = new TextSpanAnnotatedRegion { StartIndex = start, EndIndex = end };
            return true;
        }

        var span = text.AsSpan();
        var bytePos = 0;
        var charPos = 0;
        var charStart = -1;
        var charEnd = -1;
        var targetEnd = endByte ?? int.MaxValue;

        while (charPos < span.Length)
        {
            if (charStart < 0 && bytePos >= start)
            {
                charStart = charPos;
            }

            if (bytePos >= targetEnd)
            {
                charEnd = charPos;
                break;
            }

            if (Rune.DecodeFromUtf16(span[charPos..], out var rune, out var charsConsumed) != OperationStatus.Done)
            {
                // Unpaired surrogate: UTF-8 replacement character is 3 bytes and 1 UTF-16 code unit.
                bytePos += 3;
                charPos += 1;
            }
            else
            {
                bytePos += rune.Utf8SequenceLength;
                charPos += charsConsumed;
            }
        }

        if (charStart < 0)
        {
            if (bytePos >= start)
            {
                charStart = span.Length;
            }
            else
            {
                region = null!;
                return false;
            }
        }

        if (charEnd < 0)
        {
            charEnd = span.Length;
        }

        region = new TextSpanAnnotatedRegion { StartIndex = charStart, EndIndex = charEnd };
        return true;
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

    private static AdditionalPropertiesDictionary? CreateResponseProperties(BlockReason? blockReason, GroundingMetadata? groundingMetadata)
    {
        var queries = groundingMetadata?.RawWebSearchQueries;
        if (blockReason is null && queries is not { Count: > 0 })
        {
            return null;
        }

        var properties = new AdditionalPropertiesDictionary();
        if (blockReason is { } reason)
        {
            properties[GeminiChatPropertyNames.BlockReason] = reason.ToString();
        }

        if (queries is { Count: > 0 })
        {
            properties[GeminiChatPropertyNames.WebSearchQueries] = queries;
        }

        return properties;
    }
}
