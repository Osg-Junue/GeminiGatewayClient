using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GeminiGatewayClient.ExploratoryTests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests;

/// <summary>
/// Exploratory tests for <c>Microsoft.Extensions.AI</c> (<see cref="IChatClient"/>) integration:
/// automatic multi-step tool invocation via <c>UseFunctionInvocation()</c>, streaming with tools,
/// <see cref="ReasoningOptions"/> mapping, and <c>AddGeminiClient</c> dependency injection.
/// </summary>
public sealed class ChatClientIntegrationExploratoryTests(ExploratoryTestFixture fixture, ITestOutputHelper output)
    : ExploratoryTestBase(fixture, output)
{
    [ExploratoryFact]
    public async Task IChatClient_UseFunctionInvocation_AutomaticallyChainsFourLocalTools()
    {
        LogSection("IChatClient + UseFunctionInvocation() Automatic 4-Step Tool Chain");
        using var geminiClient = CreateGeminiClient();

        var executionTrace = new List<string>();

        [Description("顧客メールアドレスから顧客IDと会員ランク(Gold / Silver / Standard)を照会します。")]
        object LookupCustomerTier(
            [Description("顧客のメールアドレス")] string email)
        {
            var entry = $"LookupCustomerTier(email={email})";
            executionTrace.Add(entry);
            Output.WriteLine($"  [C# Tool Invoked] {entry}");
            return new { customerId = "CUST-901", email, tier = "Gold" };
        }

        [Description("商品SKUと数量から単価と税抜小計(subtotalJpy)を取得します。")]
        object GetProductQuote(
            [Description("商品SKUコード (例: SKU-NVME-4TB)")] string sku,
            [Description("購入数量")] int quantity)
        {
            var entry = $"GetProductQuote(sku={sku}, quantity={quantity})";
            executionTrace.Add(entry);
            Output.WriteLine($"  [C# Tool Invoked] {entry}");
            var unitPrice = 20000;
            return new { sku, quantity, unitPriceJpy = unitPrice, subtotalJpy = unitPrice * quantity };
        }

        [Description("会員ランクと小計金額からランク割引額と割引後金額(discountedTotalJpy)を計算します。")]
        object ComputeTierDiscount(
            [Description("会員ランク (例: Gold)")] string tier,
            [Description("割引前の小計金額(円)")] int subtotalJpy)
        {
            var entry = $"ComputeTierDiscount(tier={tier}, subtotalJpy={subtotalJpy})";
            executionTrace.Add(entry);
            Output.WriteLine($"  [C# Tool Invoked] {entry}");
            var discountJpy = string.Equals(tier, "Gold", StringComparison.OrdinalIgnoreCase)
                ? (int)(subtotalJpy * 0.15)
                : 0;
            return new { tier, discountJpy, discountedTotalJpy = subtotalJpy - discountJpy };
        }

        [Description("顧客ID・商品SKU・数量・最終支払金額から注文確定番号(confirmationNumber)を発行します。")]
        object IssueOrderConfirmation(
            [Description("顧客ID (例: CUST-901)")] string customerId,
            [Description("商品SKU")] string sku,
            [Description("数量")] int quantity,
            [Description("割引適用後の最終支払金額(円)")] int finalAmountJpy)
        {
            var entry = $"IssueOrderConfirmation(customerId={customerId}, sku={sku}, qty={quantity}, finalAmountJpy={finalAmountJpy})";
            executionTrace.Add(entry);
            Output.WriteLine($"  [C# Tool Invoked] {entry}");
            return new
            {
                confirmationNumber = $"ORD-2026-{customerId}-{finalAmountJpy}",
                status = "CONFIRMED",
                finalAmountJpy
            };
        }

        // Build an IChatClient pipeline with automatic function invocation
        IChatClient pipeline = new ChatClientBuilder(geminiClient.AsIChatClient())
            .UseFunctionInvocation()
            .Build();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(LookupCustomerTier, "lookup_customer_tier"),
            AIFunctionFactory.Create(GetProductQuote, "get_product_quote"),
            AIFunctionFactory.Create(ComputeTierDiscount, "compute_tier_discount"),
            AIFunctionFactory.Create(IssueOrderConfirmation, "issue_order_confirmation")
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "あなたはEC注文処理アシスタントです。提供されたツールを順番に呼び出して注文を確定し、発行された注文確定番号と最終支払金額を報告してください。"),
            new(ChatRole.User, "顧客メール `kenji.tanaka@example.jp` の会員ランクを確認し、商品 `SKU-NVME-4TB` を 3個 購入する場合の小計と会員割引を計算した上で、注文確定番号を発行してください。")
        };

        var sw = Stopwatch.StartNew();
        var response = await pipeline.GetResponseAsync(messages, new ChatOptions
        {
            Temperature = 0.0f,
            Tools = tools
        });
        sw.Stop();

        LogChatResponseSummary(response, sw.Elapsed);

        Assert.Equal(4, executionTrace.Count);
        Assert.Contains(executionTrace, t => t.StartsWith("LookupCustomerTier", StringComparison.Ordinal));
        Assert.Contains(executionTrace, t => t.StartsWith("GetProductQuote", StringComparison.Ordinal));
        Assert.Contains(executionTrace, t => t.StartsWith("ComputeTierDiscount", StringComparison.Ordinal));
        Assert.Contains(executionTrace, t => t.StartsWith("IssueOrderConfirmation", StringComparison.Ordinal));

        Assert.NotNull(response.Text);
        // 20,000 * 3 = 60,000; 15% Gold discount = 9,000; final = 51,000 -> ORD-2026-CUST-901-51000
        Assert.Contains("ORD-2026-CUST-901-51000", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            response.Text.Contains("51,000", StringComparison.Ordinal) ||
            response.Text.Contains("51000", StringComparison.Ordinal),
            $"Expected final amount 51,000 in response, but got: {response.Text}");
    }

    [ExploratoryFact]
    public async Task IChatClient_UseFunctionInvocation_Streaming_StreamsFinalResponseAndSingleUsage()
    {
        LogSection("IChatClient Streaming + UseFunctionInvocation()");
        using var geminiClient = CreateGeminiClient();

        var toolCallCount = 0;

        [Description("サービス名から稼働中のリージョン識別子を返します。")]
        string GetActiveRegion([Description("サービス名")] string serviceName)
        {
            Interlocked.Increment(ref toolCallCount);
            Output.WriteLine($"  [Stream Tool 1] GetActiveRegion({serviceName}) => ap-northeast-1");
            return "ap-northeast-1";
        }

        [Description("リージョン識別子から現在のP99レイテンシ(ミリ秒)と稼働ステータスを返します。")]
        object GetRegionHealth([Description("リージョン識別子")] string regionId)
        {
            Interlocked.Increment(ref toolCallCount);
            Output.WriteLine($"  [Stream Tool 2] GetRegionHealth({regionId}) => p99=14ms, Healthy");
            return new { regionId, p99LatencyMs = 14, status = "Healthy" };
        }

        IChatClient pipeline = new ChatClientBuilder(geminiClient.AsIChatClient())
            .UseFunctionInvocation()
            .Build();

        var options = new ChatOptions
        {
            Temperature = 0.0f,
            Tools =
            [
                AIFunctionFactory.Create(GetActiveRegion, "get_active_region"),
                AIFunctionFactory.Create(GetRegionHealth, "get_region_health")
            ]
        };

        var updates = new List<ChatResponseUpdate>();
        var textBuilder = new StringBuilder();
        var sw = Stopwatch.StartNew();

        await foreach (var update in pipeline.GetStreamingResponseAsync(
            [
                new ChatMessage(ChatRole.System, "ツールを使って稼働リージョンとそのP99レイテンシを調べ、1文で報告してください。"),
                new ChatMessage(ChatRole.User, "サービス `payment-gateway` の現在の稼働リージョンとP99レイテンシ(ms)を教えてください。")
            ],
            options))
        {
            updates.Add(update);
            if (!string.IsNullOrEmpty(update.Text))
            {
                textBuilder.Append(update.Text);
            }
        }

        sw.Stop();
        var aggregated = updates.ToChatResponse();
        LogChatResponseSummary(aggregated, sw.Elapsed);

        Assert.True(toolCallCount >= 2, $"Expected both chained tools to be called, but toolCallCount={toolCallCount}.");
        Assert.Contains("ap-northeast-1", textBuilder.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("14", textBuilder.ToString(), StringComparison.Ordinal);
        Assert.NotNull(aggregated.Usage);
        Assert.True(aggregated.Usage.TotalTokenCount > 0);
    }

    [ExploratoryFact]
    public async Task IChatClient_ReasoningOptions_MapsEffortAndReturnsReasoningContent()
    {
        LogSection("IChatClient ReasoningOptions (Effort + Summary Output)");
        using var geminiClient = CreateGeminiClient();
        IChatClient chatClient = geminiClient.AsIChatClient();

        var sw = Stopwatch.StartNew();
        var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "2の10乗から2の5乗を引いた値を計算し、答えの数値を書いてください。")
            ],
            new ChatOptions
            {
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.Low,
                    Output = ReasoningOutput.Summary
                }
            });
        sw.Stop();

        LogChatResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Text);
        // 1024 - 32 = 992
        Assert.Contains("992", response.Text, StringComparison.Ordinal);
        Assert.NotNull(response.Usage);
        Assert.True(
            response.Usage.ReasoningTokenCount is > 0 ||
            response.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Any(),
            "Expected reasoning tokens or TextReasoningContent when ReasoningOptions is configured.");
    }

    [ExploratoryFact]
    public async Task DependencyInjection_AddGeminiClient_WithDynamicApiKeyProviderAndCustomHeaders_WorksEndToEnd()
    {
        LogSection("Dependency Injection (AddGeminiClient + ApiKeyProvider + AdditionalHeaders)");

        var providerInvocations = 0;
        var services = new ServiceCollection();

        services.AddGeminiClient(options =>
        {
            options.Model = DefaultModel;
            options.ApiKeyProvider = _ =>
            {
                Interlocked.Increment(ref providerInvocations);
                return ValueTask.FromResult(ExploratoryTestSettings.GetRequiredApiKey());
            };
            options.AdditionalHeaders["x-client-scenario"] = "di-exploratory-test";
            options.Timeout = TimeSpan.FromMinutes(2);
        });

        await using var serviceProvider = services.BuildServiceProvider();

        var geminiClient = serviceProvider.GetRequiredService<IGeminiClient>();
        var chatClient = serviceProvider.GetRequiredService<IChatClient>();

        var directResponse = await geminiClient.GenerateContentAsync("「DI-OK」とだけ返答してください。");
        Output.WriteLine($"Direct IGeminiClient response: {directResponse.Text}");

        var chatResponse = await chatClient.GetResponseAsync("「CHAT-OK」とだけ返答してください。");
        Output.WriteLine($"Resolved IChatClient response: {chatResponse.Text}");
        Output.WriteLine($"ApiKeyProvider invocation count: {providerInvocations}");

        Assert.NotNull(directResponse.Text);
        Assert.NotNull(chatResponse.Text);
        Assert.Equal(2, providerInvocations);
    }
}

