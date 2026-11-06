using System.Text;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Turns a candidate's <c>citationMetadata</c> and <c>groundingMetadata</c> into <see cref="CitationAnnotation"/> instances.
/// </summary>
internal static class CitationAnnotator
{
    private const string GoogleSearchToolName = "google_search";

    /// <summary>
    /// Adds the annotations to <paramref name="target"/>. Gemini counts offsets in UTF-8 bytes from the start of the response
    /// text, which <paramref name="text"/> must hold.
    /// </summary>
    public static void Annotate(Candidate candidate, AIContent target, string text)
    {
        foreach (var citation in candidate.CitationMetadata?.Citations ?? [])
        {
            if (citation is not null)
            {
                AddCitation(citation, target, text);
            }
        }

        if (candidate.GroundingMetadata is { } grounding)
        {
            AddGrounding(grounding, target, text);
        }
    }

    /// <summary>
    /// Adds the annotations on an annotation-only content, when there are any.
    /// </summary>
    public static void AddAnnotationContent(Candidate candidate, List<AIContent> contents, string text)
    {
        var carrier = new AIContent { RawRepresentation = (object?)candidate.GroundingMetadata ?? candidate.CitationMetadata };
        Annotate(candidate, carrier, text);
        if (carrier.Annotations is { Count: > 0 })
        {
            contents.Add(carrier);
        }
    }

    private static void AddCitation(Citation citation, AIContent target, string text)
    {
        var annotation = new CitationAnnotation { Title = citation.Title, Url = ToUri(citation.Uri), RawRepresentation = citation };
        AddRegion(annotation, text, citation.StartIndex, citation.EndIndex);
        (target.Annotations ??= []).Add(annotation);
    }

    private static void AddGrounding(GroundingMetadata grounding, AIContent target, string text)
    {
        var chunks = grounding.GroundingChunks;
        var referenced = new HashSet<int>();

        foreach (var support in grounding.GroundingSupports)
        {
            if (support is null)
            {
                continue;
            }

            var segment = support.Segment;
            var attached = false;

            foreach (var index in support.GroundingChunkIndices)
            {
                if ((uint)index < (uint)chunks.Count && chunks[index] is { } chunk)
                {
                    attached = true;
                    referenced.Add(index);
                    var annotation = GetOrAddChunkAnnotation(target, chunk);
                    annotation.Snippet ??= string.IsNullOrEmpty(segment?.Text) ? null : segment.Text;
                    AddRegion(annotation, text, segment?.StartIndex, segment?.EndIndex);
                }
            }

            if (!attached && segment is not null)
            {
                var annotation = new CitationAnnotation { Snippet = segment.Text, RawRepresentation = support };
                AddRegion(annotation, text, segment.StartIndex, segment.EndIndex);
                (target.Annotations ??= []).Add(annotation);
            }
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            if (!referenced.Contains(i) && chunks[i] is { } chunk && (chunk.Web is not null || chunk.RetrievedContext is not null))
            {
                GetOrAddChunkAnnotation(target, chunk);
            }
        }
    }

    private static CitationAnnotation GetOrAddChunkAnnotation(AIContent target, GroundingChunk chunk)
    {
        var existing = target.Annotations?.OfType<CitationAnnotation>().FirstOrDefault(a => ReferenceEquals(a.RawRepresentation, chunk));
        if (existing is not null)
        {
            return existing;
        }

        var created = new CitationAnnotation
        {
            Title = chunk.Web?.Title ?? chunk.Web?.Domain ?? chunk.RetrievedContext?.Title,
            Url = ToUri(chunk.Web?.Uri ?? chunk.RetrievedContext?.Uri),
            Snippet = chunk.RetrievedContext?.Text,
            ToolName = chunk.Web is not null ? GoogleSearchToolName : null,
            RawRepresentation = chunk
        };
        (target.Annotations ??= []).Add(created);
        return created;
    }

    private static Uri? ToUri(string? value) =>
        Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri) ? uri : null;

    /// <summary>
    /// Records the span given by Gemini's UTF-8 byte offsets as UTF-16 indices.
    /// </summary>
    private static void AddRegion(CitationAnnotation annotation, string text, int? startByte, int? endByte)
    {
        var length = Encoding.UTF8.GetByteCount(text);
        var start = startByte ?? 0;
        if ((startByte is null && endByte is null) || start < 0 || start > length || endByte < start)
        {
            return;
        }

        (annotation.AnnotatedRegions ??= []).Add(new TextSpanAnnotatedRegion
        {
            StartIndex = ToUtf16Index(text, start),
            EndIndex = ToUtf16Index(text, Math.Min(endByte ?? length, length))
        });
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
}
