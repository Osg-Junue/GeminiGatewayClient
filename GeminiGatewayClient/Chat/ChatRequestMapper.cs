using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Converts <c>Microsoft.Extensions.AI</c> chat messages and options into a <see cref="GenerateContentRequest"/>.
/// One instance builds one request, because a function result needs the name of the call made earlier in the history.
/// </summary>
internal sealed class ChatRequestMapper(JsonSerializerOptions toolSerializerOptions)
{
    private readonly Dictionary<string, (string Name, string? ApiId)> _functionCalls = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds a request from <paramref name="messages"/> and <paramref name="options"/>.
    /// A <see cref="GenerateContentRequest"/> returned by <see cref="ChatOptions.RawRepresentationFactory"/> is the starting
    /// point, and its explicitly set values take precedence over <see cref="ChatOptions"/>. As that callback's contract
    /// allows, the returned instance is modified, so it must not be shared between calls.
    /// </summary>
    public static GenerateContentRequest ToRequest(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IChatClient client,
        JsonSerializerOptions toolSerializerOptions)
    {
        var request = options?.RawRepresentationFactory?.Invoke(client) as GenerateContentRequest ?? new GenerateContentRequest();
        new ChatRequestMapper(toolSerializerOptions).AppendMessages(request, messages, options?.Instructions);

        if (options is not null)
        {
            ChatOptionsMapper.Apply(request, options);
        }

        return request;
    }

    private static string? GetString(AdditionalPropertiesDictionary? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value)
            ? value switch
            {
                string { Length: > 0 } text => text,
                JsonElement { ValueKind: JsonValueKind.String } element when element.GetString() is { Length: > 0 } text => text,
                _ => null
            }
            : null;

    private static bool GetFlag(AdditionalPropertiesDictionary? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value) && value is true or JsonElement { ValueKind: JsonValueKind.True };

    private static Part WithMetadata(Part part, Part? raw, string? signature)
    {
        part.ThoughtSignature = signature;
        part.Thought ??= raw?.Thought;
        part.VideoMetadata = raw?.VideoMetadata;
        part.ExtensionData = raw?.ExtensionData;

        // Fields this client does not model, such as the id pairing code with its result, must reach the next turn.
        part.InlineData?.ExtensionData = raw?.InlineData?.ExtensionData;
        part.FileData?.ExtensionData = raw?.FileData?.ExtensionData;
        part.FunctionCall?.ExtensionData = raw?.FunctionCall?.ExtensionData;
        part.ExecutableCode?.ExtensionData = raw?.ExecutableCode?.ExtensionData;
        part.CodeExecutionResult?.ExtensionData = raw?.CodeExecutionResult?.ExtensionData;
        return part;
    }

    private static void AppendReasoning(List<Part> parts, TextReasoningContent reasoning, Part? raw)
    {
        var signature = string.IsNullOrEmpty(reasoning.ProtectedData) ? raw?.ThoughtSignature : reasoning.ProtectedData;
        if (!string.IsNullOrEmpty(reasoning.Text))
        {
            parts.Add(WithMetadata(new Part { Text = reasoning.Text, Thought = true }, raw, signature));
        }
        else if (GetFlag(reasoning.AdditionalProperties, GeminiChatPropertyNames.SignatureCompanion))
        {
            // Streaming aggregation may have dropped the signature from the text it belongs to.
            if (parts.Count > 0 && parts[^1] is { Text.Length: > 0 } text)
            {
                text.ThoughtSignature = signature;
            }
        }
        else if (!string.IsNullOrEmpty(signature))
        {
            // A signature on its own part stays there, so it goes back as it arrived.
            parts.Add(WithMetadata(new Part { Text = string.Empty, Thought = raw?.Thought }, raw, signature));
        }
    }

    private static void AppendCodeExecution(List<Part> parts, AIContent item, Part? raw, string? signature)
    {
        var properties = item.AdditionalProperties;
        var code = GetString(properties, GeminiChatPropertyNames.ExecutableCode);
        var language = GetString(properties, GeminiChatPropertyNames.ExecutableCodeLanguage);
        var output = GetString(properties, GeminiChatPropertyNames.CodeExecutionOutput);
        var outcome = GetString(properties, GeminiChatPropertyNames.CodeExecutionOutcome);

        if (code is not null || language is not null)
        {
            var executable = new ExecutableCode { Code = code, Language = Enum.TryParse<CodeExecutionLanguage>(language, ignoreCase: true, out var parsed) ? parsed : null };
            parts.Add(WithMetadata(new Part { ExecutableCode = executable }, raw, signature));
        }
        else if (output is not null || outcome is not null)
        {
            var result = new CodeExecutionResult { Output = output, Outcome = Enum.TryParse<CodeExecutionOutcome>(outcome, ignoreCase: true, out var parsed) ? parsed : null };
            parts.Add(WithMetadata(new Part { CodeExecutionResult = result }, raw, signature));
        }
        else if (raw is not null)
        {
            parts.Add(raw);
        }
    }

