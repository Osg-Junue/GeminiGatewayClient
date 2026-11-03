using System.Runtime.InteropServices;
using System.Text.Json;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Converts <c>Microsoft.Extensions.AI</c> chat messages and options into a <see cref="GenerateContentRequest"/>.
/// </summary>
internal static class ChatRequestMapper
{
    /// <summary>
    /// Builds a request from <paramref name="messages"/> and <paramref name="options"/>.
    /// A <see cref="GenerateContentRequest"/> returned by <see cref="ChatOptions.RawRepresentationFactory"/> is used as the
    /// starting point; its explicitly set values take precedence over <see cref="ChatOptions"/>.
    /// </summary>
    public static GenerateContentRequest ToRequest(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IChatClient client,
        string? modelId,
        JsonSerializerOptions toolSerializerOptions)
    {
        var template = options?.RawRepresentationFactory?.Invoke(client) as GenerateContentRequest;
        var messageList = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        var contents = new List<Content>((template?.Contents?.Count ?? 0) + messageList.Count);
        if (template?.Contents is { Count: > 0 } existing)
        {
            for (var i = 0; i < existing.Count; i++)
            {
                if (existing[i] is { } existingContent)
                {
                    contents.Add(existingContent.Clone());
                }
            }
        }

        var hasTemplateSystemInstruction = template?.SystemInstruction is not null;
        List<Part>? systemParts = null;

        if (!hasTemplateSystemInstruction && !string.IsNullOrWhiteSpace(options?.Instructions))
        {
            systemParts = new List<Part>(2) { Part.FromText(options.Instructions) };
        }

        var calls = FunctionCallRegistry.Create(messageList);

        foreach (var message in messageList)
        {
            if (message.Role == ChatRole.System)
            {
                if (!hasTemplateSystemInstruction)
                {
                    foreach (var contentItem in message.Contents)
                    {
                        if (contentItem is TextContent { Text.Length: > 0 } sysText)
                        {
                            (systemParts ??= new List<Part>(message.Contents.Count)).Add(Part.FromText(sysText.Text));
                        }
                    }
                }

                continue;
            }

            if (message.Contents.Count == 0)
            {
                continue;
            }

            var targetRole = message.Role == ChatRole.Assistant ? Role.Model : Role.User;
            var parts = new List<Part>(message.Contents.Count);
            foreach (var item in message.Contents)
            {
                AppendPart(parts, item, calls, toolSerializerOptions);
            }

            if (parts.Count == 0)
            {
                continue;
            }

            if (contents.Count > 0 && contents[^1].Role == targetRole)
            {
                var previousParts = contents[^1].Parts;
                if (previousParts is List<Part> previousList)
                {
                    previousList.AddRange(parts);
                }
                else
                {
                    foreach (var part in parts)
                    {
                        previousParts.Add(part);
                    }
                }
            }
            else
            {
                contents.Add(new Content
                {
                    Role = targetRole,
                    Parts = parts
                });
            }
        }

        var request = template is null
            ? new GenerateContentRequest { Contents = contents }
            : template.CloneWithContents(contents);

        if (request.SystemInstruction is null && systemParts is { Count: > 0 })
        {
            request.SystemInstruction = new Content { Parts = systemParts };
        }

        if (options is not null)
        {
            ChatOptionsMapper.Apply(request, options, modelId);
        }

        return request;
    }

