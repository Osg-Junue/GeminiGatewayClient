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
        var templateContents = template?.Contents;
        var initialCapacity = (templateContents?.Count ?? 0) + (messages.TryGetNonEnumeratedCount(out var count) && count > 0 ? count : 4);
        var contents = new List<Content>(initialCapacity);
        if (templateContents is { Count: > 0 } existing)
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

        Dictionary<string, (string Name, string? ApiId)>? functionCalls = null;
        ChatRole? lastMergedChatRole = contents.Count > 0
            ? (contents[^1].Role == Role.Model ? ChatRole.Assistant : ChatRole.User)
            : null;

        foreach (var message in messages)
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
            var isMerging = contents.Count > 0 &&
                            contents[^1].Role == targetRole &&
                            lastMergedChatRole == message.Role;

            IList<Part> targetParts;
            if (isMerging)
            {
                var prevParts = contents[^1].Parts;
                if (prevParts.IsReadOnly)
                {
                    var mutableList = new List<Part>(prevParts.Count + message.Contents.Count);
                    mutableList.AddRange(prevParts);

                    contents[^1].Parts = mutableList;
                    targetParts = mutableList;
                }
                else
                {
                    targetParts = prevParts;
                }
            }
            else
            {
                targetParts = new List<Part>(message.Contents.Count);
            }

            foreach (var item in message.Contents)
            {
                AppendPart(targetParts, item, ref functionCalls, toolSerializerOptions);
            }

            if (!isMerging)
            {
                if (targetParts.Count == 0)
                {
                    continue;
                }

                contents.Add(new Content
                {
                    Role = targetRole,
                    Parts = targetParts
                });
                lastMergedChatRole = message.Role;
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
            ChatOptionsMapper.Apply(request, options, modelId, toolSerializerOptions);
        }

        return request;
    }

    private static void AppendPart(IList<Part> parts, AIContent item, ref Dictionary<string, (string Name, string? ApiId)>? functionCalls, JsonSerializerOptions toolSerializerOptions)
    {
        var rawPart = item.RawRepresentation as Part;
        var signature = GetStringProperty(item.AdditionalProperties, GeminiChatPropertyNames.ThoughtSignature)
            ?? rawPart?.ThoughtSignature;

        // Content produced by GeminiChatClient carries the exact wire part; replay a clone of it (keeps ids, extension data, and signatures)
        // unless the caller mutated the high-level AIContent payload after creation.
        if (rawPart is not null && IsUnmodifiedRawPart(item, rawPart, signature))
        {
            var clonedPart = rawPart.Clone();
            if (string.IsNullOrEmpty(clonedPart.Text) && HasNonTextPrimaryDataField(clonedPart))
            {
                clonedPart.Text = null;
            }

            parts.Add(clonedPart);
            return;
        }

        switch (item)
        {
            case TextContent text when !string.IsNullOrEmpty(text.Text):
                var textPart = Part.FromText(text.Text);
                textPart.ThoughtSignature = signature;
                textPart.VideoMetadata = rawPart?.VideoMetadata?.Clone();
                parts.Add(textPart);
                break;

            case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text) || !string.IsNullOrEmpty(reasoning.ProtectedData) || !string.IsNullOrEmpty(rawPart?.ThoughtSignature):
                AppendReasoningPart(parts, reasoning, rawPart);
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
                var apiId = RegisterFunctionCall(ref functionCalls, call);
                var rawCall = rawPart?.FunctionCall;
                var args = call.Arguments is null
                    ? null
                    : rawCall is not null && AreUnmodifiedArguments(call.Arguments, rawCall.Args)
                        ? rawCall.Args
                        : ToolJsonConverter.ToArguments(call.Arguments, toolSerializerOptions);
                parts.Add(new Part
                {
                    ThoughtSignature = signature,
                    VideoMetadata = rawPart?.VideoMetadata?.Clone(),
                    ExtensionData = rawPart?.ExtensionData is not null
                        ? new Dictionary<string, JsonElement>(rawPart.ExtensionData, StringComparer.Ordinal)
                        : null,
                    FunctionCall = new FunctionCall
                    {
                        Id = apiId,
                        Name = call.Name,
                        Args = args,
                        ExtensionData = rawCall?.ExtensionData is not null
                            ? new Dictionary<string, JsonElement>(rawCall.ExtensionData, StringComparer.Ordinal)
                            : null
                    }
                });
                break;

            case FunctionResultContent result:
                var (name, id) = ResolveFunctionCall(functionCalls, result.CallId);
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
                AppendCodeExecutionParts(parts, item.AdditionalProperties, signature, rawPart);
                break;
        }
    }

    private static bool AreUnmodifiedArguments(IDictionary<string, object?> arguments, JsonElement? rawArgs)
    {
        if (rawArgs is not { ValueKind: JsonValueKind.Object } rawObj || rawObj.GetPropertyCount() != arguments.Count)
        {
            return false;
        }

        foreach (var (key, value) in arguments)
        {
            if (value is not JsonElement element ||
                !rawObj.TryGetProperty(key, out var rawProp) ||
                !JsonElement.DeepEquals(element, rawProp))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUnmodifiedRawPart(AIContent item, Part rawPart, string? signature)
    {
        if (item is not TextReasoningContent &&
            !string.Equals(rawPart.ThoughtSignature, signature, StringComparison.Ordinal))
        {
            return false;
        }

        return item switch
        {
            TextContent text =>
                !string.IsNullOrEmpty(text.Text) &&
                rawPart.Thought != true &&
                !HasNonTextPrimaryDataField(rawPart) &&
                string.Equals(rawPart.Text, text.Text, StringComparison.Ordinal),
            TextReasoningContent reasoning =>
                !string.IsNullOrEmpty(reasoning.Text) &&
                rawPart.Thought == true &&
                !HasNonTextPrimaryDataField(rawPart) &&
                string.Equals(rawPart.Text, reasoning.Text, StringComparison.Ordinal) &&
                string.Equals(rawPart.ThoughtSignature, reasoning.ProtectedData, StringComparison.Ordinal),
            DataContent data =>
                string.IsNullOrEmpty(rawPart.Text) &&
                rawPart.InlineData?.Data is { } rawBytes &&
                string.Equals(
                     string.IsNullOrWhiteSpace(rawPart.InlineData.MimeType) ? MimeTypes.ApplicationOctetStream : rawPart.InlineData.MimeType,
                     string.IsNullOrWhiteSpace(data.MediaType) ? MimeTypes.ApplicationOctetStream : data.MediaType,
                     StringComparison.Ordinal) &&
                data.Data.Span.SequenceEqual(rawBytes),
            UriContent uri =>
                string.IsNullOrEmpty(rawPart.Text) &&
                rawPart.FileData is not null &&
                string.Equals(rawPart.FileData.FileUri, uri.Uri.ToString(), StringComparison.Ordinal) &&
                string.Equals(
                    string.IsNullOrWhiteSpace(rawPart.FileData.MimeType) ? MimeTypes.ApplicationOctetStream : rawPart.FileData.MimeType,
                    string.IsNullOrWhiteSpace(uri.MediaType) ? MimeTypes.ApplicationOctetStream : uri.MediaType,
                    StringComparison.Ordinal),
            FunctionCallContent => false,
            _ => string.IsNullOrEmpty(rawPart.Text) && IsUnmodifiedCodeExecutionPart(item.AdditionalProperties, rawPart)
        };
    }

    private static bool HasNonTextPrimaryDataField(Part part) =>
        part.InlineData is not null ||
        part.FileData is not null ||
        part.FunctionCall is not null ||
        part.FunctionResponse is not null ||
        part.ExecutableCode is not null ||
        part.CodeExecutionResult is not null;

    private static bool IsUnmodifiedCodeExecutionPart(AdditionalPropertiesDictionary? properties, Part rawPart)
    {
        var hasCode = TryGetRawStringProperty(properties, GeminiChatPropertyNames.ExecutableCode, out var code);
        var hasLang = TryGetRawStringProperty(properties, GeminiChatPropertyNames.ExecutableCodeLanguage, out var langStr);
        var hasOutput = TryGetRawStringProperty(properties, GeminiChatPropertyNames.CodeExecutionOutput, out var output);
        var hasOutcome = TryGetRawStringProperty(properties, GeminiChatPropertyNames.CodeExecutionOutcome, out var outcomeStr);

        if (rawPart.ExecutableCode is { } rawExec)
        {
            if (rawPart.CodeExecutionResult is not null || hasOutput || hasOutcome)
            {
                return false;
            }

            var expectedLang = rawExec.Language?.ToString();
            return string.Equals(rawExec.Code, hasCode ? code : null, StringComparison.Ordinal) &&
                   string.Equals(expectedLang, hasLang ? langStr : null, StringComparison.Ordinal);
        }

        if (rawPart.CodeExecutionResult is { } rawResult)
        {
            if (hasCode || hasLang)
            {
                return false;
            }

            var expectedOutcome = rawResult.Outcome?.ToString();
            return string.Equals(rawResult.Output, hasOutput ? output : null, StringComparison.Ordinal) &&
                   string.Equals(expectedOutcome, hasOutcome ? outcomeStr : null, StringComparison.Ordinal);
        }

        return !hasCode && !hasLang && !hasOutput && !hasOutcome;
    }

    private static void AppendCodeExecutionParts(
        IList<Part> parts,
        AdditionalPropertiesDictionary? properties,
        string? signature,
        Part? rawPart)
    {
        if (properties is null || properties.Count == 0)
        {
            return;
        }

        var hasCode = TryGetRawStringProperty(properties, GeminiChatPropertyNames.ExecutableCode, out var code);
        var hasLang = TryGetRawStringProperty(properties, GeminiChatPropertyNames.ExecutableCodeLanguage, out var langStr);
        var hasOutput = TryGetRawStringProperty(properties, GeminiChatPropertyNames.CodeExecutionOutput, out var output);
        var hasOutcome = TryGetRawStringProperty(properties, GeminiChatPropertyNames.CodeExecutionOutcome, out var outcomeStr);

        if (hasCode || hasLang)
        {
            parts.Add(new Part
            {
                ThoughtSignature = (hasOutput || hasOutcome) ? null : signature,
                VideoMetadata = rawPart?.VideoMetadata?.Clone(),
                ExecutableCode = new ExecutableCode
                {
                    Code = code,
                    Language = hasLang && Enum.TryParse<CodeExecutionLanguage>(langStr, ignoreCase: true, out var lang) ? lang : null
                }
            });
        }

        if (hasOutput || hasOutcome)
        {
            parts.Add(new Part
            {
                ThoughtSignature = signature,
                VideoMetadata = (hasCode || hasLang) ? null : rawPart?.VideoMetadata?.Clone(),
                CodeExecutionResult = new CodeExecutionResult
                {
                    Output = output,
                    Outcome = hasOutcome && Enum.TryParse<CodeExecutionOutcome>(outcomeStr, ignoreCase: true, out var outcome) ? outcome : null
                }
            });
        }
    }

    private static bool TryGetRawStringProperty(AdditionalPropertiesDictionary? properties, string key, out string? value)
    {
        if (properties is not null && properties.TryGetValue(key, out var raw))
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

    private static void AppendReasoningPart(IList<Part> parts, TextReasoningContent reasoning, Part? rawPart)
    {
        var hasText = !string.IsNullOrEmpty(reasoning.Text);
        var signature = !string.IsNullOrEmpty(reasoning.ProtectedData)
            ? reasoning.ProtectedData
            : rawPart?.ThoughtSignature;

        // A signature-only companion of the preceding part (text + signature split on receive) must not be sent twice,
        // and when multi-chunk TextContent coalescing dropped the signature from the preceding text part, re-attach it.
        if (!hasText && !string.IsNullOrEmpty(signature) && parts.Count > 0)
        {
            if (string.Equals(parts[^1].ThoughtSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            if (parts[^1] is { Text.Length: > 0, Thought: not true, ThoughtSignature: null or "" } prevPart)
            {
                prevPart.ThoughtSignature = signature;
                if (prevPart.ExtensionData is null && rawPart?.ExtensionData is { Count: > 0 } rawExt)
                {
                    prevPart.ExtensionData = new Dictionary<string, JsonElement>(rawExt, StringComparer.Ordinal);
                }

                return;
            }
        }

        parts.Add(new Part
        {
            Text = hasText ? reasoning.Text : null,
            Thought = hasText ? true : null,
            ThoughtSignature = string.IsNullOrEmpty(signature) ? null : signature,
            VideoMetadata = rawPart?.VideoMetadata?.Clone(),
            ExtensionData = rawPart?.ExtensionData is not null
                ? new Dictionary<string, JsonElement>(rawPart.ExtensionData, StringComparer.Ordinal)
                : null
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

    private static string? RegisterFunctionCall(ref Dictionary<string, (string Name, string? ApiId)>? calls, FunctionCallContent call)
    {
        var apiId = GetFunctionCallApiId(call);
        if (!string.IsNullOrEmpty(call.CallId) && !string.IsNullOrEmpty(call.Name))
        {
            (calls ??= new Dictionary<string, (string Name, string? ApiId)>(StringComparer.Ordinal))[call.CallId] =
                (call.Name, apiId);
        }

        return apiId;
    }

    private static (string Name, string? ApiId) ResolveFunctionCall(Dictionary<string, (string Name, string? ApiId)>? calls, string callId) =>
        !string.IsNullOrEmpty(callId) && calls is not null && calls.TryGetValue(callId, out var call) ? call : (callId, null);

    private static string? GetFunctionCallApiId(FunctionCallContent call)
    {
        if (GetBooleanProperty(call.AdditionalProperties, GeminiChatPropertyNames.SynthesizedCallId))
        {
            return null;
        }

        if (call.RawRepresentation is Part { FunctionCall: { } rawCall } && string.IsNullOrEmpty(rawCall.Id))
        {
            return null;
        }

        return string.IsNullOrEmpty(call.CallId) ? null : call.CallId;
    }
}
