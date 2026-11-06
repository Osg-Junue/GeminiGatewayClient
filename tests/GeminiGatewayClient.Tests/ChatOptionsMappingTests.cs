using System.Text.Json;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Tests;

/// <summary>
/// What a chat call sends and reports: each <see cref="ChatOptions"/> value on the wire, and each Gemini finish reason.
/// </summary>
public class ChatOptionsMappingTests
{
    private const string OkResponse = """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""";

    private static GeminiClient CreateClient(RecordingHandler handler) =>
        new(new GeminiClientOptions { ApiKey = "k", Model = "gemini-2.5-flash" }, new HttpClient(handler));

    private static async Task<JsonElement> SentBodyAsync(ChatOptions options)
    {
        using var handler = new RecordingHandler(OkResponse);
        using var client = CreateClient(handler);
        await client.AsIChatClient().GetResponseAsync("hi", options);
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task GenerationSettings_ReachTheGenerationConfig()
    {
        var body = await SentBodyAsync(new ChatOptions
        {
            Temperature = 0.5f,
            TopP = 0.25f,
            TopK = 7,
            MaxOutputTokens = 99,
            PresencePenalty = 0.5f,
            FrequencyPenalty = 0.75f,
            Seed = 42,
            StopSequences = ["END"]
        });

        var config = body.GetProperty("generationConfig");
        Assert.Equal(0.5, config.GetProperty("temperature").GetDouble());
        Assert.Equal(0.25, config.GetProperty("topP").GetDouble());
        Assert.Equal(7, config.GetProperty("topK").GetInt32());
        Assert.Equal(99, config.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(0.5, config.GetProperty("presencePenalty").GetDouble());
        Assert.Equal(0.75, config.GetProperty("frequencyPenalty").GetDouble());
        Assert.Equal(42, config.GetProperty("seed").GetInt32());
        Assert.Equal("END", Assert.Single(config.GetProperty("stopSequences").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task TextResponseFormat_AsksForPlainText()
    {
        var body = await SentBodyAsync(new ChatOptions { ResponseFormat = ChatResponseFormat.Text });

        Assert.Equal("text/plain", body.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
    }

    private static JsonElement ThinkingConfigOf(JsonElement body) => body.GetProperty("generationConfig").GetProperty("thinkingConfig");

    [Theory]
    [InlineData(ReasoningEffort.None, "MINIMAL")]
    [InlineData(ReasoningEffort.Low, "LOW")]
    [InlineData(ReasoningEffort.Medium, "MEDIUM")]
    [InlineData(ReasoningEffort.High, "HIGH")]
    [InlineData(ReasoningEffort.ExtraHigh, "HIGH")]
    public async Task ReasoningEffort_MapsToTheThinkingLevel(ReasoningEffort effort, string expected)
    {
        var body = await SentBodyAsync(new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } });

        var thinking = ThinkingConfigOf(body);
        Assert.Equal(expected, thinking.GetProperty("thinkingLevel").GetString());
        Assert.False(thinking.TryGetProperty("thinkingBudget", out _));
        Assert.False(thinking.TryGetProperty("includeThoughts", out _));
    }

    [Theory]
    [InlineData(ReasoningOutput.None, false)]
    [InlineData(ReasoningOutput.Summary, true)]
    [InlineData(ReasoningOutput.Full, true)]
    public async Task ReasoningOutput_DecidesWhetherThoughtsAreIncluded(ReasoningOutput output, bool expected)
    {
        var body = await SentBodyAsync(new ChatOptions { Reasoning = new ReasoningOptions { Output = output } });

        var thinking = ThinkingConfigOf(body);
        Assert.Equal(expected, thinking.GetProperty("includeThoughts").GetBoolean());
        Assert.False(thinking.TryGetProperty("thinkingLevel", out _));
    }

    [Fact]
    public async Task EmptyReasoningOptions_SendNoThinkingConfig()
    {
        var body = await SentBodyAsync(new ChatOptions { Reasoning = new ReasoningOptions() });

        Assert.False(body.TryGetProperty("generationConfig", out var config) && config.TryGetProperty("thinkingConfig", out _));
    }

    [Fact]
    public async Task RawThinkingConfig_WinsOverReasoning()
    {
        var body = await SentBodyAsync(new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low, Output = ReasoningOutput.Summary },
            RawRepresentationFactory = _ => new GenerateContentRequest
            {
                GenerationConfig = new GenerationConfig { ThinkingConfig = new ThinkingConfig { ThinkingLevel = ThinkingLevel.High, IncludeThoughts = false } }
            }
        });

        var thinking = ThinkingConfigOf(body);
        Assert.Equal("HIGH", thinking.GetProperty("thinkingLevel").GetString());
        Assert.False(thinking.GetProperty("includeThoughts").GetBoolean());
    }

    [Fact]
    public async Task RawThinkingBudget_IsNotJoinedByALevel()
    {
        var body = await SentBodyAsync(new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High, Output = ReasoningOutput.Full },
            RawRepresentationFactory = _ => new GenerateContentRequest
            {
                GenerationConfig = new GenerationConfig { ThinkingConfig = new ThinkingConfig { ThinkingBudget = 1024 } }
            }
        });

        var thinking = ThinkingConfigOf(body);
        Assert.Equal(1024, thinking.GetProperty("thinkingBudget").GetInt32());
        Assert.False(thinking.TryGetProperty("thinkingLevel", out _));
        Assert.True(thinking.GetProperty("includeThoughts").GetBoolean());
    }

