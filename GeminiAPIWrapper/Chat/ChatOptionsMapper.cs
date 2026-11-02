using System.Text.Json;
using GeminiAPIWrapper.Models;
using Microsoft.Extensions.AI;

namespace GeminiAPIWrapper.Chat;

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

        request.GenerationConfig = config;
    }

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

        if (thinking is not { ThinkingLevel: null, ThinkingBudget: null, IncludeThoughts: null })
        {
            config.ThinkingConfig = thinking;
        }
    }

    private static void ApplyTools(GenerateContentRequest request, ChatOptions options)
    {
        if (request.Tools is not null || options.Tools is not { Count: > 0 } tools)
        {
            return;
        }

        var declarations = new List<FunctionDeclaration>(tools.Count);
        foreach (var tool in tools)
        {
            declarations.Add(tool switch
            {
                AIFunctionDeclaration function => new FunctionDeclaration
                {
                    Name = function.Name,
                    Description = string.IsNullOrEmpty(function.Description) ? null : function.Description,
                    ParametersJsonSchema = ExtractParametersJsonSchema(function.JsonSchema)
                },
                _ => throw new NotSupportedException(
                    $"ChatOptions.Tools contains '{tool.GetType().Name}' ('{tool.Name}'), which is not supported. " +
                    "Only AIFunctionDeclaration tools (for example AIFunctionFactory.Create) can be mapped to Gemini function declarations.")
            });
        }

        request.Tools = [new Tool { FunctionDeclarations = declarations }];
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
            return properties.ValueKind == JsonValueKind.Object && properties.EnumerateObject().Any()
                ? schema.Clone()
                : null;
        }

        // Non-trivial schema without top-level "properties" (for example $ref / anyOf / oneOf).
        return schema.TryGetProperty("$ref", out _) || schema.TryGetProperty("anyOf", out _) || schema.TryGetProperty("oneOf", out _)
            ? schema.Clone()
            : null;
    }
}

