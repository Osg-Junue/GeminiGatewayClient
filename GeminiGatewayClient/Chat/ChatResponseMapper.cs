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

    internal readonly record struct StreamedTextSegment(TextContent Content, int StartByte, int EndByte);

    public static ChatResponse ToChatResponse(GenerateContentResponse response, string? modelId)
    {
        var candidate = response.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
        List<StreamedTextSegment>? unusedSegments = null;
        var unusedBytes = 0;
        var contents = MapContents(candidate, isStreaming: false, trackStreamSegments: false, ref unusedSegments, ref unusedBytes, out var hasFunctionCall);

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
        List<StreamedTextSegment>? unusedSegments = null;
        var unusedBytes = 0;
        var contents = MapContents(candidate, isStreaming: true, trackStreamSegments: false, ref unusedSegments, ref unusedBytes, out var hasFunctionCall);

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
    /// Maps a streamed chunk while tracking streamed <see cref="TextContent"/> segments and their cumulative UTF-8 byte ranges
    /// so trailing <c>groundingMetadata</c> and <c>citationMetadata</c> attach to the corresponding text segment with valid local UTF-16 spans.
    /// </summary>
    internal static ChatResponseUpdate ToChatResponseUpdate(
        GenerateContentResponse chunk,
        string? modelId,
        ref List<StreamedTextSegment>? streamedSegments,
        ref int cumulativeUtf8Bytes)
    {
        var candidate = chunk.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
        var contents = MapContents(candidate, isStreaming: true, trackStreamSegments: true, ref streamedSegments, ref cumulativeUtf8Bytes, out var hasFunctionCall);

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

    private static List<AIContent> MapContents(
        Candidate? candidate,
        bool isStreaming,
        bool trackStreamSegments,
        ref List<StreamedTextSegment>? streamedSegments,
        ref int cumulativeUtf8Bytes,
        out bool hasFunctionCall)
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
            AttachCandidateAnnotations(candidate, parts, annotatedOnly, streamedSegments, isStreaming);
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
                    if (isStreaming)
                    {
                        if (trackStreamSegments)
                        {
                            var startByte = cumulativeUtf8Bytes;
                            cumulativeUtf8Bytes += Encoding.UTF8.GetByteCount(text);
                            (streamedSegments ??= new List<StreamedTextSegment>(4)).Add(
                                new StreamedTextSegment(textContent, startByte, cumulativeUtf8Bytes));
                        }

                        if (!string.IsNullOrEmpty(part.ThoughtSignature))
                        {
                            contents.Add(new TextReasoningContent(string.Empty)
                            {
                                ProtectedData = part.ThoughtSignature,
                                RawRepresentation = part
                            });
                        }
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

                // Part kinds without a chat mapping (for example toolCall) still have to reach the next turn,
                // unless the part could not be resent, which would break every later turn of the conversation.
                case { ExtensionData.Count: > 0 } when part.GetValidationError() is null:
                    contents.Add(new AIContent { RawRepresentation = part });
                    break;
            }
        }

        if (hasAnnotations)
        {
            AttachCandidateAnnotations(candidate, parts, contents, streamedSegments, isStreaming);
        }

        return contents;
    }

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
        List<StreamedTextSegment>? streamedSegments = null,
        bool isStreaming = false)
    {
        var needsRealtimeCarrier = isStreaming && !ContainsTextContent(contents);
        var primaryText = FindPrimaryTextContent(contents, parts, streamedSegments, allowCreateEmpty: !isStreaming);
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

            ulong referencedChunksMask = 0;
            HashSet<int>? extraReferencedChunks = null;

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
                            if ((uint)chunkIndex < 64)
                            {
                                referencedChunksMask |= 1UL << chunkIndex;
                            }
                            else
                            {
                                (extraReferencedChunks ??= []).Add(chunkIndex);
                            }

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
                    var isReferenced = i < 64
                        ? (referencedChunksMask & (1UL << i)) != 0
                        : extraReferencedChunks?.Contains(i) == true;

                    if (isReferenced || chunks[i] is not { } chunk)
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
