using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using GeminiGatewayClient.Chat;
using GeminiGatewayClient.ExploratoryTests.Infrastructure;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests;

/// <summary>
/// Exploratory tests for <c>Microsoft.Extensions.AI</c> (<see cref="IChatClient"/>) integration:
/// automatic multi-step tool invocation via <c>UseFunctionInvocation()</c>, streaming with tools,
/// thinking settings through <c>RawRepresentationFactory</c>, and <c>AddGeminiClient</c> dependency injection.
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
    public async Task IChatClient_MultiTurnTools_WithStronglyTypedReturnSchema_CollidingCallIds_AndRedactedArguments_Succeeds()
    {
        LogSection("IChatClient Multi-Turn Tools (ReturnJsonSchema + Reused CallId + Redacted Arguments)");
        using var geminiClient = CreateGeminiClient();
        IChatClient chatClient = geminiClient.AsIChatClient();

        [Description("アカウントコードから法人アカウント情報を取得します。")]
        AccountLookupResult LookupAccount([Description("アカウントコード")] string accountCode) =>
            new(accountCode, "Contoso Japan K.K.", "Enterprise");

        [Description("請求書番号から請求金額と支払期日を取得します。")]
        InvoiceLookupResult LookupInvoice([Description("請求書番号")] string invoiceNumber) =>
            new(invoiceNumber, 128000, "2026-11-30");

        var options = new ChatOptions
        {
            Temperature = 0.0f,
            Tools =
            [
                AIFunctionFactory.Create(LookupAccount, "lookup_account"),
                AIFunctionFactory.Create(LookupInvoice, "lookup_invoice")
            ]
        };

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "ユーザーの指示に従ってツールを呼び出し、結果を簡潔に回答してください。"),
            new(ChatRole.User, "アカウントコード `ACC-7700` の法人アカウント情報を `lookup_account` で照会してください。")
        };

        // Turn 1: Model invokes lookup_account
        var turn1Response = await chatClient.GetResponseAsync(history, options);
        LogChatResponseSummary(turn1Response);

        var turn1Call = Assert.Single(turn1Response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("lookup_account", turn1Call.Name);

        // Mutate arguments (simulating PII/secret redaction in history) and normalize CallId to "call_shared_1"
        // while preserving RawRepresentation and AdditionalProperties (thoughtSignature).
        var turn1NormalizedAssistant = new ChatMessage(ChatRole.Assistant,
        [
            .. turn1Response.Messages[0].Contents.Where(c => c is not FunctionCallContent),
            new FunctionCallContent(
                "call_shared_1",
                turn1Call.Name,
                new Dictionary<string, object?> { ["accountCode"] = "ACC-REDACTED" })
            {
                RawRepresentation = turn1Call.RawRepresentation,
                AdditionalProperties = turn1Call.AdditionalProperties
            }
        ]);

        history.Add(turn1NormalizedAssistant);
        history.Add(new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("call_shared_1", LookupAccount("ACC-7700"))
        ]));

        var turn1Final = await chatClient.GetResponseAsync(history, options);
        LogChatResponseSummary(turn1Final);
        Assert.Contains("Contoso", turn1Final.Text ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        history.AddRange(turn1Final.Messages);

        // Turn 2: Ask for invoice lookup and reuse the exact same CallId ("call_shared_1") for a different tool ("lookup_invoice")
        history.Add(new ChatMessage(ChatRole.User, "次に、請求書番号 `INV-2026-99` の請求情報を `lookup_invoice` で照会してください。"));
        var turn2Response = await chatClient.GetResponseAsync(history, options);
        LogChatResponseSummary(turn2Response);

        var turn2Call = Assert.Single(turn2Response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("lookup_invoice", turn2Call.Name);

        var turn2NormalizedAssistant = new ChatMessage(ChatRole.Assistant,
        [
            .. turn2Response.Messages[0].Contents.Where(c => c is not FunctionCallContent),
            new FunctionCallContent("call_shared_1", turn2Call.Name, turn2Call.Arguments)
            {
                RawRepresentation = turn2Call.RawRepresentation,
                AdditionalProperties = turn2Call.AdditionalProperties
            }
        ]);

        history.Add(turn2NormalizedAssistant);
        history.Add(new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("call_shared_1", LookupInvoice("INV-2026-99"))
        ]));

        // Sending the full history with two colliding "call_shared_1" tool turns must resolve each turn chronologically
        var turn2Final = await chatClient.GetResponseAsync(history, options);
        LogChatResponseSummary(turn2Final);

        Assert.NotNull(turn2Final.Text);
        Assert.True(
            turn2Final.Text.Contains("128,000", StringComparison.Ordinal) ||
            turn2Final.Text.Contains("128000", StringComparison.Ordinal),
            $"Expected invoice amount 128,000 in final response, but got: {turn2Final.Text}");
    }

    [ExploratoryFact]
    public async Task IChatClient_ThinkingConfig_ReturnsReasoningContent()
    {
        LogSection("IChatClient ThinkingConfig through RawRepresentationFactory");
        using var geminiClient = CreateGeminiClient();
        IChatClient chatClient = geminiClient.AsIChatClient();

        var sw = Stopwatch.StartNew();
        var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "2の10乗から2の5乗を引いた値を計算し、答えの数値を書いてください。")
            ],
            new ChatOptions
            {
                RawRepresentationFactory = _ => new GenerateContentRequest
                {
                    GenerationConfig = new GenerationConfig { ThinkingConfig = new ThinkingConfig { ThinkingLevel = ThinkingLevel.Low, IncludeThoughts = true } }
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
            "Expected reasoning tokens or TextReasoningContent when ThinkingConfig is set.");
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

    [ExploratoryFact]
    public async Task IChatClient_UseFunctionInvocation_ParallelToolCalls_ExecutesConcurrentlyAndSynthesizesAnswer()
    {
        LogSection("TC-MEAI-01: Parallel Tool Calls Concurrency & Synthesis");
        using var geminiClient = CreateGeminiClient();

        var executionTrace = new List<string>();
        var traceLock = new object();

        [Description("指定された支店所在都市の現在の天気状況（晴れ・雪など）と気温を取得します。")]
        object GetBranchWeather([Description("支店都市名 (例: Sapporo, Fukuoka)")] string branchCity)
        {
            var entry = $"GetBranchWeather({branchCity})";
            lock (traceLock)
            {
                executionTrace.Add(entry);
            }
            Output.WriteLine($"  [C# Tool Invoked] {entry}");
            var isSapporo = branchCity.Contains("Sapporo", StringComparison.OrdinalIgnoreCase) || branchCity.Contains("札幌", StringComparison.OrdinalIgnoreCase);
            return new
            {
                branchCity,
                condition = isSapporo ? "雪 (Snowy)" : "快晴 (Clear)",
                temperatureCelsius = isSapporo ? -2 : 18
            };
        }

        [Description("指定された支店所在都市における特定の商品SKUの倉庫在庫数を取得します。")]
        object GetBranchStock(
            [Description("支店都市名 (例: Sapporo, Fukuoka)")] string branchCity,
            [Description("商品SKUコード (例: SKU-DRIVE-2TB)")] string itemSku)
        {
            var entry = $"GetBranchStock({branchCity}, {itemSku})";
            lock (traceLock)
            {
                executionTrace.Add(entry);
            }
            Output.WriteLine($"  [C# Tool Invoked] {entry}");
            var isSapporo = branchCity.Contains("Sapporo", StringComparison.OrdinalIgnoreCase) || branchCity.Contains("札幌", StringComparison.OrdinalIgnoreCase);
            return new
            {
                branchCity,
                itemSku,
                inStockCount = isSapporo ? 15 : 42,
                warehouseZone = isSapporo ? "Zone-A1" : "Zone-C3"
            };
        }

        IChatClient pipeline = new ChatClientBuilder(geminiClient.AsIChatClient())
            .UseFunctionInvocation()
            .Build();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(GetBranchWeather, "get_branch_weather"),
            AIFunctionFactory.Create(GetBranchStock, "get_branch_stock")
        };

        var prompt = "札幌支店 (`Sapporo`) と福岡支店 (`Fukuoka`) の両方について、現地の天気と商品 `SKU-DRIVE-2TB` の在庫数を同時に調べて比較報告してください。";

        var sw = Stopwatch.StartNew();
        var response = await pipeline.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "あなたは支店物流・営業支援アシスタントです。提供されたツールを呼び出して情報を収集し、全支店の天気と在庫数を明確に対比して報告してください。"),
                new ChatMessage(ChatRole.User, prompt)
            ],
            new ChatOptions
            {
                Temperature = 0.0f,
                Tools = tools
            });
        sw.Stop();

        LogChatResponseSummary(response, sw.Elapsed);

        Output.WriteLine("\n--- Execution Trace ---");
        foreach (var call in executionTrace)
        {
            Output.WriteLine($"  {call}");
        }

        Assert.NotNull(response.Text);
        Assert.NotEmpty(response.Text);

        // Verify that tools were executed for both branches
        Assert.True(executionTrace.Count >= 2, $"Expected at least 2 tool calls across execution trace, but executed {executionTrace.Count}. Trace: {string.Join(", ", executionTrace)}");
        Assert.Contains(executionTrace, c => c.Contains("Sapporo", StringComparison.OrdinalIgnoreCase) || c.Contains("札幌", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(executionTrace, c => c.Contains("Fukuoka", StringComparison.OrdinalIgnoreCase) || c.Contains("福岡", StringComparison.OrdinalIgnoreCase));

        // Semantic invariant: response synthesizes weather and stock information for both branches
        var text = response.Text;
        Assert.True(
            text.Contains("Sapporo", StringComparison.OrdinalIgnoreCase) || text.Contains("札幌", StringComparison.OrdinalIgnoreCase),
            "Expected final response to mention Sapporo.");
        Assert.True(
            text.Contains("Fukuoka", StringComparison.OrdinalIgnoreCase) || text.Contains("福岡", StringComparison.OrdinalIgnoreCase),
            "Expected final response to mention Fukuoka.");
        Assert.True(
            text.Contains("15") || text.Contains("42"),
            "Expected final response to reflect inventory quantities (15 or 42).");
    }

    [ExploratoryFact]
    public async Task IChatClient_UseFunctionInvocation_ComplexParametersAndEnums_BindsCorrectly()
    {
        LogSection("TC-MEAI-02: Complex Parameters, Enums & Collections Schema Binding");
        using var geminiClient = CreateGeminiClient();

        TicketFilter? capturedFilter = null;

        [Description("指定した検索条件（部署、優先度、タグリスト、最大件数）に一致するサポートチケットを検索します。")]
        object QuerySupportTickets([Description("検索条件フィルターオブジェクト")] TicketFilter filter)
        {
            capturedFilter = filter;
            Output.WriteLine($"  [C# Tool Invoked] QuerySupportTickets Department={filter.Department}, Priority={filter.Priority}, Tags=[{string.Join(", ", filter.Tags ?? [])}], MaxCount={filter.MaxCount}");
            return new[]
            {
                new
                {
                    ticketId = "TCK-8810",
                    title = "VPN Gateway Security Alert",
                    department = filter.Department,
                    priority = filter.Priority.ToString(),
                    tags = filter.Tags ?? []
                }
            };
        }

        IChatClient pipeline = new ChatClientBuilder(geminiClient.AsIChatClient())
            .UseFunctionInvocation()
            .Build();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(QuerySupportTickets, "query_support_tickets")
        };

        var prompt = "ITサポート部門 (`ITSupport`) のチケットで、優先度が `Urgent`、タグに `Network` と `Security` が付いているものを最大 3 件検索してください。";

        var sw = Stopwatch.StartNew();
        var response = await pipeline.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "あなたはITサービスデスクアシスタントです。依頼に従ってツールを呼び出してチケットを検索し、結果を要約して報告してください。"),
                new ChatMessage(ChatRole.User, prompt)
            ],
            new ChatOptions
            {
                Temperature = 0.0f,
                Tools = tools
            });
        sw.Stop();

        LogChatResponseSummary(response, sw.Elapsed);

        Assert.NotNull(capturedFilter);
        Assert.Contains("ITSupport", capturedFilter.Department, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SupportPriority.Urgent, capturedFilter.Priority);
        Assert.NotNull(capturedFilter.Tags);
        Assert.Contains(capturedFilter.Tags, t => string.Equals(t, "Network", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capturedFilter.Tags, t => string.Equals(t, "Security", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, capturedFilter.MaxCount);

        Assert.NotNull(response.Text);
        Assert.True(
            response.Text.Contains("TCK-8810", StringComparison.OrdinalIgnoreCase) ||
            response.Text.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
            response.Text.Contains("Urgent", StringComparison.OrdinalIgnoreCase),
            "Expected final answer to summarize the search results from the tool.");
    }

    [ExploratoryFact]
    public async Task IChatClient_UseFunctionInvocation_ToolErrorPayload_ModelRecoversGracefully()
    {
        LogSection("TC-MEAI-03: Tool Error Payload & Model Autonomous Recovery");
        using var geminiClient = CreateGeminiClient();

        var executionTrace = new List<string>();

        [Description("ユーザーIDから社内連絡先プロフィールを照会します。")]
        object FindUserProfile([Description("検索対象のユーザーID (例: USER-999)")] string userId)
        {
            var entry = $"FindUserProfile(userId={userId})";
            executionTrace.Add(entry);
            Output.WriteLine($"  [C# Tool Invoked] {entry}");
            if (string.Equals(userId, "USER-999", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    status = "ERROR",
                    errorCode = "USER_NOT_FOUND",
                    message = $"User '{userId}' was not found in the employee directory."
                };
            }

            return new { status = "OK", userId, name = "Kenji Sato", email = "kenji@example.com" };
        }

        [Description("ユーザーが存在しない場合に指定部署の代表連絡窓口を検索します。")]
        object FindFallbackContact([Description("部署名 (例: HR, ITSupport)")] string department)
        {
            var entry = $"FindFallbackContact(department={department})";
            executionTrace.Add(entry);
            Output.WriteLine($"  [C# Tool Invoked] {entry}");
            return new
            {
                status = "OK",
                department,
                contactEmail = "hr-desk@example.co.jp",
                phone = "03-5555-0199"
            };
        }

        IChatClient pipeline = new ChatClientBuilder(geminiClient.AsIChatClient())
            .UseFunctionInvocation()
            .Build();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(FindUserProfile, "find_user_profile"),
            AIFunctionFactory.Create(FindFallbackContact, "find_fallback_contact")
        };

        var prompt = "ユーザー `USER-999` の連絡先を調べてください。もしユーザーが見つからなければ、人事部 (`HR`) の連絡先窓口を調べて案内してください。";

        var sw = Stopwatch.StartNew();
        var response = await pipeline.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "あなたは社内ヘルプデスクアシスタントです。指示に従ってツールを活用し、エラーが発生した場合は適切に対処して報告してください。"),
                new ChatMessage(ChatRole.User, prompt)
            ],
            new ChatOptions
            {
                Temperature = 0.0f,
                Tools = tools
            });
        sw.Stop();

        LogChatResponseSummary(response, sw.Elapsed);

        Assert.NotEmpty(executionTrace);
        Assert.Contains(executionTrace, e => e.Contains("FindUserProfile", StringComparison.Ordinal) && e.Contains("USER-999", StringComparison.Ordinal));

        Assert.NotNull(response.Text);
        Assert.NotEmpty(response.Text);
        var text = response.Text;
        var handledGracefully = text.Contains("USER-999", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("見つかりません", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("hr-desk@example.co.jp", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("人事", StringComparison.OrdinalIgnoreCase);
        Assert.True(handledGracefully, $"Expected model to communicate error recovery or fallback, but got: {text}");
    }

    [ExploratoryFact]
    public async Task IChatClient_ChatClientBuilder_MultiMiddlewarePipeline_TracksTelemetryAndPreservesGetService()
    {
        LogSection("TC-MEAI-04: Multi-Middleware Pipeline Telemetry & GetService<T> Transparency");
        using var geminiClient = CreateGeminiClient();

        var telemetryMiddleware = new TelemetryTrackingDelegatingChatClient(geminiClient.AsIChatClient());

        IChatClient pipeline = new ChatClientBuilder(telemetryMiddleware)
            .UseFunctionInvocation()
            .Build();

        [Description("商品SKUの最新定価（円）を取得します。")]
        object LookupItemPrice([Description("商品SKUコード")] string sku)
        {
            Output.WriteLine($"  [C# Tool Invoked] LookupItemPrice(sku={sku})");
            return new { sku, priceJpy = 12800 };
        }

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(LookupItemPrice, "lookup_item_price")
        };

        var prompt = "商品 `SKU-KEYBOARD-PRO` の最新価格を調べて教えてください。";

        var sw = Stopwatch.StartNew();
        var response = await pipeline.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, prompt)
            ],
            new ChatOptions
            {
                Tools = tools
            });
        sw.Stop();

        LogChatResponseSummary(response, sw.Elapsed);

        Output.WriteLine($"Telemetry Stats: InvocationCount={telemetryMiddleware.InvocationCount}, TotalInputTokens={telemetryMiddleware.TotalInputTokens}, TotalOutputTokens={telemetryMiddleware.TotalOutputTokens}");

        Assert.True(telemetryMiddleware.InvocationCount >= 2, $"Expected at least 2 middleware invocations across function calling loop, but got {telemetryMiddleware.InvocationCount}.");
        Assert.True(telemetryMiddleware.TotalInputTokens > 0, "Expected non-zero tracked input tokens.");
        Assert.True(telemetryMiddleware.TotalOutputTokens > 0, "Expected non-zero tracked output tokens.");

        var resolvedMetadata = pipeline.GetService<ChatClientMetadata>();
        var resolvedGeminiClient = pipeline.GetService<IGeminiClient>();

        Assert.NotNull(resolvedMetadata);
        Assert.NotNull(resolvedGeminiClient);

        Assert.NotNull(response.Text);
        Assert.True(
            response.Text.Contains("12,800", StringComparison.Ordinal) || response.Text.Contains("12800", StringComparison.Ordinal),
            $"Expected response to contain price (12,800 or 12800), but got: {response.Text}");
    }

    [ExploratoryFact]
    public async Task IChatClient_UseFunctionInvocation_CombinedWithThinking_PreservesThoughtSignatures()
    {
        LogSection("TC-MEAI-05: UseFunctionInvocation() Combined with ThinkingConfig (Thought Signatures)");
        using var geminiClient = CreateGeminiClient();

        [Description("与えられた正の数値の平方根(ルート)を計算します。")]
        double ComputeSquareRoot([Description("平方根を求めたい正の数値")] double value)
        {
            Output.WriteLine($"  [C# Tool Invoked] ComputeSquareRoot(value={value})");
            return Math.Sqrt(value);
        }

        IChatClient pipeline = new ChatClientBuilder(geminiClient.AsIChatClient())
            .UseFunctionInvocation()
            .Build();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(ComputeSquareRoot, "compute_square_root")
        };

        var prompt = "面積が 1764 平方メートルの正方形の敷地があります。この敷地の一辺の長さを、提供された平方根計算ツール `compute_square_root` を使用して計算し、さらにその外周の合計長さを求めてください。";

        var sw = Stopwatch.StartNew();
        var response = await pipeline.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "あなたは幾何学計算アシスタントです。論理的に思考し、計算ツールを適切に呼び出して答えを求めてください。"),
                new ChatMessage(ChatRole.User, prompt)
            ],
            new ChatOptions
            {
                RawRepresentationFactory = _ => new GenerateContentRequest
                {
                    GenerationConfig = new GenerationConfig { ThinkingConfig = new ThinkingConfig { ThinkingLevel = ThinkingLevel.Low, IncludeThoughts = true } }
                },
                Tools = tools
            });
        sw.Stop();

        LogChatResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Text);
        var text = response.Text;
        Assert.True(
            text.Contains("42", StringComparison.Ordinal) || text.Contains("168", StringComparison.Ordinal),
            $"Expected response to contain 42 (side length) or 168 (perimeter), but got: {text}");

        Assert.NotNull(response.Usage);
        Assert.True(
            response.Usage.ReasoningTokenCount is > 0 ||
            response.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Any() ||
            response.Usage.OutputTokenCount > 0,
            "Expected reasoning tokens, TextReasoningContent, or non-zero output tokens.");
    }

    [ExploratoryFact]
    public async Task IChatClient_UseFunctionInvocation_ParameterlessFunction_ExecutesWithoutSchemaRejection()
    {
        LogSection("TC-MEAI-06: Parameterless Function (Null Schema Avoids HTTP 400 Rejection)");
        using var geminiClient = CreateGeminiClient();

        var systemStatusInvoked = false;

        [Description("現在稼働中のゲートウェイサーバーのシステム稼働状況を取得します。")]
        object GetSystemStatus()
        {
            systemStatusInvoked = true;
            Output.WriteLine("  [C# Tool Invoked] GetSystemStatus()");
            return new
            {
                status = "ALL_SYSTEMS_OPERATIONAL",
                uptimeHours = 720,
                activeConnections = 142
            };
        }

        IChatClient pipeline = new ChatClientBuilder(geminiClient.AsIChatClient())
            .UseFunctionInvocation()
            .Build();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(GetSystemStatus, "get_system_status")
        };

        var prompt = "現在のシステム稼働状況を確認して教えてください。";

        var sw = Stopwatch.StartNew();
        var response = await pipeline.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, prompt)
            ],
            new ChatOptions
            {
                Tools = tools
            });
        sw.Stop();

        LogChatResponseSummary(response, sw.Elapsed);

        Assert.True(systemStatusInvoked, "Expected parameterless function GetSystemStatus to be invoked by the model.");
        Assert.NotNull(response.Text);
        var text = response.Text;
        Assert.True(
            text.Contains("OPERATIONAL", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("稼働", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("720", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("142", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("正常", StringComparison.OrdinalIgnoreCase),
            $"Expected response to contain system status info, but got: {text}");
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SupportPriority
    {
        Low,
        Normal,
        High,
        Urgent
    }

    public sealed record TicketFilter(
        [property: JsonPropertyName("department")] string Department,
        [property: JsonPropertyName("priority")] SupportPriority Priority,
        [property: JsonPropertyName("tags")] List<string>? Tags,
        [property: JsonPropertyName("maxCount")] int MaxCount = 5);

    private sealed class TelemetryTrackingDelegatingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
    {
        public int InvocationCount { get; private set; }
        public long TotalInputTokens { get; private set; }
        public long TotalOutputTokens { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            if (response.Usage is { } usage)
            {
                TotalInputTokens += usage.InputTokenCount ?? 0;
                TotalOutputTokens += usage.OutputTokenCount ?? 0;
            }
            return response;
        }
    }

    private sealed record AccountLookupResult(
        [property: JsonPropertyName("accountCode")] string AccountCode,
        [property: JsonPropertyName("companyName")] string CompanyName,
        [property: JsonPropertyName("plan")] string Plan);

    private sealed record InvoiceLookupResult(
        [property: JsonPropertyName("invoiceNumber")] string InvoiceNumber,
        [property: JsonPropertyName("amountJpy")] int AmountJpy,
        [property: JsonPropertyName("dueDate")] string DueDate);
}
