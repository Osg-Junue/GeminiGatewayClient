using System.Text.Json;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Applies <see cref="ChatOptions"/> to a <see cref="GenerateContentRequest"/>. Values already present on the request
/// (from <see cref="ChatOptions.RawRepresentationFactory"/>) always win.
/// </summary>
internal static class ChatOptionsMapper
{
    public static void Apply(
        GenerateContentRequest request,
        ChatOptions options,
        string? modelId,
        JsonSerializerOptions? serializerOptions = null)
    {
        ApplyGenerationConfig(request, options, modelId);
        ApplyTools(request, options);
        ApplyToolMode(request, options);
        ApplyAdditionalProperties(request, options, serializerOptions);
    }

    private static void ApplyAdditionalProperties(
        GenerateContentRequest request,
        ChatOptions options,
        JsonSerializerOptions? serializerOptions)
    {
        if (options.AdditionalProperties is not { Count: > 0 } properties)
        {
            return;
        }

        var effectiveSerializerOptions = serializerOptions ?? AIJsonUtilities.DefaultOptions;
        GenerationConfig? config = null;

        GenerationConfig EnsureConfig() => config ??= request.GenerationConfig?.Clone() ?? new GenerationConfig();

        foreach (var (key, value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                continue;
            }

            switch (key)
            {
                case "cachedContent":
                    if (request.CachedContent is null && TryGetString(value) is { } cachedContent)
                    {
                        request.CachedContent = cachedContent;
                    }

                    break;

                case "store":
                    if (request.Store is null && TryGetBoolean(value) is { } store)
                    {
                        request.Store = store;
                    }

                    break;

                case "labels":
                    if (request.Labels is null && TryGetStringDictionary(value) is { } labels)
                    {
                        request.Labels = labels;
                    }

                    break;

                case "candidateCount":
                    if (TryGetInt32(value) is { } candidateCount)
                    {
                        EnsureConfig().CandidateCount ??= candidateCount;
                    }

                    break;

                case "responseLogprobs":
                    if (TryGetBoolean(value) is { } responseLogprobs)
                    {
                        EnsureConfig().ResponseLogprobs ??= responseLogprobs;
                    }

                    break;

                case "logprobs":
                    if (TryGetInt32(value) is { } logprobs)
                    {
                        EnsureConfig().Logprobs ??= logprobs;
                    }

                    break;

                case "enableEnhancedCivicAnswers":
                    if (TryGetBoolean(value) is { } civicAnswers)
                    {
                        EnsureConfig().EnableEnhancedCivicAnswers ??= civicAnswers;
                    }

                    break;

                case "audioTimestamp":
                    if (TryGetBoolean(value) is { } audioTimestamp)
                    {
                        EnsureConfig().AudioTimestamp ??= audioTimestamp;
                    }

                    break;

                case "mediaResolution":
                    if (TryGetMediaResolution(value) is { } mediaResolution)
                    {
                        EnsureConfig().MediaResolution ??= mediaResolution;
                    }

                    break;

                case "responseModalities":
                    if (TryGetStringList(value) is { } modalities)
                    {
                        EnsureConfig().ResponseModalities ??= modalities;
                    }

                    break;

                case "temperature":
                    if (TryGetDouble(value) is { } temperature)
                    {
                        EnsureConfig().Temperature ??= temperature;
                    }

                    break;

                case "topP":
                    if (TryGetDouble(value) is { } topP)
                    {
                        EnsureConfig().TopP ??= topP;
                    }

                    break;

                case "topK":
                    if (TryGetInt32(value) is { } topK)
                    {
                        EnsureConfig().TopK ??= topK;
                    }

                    break;

                case "maxOutputTokens":
                    if (TryGetInt32(value) is { } maxOutputTokens)
                    {
                        EnsureConfig().MaxOutputTokens ??= maxOutputTokens;
                    }

                    break;

                case "presencePenalty":
                    if (TryGetDouble(value) is { } presencePenalty)
                    {
                        EnsureConfig().PresencePenalty ??= presencePenalty;
                    }

                    break;

                case "frequencyPenalty":
                    if (TryGetDouble(value) is { } frequencyPenalty)
                    {
                        EnsureConfig().FrequencyPenalty ??= frequencyPenalty;
                    }

                    break;

                case "seed":
                    if (TryGetInt32(value) is { } seed)
                    {
                        EnsureConfig().Seed ??= seed;
                    }

                    break;

                case "stopSequences":
                    if (TryGetStringList(value) is { } stopSequences)
                    {
                        EnsureConfig().StopSequences ??= stopSequences;
                    }

                    break;

                case "responseMimeType":
                    if (TryGetString(value) is { } responseMimeType)
                    {
                        EnsureConfig().ResponseMimeType ??= responseMimeType;
                    }

                    break;

                case "responseSchema":
                    if (value is Schema schema)
                    {
                        var targetConfig = EnsureConfig();
                        if (targetConfig.ResponseJsonSchema is null)
                        {
                            targetConfig.ResponseSchema ??= schema.Clone();
                        }
                    }

                    break;

                case "responseJsonSchema":
                {
                    var targetConfig = EnsureConfig();
                    if (targetConfig.ResponseSchema is null && targetConfig.ResponseJsonSchema is null)
                    {
                        targetConfig.ResponseJsonSchema = ToolJsonConverter.ToJsonElement(value, effectiveSerializerOptions);
                    }

                    break;
                }

                case "thinkingConfig":
                    if (value is ThinkingConfig thinkingConfig)
                    {
                        EnsureConfig().ThinkingConfig ??= thinkingConfig.Clone();
                    }

                    break;

                case "speechConfig" or "imageConfig":
                {
                    var targetConfig = EnsureConfig();
                    var ext = targetConfig.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    if (!ext.ContainsKey(key))
                    {
                        ext[key] = ToolJsonConverter.ToJsonElement(value, effectiveSerializerOptions);
                    }

                    break;
                }

                case "contents" or "systemInstruction" or "generationConfig" or "tools" or "toolConfig":
                    break;

                case "safetySettings" when request.SafetySettings is not null:
                    break;

                default:
                {
                    var ext = request.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    if (!ext.ContainsKey(key))
                    {
                        ext[key] = ToolJsonConverter.ToJsonElement(value, effectiveSerializerOptions);
                    }

                    break;
                }
            }
        }

        if (config is not null && (request.GenerationConfig is not null || !IsEmpty(config)))
        {
            request.GenerationConfig = config;
        }
    }

