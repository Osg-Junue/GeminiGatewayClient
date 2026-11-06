using System.Diagnostics;
using System.Text.Json;
using GeminiGatewayClient.ExploratoryTests.Infrastructure;
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests;

/// <summary>
/// Exploratory tests for multi-turn long conversations, verifying context retention across many turns,
/// token growth tracking, and round-trip persistence when <see cref="AIContent.RawRepresentation"/> is stripped.
/// </summary>
public sealed class LongConversationExploratoryTests(ExploratoryTestFixture fixture, ITestOutputHelper output)
    : ExploratoryTestBase(fixture, output)
{
    [ExploratoryFact]
    public async Task MultiTurn_NativeGeminiClient_RetainsAllConstraintsAcrossSixTurns()
    {
        LogSection("6-Turn Architecture Consultation (Native GeminiClient)");
        using var client = CreateGeminiClient();

        var conversation = new List<Content>();
        var systemInstruction = new Content
        {
            Parts = [Part.FromText("あなたはクラウドアーキテクトです。ユーザーが段階的に提示する条件をすべて記憶し、各ターンでは1〜2文で簡潔に受領確認してください。")]
        };

        string[] userPrompts =
        [
            "ターン1: 新規プロジェクトのコードネームは『Project-Orchid』に決まりました。覚えておいてください。",
            "ターン2: 月額インフラ予算の上限は 450,000円 です。",
            "ターン3: プライマリリージョンは『東京 (ap-northeast-1)』、DRリージョンは『大阪 (ap-northeast-3)』とします。",
            "ターン4: メインデータベースには『PostgreSQL 17』を採用し、テナント分離には Row-Level Security (RLS) を使います。",
            "ターン5: ピーク時の目標スループットは 3,200 RPS で、認証ゲートウェイには『Ocp-Apim-Subscription-Key』ヘッダを使います。",
            "ターン6: これまでの5ターンで私が伝えた要件（①コードネーム、②月額予算、③プライマリとDRリージョン、④データベースと分離方式、⑤目標RPSとヘッダ名）をすべて漏らさず箇条書きでまとめてください。"
        ];

        var promptTokenHistory = new List<int>();
        GenerateContentResponse? lastResponse = null;

        for (var i = 0; i < userPrompts.Length; i++)
        {
            var userPrompt = userPrompts[i];
            Output.WriteLine($"\n--- Turn {i + 1} User ---");
            Output.WriteLine(userPrompt);

            conversation.Add(Content.FromText(userPrompt, Role.User));

            var request = new GenerateContentRequest
            {
                SystemInstruction = systemInstruction,
                Contents = conversation,
                GenerationConfig = new GenerationConfig
                {
                    Temperature = 0.1,
                    MaxOutputTokens = 4096,
                    ThinkingConfig = new ThinkingConfig
                    {
                        ThinkingLevel = ThinkingLevel.Low
                    }
                }
            };

            var sw = Stopwatch.StartNew();
            var response = await client.GenerateContentAsync(request);
            sw.Stop();

            LogResponseSummary(response, sw.Elapsed);

            Assert.NotNull(response.Text);
            Assert.NotEmpty(response.Candidates);
            Assert.True(response.TryGetModelContent(out var modelContent));

            // Preserve the exact model Content (including any thoughtSignature parts) in conversation history
            conversation.Add(modelContent!);

            if (response.UsageMetadata is { } usage)
            {
                promptTokenHistory.Add(usage.PromptTokenCount);
            }

            lastResponse = response;
        }

        Output.WriteLine($"\nPrompt token progression across {promptTokenHistory.Count} turns: [{string.Join(" -> ", promptTokenHistory)}]");

        Assert.NotNull(lastResponse?.Text);
        var finalSummary = lastResponse.Text!;
        Assert.Contains("Project-Orchid", finalSummary, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            finalSummary.Contains("450,000", StringComparison.Ordinal) ||
            finalSummary.Contains("45万", StringComparison.Ordinal) ||
            finalSummary.Contains("450000", StringComparison.Ordinal),
            $"Expected budget 450,000 in final summary, but got: {finalSummary}");
        Assert.Contains("東京", finalSummary, StringComparison.Ordinal);
        Assert.Contains("大阪", finalSummary, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL", finalSummary, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            finalSummary.Contains("3,200", StringComparison.Ordinal) ||
            finalSummary.Contains("3200", StringComparison.Ordinal),
            $"Expected 3,200 RPS in final summary, but got: {finalSummary}");
        Assert.Contains("Ocp-Apim-Subscription-Key", finalSummary, StringComparison.OrdinalIgnoreCase);

        // Prompt token count must grow monotonically as history accumulates
        Assert.Equal(6, promptTokenHistory.Count);
        Assert.True(promptTokenHistory[^1] > promptTokenHistory[0]);
    }

    [ExploratoryFact]
    public async Task MultiTurn_IChatClient_WithSimulatedJsonPersistence_ContinuesCleanlyAcrossTurns()
    {
        LogSection("Multi-Turn IChatClient with Simulated Database/JSON Persistence");
        using var geminiClient = CreateGeminiClient();
        IChatClient chatClient = geminiClient.AsIChatClient();

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "あなたは端的に回答するアシスタントです。ユーザーが登録したキーワードと数値を正確に記憶してください。")
        };

        var turns = new[]
        {
            "合言葉1は『紅蓮の灯台』、数値1は 17 です。了解とだけ答えてください。",
            "合言葉2は『蒼穹の歯車』、数値2は 25 です。了解とだけ答えてください。",
            "合言葉1と合言葉2を並べ、さらに数値1と数値2の合計（17 + 25）を計算して答えてください。"
        };

        ChatResponse? finalResponse = null;

        for (var t = 0; t < turns.Length; t++)
        {
            Output.WriteLine($"\n--- Persisted Chat Turn {t + 1} ---");
            Output.WriteLine($"User: {turns[t]}");
            history.Add(new ChatMessage(ChatRole.User, turns[t]));

            var sw = Stopwatch.StartNew();
            var response = await chatClient.GetResponseAsync(history, new ChatOptions
            {
                Temperature = 0.0f,
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.Low,
                    Output = ReasoningOutput.Summary
                }
            });
            sw.Stop();

            LogChatResponseSummary(response, sw.Elapsed);

            // Simulate persisting the assistant message to a database/JSON store where RawRepresentation is lost
            // and AdditionalProperties values are round-tripped as JsonElement.
            foreach (var msg in response.Messages)
            {
                var persistedMessage = SimulateDatabasePersistence(msg);
                Assert.All(persistedMessage.Contents, c => Assert.Null(c.RawRepresentation));
                history.Add(persistedMessage);
            }

            finalResponse = response;
        }

        Assert.NotNull(finalResponse?.Text);
        Assert.Contains("紅蓮の灯台", finalResponse.Text, StringComparison.Ordinal);
        Assert.Contains("蒼穹の歯車", finalResponse.Text, StringComparison.Ordinal);
        Assert.Contains("42", finalResponse.Text, StringComparison.Ordinal);
    }

    [ExploratoryFact]
    public async Task MultiTurn_IChatClient_Streaming_PreservesThoughtSignatureAcrossToChatResponse_AndOmitsEmptyTextContent()
    {
        LogSection("Multi-Turn IChatClient Streaming + ToChatResponse() Round-Trip");
        using var geminiClient = CreateGeminiClient();
        IChatClient chatClient = geminiClient.AsIChatClient();

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "あなたはシステム設計アシスタントです。各ターンで2〜3文の説明を添えて回答してください。")
        };

        var turns = new[]
        {
            "第1ターン: キャッシュ層には『Valkey 8.0』を採用し、TTLは 900秒 とします。この設定の利点を2文で説明しつつ記憶してください。",
            "第2ターン: メッセージブローカーには『Apache Kafka』を採用し、パーティション数は 12 とします。この設定の利点を2文で説明しつつ記憶してください。",
            "第3ターン: 第1ターンと第2ターンで決定したミドルウェア名と数値（TTL秒数・パーティション数）をすべて含めて要約してください。"
        };

        ChatResponse? finalResponse = null;

        for (var t = 0; t < turns.Length; t++)
        {
            Output.WriteLine($"\n--- Streaming Chat Turn {t + 1} ---");
            history.Add(new ChatMessage(ChatRole.User, turns[t]));

            var updates = new List<ChatResponseUpdate>();
            var sw = Stopwatch.StartNew();

            await foreach (var update in chatClient.GetStreamingResponseAsync(history, new ChatOptions
            {
                Temperature = 0.0f,
                Reasoning = new ReasoningOptions
                {
                    Effort = ReasoningEffort.Low,
                    Output = ReasoningOutput.Summary
                }
            }))
            {
                updates.Add(update);
            }

            sw.Stop();

            // Verify no empty TextContent("") was emitted from empty or signature-only SSE chunks
            var allTextContents = updates.SelectMany(u => u.Contents).OfType<TextContent>().ToList();
            Assert.NotEmpty(allTextContents);
            Assert.All(allTextContents, tc => Assert.False(string.IsNullOrEmpty(tc.Text)));

            var aggregated = updates.ToChatResponse();
            LogChatResponseSummary(aggregated, sw.Elapsed);

            // Verify thoughtSignature survived ToChatResponse() even when multiple TextContent chunks were coalesced
            var hasSignature = aggregated.Messages
                .SelectMany(m => m.Contents)
                .Any(c =>
                    (c is TextReasoningContent rc && !string.IsNullOrEmpty(rc.ProtectedData)) ||
                    (c.AdditionalProperties?.TryGetValue(GeminiChatPropertyNames.ThoughtSignature, out var sig) == true && sig is not null));
            Assert.True(hasSignature, $"Expected thoughtSignature to be preserved after ToChatResponse() on Turn {t + 1}.");

            // Verify that any companion streaming signature-only TextReasoningContent preserves its source Part as RawRepresentation
            var companionSignatureItems = aggregated.Messages
                .SelectMany(m => m.Contents)
                .OfType<TextReasoningContent>()
                .Where(rc => string.IsNullOrEmpty(rc.Text) && !string.IsNullOrEmpty(rc.ProtectedData))
                .ToList();
            Assert.All(companionSignatureItems, rc => Assert.IsType<Part>(rc.RawRepresentation));

            history.AddRange(aggregated.Messages);
            finalResponse = aggregated;
        }

        Assert.NotNull(finalResponse?.Text);
        Assert.Contains("Valkey", finalResponse.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("900", finalResponse.Text, StringComparison.Ordinal);
        Assert.Contains("Kafka", finalResponse.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12", finalResponse.Text, StringComparison.Ordinal);
    }

    [ExploratoryFact]
    public async Task MultiTurn_NativeStreaming_ToModelContentAsync_KeepsPartsForSubsequentTurns()
    {
        LogSection("Multi-Turn Native StreamGenerateContentAsync + ToModelContentAsync()");
        using var client = CreateGeminiClient();

        var isGemini3OrLater = DefaultModel.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase);
        var thinkingConfig = isGemini3OrLater
            ? new ThinkingConfig { ThinkingLevel = ThinkingLevel.Low, IncludeThoughts = true }
            : new ThinkingConfig { ThinkingBudget = 1024, IncludeThoughts = true };

        var conversation = new List<Content>();
        var turns = new[]
        {
            "サーバー識別子『SRV-TOKYO-07』とポート番号『8443』を記憶し、受領確認を2文で出力してください。",
            "先ほど記憶したサーバー識別子とポート番号をそのまま答えてください。"
        };

        Content? lastModelContent = null;

        for (var t = 0; t < turns.Length; t++)
        {
            Output.WriteLine($"\n--- Native Stream Turn {t + 1} ---");
            conversation.Add(Content.FromText(turns[t], Role.User));

            var request = new GenerateContentRequest
            {
                Contents = conversation,
                GenerationConfig = new GenerationConfig
                {
                    Temperature = 0.0,
                    ThinkingConfig = thinkingConfig
                }
            };

            var sw = Stopwatch.StartNew();
            var modelContent = await client.StreamGenerateContentAsync(request).ToModelContentAsync();
            sw.Stop();

            Assert.NotNull(modelContent);
            Assert.Equal(Role.Model, modelContent.Role);
            Assert.NotEmpty(modelContent.Parts);
            Assert.All(modelContent.Parts, p => Assert.False(string.IsNullOrEmpty(p.Text) && string.IsNullOrEmpty(p.ThoughtSignature)));

            Output.WriteLine(
                $"[Aggregated Content ({sw.ElapsedMilliseconds} ms)] Parts={modelContent.Parts.Count}, " +
                $"HasSignature={modelContent.Parts.Any(p => !string.IsNullOrEmpty(p.ThoughtSignature))}");

            conversation.Add(modelContent);
            lastModelContent = modelContent;
        }

        Assert.NotNull(lastModelContent);
        var combinedText = string.Concat(lastModelContent.Parts.Where(p => p.Thought != true).Select(p => p.Text));
        Output.WriteLine($"Final Answer: {combinedText}");
        Assert.Contains("SRV-TOKYO-07", combinedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8443", combinedText, StringComparison.Ordinal);
    }

    private static ChatMessage SimulateDatabasePersistence(ChatMessage original)
    {
        var clonedContents = new List<AIContent>(original.Contents.Count);
        foreach (var item in original.Contents)
        {
            var clonedProps = CloneAdditionalPropertiesAsJsonElements(item.AdditionalProperties);
            AIContent rehydrated = item switch
            {
                TextContent tc => new TextContent(tc.Text) { AdditionalProperties = clonedProps },
                TextReasoningContent rc => new TextReasoningContent(rc.Text)
                {
                    ProtectedData = rc.ProtectedData,
                    AdditionalProperties = clonedProps
                },
                FunctionCallContent fc => new FunctionCallContent(fc.CallId, fc.Name, fc.Arguments)
                {
                    AdditionalProperties = clonedProps
                },
                _ => new AIContent { AdditionalProperties = clonedProps }
            };
            clonedContents.Add(rehydrated);
        }

        return new ChatMessage(original.Role, clonedContents)
        {
            MessageId = original.MessageId
        };
    }

    private static AdditionalPropertiesDictionary? CloneAdditionalPropertiesAsJsonElements(
        AdditionalPropertiesDictionary? source)
    {
        if (source is null || source.Count == 0)
        {
            return null;
        }

        var result = new AdditionalPropertiesDictionary();
        foreach (var (key, value) in source)
        {
            result[key] = JsonSerializer.SerializeToElement(value);
        }

        return result;
    }
}
