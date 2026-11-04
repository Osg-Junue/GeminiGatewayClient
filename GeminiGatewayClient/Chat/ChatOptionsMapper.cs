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
    public static void Apply(GenerateContentRequest request, ChatOptions options, string? modelId)
    {
        ApplyGenerationConfig(request, options, modelId);
        ApplyTools(request, options);
        ApplyToolMode(request, options);
    }

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

                if (existingFunctionTool is null && existingTool.FunctionDeclarations is not null)
                {
                    existingFunctionTool = existingTool;
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
            if (existingFunctionTool?.FunctionDeclarations is { } existingDecls)
            {
                if (existingDecls is List<FunctionDeclaration> declList)
                {
                    declList.AddRange(declarations);
                }
                else if (existingDecls.IsReadOnly)
                {
                    existingFunctionTool.FunctionDeclarations = [.. existingDecls, .. declarations];
                }
                else
                {
                    foreach (var decl in declarations)
                    {
                        existingDecls.Add(decl);
                    }
                }
            }
            else
            {
                var functionTool = new Tool { FunctionDeclarations = declarations };
                if (mappedTools is null)
                {
                    mappedTools = [functionTool];
                }
                else
                {
                    mappedTools.Insert(0, functionTool);
                }
            }
        }

        if (mappedTools is { Count: > 0 })
        {
            if (existingTools is null)
            {
                request.Tools = mappedTools;
            }
            else if (existingTools is List<Tool> toolList)
            {
                toolList.AddRange(mappedTools);
            }
            else if (existingTools.IsReadOnly)
            {
                request.Tools = [.. existingTools, .. mappedTools];
            }
            else
            {
                foreach (var mappedTool in mappedTools)
                {
                    existingTools.Add(mappedTool);
                }
            }
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
