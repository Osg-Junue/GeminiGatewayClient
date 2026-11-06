using System.Text.Json;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Tests;

/// <summary>
/// Citations, tool modes, embeddings, and history handling, checked through the public API.
/// </summary>
public class ChatFeatureTests
{
    private const string OkResponse = """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""";

    private static GeminiClient CreateClient(RecordingHandler handler) =>
        new(new GeminiClientOptions { ApiKey = "test-key", Model = "gemini-2.5-flash", EmbeddingModel = "gemini-embedding-001" }, new HttpClient(handler));

    private static string Grounded(string parts, int start, int end) =>
        "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" + parts + "]},\"finishReason\":\"STOP\",\"groundingMetadata\":{" +
        "\"groundingChunks\":[{\"web\":{\"uri\":\"https://example.com\",\"title\":\"ex\"}}]," +
        "\"groundingSupports\":[{\"segment\":{\"startIndex\":" + start + ",\"endIndex\":" + end + ",\"text\":\"s\"},\"groundingChunkIndices\":[0]}]}}]}";

    private static TextSpanAnnotatedRegion RegionOf(AIContent content) =>
        Assert.IsType<TextSpanAnnotatedRegion>(Assert.Single(Assert.IsType<CitationAnnotation>(Assert.Single(content.Annotations!)).AnnotatedRegions!));

    [Theory]
    [InlineData("こんにちは世界", 15, 21, 5, 7)]
    [InlineData("🌸a", 4, 5, 2, 3)]
    [InlineData("ab", 0, 2, 0, 2)]
    public async Task Citation_OnASingleText_ConvertsUtf8OffsetsToUtf16(string text, int startByte, int endByte, int start, int end)
    {
        using var handler = new RecordingHandler(Grounded("{\"text\":\"" + text + "\"}", startByte, endByte));

        var response = await CreateClient(handler).AsIChatClient().GetResponseAsync("hi");

        var region = RegionOf(Assert.IsType<TextContent>(Assert.Single(response.Messages[0].Contents)));
        Assert.Equal((start, end), (region.StartIndex, region.EndIndex));
    }

    [Theory]
    [InlineData("citationSources")]
    [InlineData("citations")]
    public async Task CitationMetadata_IsReadUnderBothApiNames(string key)
    {
        var body = "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"日本語\"}]},\"finishReason\":\"STOP\",\"citationMetadata\":{\"" + key +
                   "\":[{\"startIndex\":3,\"endIndex\":6,\"uri\":\"https://example.com/a\"}]}}]}";
        using var handler = new RecordingHandler(body);

        var response = await CreateClient(handler).AsIChatClient().GetResponseAsync("hi");

        var region = RegionOf(Assert.Single(response.Messages[0].Contents));
        Assert.Equal((1, 2), (region.StartIndex, region.EndIndex));
    }

    [Fact]
    public async Task Usage_WithoutATotal_IsSummed()
    {
        using var handler = new RecordingHandler("""{"candidates":[{"content":{"parts":[{"text":"a"}]}}],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":2}}""");

        var response = await CreateClient(handler).AsIChatClient().GetResponseAsync("hi");

        Assert.Equal(12, response.Usage?.TotalTokenCount);
    }

    [Fact]
    public async Task FileData_WithoutAMediaType_IsResentWithoutOne()
    {
        using var first = new RecordingHandler("""{"candidates":[{"content":{"role":"model","parts":[{"fileData":{"fileUri":"https://example.com/a"}}]}}]}""");
        var response = await CreateClient(first).AsIChatClient().GetResponseAsync("hi");

        using var second = new RecordingHandler(OkResponse);
        await CreateClient(second).AsIChatClient().GetResponseAsync([.. response.Messages, new ChatMessage(ChatRole.User, "next")]);

        using var body = JsonDocument.Parse(second.LastRequestBody!);
        Assert.False(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("fileData").TryGetProperty("mimeType", out _));
    }

    [Fact]
    public async Task Citation_OverSeveralTexts_CountsFromTheStartOfTheResponse()
    {
        using var handler = new RecordingHandler(Grounded("""{"text":"日本語"},{"text":"東京"}""", 9, 15));

        var response = await CreateClient(handler).AsIChatClient().GetResponseAsync("hi");

        var region = RegionOf(response.Messages[0].Contents[^1]);
        Assert.Equal("東京", response.Text[region.StartIndex!.Value..region.EndIndex!.Value]);
    }

    [Fact]
    public async Task Citation_InAStream_ArrivesWithItsChunkAndCountsFromTheStartOfTheResponse()
    {
        var first = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"日本語\"}]}}]}\n\n";
        var last = "data: " + Grounded("""{"text":"東京"}""", 9, 15) + "\n\n";
        using var handler = new RecordingHandler(first + last);
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in CreateClient(handler).AsIChatClient().GetStreamingResponseAsync("hi"))
        {
            updates.Add(update);
        }

