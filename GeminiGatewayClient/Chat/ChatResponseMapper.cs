using System.Net.Http.Headers;
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

    internal readonly record struct StreamedTextSegment(TextContent Content, int StartByte, int EndByte);

    /// <summary>
    /// What a streamed response has produced so far. A later chunk's citations and finish reason depend on earlier chunks.
    /// </summary>
    internal sealed class StreamState
    {
        public List<StreamedTextSegment> Segments { get; } = [];

        public int Utf8Bytes { get; set; }

        public bool SawFunctionCall { get; set; }
    }

    public static ChatResponse ToChatResponse(GenerateContentResponse response, string? modelId)
    {
        var candidate = response.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
        var contents = MapContents(candidate, stream: null, out var hasFunctionCall);

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
    public static ChatResponseUpdate ToChatResponseUpdate(GenerateContentResponse chunk, string? modelId, StreamState? stream = null)
    {
        stream ??= new StreamState();
        var candidate = chunk.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
        var contents = MapContents(candidate, stream, out var hasFunctionCall);
        stream.SawFunctionCall |= hasFunctionCall;

        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            RawRepresentation = chunk,
            ResponseId = chunk.ResponseId,
            MessageId = chunk.ResponseId,
            ModelId = chunk.ModelVersion ?? candidate?.ModelVersion ?? modelId,
            FinishReason = MapFinishReason(candidate?.FinishReason, chunk.BlockReason, stream.SawFunctionCall),
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

    private static List<AIContent> MapContents(Candidate? candidate, StreamState? stream, out bool hasFunctionCall)
    {
        hasFunctionCall = false;
        if (candidate is null)
        {
            return [];
        }

        var parts = candidate.Content?.Parts;
        var hasAnnotations = HasCandidateAnnotations(candidate);
        if (parts is not { Count: > 0 })
        {
            if (!hasAnnotations)
            {
                return [];
            }

            var annotatedOnly = new List<AIContent>(1);
            AttachCandidateAnnotations(candidate, parts, annotatedOnly, stream);
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

                case { ThoughtSignature: { Length: > 0 }, Text: null or "", InlineData: null, FileData: null, ExecutableCode: null, CodeExecutionResult: null, FunctionResponse: null }
                    when !IsUnknownPartKind(part):
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
                    if (stream is not null)
                    {
                        var startByte = stream.Utf8Bytes;
                        stream.Utf8Bytes += Encoding.UTF8.GetByteCount(text);
                        stream.Segments.Add(new StreamedTextSegment(textContent, startByte, stream.Utf8Bytes));

                        // Merging streamed text drops AdditionalProperties, so the signature also travels on a companion content.
                        if (!string.IsNullOrEmpty(part.ThoughtSignature))
                        {
                            contents.Add(new TextReasoningContent(string.Empty)
                            {
                                ProtectedData = part.ThoughtSignature,
                                RawRepresentation = part,
                                AdditionalProperties = new AdditionalPropertiesDictionary { [GeminiChatPropertyNames.SignatureCompanion] = true }
                            });
                        }
                    }
                    break;

                case { InlineData: { Data.Length: > 0 } blob }:
                    var dataContent = new DataContent(blob.Data, ToValidMediaType(blob.MimeType))
                    {
                        RawRepresentation = part
                    };
                    AttachThoughtSignature(dataContent, part.ThoughtSignature);
                    contents.Add(dataContent);
                    break;

                case { FileData: { FileUri.Length: > 0 } file } when Uri.TryCreate(file.FileUri, UriKind.RelativeOrAbsolute, out var fileUri):
                    var uriContent = new UriContent(fileUri, ToValidMediaType(file.MimeType))
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

                // Part kinds without a chat mapping (for example toolCall) still have to reach the next turn,
                // unless the part could not be resent, which would break every later turn of the conversation.
                case { ExtensionData.Count: > 0 } when part.GetValidationError() is null:
                    contents.Add(new AIContent { RawRepresentation = part });
                    break;
            }
        }

        if (hasAnnotations)
        {
            AttachCandidateAnnotations(candidate, parts, contents, stream);
        }

        return contents;
    }

    /// <summary>
    /// The content types reject a value they cannot parse, and one bad part must not fail the rest of a response.
    /// </summary>
    private static string ToValidMediaType(string? mediaType) =>
        MediaTypeHeaderValue.TryParse(mediaType, out _) ? mediaType! : MimeTypes.ApplicationOctetStream;

    /// <summary>
    /// An empty <c>text</c> key marks a trailing signature carrier; without one, unknown fields mean a part kind this client does not model.
    /// </summary>
    private static bool IsUnknownPartKind(Part part) => part is { Text: null, ExtensionData.Count: > 0 };

    private static bool HasCandidateAnnotations(Candidate candidate) =>
        candidate.CitationMetadata?.Citations is { Count: > 0 } ||
        candidate.GroundingMetadata?.GroundingChunks is { Count: > 0 } ||
        candidate.GroundingMetadata?.GroundingSupports is { Count: > 0 };

    private static void AttachCandidateAnnotations(
        Candidate candidate,
        IList<Part>? parts,
        List<AIContent> contents,
        StreamState? stream)
    {
        var streamedSegments = stream?.Segments;
        var needsRealtimeCarrier = stream is not null && !ContainsTextContent(contents);
        var primaryText = FindPrimaryTextContent(contents, parts, streamedSegments, allowCreateEmpty: stream is null);
        List<AIAnnotation>? carrierAnnotations = needsRealtimeCarrier ? [] : null;

        if (candidate.CitationMetadata?.Citations is { Count: > 0 } citations)
        {
            for (var i = 0; i < citations.Count; i++)
            {
                if (citations[i] is not { } citation)
                {
                    continue;
                }

                var targets = ResolveTargetsAndRegions(
                    contents,
                    parts,
                    streamedSegments,
                    primaryText,
                    partIndex: null,
                    citation.StartIndex,
                    citation.EndIndex);

                for (var t = 0; t < targets.Count; t++)
                {
                    var (citationTarget, region) = targets[t];
                    var annotation = CreateCitationAnnotation(citation, region);
                    (citationTarget.Annotations ??= []).Add(annotation);
                }

                if (carrierAnnotations is not null)
                {
                    var carrierCitation = CreateCitationAnnotation(citation, region: null);
                    for (var t = 0; t < targets.Count; t++)
                    {
                        if (targets[t].Region is { } r)
                        {
                            (carrierCitation.AnnotatedRegions ??= []).Add(r);
                        }
                    }

                    carrierAnnotations.Add(carrierCitation);
                }
            }
        }

        if (candidate.GroundingMetadata is { } grounding)
        {
            var chunks = grounding.GroundingChunks;
            var supports = grounding.GroundingSupports;
            var chunkCount = chunks?.Count ?? 0;

            var referencedChunks = new HashSet<int>();

            if (supports is { Count: > 0 })
            {
                for (var i = 0; i < supports.Count; i++)
                {
                    if (supports[i] is not { } support)
                    {
                        continue;
                    }

                    var segment = support.Segment;
                    var targets = segment is not null
                        ? ResolveTargetsAndRegions(
                            contents,
                            parts,
                            streamedSegments,
                            primaryText,
                            segment.PartIndex,
                            segment.StartIndex,
                            segment.EndIndex)
                        : primaryText is not null
                            ? [(primaryText, (AnnotatedRegion?)null)]
                            : [];

                    var indices = support.GroundingChunkIndices;
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
                            referencedChunks.Add(chunkIndex);

                            for (var t = 0; t < targets.Count; t++)
                            {
                                var (target, regionToAdd) = targets[t];
                                var annotation = GetOrAddChunkAnnotation(target, chunk);

                                if (annotation.Snippet is null && !string.IsNullOrEmpty(segment?.Text))
                                {
                                    annotation.Snippet = segment.Text;
                                }

                                if (regionToAdd is not null)
                                {
                                    (annotation.AnnotatedRegions ??= []).Add(regionToAdd);
                                }
                            }

                            if (carrierAnnotations is not null)
                            {
                                var carrierAnnotation = GetOrAddCarrierChunkAnnotation(carrierAnnotations, chunk);
                                if (carrierAnnotation.Snippet is null && !string.IsNullOrEmpty(segment?.Text))
                                {
                                    carrierAnnotation.Snippet = segment.Text;
                                }

                                for (var t = 0; t < targets.Count; t++)
                                {
                                    if (targets[t].Region is { } r)
                                    {
                                        (carrierAnnotation.AnnotatedRegions ??= []).Add(r);
                                    }
                                }
                            }
                        }
                    }

                    if (!attachedToChunk)
                    {
                        for (var t = 0; t < targets.Count; t++)
                        {
                            var (target, regionToAdd) = targets[t];
                            if (regionToAdd is not null || !string.IsNullOrEmpty(segment?.Text))
                            {
                                var supportAnnotation = new CitationAnnotation
                                {
                                    Snippet = segment?.Text,
                                    RawRepresentation = support,
                                    AnnotatedRegions = regionToAdd is not null ? [regionToAdd] : null
                                };
                                (target.Annotations ??= []).Add(supportAnnotation);
                            }
                        }

                        if (carrierAnnotations is not null && (HasAnyRegion(targets) || !string.IsNullOrEmpty(segment?.Text)))
                        {
                            List<AnnotatedRegion>? carrierRegions = null;
                            for (var t = 0; t < targets.Count; t++)
                            {
                                if (targets[t].Region is { } r)
                                {
                                    (carrierRegions ??= new List<AnnotatedRegion>(targets.Count)).Add(r);
                                }
                            }

                            carrierAnnotations.Add(new CitationAnnotation
                            {
                                Snippet = segment?.Text,
                                RawRepresentation = support,
                                AnnotatedRegions = carrierRegions
                            });
                        }
                    }
                }
            }

            if (chunks is { Count: > 0 })
            {
                for (var i = 0; i < chunkCount; i++)
                {
                    if (referencedChunks.Contains(i) || chunks[i] is not { } chunk)
                    {
                        continue;
                    }

                    if (chunk.Web is null && chunk.RetrievedContext is null)
                    {
                        continue;
                    }

                    if (primaryText is not null)
                    {
                        (primaryText.Annotations ??= []).Add(CreateChunkAnnotation(chunk));
                    }

                    carrierAnnotations?.Add(CreateChunkAnnotation(chunk));
                }
            }
        }

        if (carrierAnnotations is { Count: > 0 })
        {
            contents.Add(new AIContent
            {
                Annotations = carrierAnnotations,
                RawRepresentation = (object?)candidate.GroundingMetadata ?? candidate.CitationMetadata
            });
        }
    }

    private static bool ContainsTextContent(List<AIContent> contents)
    {
        for (var i = 0; i < contents.Count; i++)
        {
            if (contents[i] is TextContent)
            {
                return true;
            }
        }

        return false;
    }

    private static CitationAnnotation CreateCitationAnnotation(Citation citation, AnnotatedRegion? region)
    {
        var annotation = new CitationAnnotation
        {
            Title = citation.Title,
            Url = TryCreateUri(citation.Uri),
            RawRepresentation = citation
        };

        if (region is not null)
        {
            annotation.AnnotatedRegions = [region];
        }

        return annotation;
    }

    private static CitationAnnotation GetOrAddChunkAnnotation(TextContent target, GroundingChunk chunk)
    {
        if (target.Annotations is { } existingList)
        {
            for (var i = 0; i < existingList.Count; i++)
            {
                if (existingList[i] is CitationAnnotation existing && ReferenceEquals(existing.RawRepresentation, chunk))
                {
                    return existing;
                }
            }
        }

        var created = CreateChunkAnnotation(chunk);
        (target.Annotations ??= []).Add(created);
        return created;
    }

    private static CitationAnnotation GetOrAddCarrierChunkAnnotation(List<AIAnnotation> carrierAnnotations, GroundingChunk chunk)
    {
        for (var i = 0; i < carrierAnnotations.Count; i++)
        {
            if (carrierAnnotations[i] is CitationAnnotation existing && ReferenceEquals(existing.RawRepresentation, chunk))
            {
                return existing;
            }
        }

        var created = CreateChunkAnnotation(chunk);
        carrierAnnotations.Add(created);
        return created;
    }

    private static bool HasAnyRegion(IReadOnlyList<(TextContent Target, AnnotatedRegion? Region)> targets)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i].Region is not null)
            {
                return true;
            }
        }

        return false;
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

    private static TextContent? FindPrimaryTextContent(
        List<AIContent> contents,
        IList<Part>? parts,
        List<StreamedTextSegment>? streamedSegments,
        bool allowCreateEmpty)
    {
        for (var i = 0; i < contents.Count; i++)
        {
            if (contents[i] is TextContent tc)
            {
                return tc;
            }
        }

        if (streamedSegments is { Count: > 0 })
        {
            return streamedSegments[^1].Content;
        }

        return allowCreateEmpty
            ? FindOrCreateTargetTextContent(contents, parts, partIndex: null)
            : null;
    }

    private static IReadOnlyList<(TextContent Target, AnnotatedRegion? Region)> ResolveTargetsAndRegions(
        List<AIContent> contents,
        IList<Part>? parts,
        List<StreamedTextSegment>? streamedSegments,
        TextContent? primaryText,
        int? partIndex,
        int? startByte,
        int? endByte)
    {
        if (partIndex is >= 0 and var explicitIdx &&
            parts is not null &&
            explicitIdx < parts.Count &&
            parts[explicitIdx] is { } explicitPart &&
            (streamedSegments is not { Count: > 1 } || parts.Count > 1))
        {
            for (var i = 0; i < contents.Count; i++)
            {
                if (contents[i] is TextContent explicitTc && ReferenceEquals(explicitTc.RawRepresentation, explicitPart))
                {
                    AnnotatedRegion? explicitRegion = TryCreateRegion(explicitTc.Text, startByte, endByte, out var er) ? er : null;
                    return [(explicitTc, explicitRegion)];
                }
            }
        }

        if (streamedSegments is { Count: > 0 })
        {
            if (startByte is not null || endByte is not null)
            {
                var sb = startByte ?? 0;
                if (sb >= 0 && (endByte is null || endByte >= sb))
                {
                    List<(TextContent Target, AnnotatedRegion? Region)>? matched = null;
                    for (var i = 0; i < streamedSegments.Count; i++)
                    {
                        var seg = streamedSegments[i];
                        var isLast = i == streamedSegments.Count - 1;
                        bool overlaps;
                        if (endByte is { } eb)
                        {
                            overlaps = eb > sb
                                ? seg.EndByte > sb && seg.StartByte < eb
                                : sb >= seg.StartByte && (sb < seg.EndByte || (isLast && sb == seg.EndByte));
                        }
                        else
                        {
                            overlaps = seg.EndByte > sb || (isLast && sb == seg.EndByte);
                        }

                        if (overlaps)
                        {
                            var localStart = Math.Max(0, sb - seg.StartByte);
                            int? localEnd = endByte is { } ebVal
                                ? Math.Min(ebVal, seg.EndByte) - seg.StartByte
                                : null;
                            AnnotatedRegion? region = TryCreateRegion(seg.Content.Text, localStart, localEnd, out var r) ? r : null;
                            (matched ??= new List<(TextContent Target, AnnotatedRegion? Region)>(2)).Add((seg.Content, region));
                        }
                    }

                    if (matched is not null)
                    {
                        return matched;
                    }
                }
            }

            return primaryText is not null ? [(primaryText, null)] : [];
        }

        if (primaryText is null)
        {
            return [];
        }

        var target = partIndex is not null
            ? FindOrCreateTargetTextContent(contents, parts, partIndex, primaryText)
            : primaryText;
        AnnotatedRegion? nonStreamRegion = TryCreateRegion(target.Text, startByte, endByte, out var regionResult)
            ? regionResult
            : null;
        return [(target, nonStreamRegion)];
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
    /// <see cref="TextSpanAnnotatedRegion"/>.
    /// </summary>
    private static bool TryCreateRegion(
        string? text,
        int? startByte,
        int? endByte,
        out TextSpanAnnotatedRegion region)
    {
        region = null!;
        if (startByte is null && endByte is null)
        {
            return false;
        }

        text ??= string.Empty;
        var length = Encoding.UTF8.GetByteCount(text);
        var start = startByte ?? 0;
        if (start < 0 || start > length || endByte < start || (length == 0 && endByte > 0))
        {
            return false;
        }

        region = new TextSpanAnnotatedRegion
        {
            StartIndex = ToUtf16Index(text, start),
            EndIndex = ToUtf16Index(text, Math.Min(endByte ?? length, length))
        };
        return true;
    }

    /// <summary>
    /// Returns the UTF-16 index of the first character at or after <paramref name="utf8Offset"/>.
    /// </summary>
    private static int ToUtf16Index(string text, int utf8Offset)
    {
        var bytes = 0;
        var chars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes >= utf8Offset)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            chars += rune.Utf16SequenceLength;
        }

        return chars;
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
            _ => null
        };
    }

    private static UsageDetails? MapUsage(UsageMetadata? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var prompt = usage.PromptTokenCount;
        var candidates = usage.CandidatesTokenCount;
        var toolPrompt = usage.ToolUsePromptTokenCount ?? 0;
        var thoughts = usage.ThoughtsTokenCount ?? 0;
        var total = usage.TotalTokenCount;

        // Gemini Developer API reports totalTokenCount = prompt + toolUsePrompt + candidates + thoughts
        // (candidatesTokenCount excludes thoughtsTokenCount), whereas Microsoft.Extensions.AI UsageDetails
        // defines OutputTokenCount as inclusive of ReasoningTokenCount. If an endpoint, gateway, or model
        // version already includes toolUsePromptTokenCount in promptTokenCount or thoughtsTokenCount in
        // candidatesTokenCount (so adding them would exceed totalTokenCount), avoid double-counting.
        bool includeToolPrompt;
        bool includeThoughts;
        if (total <= 0 || prompt + toolPrompt + candidates + thoughts <= total)
        {
            includeToolPrompt = toolPrompt > 0;
            includeThoughts = thoughts > 0;
        }
        else if (prompt + candidates == total)
        {
            includeToolPrompt = false;
            includeThoughts = false;
        }
        else if (prompt + candidates + thoughts == total)
        {
            includeToolPrompt = false;
            includeThoughts = thoughts > 0;
        }
        else if (prompt + toolPrompt + candidates == total)
        {
            includeToolPrompt = toolPrompt > 0;
            includeThoughts = false;
        }
        else
        {
            includeToolPrompt = false;
            includeThoughts = false;
        }

        var inputTokens = prompt + (includeToolPrompt ? toolPrompt : 0);
        var outputTokens = candidates + (includeThoughts ? thoughts : 0);

        return new UsageDetails
        {
            InputTokenCount = inputTokens,
            OutputTokenCount = outputTokens,
            TotalTokenCount = total > 0 ? total : inputTokens + outputTokens,
            ReasoningTokenCount = usage.ThoughtsTokenCount,
            CachedInputTokenCount = usage.CachedContentTokenCount
        };
    }

    private static AdditionalPropertiesDictionary? CreateResponseProperties(BlockReason? blockReason, GroundingMetadata? groundingMetadata)
    {
        var queries = groundingMetadata?.WebSearchQueries;
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
