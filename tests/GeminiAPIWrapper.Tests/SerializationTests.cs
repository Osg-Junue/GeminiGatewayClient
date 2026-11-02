using System.Text.Json;
using GeminiAPIWrapper.Models;
using GeminiAPIWrapper.Serialization;

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

        var response = JsonSerializer.Deserialize(futureApiResponse, GeminiJsonContext.Default.GenerateContentResponse);

        Assert.NotNull(response);
        Assert.Equal("Still readable", response.Text);
        Assert.Equal(BlockReason.Unknown, response.BlockReason);
        Assert.Equal(FinishReason.Unknown, response.FinishReason);
        Assert.Equal(HarmCategory.Unknown, response.Candidates[0].SafetyRatings[0].Category);
        Assert.Equal(HarmProbability.Unknown, response.Candidates[0].SafetyRatings[0].Probability);
    }

    [Fact]
    public void ThinkingConfig_WithIncludeThoughtsOnly_DoesNotSerializeZeroThinkingBudget_AndUsesCamelCase()
    {
        var request = GenerateContentRequest.FromPrompt(
            "Hi",
            generationConfig: new GenerationConfig
            {
                ThinkingConfig = new ThinkingConfig
                {
                    IncludeThoughts = true
                }
            });

        var json = JsonSerializer.Serialize(request, GeminiJsonContext.Default.GenerateContentRequest);
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
        using var handler = new RecordingHandler("{}");
        using var httpClient = new HttpClient(handler);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Model = "gemini-2.5-flash",
            ApiKey = "key"
        }, httpClient);

        var badRequest = GenerateContentRequest.FromPrompt(
            "Hi",
            generationConfig: new GenerationConfig
            {
                ThinkingConfig = new ThinkingConfig
                {
                    ThinkingBudget = 1024,
                    ThinkingLevel = ThinkingLevel.High
                }
            });

        await Assert.ThrowsAsync<ArgumentException>(() => client.GenerateContentAsync(badRequest));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public void ConvenienceProperties_EmptyCandidatesOrParts_ReturnSafeDefaults()
    {
        var emptyCandidates = new GenerateContentResponse { Candidates = [] };
        var emptyParts = new GenerateContentResponse
        {
            Candidates = [new Candidate { Content = new Content { Role = Role.Model, Parts = [] } }]
        };

        Assert.Null(emptyCandidates.Text);
        Assert.Null(emptyParts.Text);
        Assert.Empty(emptyCandidates.FunctionCalls);
        Assert.Empty(emptyParts.FunctionCalls);
        Assert.Null(emptyCandidates.BlockReason);
        Assert.Null(emptyCandidates.FinishReason);
    }

    [Fact]
    public void Text_ConcatenatesNonThoughtTextParts()
    {
        var response = new GenerateContentResponse
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

        Assert.Equal("Part 1, Part 2.", response.Text);
    }

    [Fact]
    public void Blob_WithByteArrayAndParameterizedAudioMimeType_RoundTripsBase64Cleanly()
    {
        var part = Part.FromBytes([1, 2, 3, 4], "audio/L16;codec=pcm;rate=24000");
        var request = new GenerateContentRequest
        {
            Contents = [Content.FromParts(Role.User, part)]
        };

        var serialized = JsonSerializer.Serialize(request, GeminiJsonContext.Default.GenerateContentRequest);
        using var reqDoc = JsonDocument.Parse(serialized);
        var inlineData = reqDoc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("inlineData");
        Assert.Equal("audio/L16;codec=pcm;rate=24000", inlineData.GetProperty("mimeType").GetString());
        Assert.Equal("AQIDBA==", inlineData.GetProperty("data").GetString());

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

        var response = JsonSerializer.Deserialize(ttsResponseJson, GeminiJsonContext.Default.GenerateContentResponse);
        Assert.NotNull(response);
        var blob = response.Candidates[0].Content?.Parts[0].InlineData;
        Assert.NotNull(blob);
        Assert.Equal("audio/L16;codec=pcm;rate=24000", blob.MimeType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, blob.Data);
    }
}