        Assert.All(updates[0].Contents, content => Assert.Null(content.Annotations));
        var region = RegionOf(updates[1].Contents[^1]);
        Assert.Equal("東京", "日本語東京"[region.StartIndex!.Value..region.EndIndex!.Value]);
    }

    [Theory]
    [InlineData("auto", "AUTO")]
    [InlineData("none", "NONE")]
    [InlineData("any", "ANY")]
    public async Task ToolMode_MapsToTheFunctionCallingMode(string mode, string expected)
    {
        using var handler = new RecordingHandler(OkResponse);
        ChatToolMode toolMode = mode switch { "auto" => ChatToolMode.Auto, "none" => ChatToolMode.None, _ => ChatToolMode.RequireAny };

        await CreateClient(handler).AsIChatClient().GetResponseAsync("hi", new ChatOptions { ToolMode = toolMode });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(expected, body.RootElement.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task SignatureOnlyPart_SurvivesPersistedHistoryAsAPartWithData()
    {
        using var first = new RecordingHandler("""{"candidates":[{"content":{"role":"model","parts":[{"text":"a"},{"text":"","thoughtSignature":"U0lH"}]},"finishReason":"STOP"}]}""");
        var response = await CreateClient(first).AsIChatClient().GetResponseAsync("hi");
        var json = JsonSerializer.Serialize(response.Messages, AIJsonUtilities.DefaultOptions);
        var restored = JsonSerializer.Deserialize<List<ChatMessage>>(json, AIJsonUtilities.DefaultOptions)!;

        using var second = new RecordingHandler(OkResponse);
        await CreateClient(second).AsIChatClient().GetResponseAsync([.. restored, new ChatMessage(ChatRole.User, "next")]);

        using var body = JsonDocument.Parse(second.LastRequestBody!);
        var signaturePart = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[1];
        Assert.Equal("U0lH", signaturePart.GetProperty("thoughtSignature").GetString());
        Assert.Equal(string.Empty, signaturePart.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToModelContentAsync_KeepsPartBoundariesAndDropsPlaceholders()
    {
        const string sse = """
            data: {"candidates":[{"content":{"role":"model","parts":[{"text":"a"}]}}]}

            data: {"candidates":[{"content":{"role":"model","parts":[{"text":""}]}}]}

            data: {"candidates":[{"content":{"role":"model","parts":[{"text":"","thoughtSignature":"U0lH"}]}}]}
            """;
        using var handler = new RecordingHandler(sse.Replace("\r\n", "\n"));

        var content = await CreateClient(handler).StreamGenerateContentAsync(new GenerateContentRequest { Contents = [Content.FromText("hi")] }).ToModelContentAsync();

        Assert.Equal(Role.Model, content!.Role);
        Assert.Equal(2, content.Parts.Count);
        Assert.Equal("U0lH", content.Parts[1].ThoughtSignature);
    }

    [Theory]
    [InlineData("""{"executableCode":{"code":"1","language":"PYTHON","id":"x"}}""", "executableCode")]
    [InlineData("""{"codeExecutionResult":{"outcome":"OUTCOME_OK","output":"1","id":"x"}}""", "codeExecutionResult")]
    [InlineData("""{"inlineData":{"mimeType":"image/png","data":"AAEC","id":"x"}}""", "inlineData")]
    [InlineData("""{"fileData":{"mimeType":"image/png","fileUri":"https://example.com/a","id":"x"}}""", "fileData")]
    public async Task UnmodeledFieldsInsideAPart_ReachTheNextTurn(string part, string field)
    {
        using var first = new RecordingHandler("{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" + part + "]}}]}");
        var response = await CreateClient(first).AsIChatClient().GetResponseAsync("hi");

        using var second = new RecordingHandler(OkResponse);
        await CreateClient(second).AsIChatClient().GetResponseAsync([.. response.Messages, new ChatMessage(ChatRole.User, "next")]);

        using var body = JsonDocument.Parse(second.LastRequestBody!);
        Assert.Equal("x", body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty(field).GetProperty("id").GetString());
    }

    [Fact]
    public void NonObjectToolResult_IsWrappedWithoutTheCallersKeyPolicy()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseUpper };

        var element = ToolJsonConverter.ToResponseObject("sunny", options);

        Assert.Equal("""{"result":"sunny"}""", element.GetRawText());
    }

    [Fact]
    public async Task Embeddings_UseTheBatchEndpointWithSharedSettings()
    {
        using var handler = new RecordingHandler("""{"embeddings":[{"values":[0.5]},{"values":[0.25]}]}""");
        var generator = CreateClient(handler).AsIEmbeddingGenerator();

        var result = await generator.GenerateAsync(["a", "b"], new EmbeddingGenerationOptions
        {
            RawRepresentationFactory = _ => new EmbedContentRequest { TaskType = TaskType.RetrievalDocument }
        });

        Assert.Equal(2, result.Count);
        Assert.EndsWith("models/gemini-embedding-001:batchEmbedContents", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        foreach (var request in body.RootElement.GetProperty("requests").EnumerateArray())
        {
            Assert.Equal("models/gemini-embedding-001", request.GetProperty("model").GetString());
            Assert.Equal("RETRIEVAL_DOCUMENT", request.GetProperty("taskType").GetString());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Embeddings_ModelIdWinsOverTheTemplate_WhateverTheInputCount(int inputs)
    {
        var embeddings = string.Join(",", Enumerable.Repeat("""{"values":[0.5]}""", inputs));
        using var handler = new RecordingHandler("{\"embeddings\":[" + embeddings + "]}");

        await CreateClient(handler).AsIEmbeddingGenerator().GenerateAsync(Enumerable.Repeat("a", inputs), new EmbeddingGenerationOptions
        {
            ModelId = "chosen",
            RawRepresentationFactory = _ => new EmbedContentRequest { Model = "ignored" }
        });

        Assert.Contains("models/chosen:", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }
}