    private void AppendMessages(GenerateContentRequest request, IEnumerable<ChatMessage> messages, string? instructions)
    {
        var contents = request.Contents.ToList();
        var systemParts = new List<Part>();
        ChatRole? previousRole = null;

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            systemParts.Add(Part.FromText(instructions));
        }

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                systemParts.AddRange(message.Contents.OfType<TextContent>().Where(t => t.Text.Length > 0).Select(t => Part.FromText(t.Text)));
                continue;
            }

            var parts = new List<Part>(message.Contents.Count);
            foreach (var item in message.Contents)
            {
                AppendContent(parts, item);
            }

            if (parts.Count == 0)
            {
                continue;
            }

            // Gemini expects consecutive turns of one speaker, such as parallel tool results, in a single content.
            if (message.Role == previousRole)
            {
                ((List<Part>)contents[^1].Parts).AddRange(parts);
            }
            else
            {
                contents.Add(new Content { Role = message.Role == ChatRole.Assistant ? Role.Model : Role.User, Parts = parts });
                previousRole = message.Role;
            }
        }

        request.Contents = contents;
        if (request.SystemInstruction is null && systemParts.Count > 0)
        {
            request.SystemInstruction = new Content { Parts = systemParts };
        }
    }

    private void AppendContent(List<Part> parts, AIContent item)
    {
        var raw = item.RawRepresentation as Part;
        var signature = GetString(item.AdditionalProperties, GeminiChatPropertyNames.ThoughtSignature) ?? raw?.ThoughtSignature;

        switch (item)
        {
            case TextContent { Text.Length: > 0 } text:
                parts.Add(WithMetadata(Part.FromText(text.Text), raw, signature));
                break;

            case TextContent:
                break;

            case TextReasoningContent reasoning:
                AppendReasoning(parts, reasoning, raw);
                break;

            case DataContent data:
                parts.Add(WithMetadata(Part.FromBytes(data.Data.Span, data.MediaType), raw, signature));
                break;

            case UriContent uri:
                // A media type absent from the response was only filled in because UriContent requires one.
                var mediaType = raw is { FileData.MimeType: null } || string.IsNullOrWhiteSpace(uri.MediaType) ? null : uri.MediaType;
                parts.Add(WithMetadata(Part.FromUri(uri.Uri.OriginalString, mediaType), raw, signature));
                break;

            case FunctionCallContent call:
                parts.Add(WithMetadata(ToFunctionCallPart(call), raw, signature));
                break;

            case FunctionResultContent result:
                parts.Add(ToFunctionResponsePart(result));
                break;

            case HostedFileContent file:
                throw new NotSupportedException(
                    $"HostedFileContent ('{file.FileId}') cannot be sent to Gemini. Use UriContent with the uploaded file's URI and media type.");

            default:
                AppendCodeExecution(parts, item, raw, signature);
                break;
        }
    }

    private Part ToFunctionCallPart(FunctionCallContent call)
    {
        var apiId = GetFlag(call.AdditionalProperties, GeminiChatPropertyNames.SynthesizedCallId) || string.IsNullOrEmpty(call.CallId) ? null : call.CallId;
        if (!string.IsNullOrEmpty(call.CallId))
        {
            _functionCalls[call.CallId] = (call.Name, apiId);
        }

        return new Part
        {
            FunctionCall = new FunctionCall
            {
                Id = apiId,
                Name = call.Name,
                Args = ToolJsonConverter.ToArguments(call.Arguments, toolSerializerOptions)
            }
        };
    }

    private Part ToFunctionResponsePart(FunctionResultContent result)
    {
        if (!_functionCalls.TryGetValue(result.CallId, out var call))
        {
            throw new ArgumentException(
                $"The function result for call '{result.CallId}' has no matching FunctionCallContent in the messages, so its function name is unknown. Keep the call in the history.",
                "messages");
        }

        return new Part
        {
            FunctionResponse = new FunctionResponse
            {
                Id = call.ApiId,
                Name = call.Name,
                Response = ToolJsonConverter.ToResponseObject(result.Result, toolSerializerOptions)
            }
        };
    }
}
