using System.Net.Http.Headers;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Converts a complete <see cref="GenerateContentResponse"/> into a <see cref="ChatResponse"/>, and holds the mapping it
/// shares with <see cref="ChatStreamMapper"/>.
/// </summary>
internal static class ChatResponseMapper
{
    public static ChatResponse ToChatResponse(GenerateContentResponse response, string? modelId)
    {
        var candidate = FirstCandidate(response);
        var contents = ToContents(candidate, isStreaming: false);
        if (candidate is not null)
        {
            AttachCitations(candidate, contents);
        }

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
            FinishReason = ToFinishReason(candidate?.FinishReason, response.BlockReason, contents.OfType<FunctionCallContent>().Any()),
            Usage = ToUsage(response.UsageMetadata),
            AdditionalProperties = ToResponseProperties(response.BlockReason, candidate?.GroundingMetadata)
        };
    }

    public static Candidate? FirstCandidate(GenerateContentResponse response) =>
        response.Candidates is { Count: > 0 } candidates ? candidates[0] : null;

    public static List<AIContent> ToContents(Candidate? candidate, bool isStreaming)
    {
        var contents = new List<AIContent>();
        if (candidate?.Content?.Parts is { } parts)
        {
            foreach (var part in parts)
            {
                AppendContent(contents, part, isStreaming);
            }
        }

        return contents;
    }

    public static ChatFinishReason? ToFinishReason(FinishReason? reason, BlockReason? blockReason, bool hasFunctionCall)
    {
        // A failed tool call is not reported as ToolCalls, even next to functionCall parts.
        if (reason is FinishReason.MalformedFunctionCall or FinishReason.UnexpectedToolCall or FinishReason.TooManyToolCalls)
        {
            return null;
        }

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

    /// <summary>
    /// Gemini reports thoughts and tool prompts apart from <c>candidatesTokenCount</c> and <c>promptTokenCount</c>,
    /// whereas <see cref="UsageDetails"/> counts them as output and input.
    /// </summary>
    public static UsageDetails? ToUsage(UsageMetadata? usage) =>
        usage is null
            ? null
            : new UsageDetails
            {
                InputTokenCount = usage.PromptTokenCount + (usage.ToolUsePromptTokenCount ?? 0),
                OutputTokenCount = usage.CandidatesTokenCount + (usage.ThoughtsTokenCount ?? 0),
                TotalTokenCount = usage.TotalTokenCount > 0
                    ? usage.TotalTokenCount
                    : usage.PromptTokenCount + usage.CandidatesTokenCount + (usage.ToolUsePromptTokenCount ?? 0) + (usage.ThoughtsTokenCount ?? 0),
                ReasoningTokenCount = usage.ThoughtsTokenCount,
                CachedInputTokenCount = usage.CachedContentTokenCount
            };

    public static AdditionalPropertiesDictionary? ToResponseProperties(BlockReason? blockReason, GroundingMetadata? groundingMetadata)
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

    private static void AppendContent(List<AIContent> contents, Part part, bool isStreaming)
    {
        switch (part)
        {
            case { FunctionCall: { } call }:
                contents.Add(ToFunctionCallContent(part, call));
                break;

            // A thought with data of its own, even data this client does not model, is handled below.
            case { Thought: true, HasNonTextData: false } when part is not { Text: null, ExtensionData.Count: > 0 }:
                if (!string.IsNullOrEmpty(part.Text) || !string.IsNullOrEmpty(part.ThoughtSignature))
                {
                    contents.Add(ToReasoningContent(part));
                }

                break;

            // Unknown fields without a text key mean a part kind this client does not model, handled below.
            case { HasNonTextData: false, Text: null or "", ThoughtSignature.Length: > 0 } when part is not { Text: null, ExtensionData.Count: > 0 }:
                contents.Add(ToReasoningContent(part));
                break;

            case { Text.Length: > 0 }:
                contents.Add(WithSignature(new TextContent(part.Text) { RawRepresentation = part }, part));

                // Merging streamed text drops AdditionalProperties, so the signature also travels on a companion content.
                if (isStreaming && !string.IsNullOrEmpty(part.ThoughtSignature))
                {
                    var companion = ToReasoningContent(part, text: string.Empty);
                    companion.AdditionalProperties = new AdditionalPropertiesDictionary { [GeminiChatPropertyNames.SignatureCompanion] = true };
                    contents.Add(companion);
                }

                break;

            case { InlineData: { Data.Length: > 0 } blob }:
                contents.Add(WithSignature(new DataContent(blob.Data, ToValidMediaType(blob.MimeType)) { RawRepresentation = part }, part));
                break;

            case { FileData: { FileUri.Length: > 0 } file } when Uri.TryCreate(file.FileUri, UriKind.RelativeOrAbsolute, out var fileUri):
                contents.Add(WithSignature(new UriContent(fileUri, ToValidMediaType(file.MimeType)) { RawRepresentation = part }, part));
                break;

            case { ExecutableCode: not null } or { CodeExecutionResult: not null }:
                contents.Add(WithSignature(ToCodeExecutionContent(part), part));
                break;

            // Part kinds without a chat mapping (for example toolCall) still have to reach the next turn.
            case { ExtensionData.Count: > 0 }:
                contents.Add(new AIContent { RawRepresentation = part });
                break;
        }
    }

    private static TextReasoningContent ToReasoningContent(Part part, string? text = null) =>
        new(text ?? part.Text ?? string.Empty) { ProtectedData = part.ThoughtSignature, RawRepresentation = part };

    /// <summary>
    /// The content types reject a value they cannot parse, and one bad part must not fail the rest of a response.
    /// </summary>
    private static string ToValidMediaType(string? mediaType) =>
        MediaTypeHeaderValue.TryParse(mediaType, out _) ? mediaType! : MimeTypes.ApplicationOctetStream;

    private static T WithSignature<T>(T content, Part part)
        where T : AIContent
    {
        if (!string.IsNullOrEmpty(part.ThoughtSignature))
        {
            (content.AdditionalProperties ??= [])[GeminiChatPropertyNames.ThoughtSignature] = part.ThoughtSignature;
        }

        return content;
    }

    /// <summary>
    /// Metadata lives in <see cref="AIContent.AdditionalProperties"/> so it survives (de)serialization of chat history,
    /// which drops <see cref="AIContent.RawRepresentation"/>.
    /// </summary>
    private static FunctionCallContent ToFunctionCallContent(Part part, FunctionCall call)
    {
        var synthesized = string.IsNullOrEmpty(call.Id);
        var callId = synthesized ? $"call_{Guid.NewGuid():N}" : call.Id!;
        var content = new FunctionCallContent(callId, call.Name, ToolJsonConverter.ToDictionary(call.Args)) { RawRepresentation = part };
        if (synthesized)
        {
            (content.AdditionalProperties ??= [])[GeminiChatPropertyNames.SynthesizedCallId] = true;
        }

        return WithSignature(content, part);
    }

    private static AIContent ToCodeExecutionContent(Part part)
    {
        var properties = new AdditionalPropertiesDictionary();
        void Set(string name, object? value)
        {
            if (value is not null)
            {
                properties[name] = value.ToString();
            }
        }

        Set(GeminiChatPropertyNames.ExecutableCodeLanguage, part.ExecutableCode?.Language);
        Set(GeminiChatPropertyNames.ExecutableCode, part.ExecutableCode?.Code);
        Set(GeminiChatPropertyNames.CodeExecutionOutcome, part.CodeExecutionResult?.Outcome);
        Set(GeminiChatPropertyNames.CodeExecutionOutput, part.CodeExecutionResult?.Output);
        return new AIContent { RawRepresentation = part, AdditionalProperties = properties };
    }

    /// <summary>
    /// A single text takes the annotations directly. Offsets span the whole response, so several texts share an
    /// annotation-only content instead, as streamed responses do.
    /// </summary>
    private static void AttachCitations(Candidate candidate, List<AIContent> contents)
    {
        var texts = contents.OfType<TextContent>().ToList();
        if (texts is [var text])
        {
            CitationAnnotator.Annotate(candidate, text, text.Text);
            return;
        }

        CitationAnnotator.AddAnnotationContent(candidate, contents, string.Concat(texts.Select(t => t.Text)));
    }
}