    private static int? TryGetInt32(object value) =>
        value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            short s => s,
            sbyte sb => sb,
            byte b => b,
            ushort us => us,
            uint u when u <= int.MaxValue => (int)u,
            JsonElement { ValueKind: JsonValueKind.Number } el when el.TryGetInt32(out var parsed) => parsed,
            _ => null
        };

    private static double? TryGetDouble(object value) =>
        value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            JsonElement { ValueKind: JsonValueKind.Number } el when el.TryGetDouble(out var parsed) => parsed,
            _ => null
        };

    private static bool? TryGetBoolean(object value) =>
        value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null
        };

    private static string? TryGetString(object value) =>
        value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => null
        };

    private static List<string>? TryGetStringList(object value)
    {
        switch (value)
        {
            case IEnumerable<string> strings:
                return [.. strings];

            case JsonElement { ValueKind: JsonValueKind.Array } array:
            {
                var result = new List<string>(array.GetArrayLength());
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    {
                        result.Add(s);
                    }
                }

                return result;
            }

            default:
                return null;
        }
    }

    private static Dictionary<string, string>? TryGetStringDictionary(object value)
    {
        switch (value)
        {
            case IEnumerable<KeyValuePair<string, string>> pairs:
            {
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in pairs)
                {
                    dict[k] = v;
                }

                return dict;
            }

            case JsonElement { ValueKind: JsonValueKind.Object } obj:
            {
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in obj.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } s)
                    {
                        dict[prop.Name] = s;
                    }
                }

                return dict;
            }

            default:
                return null;
        }
    }

    private static MediaResolution? TryGetMediaResolution(object value) =>
        value switch
        {
            MediaResolution mr => mr,
            string s => ParseMediaResolutionString(s),
            JsonElement { ValueKind: JsonValueKind.String } el when el.GetString() is { } s => ParseMediaResolutionString(s),
            _ => null
        };

    private static MediaResolution? ParseMediaResolutionString(string s) =>
        s switch
        {
            _ when s.Equals("MEDIA_RESOLUTION_LOW", StringComparison.OrdinalIgnoreCase) || s.Equals("Low", StringComparison.OrdinalIgnoreCase) => MediaResolution.Low,
            _ when s.Equals("MEDIA_RESOLUTION_MEDIUM", StringComparison.OrdinalIgnoreCase) || s.Equals("Medium", StringComparison.OrdinalIgnoreCase) => MediaResolution.Medium,
            _ when s.Equals("MEDIA_RESOLUTION_HIGH", StringComparison.OrdinalIgnoreCase) || s.Equals("High", StringComparison.OrdinalIgnoreCase) => MediaResolution.High,
            _ when s.Equals("MEDIA_RESOLUTION_UNSPECIFIED", StringComparison.OrdinalIgnoreCase) || s.Equals("Unspecified", StringComparison.OrdinalIgnoreCase) => MediaResolution.Unspecified,
            _ => null
        };

    private static void ApplyGenerationConfig(GenerateContentRequest request, ChatOptions options, string? modelId)
    {
        if (options.Seed is { } rawSeed && (rawSeed < int.MinValue || rawSeed > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                rawSeed,
                $"ChatOptions.Seed ({rawSeed}) exceeds the 32-bit signed integer range supported by Gemini GenerationConfig.Seed.");
        }

        if (options is
            {
                Temperature: null, TopP: null, TopK: null, MaxOutputTokens: null, PresencePenalty: null,
                FrequencyPenalty: null, Seed: null, ResponseFormat: null, Reasoning: null
            } &&
            options.StopSequences is not { Count: > 0 })
        {
            return;
        }

        // Clone so a template object shared across calls via RawRepresentationFactory is never mutated.
        var config = request.GenerationConfig?.Clone() ?? new GenerationConfig();

        config.Temperature ??= options.Temperature;
        config.TopP ??= options.TopP;
        config.TopK ??= options.TopK;
        config.MaxOutputTokens ??= options.MaxOutputTokens;
        config.PresencePenalty ??= options.PresencePenalty;
        config.FrequencyPenalty ??= options.FrequencyPenalty;
        config.Seed ??= (int?)options.Seed;
        if (options.StopSequences is { Count: > 0 } stopSequences)
        {
            config.StopSequences ??= [.. stopSequences];
        }

        ApplyResponseFormat(config, options.ResponseFormat);

        if (options.Reasoning is { } reasoning)
        {
            ApplyReasoning(config, reasoning, modelId);
        }

        if (request.GenerationConfig is not null || !IsEmpty(config))
        {
            request.GenerationConfig = config;
        }
    }

    private static bool IsEmpty(GenerationConfig config) =>
        config is
        {
            Temperature: null,
            TopP: null,
            TopK: null,
            MaxOutputTokens: null,
            CandidateCount: null,
            StopSequences: null,
            PresencePenalty: null,
            FrequencyPenalty: null,
            ResponseMimeType: null,
            ResponseSchema: null,
            ResponseJsonSchema: null,
            ThinkingConfig: null,
            Seed: null,
            ResponseLogprobs: null,
            Logprobs: null,
            EnableEnhancedCivicAnswers: null,
            AudioTimestamp: null,
            ResponseModalities: null,
            MediaResolution: null,
            ExtensionData: null
        };

    private static void ApplyResponseFormat(GenerationConfig config, ChatResponseFormat? format)
    {
        if (config.ResponseMimeType is not null)
        {
            return;
        }

        switch (format)
        {
            case ChatResponseFormatJson json:
                config.ResponseMimeType = MimeTypes.ApplicationJson;
                if (config.ResponseSchema is null && config.ResponseJsonSchema is null &&
                    json.Schema is { ValueKind: JsonValueKind.Object } schema)
                {
                    config.ResponseJsonSchema = schema.Clone();
                }

                break;

            case ChatResponseFormatText:
                config.ResponseMimeType = MimeTypes.TextPlain;
                break;
        }
    }

    private static void ApplyReasoning(GenerationConfig config, ReasoningOptions reasoning, string? modelId)
    {
        var thinking = config.ThinkingConfig?.Clone() ?? new ThinkingConfig();

        if (thinking is { ThinkingLevel: null, ThinkingBudget: null } && reasoning.Effort is { } effort)
        {
            ReasoningEffortMapper.Apply(thinking, effort, modelId);
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

        if (thinking is not { ThinkingLevel: null, ThinkingBudget: null, IncludeThoughts: null, ExtensionData: null })
        {
            config.ThinkingConfig = thinking;
        }
    }

    private static void ApplyTools(GenerateContentRequest request, ChatOptions options)
    {
        if (options.Tools is not { Count: > 0 } tools)
        {
            return;
        }

        List<FunctionDeclaration>? declarations = null;
        List<Tool>? mappedTools = null;

        var existingTools = request.Tools;
        var hasExistingGoogleSearch = false;
        var hasExistingCodeExecution = false;
        Tool? existingFunctionTool = null;

        if (existingTools is { Count: > 0 })
        {
            for (var i = 0; i < existingTools.Count; i++)
            {
                if (existingTools[i] is not { } existingTool)
                {
                    continue;
                }

                if (existingTool.GoogleSearch is not null)
                {
                    hasExistingGoogleSearch = true;
                }

                if (existingTool.CodeExecution is not null)
                {
                    hasExistingCodeExecution = true;
                }

                if (existingTool.FunctionDeclarations is not null)
                {
                    existingFunctionTool ??= existingTool;
                }
            }
        }

        foreach (var tool in tools)
        {
            switch (tool)
            {
                case AIFunctionDeclaration function:
                    if (existingTools is { Count: > 0 } && ContainsFunctionDeclaration(existingTools, function.Name))
                    {
                        break;
                    }

                    declarations ??= new List<FunctionDeclaration>(tools.Count);
                    declarations.Add(new FunctionDeclaration
                    {
                        Name = function.Name,
                        Description = string.IsNullOrEmpty(function.Description) ? null : function.Description,
                        ParametersJsonSchema = ExtractParametersJsonSchema(function.JsonSchema),
                        ResponseJsonSchema = ExtractResponseJsonSchema(function.ReturnJsonSchema)
                    });
                    break;

                case HostedWebSearchTool:
                    if (!hasExistingGoogleSearch)
                    {
                        (mappedTools ??= new List<Tool>(2)).Add(new Tool { GoogleSearch = new GoogleSearchTool() });
                        hasExistingGoogleSearch = true;
                    }

                    break;

                case HostedCodeInterpreterTool:
                    if (!hasExistingCodeExecution)
                    {
                        (mappedTools ??= new List<Tool>(2)).Add(new Tool { CodeExecution = new CodeExecutionTool() });
                        hasExistingCodeExecution = true;
                    }

                    break;

                default:
                    throw new NotSupportedException(
                        $"ChatOptions.Tools contains '{tool.GetType().Name}' ('{tool.Name}'), which is not supported. " +
                        "Only AIFunctionDeclaration, HostedWebSearchTool, and HostedCodeInterpreterTool can be mapped to Gemini tools.");
            }
        }

        if (declarations is { Count: > 0 })
        {
            if (existingFunctionTool?.FunctionDeclarations is { } existingDeclarations)
            {
                existingFunctionTool.FunctionDeclarations = [.. existingDeclarations, .. declarations];
            }
            else
            {
                (mappedTools ??= []).Insert(0, new Tool { FunctionDeclarations = declarations });
            }
        }

        if (mappedTools is { Count: > 0 })
        {
            request.Tools = [.. existingTools ?? [], .. mappedTools];
        }
    }

    private static bool ContainsFunctionDeclaration(IList<Tool> tools, string name)
    {
        for (var i = 0; i < tools.Count; i++)
        {
            if (tools[i]?.FunctionDeclarations is not { Count: > 0 } decls)
            {
                continue;
            }

            for (var j = 0; j < decls.Count; j++)
            {
                if (decls[j] is { } decl && string.Equals(decl.Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void ApplyToolMode(GenerateContentRequest request, ChatOptions options)
    {
        if (request.ToolConfig is not null)
        {
            return;
        }

        var callingConfig = options.ToolMode switch
        {
            RequiredChatToolMode required => new FunctionCallingConfig
            {
                Mode = FunctionCallingMode.Any,
                AllowedFunctionNames = string.IsNullOrWhiteSpace(required.RequiredFunctionName) ? null : [required.RequiredFunctionName]
            },
            NoneChatToolMode => new FunctionCallingConfig { Mode = FunctionCallingMode.None },
            AutoChatToolMode => new FunctionCallingConfig { Mode = FunctionCallingMode.Auto },
            _ => null
        };

        if (callingConfig is not null)
        {
            request.ToolConfig = new ToolConfig { FunctionCallingConfig = callingConfig };
        }
    }

    /// <summary>
    /// Returns the JSON schema of a function's parameters, or <c>null</c> for parameterless functions
    /// (Gemini rejects an empty <c>properties</c> object).
    /// </summary>
    private static JsonElement? ExtractParametersJsonSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            return properties.ValueKind == JsonValueKind.Object && properties.EnumerateObject().MoveNext()
                ? schema.Clone()
                : null;
        }

        // Non-trivial schema without top-level "properties" (for example $ref / anyOf / oneOf).
        return schema.TryGetProperty("$ref", out _) || schema.TryGetProperty("anyOf", out _) || schema.TryGetProperty("oneOf", out _)
            ? schema.Clone()
            : null;
    }

    /// <summary>
    /// Returns the JSON schema of a function's return value, or <c>null</c> when no non-empty object schema is defined.
    /// </summary>
    private static JsonElement? ExtractResponseJsonSchema(JsonElement? returnSchema) =>
        returnSchema is { ValueKind: JsonValueKind.Object } schema && schema.EnumerateObject().MoveNext()
            ? schema.Clone()
            : null;
}
