using System.Diagnostics;
using GeminiGatewayClient.ExploratoryTests.Infrastructure;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests;

/// <summary>
/// Exploratory tests for Google Search Grounding (<see cref="GroundingMetadata"/>, <see cref="HostedWebSearchTool"/>,
/// <see cref="CitationAnnotation"/> with multibyte UTF-8 -> UTF-16 span mapping), URL Context (<see cref="UrlContextMetadata"/>),
/// and Embeddings (<c>:embedContent</c>, <c>:batchEmbedContents</c>, and <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>).
/// </summary>
public sealed class GroundingAndEmbeddingsExploratoryTests(ExploratoryTestFixture fixture, ITestOutputHelper output)
    : ExploratoryTestBase(fixture, output)
{
    [ExploratoryFact]
    public async Task GoogleSearchGrounding_NativeGeminiClient_PopulatesGroundingMetadataAndChunks()
    {
        LogSection("Google Search Grounding (Native GeminiClient -> GroundingMetadata)");
        using var client = CreateGeminiClient();

        var request = new GenerateContentRequest
        {
            Contents =
            [
                Content.FromText(
                    "Google検索を使って、.NET 10 の正式リリース時期（2025年11月）とサポート種別（LTS か STS か）を確認し、2〜3文で簡潔にまとめてください。",
                    Role.User)
            ],
            Tools =
            [
                new Tool { GoogleSearch = new GoogleSearchTool() }
            ],
            GenerationConfig = new GenerationConfig
            {
                Temperature = 0.0
            }
        };

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Text);
        Assert.Contains("LTS", response.Text, StringComparison.OrdinalIgnoreCase);

        var candidate = Assert.Single(response.Candidates);
        Assert.NotNull(candidate.GroundingMetadata);
        Assert.True(
            candidate.GroundingMetadata.WebSearchQueries.Count > 0 ||
            candidate.GroundingMetadata.GroundingChunks.Count > 0,
            "Expected Google Search Grounding to return WebSearchQueries or GroundingChunks.");
    }

    [ExploratoryFact]
    public async Task GoogleSearchGrounding_IChatClient_HostedWebSearchTool_MapsCitationAnnotationsAndValidatesMultibyteSpans()
    {
        LogSection("Google Search Grounding (IChatClient + HostedWebSearchTool -> CitationAnnotation)");
        using var geminiClient = CreateGeminiClient();
        IChatClient chatClient = geminiClient.AsIChatClient();

        var sw = Stopwatch.StartNew();
        var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(
                    ChatRole.User,
                    "Google検索を使って、.NET 10 の正式リリース日（2025年11月何日か）とサポート終了日（何年何月何日か）を調べ、2〜3文の日本語で回答してください。")
            ],
            new ChatOptions
            {
                Temperature = 0.0f,
                Tools = [new HostedWebSearchTool()]
            });
        sw.Stop();

        LogChatResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Text);
        var textContents = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .ToList();
        Assert.NotEmpty(textContents);

        // Verify that every mapped CitationAnnotation and TextSpanAnnotatedRegion has valid UTF-16 character bounds
        var totalAnnotations = 0;
        foreach (var tc in textContents)
        {
            if (tc.Annotations is not { Count: > 0 } annotations)
            {
                continue;
            }

            foreach (var annotation in annotations.OfType<CitationAnnotation>())
            {
                totalAnnotations++;
                if (annotation.AnnotatedRegions is not { Count: > 0 } regions)
                {
                    continue;
                }

                foreach (var region in regions.OfType<TextSpanAnnotatedRegion>())
                {
                    Assert.NotNull(region.StartIndex);
                    Assert.NotNull(region.EndIndex);
                    Assert.InRange(region.StartIndex!.Value, 0, tc.Text.Length);
                    Assert.InRange(region.EndIndex!.Value, region.StartIndex.Value, tc.Text.Length);

                    var citedSlice = tc.Text[region.StartIndex.Value..region.EndIndex.Value];
                    Output.WriteLine($"  [Verified UTF-16 Span [{region.StartIndex}..{region.EndIndex}]] => \"{citedSlice}\" (Source: {annotation.Title})");
                }
            }
        }

        Output.WriteLine($"Total CitationAnnotations mapped on TextContent: {totalAnnotations}");
        Assert.True(
            totalAnnotations > 0 ||
            (response.AdditionalProperties?.ContainsKey(GeminiChatPropertyNames.WebSearchQueries) == true),
            "Expected HostedWebSearchTool response to contain mapped CitationAnnotations or WebSearchQueries.");
    }

    [ExploratoryFact]
    public async Task UrlContext_NativeGeminiClient_RetrievesUrlAndPopulatesUrlContextMetadata()
    {
        LogSection("URL Context Tool (Native GeminiClient -> UrlContextMetadata)");
        using var client = CreateGeminiClient();

        var request = new GenerateContentRequest
        {
            Contents =
            [
                Content.FromText(
                    "URL Context ツールで https://ai.google.dev/gemini-api/docs/url-context のウェブページを直接取得し、そのページに記載されている機能名と主な制限事項を2文で教えてください。",
                    Role.User)
            ],
            Tools =
            [
                new Tool { UrlContext = new UrlContextTool() }
            ],
            GenerationConfig = new GenerationConfig
            {
                Temperature = 0.0
            }
        };

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.False(string.IsNullOrWhiteSpace(response.Text));

        var candidate = Assert.Single(response.Candidates);
        Assert.NotNull(candidate.UrlContextMetadata);
        Assert.NotEmpty(candidate.UrlContextMetadata.UrlMetadata);
        Assert.All(candidate.UrlContextMetadata.UrlMetadata, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.RetrievedUrl));
            Assert.NotNull(m.UrlRetrievalStatus);
            Assert.NotEqual(UrlRetrievalStatus.Unknown, m.UrlRetrievalStatus);
        });
    }

    [ExploratoryFact]
    public async Task EmbedContent_AndBatchEmbedContents_NativeGeminiClient_ComputesExpectedSemanticSimilarity()
    {
        LogSection("Native EmbedContentAsync & BatchEmbedContentsAsync (Semantic Similarity)");
        using var client = CreateGeminiClient();

        const int dimensions = 256;

        var sw = Stopwatch.StartNew();
        var queryResponse = await client.EmbedContentAsync(
            text: "C# の IAsyncEnumerable<T> を使った非同期ストリーミング処理の実装方法",
            taskType: TaskType.RetrievalQuery,
            outputDimensionality: dimensions);

        var batchResponse = await client.BatchEmbedContentsAsync(new BatchEmbedContentsRequest
        {
            Requests =
            [
                EmbedContentRequest.FromText(
                    "C# では yield return と await foreach を組み合わせて IAsyncEnumerable<T> の非同期ストリームを逐次処理できます。",
                    TaskType.RetrievalDocument,
                    title: "C# Async Streams Guide",
                    outputDimensionality: dimensions),
                EmbedContentRequest.FromText(
                    "ナポリタンはケチャップと玉ねぎ、ピーマン、ウインナーを炒めて作る日本の定番パスタ料理です。",
                    TaskType.RetrievalDocument,
                    title: "Cooking Recipe: Napolitan",
                    outputDimensionality: dimensions)
            ]
        });
        sw.Stop();

        Assert.NotNull(queryResponse.Embedding);
        Assert.Equal(dimensions, queryResponse.Embedding.Values.Count);
        Assert.Equal(2, batchResponse.Embeddings.Count);
        Assert.Equal(dimensions, batchResponse.Embeddings[0].Values.Count);
        Assert.Equal(dimensions, batchResponse.Embeddings[1].Values.Count);

        var simRelated = CosineSimilarity(queryResponse.Embedding.Values, batchResponse.Embeddings[0].Values);
        var simUnrelated = CosineSimilarity(queryResponse.Embedding.Values, batchResponse.Embeddings[1].Values);

        Output.WriteLine($"[Embeddings Completed in {sw.ElapsedMilliseconds} ms] Dimensions={dimensions}");
        Output.WriteLine($"  CosineSimilarity(Query, Related C# Doc)   = {simRelated:F4}");
        Output.WriteLine($"  CosineSimilarity(Query, Unrelated Recipe) = {simUnrelated:F4}");

        Assert.True(
            simRelated > simUnrelated,
            $"Expected related document similarity ({simRelated:F4}) to exceed unrelated document similarity ({simUnrelated:F4}).");
    }

    [ExploratoryFact]
    public async Task IEmbeddingGenerator_ViaDependencyInjection_SingleAndBatchGeneration_WorksEndToEnd()
    {
        LogSection("IEmbeddingGenerator<string, Embedding<float>> via AddGeminiClient DI");

        var services = new ServiceCollection();
        services.AddGeminiClient(options =>
        {
            options.Model = DefaultModel;
            options.EmbeddingModel = DefaultEmbeddingModel;
            options.ApiKey = ExploratoryTestSettings.GetRequiredApiKey();
        });

        await using var sp = services.BuildServiceProvider();
        var generator = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal(DefaultEmbeddingModel, metadata.DefaultModelId);

        // 1. Single input -> routes to :embedContent
        var single = await generator.GenerateAsync(
            ["APIゲートウェイ経由でGeminiを呼び出す.NETクライアント"],
            new EmbeddingGenerationOptions { Dimensions = 128 });
        var singleVector = Assert.Single(single).Vector;
        Output.WriteLine($"Single GenerateAsync vector length: {singleVector.Length}");
        Assert.Equal(128, singleVector.Length);

        // 2. Multiple inputs -> routes to :batchEmbedContents
        var batch = await generator.GenerateAsync(
            [
                "Azure API Management で Ocp-Apim-Subscription-Key ヘッダを使用する",
                "Cloudflare AI Gateway で cf-aig-authorization ヘッダを使用する"
            ],
            new EmbeddingGenerationOptions { Dimensions = 128 });
        Output.WriteLine($"Batch GenerateAsync count: {batch.Count}, lengths=[{batch[0].Vector.Length}, {batch[1].Vector.Length}]");
        Assert.Equal(2, batch.Count);
        Assert.Equal(128, batch[0].Vector.Length);
        Assert.Equal(128, batch[1].Vector.Length);
    }

    private static double CosineSimilarity(IList<float> a, IList<float> b)
    {
        double dot = 0;
        double normA = 0;
        double normB = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}

