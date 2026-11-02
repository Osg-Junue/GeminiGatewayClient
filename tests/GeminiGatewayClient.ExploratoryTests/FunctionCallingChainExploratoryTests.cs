using System.Diagnostics;
using System.Text.Json;
using GeminiGatewayClient.ExploratoryTests.Infrastructure;
using GeminiGatewayClient.Models;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests;

/// <summary>
/// Exploratory tests for complex multi-step and parallel function calling chains using native <see cref="GeminiClient"/>
/// and <see cref="ToolConfig"/> controls.
/// </summary>
public sealed class FunctionCallingChainExploratoryTests(ExploratoryTestFixture fixture, ITestOutputHelper output)
    : ExploratoryTestBase(fixture, output)
{
    [ExploratoryFact]
    public async Task ComplexToolChain_ParallelAndSequentialCalls_CompletesBusinessTripWorkflow()
    {
        LogSection("Complex Multi-Step & Parallel Function Calling Chain");
        using var client = CreateGeminiClient();

        var tools = BuildTravelWorkflowTools();
        var invokedToolsLog = new List<(int Round, string Name, string ArgsJson)>();

        var contents = new List<Content>
        {
            Content.FromText(
                """
                社員「佐藤健太」の来週（2026-11-10）の2泊の国内出張手配をお願いします。以下の手順をツールを使って順番に実行してください：
                1. まず `get_employee_profile` で「佐藤健太」の社員ID・出発地(homeCity)・出張規定ランク(travelTier)・1泊あたりホテル上限額(hotelCapPerNightJpy)を調べる。
                2. 候補地である `"Sapporo"` と `"Fukuoka"` の2都市について、`get_branch_weather` で 2026-11-10 の天気を調べ、晴れている都市（Sunny）を出張先に選ぶ。
                3. 選んだ都市への往復フライトを `search_flights` で検索し、同時に `search_hotels` で2泊分のホテル（社員の1泊上限額以内）を検索する。
                4. フライト料金とホテル合計料金が分かったら、必ず最後に `calculate_reimbursement_summary` を呼び出して精算承認コード(approvalCode)と総合計金額(grandTotalJpy)を発行する。
                5. 最終回答には、選ばれた出張先都市名、便名、ホテル名、総合計金額、精算承認コード(APPR-で始まるコード)をすべて含めてください。
                """,
                Role.User)
        };

        const int maxRounds = 8;
        GenerateContentResponse? finalResponse = null;
        var toolRoundsExecuted = 0;
        var observedParallelCallInSingleTurn = false;

        for (var round = 1; round <= maxRounds; round++)
        {
            Output.WriteLine($"\n--- Tool Chain Round {round} ---");
            var request = new GenerateContentRequest
            {
                SystemInstruction = new Content
                {
                    Parts = [Part.FromText("あなたは企業の出張手配AIエージェントです。必要な情報はすべて提供されたツールを段階的に呼び出して取得し、推測で値を埋めないでください。")]
                },
                Contents = contents,
                Tools = [tools],
                GenerationConfig = new GenerationConfig
                {
                    Temperature = 0.0
                }
            };

            var sw = Stopwatch.StartNew();
            var response = await client.GenerateContentAsync(request);
            sw.Stop();

            LogResponseSummary(response, sw.Elapsed);

            Assert.NotEmpty(response.Candidates);
            var modelContent = response.Candidates[0].Content;
            Assert.NotNull(modelContent);

            // Always append the exact model Content (preserving ThoughtSignature on functionCall parts)
            contents.Add(modelContent!);

            var functionCalls = response.FunctionCalls;
            if (functionCalls.Count == 0)
            {
                finalResponse = response;
                Output.WriteLine($"\n[Chain Completed at Round {round}] Final Answer:\n{response.Text}");
                break;
            }

            toolRoundsExecuted++;
            if (functionCalls.Count >= 2)
            {
                observedParallelCallInSingleTurn = true;
                Output.WriteLine($"  -> Model requested {functionCalls.Count} parallel function calls in Round {round}!");
            }

            var responseParts = new List<Part>(functionCalls.Count);
            foreach (var call in functionCalls)
            {
                var argsJson = call.Args?.GetRawText() ?? "{}";
                invokedToolsLog.Add((round, call.Name, argsJson));

                var toolResultElement = ExecuteTravelTool(call.Name, call.Args);
                Output.WriteLine($"  [Tool Executed] {call.Name}({argsJson}) => {toolResultElement.GetRawText()}");

                responseParts.Add(Part.FromFunctionResponse(call.Name, toolResultElement, call.Id));
            }

            contents.Add(new Content
            {
                Role = Role.User,
                Parts = responseParts
            });
        }

        Output.WriteLine("\n--- Tool Invocation Trace ---");
        foreach (var entry in invokedToolsLog)
        {
            Output.WriteLine($"  Round {entry.Round}: {entry.Name}({entry.ArgsJson})");
        }
        Output.WriteLine($"Parallel calls observed in single turn: {observedParallelCallInSingleTurn}");

        Assert.NotNull(finalResponse);
        Assert.False(string.IsNullOrWhiteSpace(finalResponse!.Text));
        Assert.True(toolRoundsExecuted >= 3, $"Expected at least 3 sequential tool rounds, but ran {toolRoundsExecuted}.");

        var calledNames = invokedToolsLog.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("get_employee_profile", calledNames);
        Assert.Contains("get_branch_weather", calledNames);
        Assert.Contains("search_flights", calledNames);
        Assert.Contains("search_hotels", calledNames);
        Assert.Contains("calculate_reimbursement_summary", calledNames);

        var finalText = finalResponse.Text!;
        Assert.Contains("NH-061", finalText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("APPR-2026-84500", finalText, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            finalText.Contains("84,500", StringComparison.Ordinal) ||
            finalText.Contains("84500", StringComparison.Ordinal),
            $"Expected final answer to mention grand total 84,500 JPY, but got: {finalText}");
    }

    [ExploratoryFact]
    public async Task ToolConfig_FunctionCallingModeAny_ForcesAllowedFunctionCall()
    {
        LogSection("Forced Function Calling (FunctionCallingMode.Any + AllowedFunctionNames)");
        using var client = CreateGeminiClient();

        using var emptyParamsDoc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "scope": { "type": "string", "description": "Audit scope name, e.g. 'security' or 'billing'." }
              },
              "required": ["scope"]
            }
            """);

        var request = new GenerateContentRequest
        {
            Contents = [Content.FromText("こんにちは！現在のシステムセキュリティ監査ログを確認してください。", Role.User)],
            Tools =
            [
                new Tool
                {
                    FunctionDeclarations =
                    [
                        new FunctionDeclaration
                        {
                            Name = "get_system_metrics",
                            Description = "Returns CPU and memory utilization metrics.",
                            ParametersJsonSchema = emptyParamsDoc.RootElement.Clone()
                        },
                        new FunctionDeclaration
                        {
                            Name = "get_security_audit_log",
                            Description = "Fetches recent security audit events for the specified scope.",
                            ParametersJsonSchema = emptyParamsDoc.RootElement.Clone()
                        }
                    ]
                }
            ],
            ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig
                {
                    Mode = FunctionCallingMode.Any,
                    AllowedFunctionNames = ["get_security_audit_log"]
                }
            }
        };

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        var call = Assert.Single(response.FunctionCalls);
        Assert.Equal("get_security_audit_log", call.Name);
    }

    private static Tool BuildTravelWorkflowTools()
    {
        return new Tool
        {
            FunctionDeclarations =
            [
                new FunctionDeclaration
                {
                    Name = "get_employee_profile",
                    Description = "社員名から社員ID、所属部署、出発地都市(homeCity)、出張ランク(travelTier)、1泊あたりのホテル予算上限(hotelCapPerNightJpy)を取得します。",
                    ParametersJsonSchema = ParseSchema("""
                        {
                          "type": "object",
                          "properties": {
                            "employee_name": { "type": "string", "description": "社員の氏名（例: 佐藤健太）" }
                          },
                          "required": ["employee_name"]
                        }
                        """)
                },
                new FunctionDeclaration
                {
                    Name = "get_branch_weather",
                    Description = "指定された支社都市（Sapporo または Fukuoka）と日付の天気予報を取得します。",
                    ParametersJsonSchema = ParseSchema("""
                        {
                          "type": "object",
                          "properties": {
                            "city": { "type": "string", "description": "都市名 ('Sapporo' または 'Fukuoka')" },
                            "date": { "type": "string", "description": "日付 (YYYY-MM-DD)" }
                          },
                          "required": ["city", "date"]
                        }
                        """)
                },
                new FunctionDeclaration
                {
                    Name = "search_flights",
                    Description = "出発地・目的地・日付・出張ランクから利用可能な往復フライト便名と往復料金(roundTripFareJpy)を検索します。",
                    ParametersJsonSchema = ParseSchema("""
                        {
                          "type": "object",
                          "properties": {
                            "origin": { "type": "string", "description": "出発都市 (例: 'Tokyo')" },
                            "destination": { "type": "string", "description": "目的都市 (例: 'Sapporo')" },
                            "date": { "type": "string", "description": "出発日 (YYYY-MM-DD)" },
                            "travel_tier": { "type": "string", "description": "社員の出張規定ランク (例: 'Tier-A')" }
                          },
                          "required": ["origin", "destination", "date", "travel_tier"]
                        }
                        """)
                },
                new FunctionDeclaration
                {
                    Name = "search_hotels",
                    Description = "都市名・1泊あたり予算上限・宿泊数から条件に合うホテルと宿泊合計金額(totalHotelJpy)を検索します。",
                    ParametersJsonSchema = ParseSchema("""
                        {
                          "type": "object",
                          "properties": {
                            "city": { "type": "string", "description": "宿泊都市 (例: 'Sapporo')" },
                            "max_budget_per_night_jpy": { "type": "integer", "description": "1泊あたりの上限金額(円)" },
                            "nights": { "type": "integer", "description": "宿泊数" }
                          },
                          "required": ["city", "max_budget_per_night_jpy", "nights"]
                        }
                        """)
                },
                new FunctionDeclaration
                {
                    Name = "calculate_reimbursement_summary",
                    Description = "社員ID、往復フライト料金、ホテル合計料金、宿泊数から出張日当を含む総合計金額(grandTotalJpy)と精算承認コード(approvalCode)を計算・発行します。",
                    ParametersJsonSchema = ParseSchema("""
                        {
                          "type": "object",
                          "properties": {
                            "employee_id": { "type": "string", "description": "社員ID (例: 'EMP-702')" },
                            "flight_fare_jpy": { "type": "integer", "description": "往復フライト料金(円)" },
                            "hotel_total_jpy": { "type": "integer", "description": "ホテル宿泊合計料金(円)" },
                            "nights": { "type": "integer", "description": "宿泊数" }
                          },
                          "required": ["employee_id", "flight_fare_jpy", "hotel_total_jpy", "nights"]
                        }
                        """)
                }
            ]
        };
    }

    private static JsonElement ExecuteTravelTool(string name, JsonElement? args)
    {
        return name switch
        {
            "get_employee_profile" => JsonSerializer.SerializeToElement(new
            {
                employeeId = "EMP-702",
                name = "佐藤健太",
                department = "Platform Architecture Dept",
                homeCity = "Tokyo",
                travelTier = "Tier-A",
                hotelCapPerNightJpy = 18000
            }),

            "get_branch_weather" => ExecuteWeatherLookup(args),

            "search_flights" => JsonSerializer.SerializeToElement(new
            {
                flightNumber = "NH-061",
                origin = "Tokyo",
                destination = "Sapporo",
                cabinClass = "Premium Class (Tier-A)",
                roundTripFareJpy = 48500
            }),

            "search_hotels" => JsonSerializer.SerializeToElement(new
            {
                hotelName = "Grand North Hotel Sapporo",
                city = "Sapporo",
                nightlyRateJpy = 15000,
                nights = 2,
                totalHotelJpy = 30000
            }),

            "calculate_reimbursement_summary" => JsonSerializer.SerializeToElement(new
            {
                employeeId = "EMP-702",
                flightFareJpy = 48500,
                hotelTotalJpy = 30000,
                dailyAllowanceJpy = 6000,
                grandTotalJpy = 84500,
                approvalCode = "APPR-2026-84500"
            }),

            _ => JsonSerializer.SerializeToElement(new { error = $"Unknown tool: {name}" })
        };
    }

    private static JsonElement ExecuteWeatherLookup(JsonElement? args)
    {
        var city = args is { ValueKind: JsonValueKind.Object } obj &&
                   obj.TryGetProperty("city", out var cityProp)
            ? cityProp.GetString() ?? string.Empty
            : string.Empty;

        if (city.Contains("Sapporo", StringComparison.OrdinalIgnoreCase) ||
            city.Contains("札幌", StringComparison.Ordinal))
        {
            return JsonSerializer.SerializeToElement(new
            {
                city = "Sapporo",
                date = "2026-11-10",
                condition = "Sunny",
                temperatureC = 15,
                rainProbabilityPercent = 10
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            city = "Fukuoka",
            date = "2026-11-10",
            condition = "Heavy Rain",
            temperatureC = 19,
            rainProbabilityPercent = 90
        });
    }

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

