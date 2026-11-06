using System.Text.Json;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

namespace GeminiGatewayClient.Tests;

/// <summary>
/// Where a request goes and what the client shows of that address.
/// </summary>
public class EndpointAndModelTests
{
    [Fact]
    public async Task EmbedContent_ReservedCharacterInTheModel_IsEscapedOnlyInTheUrl()
    {
        using var handler = new RecordingHandler("""{"embedding":{"values":[1]}}""");
        using var client = new GeminiClient(new GeminiClientOptions(), new HttpClient(handler));

        await client.EmbedContentAsync("x", model: "text-embedding@001");

        Assert.Contains("/models/text-embedding%40001:embedContent", handler.LastRequest!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("models/text-embedding@001", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task BatchEmbedContents_ReservedCharacterInTheModel_IsEscapedOnlyInTheUrl()
    {
        using var handler = new RecordingHandler("""{"embeddings":[{"values":[1]}]}""");
        using var client = new GeminiClient(new GeminiClientOptions(), new HttpClient(handler));

        await client.BatchEmbedContentsAsync(new BatchEmbedContentsRequest { Requests = [EmbedContentRequest.FromText("x")] }, "text-embedding@001");

        Assert.Contains("/models/text-embedding%40001:batchEmbedContents", handler.LastRequest!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("models/text-embedding@001", body.RootElement.GetProperty("requests")[0].GetProperty("model").GetString());
    }

    [Fact]
    public async Task Endpoint_CredentialsInTheAddress_AreSentButNeverShown()
    {
        using var handler = new RecordingHandler("""{"candidates":[]}""");
        var options = new GeminiClientOptions { Endpoint = new Uri("https://user:password@gw.example:8443/v1beta/?key=SECRET"), Model = "m" };
        using var client = new GeminiClient(options, new HttpClient(handler));
        var shown = new Uri("https://gw.example:8443/v1beta");

        await client.GenerateContentAsync("hi");

        Assert.Contains("key=SECRET", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal(shown, client.Endpoint);
        Assert.Equal(shown, client.AsIChatClient().GetService<ChatClientMetadata>()!.ProviderUri);
        Assert.Equal(shown, client.AsIEmbeddingGenerator().GetService<EmbeddingGeneratorMetadata>()!.ProviderUri);
    }

    [Fact]
    public async Task BatchEmbedContents_BareModelOnAnItem_IsNormalizedOnTheWire()
    {
        using var handler = new RecordingHandler("""{"embeddings":[{"values":[1]}]}""");
        using var client = new GeminiClient(new GeminiClientOptions(), new HttpClient(handler));
        var item = EmbedContentRequest.FromText("x");
        item.Model = "gemini-embedding-001";

        await client.BatchEmbedContentsAsync(new BatchEmbedContentsRequest { Requests = [item] });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("models/gemini-embedding-001", body.RootElement.GetProperty("requests")[0].GetProperty("model").GetString());
    }

    [Fact]
    public void EmbeddingModel_SurroundedByWhitespace_IsTrimmed()
    {
        using var client = new GeminiClient(new GeminiClientOptions { EmbeddingModel = "  gemini-embedding-001 " });

        Assert.Equal("gemini-embedding-001", client.DefaultEmbeddingModel);
    }
}
