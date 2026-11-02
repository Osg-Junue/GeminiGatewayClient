using System.Text.Json;
using GeminiAPIWrapper.Configurations;
using GeminiAPIWrapper.Extensions;
using GeminiAPIWrapper.Options;

namespace GeminiAPIWrapper.Tests;

public class SerializationTests
{
    [Fact]
    public void UnknownEnumValuesInResponse_DeserializeToUnknown_InsteadOfThrowing()
    {
        const string futureApiResponse = """
            {
              "promptFeedback": {
                "blockReason": "BRAND_NEW_BLOCK_REASON"
              },
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [ { "text": "Still readable" } ]
                  },
                  "finishReason": "FUTURE_FINISH_REASON_2027",
                  "safetyRatings": [
                    {
                      "category": "HARM_CATEGORY_FUTURE_RISK",
                      "probability": "ULTRA_LOW"
                    }
                  ]
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize(futureApiResponse, GeminiResponseJsonContext.Default.GeminiResponse);

        Assert.NotNull(response);
        Assert.Equal("Still readable", response.GetText());
        Assert.Equal(BlockReason.Unknown, response.GetBlockReason());
        Assert.Equal(FinishReason.Unknown, response.GetFinishReason());
        Assert.Equal(HarmCategory.Unknown, response.Candidates[0].SafetyRatings[0].Category);
        Assert.Equal(HarmProbability.Unknown, response.Candidates[0].SafetyRatings[0].Probability);
    }

    [Fact]
    public void ThinkingConfig_WithIncludeThoughtsOnly_DoesNotSerializeZeroThinkingBudget_AndUsesCamelCase()
    {
        var request = new GeminiRequest
        {
            Contents = [new Content { Parts = [new Part { Text = "Hi" }] }],
            GenerationConfig = new GenerationConfig
            {
                ThinkingConfig = new ThinkingConfig
                {
                    IncludeThoughts = true
                }
            }
        };

        var json = JsonSerializer.Serialize(request, GeminiRequestJsonContext.Default.GeminiRequest);
        using var doc = JsonDocument.Parse(json);
        var thinkingConfig = doc.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");

        Assert.True(thinkingConfig.GetProperty("includeThoughts").GetBoolean());
        Assert.False(thinkingConfig.TryGetProperty("include_thoughts", out _));
        Assert.False(thinkingConfig.TryGetProperty("thinkingBudget", out _));
        Assert.False(thinkingConfig.TryGetProperty("thinkingLevel", out _));
    }

    [Fact]
    public async Task ThinkingConfig_SettingBothBudgetAndLevel_ThrowsArgumentExceptionBeforeSending()
    {
        using var handler = new GeminiClientTests.RecordingHandler("{}");
        using var httpClient = new HttpClient(handler);
        using var service = GeminiServiceBuilder.Build(new GenerativeLanguageOptions
        {
            EndpointBase = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash",
            ApiKey = "key"
        }, httpClient);

        var badRequest = new GeminiRequest
        {
            Contents = [new Content { Parts = [new Part { Text = "Hi" }] }],
            GenerationConfig = new GenerationConfig
            {
                ThinkingConfig = new ThinkingConfig
                {
                    ThinkingBudget = 1024,
                    ThinkingLevel = ThinkingLevel.High
                }
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(badRequest));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public void GetText_EmptyCandidatesOrParts_ReturnsNullWithoutThrowing()
    {
        GeminiResponse? nullResponse = null;
        var emptyCandidates = new GeminiResponse { Candidates = [] };
        var emptyParts = new GeminiResponse
        {
            Candidates = [new Candidate { Content = new Content { Role = Role.Model, Parts = [] } }]
        };

        Assert.Null(nullResponse.GetText());
        Assert.Null(emptyCandidates.GetText());
        Assert.Null(emptyParts.GetText());
        Assert.Null(nullResponse.GetFunctionCall());
        Assert.Null(emptyCandidates.GetFunctionCall());
        Assert.Null(nullResponse.GetBlockReason());
        Assert.Null(nullResponse.GetFinishReason());
    }

    [Fact]
    public void GetText_ConcatenatesNonThoughtTextParts()
    {
        var response = new GeminiResponse
        {
            Candidates =
            [
                new Candidate
                {
                    Content = new Content
                    {
                        Role = Role.Model,
                        Parts =
                        [
                            new Part { Text = "Internal reasoning...", Thought = true },
                            new Part { Text = "Part 1, " },
                            new Part { Text = "Part 2." }
                        ]
                    }
                }
            ]
        };

        Assert.Equal("Part 1, Part 2.", response.GetText());
    }

    [Fact]
    public void InlineData_WithParameterizedAudioMimeType_RoundTripsCleanly()
    {
        const string ttsResponseJson = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "model",
                    "parts": [
                      {
                        "inlineData": {
                          "mimeType": "audio/L16;codec=pcm;rate=24000",
                          "data": "AQIDBA=="
                        }
                      }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize(ttsResponseJson, GeminiResponseJsonContext.Default.GeminiResponse);
        Assert.NotNull(response);
        Assert.Equal("audio/L16;codec=pcm;rate=24000", response.Candidates[0].Content?.Parts[0].InlineData?.MimeType);
    }
}