    [Fact]
    public async Task RawRepresentation_WinsOverChatOptions()
    {
        var body = await SentBodyAsync(new ChatOptions
        {
            Temperature = 0.5f,
            StopSequences = ["OPTION"],
            ToolMode = ChatToolMode.RequireAny,
            RawRepresentationFactory = _ => new GenerateContentRequest
            {
                GenerationConfig = new GenerationConfig { Temperature = 1.0, StopSequences = ["RAW"] },
                ToolConfig = new ToolConfig { FunctionCallingConfig = new FunctionCallingConfig { Mode = FunctionCallingMode.None } }
            }
        });

        var config = body.GetProperty("generationConfig");
        Assert.Equal(1.0, config.GetProperty("temperature").GetDouble());
        Assert.Equal("RAW", Assert.Single(config.GetProperty("stopSequences").EnumerateArray()).GetString());
        Assert.Equal("NONE", body.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task ToolMode_FillsInAToolConfigThatLeavesItUnset()
    {
        using var flag = JsonDocument.Parse("true");

        var body = await SentBodyAsync(new ChatOptions
        {
            ToolMode = ChatToolMode.RequireAny,
            RawRepresentationFactory = _ => new GenerateContentRequest
            {
                ToolConfig = new ToolConfig { ExtensionData = new Dictionary<string, JsonElement> { ["includeServerSideToolInvocations"] = flag.RootElement.Clone() } }
            }
        });

        var toolConfig = body.GetProperty("toolConfig");
        Assert.Equal("ANY", toolConfig.GetProperty("functionCallingConfig").GetProperty("mode").GetString());
        Assert.True(toolConfig.GetProperty("includeServerSideToolInvocations").GetBoolean());
    }

    [Theory]
    [InlineData("""{"type":"object","additionalProperties":{"type":"string"}}""", "additionalProperties")]
    [InlineData("""{"type":"object","allOf":[{"properties":{"a":{"type":"string"}}}]}""", "allOf")]
    [InlineData("""{"$ref":"#/$defs/Input","$defs":{"Input":{"type":"object"}}}""", "$ref")]
    [InlineData("""{"type":"object"}""", "type")]
    public async Task FunctionSchema_WithoutTopLevelProperties_IsSentAsIs(string schema, string keyword)
    {
        using var document = JsonDocument.Parse(schema);

        var body = await SentBodyAsync(new ChatOptions { Tools = [AIFunctionFactory.CreateDeclaration("f", "d", document.RootElement)] });

        var declaration = body.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
        Assert.True(declaration.GetProperty("parametersJsonSchema").TryGetProperty(keyword, out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"type":"object","properties":{}}""")]
    public async Task FunctionSchema_OfAParameterlessFunction_IsLeftOut(string schema)
    {
        using var document = JsonDocument.Parse(schema);

        var body = await SentBodyAsync(new ChatOptions { Tools = [AIFunctionFactory.CreateDeclaration("f", "d", document.RootElement)] });

        Assert.False(body.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].TryGetProperty("parametersJsonSchema", out _));
    }

    [Fact]
    public async Task FunctionResult_WithoutItsCall_IsRejectedBeforeAnythingIsSent()
    {
        using var handler = new RecordingHandler(OkResponse);
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => client.AsIChatClient().GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "q"),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("srv-abc123", "sunny")])
        ]));

        Assert.Equal("messages", error.ParamName);
        Assert.Contains("srv-abc123", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("STOP", "stop")]
    [InlineData("MAX_TOKENS", "length")]
    [InlineData("SAFETY", "content_filter")]
    [InlineData("RECITATION", "content_filter")]
    [InlineData("BLOCKLIST", "content_filter")]
    [InlineData("PROHIBITED_CONTENT", "content_filter")]
    [InlineData("SPII", "content_filter")]
    [InlineData("IMAGE_SAFETY", "content_filter")]
    [InlineData("IMAGE_PROHIBITED_CONTENT", "content_filter")]
    [InlineData("IMAGE_RECITATION", "content_filter")]
    public async Task FinishReason_IsMappedTheSameWithAndWithoutStreaming(string wire, string expected)
    {
        var response = $$"""{"candidates":[{"content":{"role":"model","parts":[{"text":"x"}]},"finishReason":"{{wire}}"}]}""";
        using var buffered = new RecordingHandler(response);
        using var streamed = new RecordingHandler($"data: {response}\n\n", contentType: "text/event-stream");
        using var bufferedClient = CreateClient(buffered);
        using var streamedClient = CreateClient(streamed);

        Assert.Equal(expected, (await bufferedClient.AsIChatClient().GetResponseAsync("hi")).FinishReason?.Value);
        Assert.Equal(expected, (await streamedClient.AsIChatClient().GetStreamingResponseAsync("hi").ToChatResponseAsync()).FinishReason?.Value);
    }

    [Theory]
    [InlineData("OTHER")]
    [InlineData("LANGUAGE")]
    [InlineData("MALFORMED_FUNCTION_CALL")]
    public async Task FinishReason_WithoutAChatCounterpart_IsNull(string wire)
    {
        using var handler = new RecordingHandler($$"""{"candidates":[{"content":{"role":"model","parts":[{"text":"x"}]},"finishReason":"{{wire}}"}]}""");
        using var client = CreateClient(handler);

        Assert.Null((await client.AsIChatClient().GetResponseAsync("hi")).FinishReason);
    }
}
