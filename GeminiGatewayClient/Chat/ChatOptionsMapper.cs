using System.Text.Json;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Chat;

/// <summary>
/// Applies <see cref="ChatOptions"/> to a <see cref="GenerateContentRequest"/>. Values already present on the request
/// (from <see cref="ChatOptions.RawRepresentationFactory"/>) always win. Settings without a <see cref="ChatOptions"/>
/// counterpart are set through that factory, not <see cref="ChatOptions.AdditionalProperties"/>.
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
        if (options.Seed is < int.MinValue or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Seed,
                $"ChatOptions.Seed ({options.Seed}) exceeds the 32-bit signed integer range supported by Gemini GenerationConfig.Seed.");
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

        var config = request.GenerationConfig ??= new GenerationConfig();
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
                    config.ResponseJsonSchema = schema;
                }

                break;

            case ChatResponseFormatText:
                config.ResponseMimeType = MimeTypes.TextPlain;
                break;
        }
    }

    private static void ApplyReasoning(GenerationConfig config, ReasoningOptions reasoning, string? modelId)
    {
        var thinking = config.ThinkingConfig ?? new ThinkingConfig();

        if (thinking is { ThinkingLevel: null, ThinkingBudget: null } && reasoning.Effort is { } effort)
        {
            ReasoningEffortMapper.Apply(thinking, effort, modelId);
        }

        thinking.IncludeThoughts ??= reasoning.Output switch
        {
            ReasoningOutput.None => false,
            ReasoningOutput.Summary or ReasoningOutput.Full => true,
            _ => null
        };

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

        var existing = request.Tools?.Where(tool => tool is not null).ToList() ?? [];
        var declarations = new List<FunctionDeclaration>();
        var added = new List<Tool>();

        for (var i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            switch (tool)
            {
                case null:
                    throw new ArgumentException($"ChatOptions.Tools[{i}] cannot be null.", "options");

                case AIFunctionDeclaration function:
                    if (!existing.Any(t => t.FunctionDeclarations?.Any(d => d?.Name == function.Name) == true) &&
                        !declarations.Any(d => d.Name == function.Name))
                    {
                        declarations.Add(ToDeclaration(function));
                    }

                    break;

                case HostedWebSearchTool:
                    if (!existing.Concat(added).Any(t => t.GoogleSearch is not null))
                    {
                        added.Add(new Tool { GoogleSearch = new GoogleSearchTool() });
                    }

                    break;

                case HostedCodeInterpreterTool:
                    if (!existing.Concat(added).Any(t => t.CodeExecution is not null))
                    {
                        added.Add(new Tool { CodeExecution = new CodeExecutionTool() });
                    }

                    break;

                default:
                    throw new NotSupportedException(
                        $"ChatOptions.Tools contains '{tool.GetType().Name}' ('{tool.Name}'), which is not supported. " +
                        "Only AIFunctionDeclaration, HostedWebSearchTool, and HostedCodeInterpreterTool can be mapped to Gemini tools.");
            }
        }

        var sent = request.Tools?.ToList() ?? [];
        if (declarations.Count > 0)
        {
            var functionTool = sent.FindIndex(t => t?.FunctionDeclarations is not null);
            if (functionTool >= 0)
            {
                sent[functionTool] = sent[functionTool].WithDeclarations(declarations);
            }
            else
            {
                added.Insert(0, new Tool { FunctionDeclarations = declarations });
            }
        }

        request.Tools = [.. sent, .. added];
    }

    private static FunctionDeclaration ToDeclaration(AIFunctionDeclaration function) => new()
    {
        Name = function.Name,
        Description = string.IsNullOrEmpty(function.Description) ? null : function.Description,
        ParametersJsonSchema = ToParametersSchema(function.JsonSchema),
        ResponseJsonSchema = function.ReturnJsonSchema is { ValueKind: JsonValueKind.Object } schema && schema.EnumerateObject().Any() ? schema : null
    };

    /// <summary>
    /// Returns the JSON schema of a function's parameters, or <c>null</c> for parameterless functions.
    /// </summary>
    private static JsonElement? ToParametersSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            return properties.ValueKind == JsonValueKind.Object && properties.EnumerateObject().Any() ? schema : null;
        }

        // Non-trivial schema without top-level "properties" (for example $ref / anyOf / oneOf).
        return schema.TryGetProperty("$ref", out _) || schema.TryGetProperty("anyOf", out _) || schema.TryGetProperty("oneOf", out _)
            ? schema
            : null;
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
}
