using System.Runtime.InteropServices;
using System.Text.Json;
using GeminiAPIWrapper.Models;
using Microsoft.Extensions.AI;

namespace GeminiAPIWrapper.Chat;

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
        var contents = template?.Contents is { Count: > 0 } existing ? new List<Content>(existing) : [];
        var systemParts = new List<Part>();

        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            systemParts.Add(Part.FromText(options.Instructions));
        }

        var messageList = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        var calls = FunctionCallRegistry.Create(messageList);

        foreach (var message in messageList)
        {
            if (message.Role == ChatRole.System)
            {
                systemParts.AddRange(message.Contents
                    .OfType<TextContent>()
                    .Where(t => !string.IsNullOrEmpty(t.Text))
                    .Select(t => Part.FromText(t.Text)));
                continue;
            }

            var targetRole = message.Role == ChatRole.Assistant ? Role.Model : Role.User;
            var parts = new List<Part>();
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
                foreach (var part in parts)
                {
                    previousParts.Add(part);
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

        if (request.SystemInstruction is null && systemParts.Count > 0)
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
        // Content produced by GeminiChatClient carries the exact wire part; replay it verbatim (keeps ids and signatures).
        if (item.RawRepresentation is Part rawPart)
        {
            parts.Add(rawPart);
            return;
        }

        var signature = GetStringProperty(item.AdditionalProperties, GeminiChatPropertyNames.ThoughtSignature);

        switch (item)
        {
            case TextContent text when !string.IsNullOrEmpty(text.Text):
                var textPart = Part.FromText(text.Text);
                textPart.ThoughtSignature = signature;
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
                parts.Add(dataPart);
                break;

            case UriContent uri:
                var uriPart = Part.FromUri(uri.Uri.ToString(), string.IsNullOrWhiteSpace(uri.MediaType) ? null : uri.MediaType);
                uriPart.ThoughtSignature = signature;
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
        }
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
        private readonly Dictionary<string, (string Name, string? ApiId)> _calls = new(StringComparer.Ordinal);

        public static FunctionCallRegistry Create(IEnumerable<ChatMessage> messages)
        {
            var registry = new FunctionCallRegistry();
            foreach (var call in messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
            {
                if (!string.IsNullOrEmpty(call.CallId) && !string.IsNullOrEmpty(call.Name))
                {
                    registry._calls[call.CallId] = (call.Name, GetApiId(call));
                }
            }

            return registry;
        }

        /// <summary>
        /// Returns the ID to send in <see cref="FunctionCall.Id"/>, or <c>null</c> when the ID was generated locally.
        /// </summary>
        public string? GetApiId(string callId) =>
            !string.IsNullOrEmpty(callId) && _calls.TryGetValue(callId, out var call) ? call.ApiId : null;

        /// <summary>
        /// Returns the function name and API call ID for a result. Unknown call IDs fall back to using the ID as the name.
        /// </summary>
        public (string Name, string? ApiId) Resolve(string callId) =>
            _calls.TryGetValue(callId, out var call) ? call : (callId, null);

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