    private static void AppendPart(List<Part> parts, AIContent item, FunctionCallRegistry calls, JsonSerializerOptions toolSerializerOptions)
    {
        // Content produced by GeminiChatClient carries the exact wire part; replay it verbatim (keeps ids and signatures)
        // unless the caller mutated the high-level AIContent payload after creation.
        var rawPart = item.RawRepresentation as Part;
        if (rawPart is not null && IsUnmodifiedRawPart(item, rawPart))
        {
            parts.Add(rawPart);
            return;
        }

        var signature = GetStringProperty(item.AdditionalProperties, GeminiChatPropertyNames.ThoughtSignature)
            ?? rawPart?.ThoughtSignature;

        switch (item)
        {
            case TextContent text when !string.IsNullOrEmpty(text.Text):
                var textPart = Part.FromText(text.Text);
                textPart.ThoughtSignature = signature;
                textPart.VideoMetadata = rawPart?.VideoMetadata?.Clone();
                parts.Add(textPart);
                break;

            case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text) || !string.IsNullOrEmpty(reasoning.ProtectedData):
                AppendReasoningPart(parts, reasoning);
                break;

            case DataContent data:
                var dataPart = MemoryMarshal.TryGetArray(data.Data, out var segment) &&
                               segment.Array is not null &&
                               segment.Offset == 0 &&
                               segment.Count == segment.Array.Length
                    ? Part.FromBytes(segment.Array, data.MediaType)
                    : Part.FromBytes(data.Data.Span, data.MediaType);
                dataPart.ThoughtSignature = signature;
                dataPart.VideoMetadata = rawPart?.VideoMetadata?.Clone();
                parts.Add(dataPart);
                break;

            case UriContent uri:
                var uriPart = Part.FromUri(uri.Uri.ToString(), string.IsNullOrWhiteSpace(uri.MediaType) ? null : uri.MediaType);
                uriPart.ThoughtSignature = signature;
                uriPart.VideoMetadata = rawPart?.VideoMetadata?.Clone();
                parts.Add(uriPart);
                break;

            case FunctionCallContent call:
                parts.Add(new Part
                {
                    ThoughtSignature = signature,
                    FunctionCall = new FunctionCall
                    {
                        Id = calls.GetApiId(call.CallId),
                        Name = call.Name,
                        Args = ToolJsonConverter.ToArguments(call.Arguments, toolSerializerOptions)
                    }
                });
                break;

            case FunctionResultContent result:
                var (name, id) = calls.Resolve(result.CallId);
                parts.Add(new Part
                {
                    FunctionResponse = new FunctionResponse
                    {
                        Id = id,
                        Name = name,
                        Response = ToolJsonConverter.ToResponseObject(result.Result, toolSerializerOptions)
                    }
                });
                break;

            default:
                if (TryCreateCodeExecutionPart(item.AdditionalProperties, signature) is { } codePart)
                {
                    parts.Add(codePart);
                }

                break;
        }
    }

    private static bool IsUnmodifiedRawPart(AIContent item, Part rawPart) => item switch
    {
        TextContent text => string.Equals(rawPart.Text, text.Text, StringComparison.Ordinal),
        TextReasoningContent reasoning =>
            string.Equals(rawPart.Text ?? string.Empty, reasoning.Text, StringComparison.Ordinal) &&
            string.Equals(rawPart.ThoughtSignature, reasoning.ProtectedData, StringComparison.Ordinal),
        UriContent uri =>
            rawPart.FileData is not null &&
            string.Equals(rawPart.FileData.FileUri, uri.Uri.ToString(), StringComparison.Ordinal),
        FunctionCallContent call =>
            rawPart.FunctionCall is not null &&
            string.Equals(rawPart.FunctionCall.Name, call.Name, StringComparison.Ordinal),
        _ => true
    };

    private static Part? TryCreateCodeExecutionPart(AdditionalPropertiesDictionary? properties, string? signature)
    {
        if (properties is null || properties.Count == 0)
        {
            return null;
        }

        var hasCode = TryGetRawStringProperty(properties, GeminiChatPropertyNames.ExecutableCode, out var code);
        var hasLang = TryGetRawStringProperty(properties, GeminiChatPropertyNames.ExecutableCodeLanguage, out var langStr);
        var hasOutput = TryGetRawStringProperty(properties, GeminiChatPropertyNames.CodeExecutionOutput, out var output);
        var hasOutcome = TryGetRawStringProperty(properties, GeminiChatPropertyNames.CodeExecutionOutcome, out var outcomeStr);

        if (!hasCode && !hasLang && !hasOutput && !hasOutcome)
        {
            return null;
        }

        var part = new Part { ThoughtSignature = signature };

        if (hasCode || hasLang)
        {
            part.ExecutableCode = new ExecutableCode
            {
                Code = code,
                Language = hasLang && Enum.TryParse<CodeExecutionLanguage>(langStr, ignoreCase: true, out var lang) ? lang : null
            };
        }

        if (hasOutput || hasOutcome)
        {
            part.CodeExecutionResult = new CodeExecutionResult
            {
                Output = output,
                Outcome = hasOutcome && Enum.TryParse<CodeExecutionOutcome>(outcomeStr, ignoreCase: true, out var outcome) ? outcome : null
            };
        }

        return part;
    }

    private static bool TryGetRawStringProperty(AdditionalPropertiesDictionary properties, string key, out string? value)
    {
        if (properties.TryGetValue(key, out var raw))
        {
            switch (raw)
            {
                case string s:
                    value = s;
                    return true;
                case JsonElement { ValueKind: JsonValueKind.String } element:
                    value = element.GetString();
                    return true;
            }
        }

        value = null;
        return false;
    }

    private static void AppendReasoningPart(List<Part> parts, TextReasoningContent reasoning)
    {
        var hasText = !string.IsNullOrEmpty(reasoning.Text);

        // A signature-only companion of the preceding part (text + signature split on receive) must not be sent twice.
        if (!hasText && parts.Count > 0 &&
            string.Equals(parts[^1].ThoughtSignature, reasoning.ProtectedData, StringComparison.Ordinal))
        {
            return;
        }

        parts.Add(new Part
        {
            Text = hasText ? reasoning.Text : null,
            Thought = hasText ? true : null,
            ThoughtSignature = string.IsNullOrEmpty(reasoning.ProtectedData) ? null : reasoning.ProtectedData
        });
    }

    internal static string? GetStringProperty(AdditionalPropertiesDictionary? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value)
            ? value switch
            {
                string { Length: > 0 } s => s,
                JsonElement { ValueKind: JsonValueKind.String } element when element.GetString() is { Length: > 0 } s => s,
                _ => null
            }
            : null;

    internal static bool GetBooleanProperty(AdditionalPropertiesDictionary? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value) && value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            _ => false
        };

    /// <summary>
    /// Correlates <see cref="FunctionResultContent"/> <c>CallId</c> values with the function name and the server-assigned call ID
    /// (if any) of the originating <see cref="FunctionCallContent"/>.
    /// </summary>
    private sealed class FunctionCallRegistry
    {
        private Dictionary<string, (string Name, string? ApiId)>? _calls;

        public static FunctionCallRegistry Create(IReadOnlyCollection<ChatMessage> messages)
        {
            var registry = new FunctionCallRegistry();
            foreach (var message in messages)
            {
                foreach (var item in message.Contents)
                {
                    if (item is FunctionCallContent call &&
                        !string.IsNullOrEmpty(call.CallId) &&
                        !string.IsNullOrEmpty(call.Name))
                    {
                        (registry._calls ??= new Dictionary<string, (string Name, string? ApiId)>(StringComparer.Ordinal))[call.CallId] =
                            (call.Name, GetApiId(call));
                    }
                }
            }

            return registry;
        }

        /// <summary>
        /// Returns the ID to send in <see cref="FunctionCall.Id"/>, or <c>null</c> when the ID was generated locally.
        /// </summary>
        public string? GetApiId(string callId) =>
            !string.IsNullOrEmpty(callId) && _calls is not null && _calls.TryGetValue(callId, out var call) ? call.ApiId : null;

        /// <summary>
        /// Returns the function name and API call ID for a result. Unknown call IDs fall back to using the ID as the name.
        /// </summary>
        public (string Name, string? ApiId) Resolve(string callId) =>
            _calls is not null && _calls.TryGetValue(callId, out var call) ? call : (callId, null);

        private static string? GetApiId(FunctionCallContent call)
        {
            if (call.RawRepresentation is Part rawPart)
            {
                return rawPart.FunctionCall?.Id;
            }

            var synthesized = GetBooleanProperty(call.AdditionalProperties, GeminiChatPropertyNames.SynthesizedCallId);
            return synthesized ? null : call.CallId;
        }
    }
}
