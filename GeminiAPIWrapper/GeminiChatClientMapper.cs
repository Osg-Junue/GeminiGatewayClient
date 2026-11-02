using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text.Json;
using GeminiAPIWrapper.Configurations;
using Microsoft.Extensions.AI;

namespace GeminiAPIWrapper;

/// <summary>
/// Maps between <c>Microsoft.Extensions.AI</c> types and Gemini request/response models without runtime reflection.
/// </summary>
internal static class GeminiChatClientMapper
{
    public const string BlockReasonPropertyKey = "gemini.blockReason";

    public static ChatClientMetadata CreateMetadata(string? endpointBase)
    {
        Uri? providerUri = null;
        string? modelId = null;

        if (!string.IsNullOrWhiteSpace(endpointBase))
        {
            var trimmed = endpointBase.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var path = uri.AbsolutePath.TrimEnd('/', ':');
                var builder = new UriBuilder(uri) { Path = path };
                providerUri = builder.Uri;

                const string modelsSegment = "/models/";
                var idx = path.LastIndexOf(modelsSegment, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var candidate = path[(idx + modelsSegment.Length)..];
                    var slashIdx = candidate.IndexOf('/');
                    if (slashIdx >= 0)
                    {
                        candidate = candidate[..slashIdx];
                    }

                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        modelId = Uri.UnescapeDataString(candidate);
                    }
                }
            }
        }

        return new ChatClientMetadata("gemini", providerUri, modelId);
    }

    public static GeminiRequest ToGeminiRequest(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IChatClient client,
        JsonSerializerOptions? serializerOptions = null)
    {
        var customRequest = options?.RawRepresentationFactory?.Invoke(client) as GeminiRequest;

        var contents = customRequest?.Contents is { Count: > 0 } existingContents
            ? new List<Content>(existingContents)
            : [];
        var systemParts = new List<Part>();

        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            systemParts.Add(new Part { Text = options.Instructions });
        }

        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var callLookup = new Dictionary<string, (string Name, string? ApiId)>(StringComparer.Ordinal);

        foreach (var message in messageList)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent fcc && !string.IsNullOrEmpty(fcc.CallId) && !string.IsNullOrEmpty(fcc.Name))
                {
                    var rawPart = fcc.RawRepresentation as Part;
                    var apiId = rawPart?.FunctionCall?.Id;
                    if (apiId is null && !string.Equals(fcc.CallId, fcc.Name, StringComparison.Ordinal) &&
                        !fcc.CallId.StartsWith($"{fcc.Name}_call_", StringComparison.Ordinal))
                    {
                        apiId = fcc.CallId;
                    }

                    callLookup[fcc.CallId] = (fcc.Name, apiId);
                }
            }
        }

        foreach (var message in messageList)
        {
            if (message.Role == ChatRole.System)
            {
                foreach (var content in message.Contents)
                {
                    if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
                    {
                        systemParts.Add(new Part { Text = tc.Text });
                    }
                }

                continue;
            }

            var parts = new List<Part>();
            foreach (var item in message.Contents)
            {
                if (item.RawRepresentation is Part rawPart)
                {
                    parts.Add(rawPart);
                    continue;
                }

                switch (item)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        parts.Add(new Part { Text = tc.Text });
                        break;

                    case TextReasoningContent trc when !string.IsNullOrEmpty(trc.Text) || !string.IsNullOrEmpty(trc.ProtectedData):
                        parts.Add(new Part
                        {
                            Text = string.IsNullOrEmpty(trc.Text) ? null : trc.Text,
                            Thought = string.IsNullOrEmpty(trc.Text) ? null : true,
                            ThoughtSignature = string.IsNullOrEmpty(trc.ProtectedData) ? null : trc.ProtectedData
                        });
                        break;

                    case DataContent dc:
                        if (string.IsNullOrWhiteSpace(dc.MediaType))
                        {
                            throw new ArgumentException("DataContent.MediaType is required when sending inline binary content to Gemini.", nameof(messages));
                        }

                        parts.Add(new Part
                        {
                            InlineData = new InlineData
                            {
                                MimeType = dc.MediaType,
                                Data = Convert.ToBase64String(dc.Data.Span)
                            }
                        });
                        break;

                    case UriContent uc:
                        parts.Add(new Part
                        {
                            FileData = new FileData
                            {
                                MimeType = string.IsNullOrWhiteSpace(uc.MediaType) ? null : uc.MediaType,
                                FileUri = uc.Uri.ToString()
                            }
                        });
                        break;

                    case FunctionCallContent fcc:
                        string? callIdToSend = null;
                        if (!string.IsNullOrEmpty(fcc.CallId) && callLookup.TryGetValue(fcc.CallId, out var callMeta))
                        {
                            callIdToSend = callMeta.ApiId;
                        }

                        parts.Add(new Part
                        {
                            FunctionCall = new FunctionCall
                            {
                                Id = callIdToSend,
                                Name = fcc.Name,
                                Args = ConvertDictionaryToJsonElement(fcc.Arguments, serializerOptions)
                            }
                        });
                        break;

                    case FunctionResultContent frc:
                        string funcName;
                        string? responseId = null;
                        if (callLookup.TryGetValue(frc.CallId, out var resolved))
                        {
                            funcName = resolved.Name;
                            responseId = resolved.ApiId;
                        }
                        else
                        {
                            funcName = frc.CallId;
                        }

                        parts.Add(new Part
                        {
                            FunctionResponse = new FunctionResponse
                            {
                                Id = responseId,
                                Name = funcName,
                                Response = ConvertResultToJsonElement(frc.Result, serializerOptions)
                            }
                        });
                        break;
                }
            }

            if (parts.Count > 0)
            {
                contents.Add(new Content
                {
                    Role = message.Role == ChatRole.Assistant ? Role.Model : Role.User,
                    Parts = parts
                });
            }
        }

        var request = new GeminiRequest
        {
            Contents = contents,
            CachedContent = customRequest?.CachedContent,
            SystemInstruction = customRequest?.SystemInstruction,
            GenerationConfig = customRequest?.GenerationConfig,
            Tools = customRequest?.Tools,
            ToolConfig = customRequest?.ToolConfig,
            SafetySettings = customRequest?.SafetySettings,
            Labels = customRequest?.Labels
        };

        if (request.SystemInstruction is null && systemParts.Count > 0)
        {
            request.SystemInstruction = new SystemInstruction { Parts = systemParts };
        }

        if (options is not null)
        {
            ApplyChatOptions(request, options);
        }

        return request;
    }

    public static ChatResponse ToChatResponse(GeminiResponse? response, string? defaultModelId)
    {
        var candidate = response?.Candidates?.FirstOrDefault();
        var contents = MapCandidateContents(candidate, out var hasFunctionCall);

        var chatMessage = new ChatMessage(ChatRole.Assistant, contents)
        {
            RawRepresentation = candidate,
            MessageId = response?.ResponseId
        };

        var finishReason = MapFinishReason(candidate?.FinishReason, response?.PromptFeedback?.BlockReason, hasFunctionCall);
        AdditionalPropertiesDictionary? additionalProps = null;
        if (response?.PromptFeedback?.BlockReason is { } blockReason)
        {
            additionalProps = new AdditionalPropertiesDictionary
            {
                [BlockReasonPropertyKey] = blockReason.ToString()
            };
        }

        return new ChatResponse(chatMessage)
        {
            RawRepresentation = response,
            ResponseId = response?.ResponseId,
            ModelId = response?.ModelVersion ?? candidate?.ModelVersion ?? defaultModelId,
            FinishReason = finishReason,
            Usage = MapUsage(response?.UsageMetadata),
            AdditionalProperties = additionalProps
        };
    }

    public static ChatResponseUpdate ToChatResponseUpdate(GeminiResponse chunk, string? defaultModelId)
    {
        var candidate = chunk.Candidates?.FirstOrDefault();
        var contents = MapCandidateContents(candidate, out var hasFunctionCall);

        if (chunk.UsageMetadata is not null && MapUsage(chunk.UsageMetadata) is { } usage)
        {
            contents.Add(new UsageContent(usage));
        }

        var finishReason = MapFinishReason(candidate?.FinishReason, chunk.PromptFeedback?.BlockReason, hasFunctionCall);
        AdditionalPropertiesDictionary? additionalProps = null;
        if (chunk.PromptFeedback?.BlockReason is { } blockReason)
        {
            additionalProps = new AdditionalPropertiesDictionary
            {
                [BlockReasonPropertyKey] = blockReason.ToString()
            };
        }

        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            RawRepresentation = chunk,
            ResponseId = chunk.ResponseId,
            MessageId = chunk.ResponseId,
            ModelId = chunk.ModelVersion ?? candidate?.ModelVersion ?? defaultModelId,
            FinishReason = finishReason,
            AdditionalProperties = additionalProps
        };
    }

    private static List<AIContent> MapCandidateContents(Candidate? candidate, out bool hasFunctionCall)
    {
        hasFunctionCall = false;
        var contents = new List<AIContent>();
        var parts = candidate?.Content?.Parts;
        if (parts is null)
        {
            return contents;
        }

        var callOrdinal = 0;
        foreach (var part in parts)
        {
            if (part.FunctionCall is { } fc)
            {
                hasFunctionCall = true;
                var callId = !string.IsNullOrEmpty(fc.Id)
                    ? fc.Id
                    : $"{fc.Name}_call_{callOrdinal++}_{Guid.NewGuid():N}";

                contents.Add(new FunctionCallContent(
                    callId: callId,
                    name: fc.Name,
                    arguments: ConvertJsonElementToDictionary(fc.Args))
                {
                    RawRepresentation = part
                });
            }
            else if (part.Thought == true || (!string.IsNullOrEmpty(part.ThoughtSignature) && part.Text is null))
            {
                contents.Add(new TextReasoningContent(part.Text ?? string.Empty)
                {
                    ProtectedData = part.ThoughtSignature,
                    RawRepresentation = part
                });
            }
            else if (part.Text is { } text)
            {
                if (!string.IsNullOrEmpty(part.ThoughtSignature))
                {
                    contents.Add(new TextContent(text) { RawRepresentation = part });
                    contents.Add(new TextReasoningContent(string.Empty)
                    {
                        ProtectedData = part.ThoughtSignature,
                        RawRepresentation = new Part { ThoughtSignature = part.ThoughtSignature }
                    });
                }
                else
                {
                    contents.Add(new TextContent(text) { RawRepresentation = part });
                }
            }
            else if (part.InlineData is { } inline && !string.IsNullOrEmpty(inline.Data))
            {
                var bytes = Convert.FromBase64String(inline.Data);
                var mimeType = string.IsNullOrWhiteSpace(inline.MimeType) ? MimeTypes.ApplicationJson : inline.MimeType;
                contents.Add(new DataContent(bytes, mimeType) { RawRepresentation = part });
            }
            else if (part.FileData is { } fileData && !string.IsNullOrEmpty(fileData.FileUri) &&
                     Uri.TryCreate(fileData.FileUri, UriKind.RelativeOrAbsolute, out var fileUri))
            {
                var mimeType = string.IsNullOrWhiteSpace(fileData.MimeType) ? MimeTypes.ApplicationJson : fileData.MimeType;
                contents.Add(new UriContent(fileUri, mimeType) { RawRepresentation = part });
            }
        }

        return contents;
    }

    private static void ApplyChatOptions(GeminiRequest request, ChatOptions options)
    {
        if (options.Seed is { } rawSeed && (rawSeed < int.MinValue || rawSeed > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                rawSeed,
                $"ChatOptions.Seed ({rawSeed}) exceeds the 32-bit signed integer range supported by Gemini GenerationConfig.Seed.");
        }

        if (options.Temperature is not null ||
            options.TopP is not null ||
            options.TopK is not null ||
            options.MaxOutputTokens is not null ||
            options.StopSequences is { Count: > 0 } ||
            options.PresencePenalty is not null ||
            options.FrequencyPenalty is not null ||
            options.Seed is not null ||
            options.ResponseFormat is not null ||
            options.Reasoning is not null)
        {
            var config = request.GenerationConfig ?? new GenerationConfig();

            if (options.Temperature is { } temp) config.Temperature ??= temp;
            if (options.TopP is { } topP) config.TopP ??= topP;
            if (options.TopK is { } topK) config.TopK ??= topK;
            if (options.MaxOutputTokens is { } maxTokens) config.MaxOutputTokens ??= maxTokens;
            if (options.StopSequences is { Count: > 0 } stopSeq) config.StopSequences ??= [.. stopSeq];
            if (options.PresencePenalty is { } presence) config.PresencePenalty ??= presence;
            if (options.FrequencyPenalty is { } frequency) config.FrequencyPenalty ??= frequency;
            if (options.Seed is { } seed) config.Seed ??= (int)seed;

            if (config.ResponseMimeType is null)
            {
                if (options.ResponseFormat is ChatResponseFormatJson jsonFormat)
                {
                    config.ResponseMimeType = ResponseMimeType.ApplicationJson;
                    if (config.ResponseSchema is null && config.ResponseJsonSchema is null &&
                        jsonFormat.Schema is { ValueKind: JsonValueKind.Object } schemaElement)
                    {
                        config.ResponseJsonSchema = schemaElement.Clone();
                    }
                }
                else if (options.ResponseFormat is ChatResponseFormatText)
                {
                    config.ResponseMimeType = ResponseMimeType.TextPlain;
                }
            }

            if (options.Reasoning is { } reasoning)
            {
                var thinking = config.ThinkingConfig ?? new ThinkingConfig();
                if (thinking.ThinkingLevel is null && thinking.ThinkingBudget is null && reasoning.Effort is { } effort)
                {
                    thinking.ThinkingLevel = effort switch
                    {
                        ReasoningEffort.None => ThinkingLevel.Minimal,
                        ReasoningEffort.Low => ThinkingLevel.Low,
                        ReasoningEffort.Medium => ThinkingLevel.Medium,
                        ReasoningEffort.High or ReasoningEffort.ExtraHigh => ThinkingLevel.High,
                        _ => null
                    };
                }

                if (thinking.IncludeThoughts is null && reasoning.Output is { } output)
                {
                    thinking.IncludeThoughts = output switch
                    {
                        ReasoningOutput.None => false,
                        ReasoningOutput.Summary or ReasoningOutput.Full => true,
                        _ => null
                    };
                }

                if (thinking.ThinkingLevel is not null || thinking.ThinkingBudget is not null || thinking.IncludeThoughts is not null)
                {
                    config.ThinkingConfig = thinking;
                }
            }

            request.GenerationConfig = config;
        }

        if (request.Tools is null && options.Tools is { Count: > 0 })
        {
            var declarations = new List<FunctionDeclaration>();
            foreach (var tool in options.Tools)
            {
                if (tool is AIFunctionDeclaration af)
                {
                    declarations.Add(new FunctionDeclaration
                    {
                        Name = af.Name,
                        Description = string.IsNullOrEmpty(af.Description) ? null : af.Description,
                        ParametersJsonSchema = ExtractParametersJsonSchema(af.JsonSchema)
                    });
                }
            }

            if (declarations.Count > 0)
            {
                request.Tools = [new Tool { FunctionDeclarations = declarations }];
            }
        }

        if (request.ToolConfig is null && options.ToolMode is not null)
        {
            if (options.ToolMode is RequiredChatToolMode requiredMode)
            {
                request.ToolConfig = new ToolConfig
                {
                    FunctionCallingConfig = new FunctionCallingConfig
                    {
                        Mode = FunctionCallingMode.ANY,
                        AllowedFunctionNames = !string.IsNullOrWhiteSpace(requiredMode.RequiredFunctionName)
                            ? [requiredMode.RequiredFunctionName]
                            : null
                    }
                };
            }
            else if (options.ToolMode is NoneChatToolMode)
            {
                request.ToolConfig = new ToolConfig
                {
                    FunctionCallingConfig = new FunctionCallingConfig { Mode = FunctionCallingMode.NONE }
                };
            }
            else if (options.ToolMode is AutoChatToolMode)
            {
                request.ToolConfig = new ToolConfig
                {
                    FunctionCallingConfig = new FunctionCallingConfig { Mode = FunctionCallingMode.AUTO }
                };
            }
        }
    }

    private static JsonElement? ExtractParametersJsonSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (schema.TryGetProperty("properties", out var props))
        {
            if (props.ValueKind != JsonValueKind.Object || !props.EnumerateObject().MoveNext())
            {
                return null;
            }

            return schema.Clone();
        }

        // Non-trivial schema without a top-level "properties" (e.g., $ref / anyOf)
        if (schema.TryGetProperty("$ref", out _) || schema.TryGetProperty("anyOf", out _) || schema.TryGetProperty("oneOf", out _))
        {
            return schema.Clone();
        }

        return null;
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
                or FinishReason.ProhibitedContent or FinishReason.Spii or FinishReason.ImageSafety => ChatFinishReason.ContentFilter,
            FinishReason.MalformedFunctionCall or FinishReason.UnexpectedToolCall => ChatFinishReason.ToolCalls,
            _ => null
        };
    }

    private static UsageDetails? MapUsage(UsageMetadata? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var toolPromptTokens = usage.ToolUsePromptTokenCount ?? 0;
        var thoughtsTokens = usage.ThoughtsTokenCount ?? 0;

        return new UsageDetails
        {
            InputTokenCount = usage.PromptTokenCount + toolPromptTokens,
            OutputTokenCount = usage.CandidatesTokenCount + thoughtsTokens,
            TotalTokenCount = usage.TotalTokenCount,
            ReasoningTokenCount = usage.ThoughtsTokenCount,
            CachedInputTokenCount = usage.CachedContentTokenCount
        };
    }

    private static JsonElement? ConvertDictionaryToJsonElement(
        IDictionary<string, object?>? dictionary,
        JsonSerializerOptions? serializerOptions)
    {
        if (dictionary is null)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in dictionary)
            {
                writer.WritePropertyName(key);
                WriteJsonValue(writer, value, serializerOptions);
            }

            writer.WriteEndObject();
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return JsonElement.ParseValue(ref reader);
    }

    private static JsonElement ConvertResultToJsonElement(object? result, JsonSerializerOptions? serializerOptions)
    {
        if (result is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                return element.Clone();
            }

            var wrappedBuffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(wrappedBuffer))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("result");
                element.WriteTo(writer);
                writer.WriteEndObject();
            }

            var wrappedReader = new Utf8JsonReader(wrappedBuffer.WrittenSpan);
            return JsonElement.ParseValue(ref wrappedReader);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (result is IEnumerable<KeyValuePair<string, object?>> dict)
            {
                writer.WriteStartObject();
                foreach (var (key, value) in dict)
                {
                    writer.WritePropertyName(key);
                    WriteJsonValue(writer, value, serializerOptions);
                }

                writer.WriteEndObject();
            }
            else if (result is not null && TrySerializeComplexObject(result, serializerOptions, out var serializedElement) &&
                     serializedElement.ValueKind == JsonValueKind.Object)
            {
                return serializedElement;
            }
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("result");
                WriteJsonValue(writer, result, serializerOptions);
                writer.WriteEndObject();
            }
        }

        var docReader = new Utf8JsonReader(buffer.WrittenSpan);
        return JsonElement.ParseValue(ref docReader);
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions? serializerOptions)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement je:
                je.WriteTo(writer);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte u8:
                writer.WriteNumberValue(u8);
                break;
            case sbyte i8:
                writer.WriteNumberValue(i8);
                break;
            case short i16:
                writer.WriteNumberValue(i16);
                break;
            case ushort u16:
                writer.WriteNumberValue(u16);
                break;
            case int i32:
                writer.WriteNumberValue(i32);
                break;
            case uint u32:
                writer.WriteNumberValue(u32);
                break;
            case long i64:
                writer.WriteNumberValue(i64);
                break;
            case ulong u64:
                writer.WriteNumberValue(u64);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case Guid guid:
                writer.WriteStringValue(guid);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt);
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto);
                break;
            case DateOnly dateOnly:
                writer.WriteStringValue(dateOnly.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeOnly timeOnly:
                writer.WriteStringValue(timeOnly.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan ts:
                writer.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture));
                break;
            case Uri uri:
                writer.WriteStringValue(uri.ToString());
                break;
            case Enum enumValue:
                writer.WriteStringValue(enumValue.ToString());
                break;
            case IEnumerable<KeyValuePair<string, object?>> dict:
                writer.WriteStartObject();
                foreach (var (k, v) in dict)
                {
                    writer.WritePropertyName(k);
                    WriteJsonValue(writer, v, serializerOptions);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteJsonValue(writer, item, serializerOptions);
                }

                writer.WriteEndArray();
                break;
            default:
                if (TrySerializeComplexObject(value, serializerOptions, out var element))
                {
                    element.WriteTo(writer);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Cannot serialize tool value of type '{value.GetType().FullName}' without JSON type metadata. " +
                        $"Pass a JsonSerializerOptions configured with your JsonSerializerContext via GenerativeLanguageOptions.JsonSerializerOptions.");
                }

                break;
        }
    }

    private static bool TrySerializeComplexObject(
        object value,
        JsonSerializerOptions? serializerOptions,
        out JsonElement element)
    {
        var effectiveOptions = serializerOptions ?? AIJsonUtilities.DefaultOptions;
        if (effectiveOptions.TryGetTypeInfo(value.GetType(), out var typeInfo))
        {
            element = JsonSerializer.SerializeToElement(value, typeInfo);
            return true;
        }

        element = default;
        return false;
    }

    private static IDictionary<string, object?>? ConvertJsonElementToDictionary(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            result[prop.Name] = prop.Value.Clone();
        }

        return result;
    }
}
