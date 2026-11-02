using System.Text.Json;
using GeminiGatewayClient.Models;
using GeminiGatewayClient.Serialization;

namespace GeminiGatewayClient.Tests;

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

    [Fact]
    public void AllEnums_HaveUnknownFallback_AndRoundTripUnknownWireName()
    {
        const string json = """
            {
              "candidates": [
                {
                  "index": 0,
                  "content": {
                    "role": "function",
                    "parts": [
                      {
                        "executableCode": {
                          "language": "RUST_2030",
                          "code": "fn main() {}"
                        },
                        "codeExecutionResult": {
                          "outcome": "OUTCOME_QUANTUM",
                          "output": "ok"
                        },
                        "futurePartField": { "nested": 42 }
                      }
                    ]
                  },
                  "finishReason": "FUTURE_REASON",
                  "finishMessage": "Stopped due to future reason",
                  "groundingMetadata": { "webSearchQueries": ["gemini"] }
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 1,
                "candidatesTokenCount": 2,
                "totalTokenCount": 3,
                "futureModalityCount": 99
              }
            }
            """;

        var response = JsonSerializer.Deserialize(json, GeminiJsonContext.Default.GenerateContentResponse);
        Assert.NotNull(response);
        var candidate = Assert.Single(response.Candidates);
        Assert.Equal(Role.Function, candidate.Content?.Role);
        Assert.Equal(FinishReason.Unknown, candidate.FinishReason);
        Assert.Equal("Stopped due to future reason", candidate.FinishMessage);
        Assert.True(candidate.ExtensionData?.ContainsKey("groundingMetadata"));
        Assert.True(response.UsageMetadata?.ExtensionData?.ContainsKey("futureModalityCount"));

        var part = Assert.Single(candidate.Content!.Parts);
        Assert.Equal(CodeExecutionLanguage.Unknown, part.ExecutableCode?.Language);
        Assert.Equal(CodeExecutionOutcome.Unknown, part.CodeExecutionResult?.Outcome);
        Assert.True(part.ExtensionData?.ContainsKey("futurePartField"));

        // Re-serializing Unknown enum values writes "UNKNOWN" instead of numeric/unspecified default
        var reserialized = JsonSerializer.Serialize(response, GeminiJsonContext.Default.GenerateContentResponse);
        using var doc = JsonDocument.Parse(reserialized);
        var candElem = doc.RootElement.GetProperty("candidates")[0];
        Assert.Equal("UNKNOWN", candElem.GetProperty("finishReason").GetString());
        Assert.Equal("function", candElem.GetProperty("content").GetProperty("role").GetString());
        Assert.True(candElem.TryGetProperty("groundingMetadata", out _));
    }

    [Fact]
    public void Schema_DefaultDoesNotEmitStringType_AndUnknownEnumsFallbackToUnknown()
    {
        var schema = new Schema
        {
            Properties = new Dictionary<string, Schema>
            {
                ["age"] = new Schema { Type = SchemaType.Integer }
            }
        };

        var request = new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            GenerationConfig = new GenerationConfig
            {
                ResponseMimeType = MimeTypes.ApplicationJson,
                ResponseSchema = schema
            }
        };

        var json = JsonSerializer.Serialize(request, GeminiJsonContext.Default.GenerateContentRequest);
        using var doc = JsonDocument.Parse(json);
        var rootSchema = doc.RootElement.GetProperty("generationConfig").GetProperty("responseSchema");
        Assert.False(rootSchema.TryGetProperty("type", out _));
        Assert.Equal("INTEGER", rootSchema.GetProperty("properties").GetProperty("age").GetProperty("type").GetString());
    }

    [Fact]
    public void Validate_RejectsEmptyPartsOrNullElements()
    {
        var emptyPartsRequest = new GenerateContentRequest
        {
            Contents = [new Content { Role = Role.User, Parts = [] }]
        };
        Assert.Throws<ArgumentException>(() => emptyPartsRequest.Validate());

        var nullContentRequest = new GenerateContentRequest
        {
            Contents = [null!]
        };
        Assert.Throws<ArgumentException>(() => nullContentRequest.Validate());

        var nullPartRequest = new GenerateContentRequest
        {
            Contents = [new Content { Role = Role.User, Parts = [null!] }]
        };
        Assert.Throws<ArgumentException>(() => nullPartRequest.Validate());
    }

    [Fact]
    public void VideoOffset_SerializesAsProtobufDurationString_AndDeserializesBothStringAndObjectFormats()
    {
        var request = new GenerateContentRequest
        {
            Contents =
            [
                new Content
                {
                    Role = Role.User,
                    Parts =
                    [
                        new Part
                        {
                            FileData = new FileData { FileUri = "https://example.com/video.mp4", MimeType = "video/mp4" },
                            VideoMetadata = new VideoMetadata
                            {
                                StartOffset = new VideoOffset { Seconds = 10 },
                                EndOffset = VideoOffset.FromTimeSpan(TimeSpan.FromSeconds(42.5)),
                                Fps = 1.0
                            }
                        }
                    ]
                }
            ]
        };

        var json = JsonSerializer.Serialize(request, GeminiJsonContext.Default.GenerateContentRequest);
        using var doc = JsonDocument.Parse(json);
        var videoMeta = doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("videoMetadata");
        Assert.Equal("10s", videoMeta.GetProperty("startOffset").GetString());
        Assert.Equal("42.5s", videoMeta.GetProperty("endOffset").GetString());

        var roundTripped = JsonSerializer.Deserialize(json, GeminiJsonContext.Default.GenerateContentRequest);
        var rtMeta = roundTripped!.Contents[0].Parts[0].VideoMetadata!;
        Assert.Equal(10, rtMeta.StartOffset?.Seconds);
        Assert.Null(rtMeta.StartOffset?.Nanos);
        Assert.Equal(42, rtMeta.EndOffset?.Seconds);
        Assert.Equal(500_000_000, rtMeta.EndOffset?.Nanos);
        Assert.Equal(TimeSpan.FromSeconds(42.5), rtMeta.EndOffset?.ToTimeSpan());

        const string objectFormatJson = """
            {
              "contents": [
                {
                  "role": "user",
                  "parts": [
                    {
                      "text": "clip",
                      "videoMetadata": {
                        "startOffset": { "seconds": 5, "nanos": 250000000 },
                        "endOffset": "15.75s"
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var parsedFromBoth = JsonSerializer.Deserialize(objectFormatJson, GeminiJsonContext.Default.GenerateContentRequest);
        var parsedMeta = parsedFromBoth!.Contents[0].Parts[0].VideoMetadata!;
        Assert.Equal(5, parsedMeta.StartOffset?.Seconds);
        Assert.Equal(250_000_000, parsedMeta.StartOffset?.Nanos);
        Assert.Equal(15, parsedMeta.EndOffset?.Seconds);
        Assert.Equal(750_000_000, parsedMeta.EndOffset?.Nanos);
    }

    [Fact]
    public void NullCollectionsInJson_NormalizeToEmptyLists_InsteadOfNull()
    {
        const string jsonWithNullArrays = """
            {
              "candidates": null,
              "promptFeedback": {
                "safetyRatings": null
              }
            }
            """;

        var response = JsonSerializer.Deserialize(jsonWithNullArrays, GeminiJsonContext.Default.GenerateContentResponse);
        Assert.NotNull(response);
        Assert.NotNull(response.Candidates);
        Assert.Empty(response.Candidates);
        Assert.NotNull(response.PromptFeedback?.SafetyRatings);
        Assert.Empty(response.PromptFeedback.SafetyRatings);
    }

    [Fact]
    public void Validate_RejectsEmptyPartSystemInstructionNullToolsSafetySettingsAndUnknownRequestEnums()
    {
        var emptyPartObj = new GenerateContentRequest
        {
            Contents = [new Content { Role = Role.User, Parts = [new Part()] }]
        };
        Assert.Throws<ArgumentException>(() => emptyPartObj.Validate());

        var emptySystemInstruction = new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            SystemInstruction = new Content { Parts = [] }
        };
        Assert.Throws<ArgumentException>(() => emptySystemInstruction.Validate());

        var nullToolElement = new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            Tools = [null!]
        };
        Assert.Throws<ArgumentException>(() => nullToolElement.Validate());

        var nullSafetyElement = new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            SafetySettings = [null!]
        };
        Assert.Throws<ArgumentException>(() => nullSafetyElement.Validate());

        var unknownSafetyCategory = new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            SafetySettings = [new SafetySetting { Category = HarmCategory.Unknown, Threshold = HarmBlockThreshold.BlockNone }]
        };
        Assert.Throws<ArgumentException>(() => unknownSafetyCategory.Validate());

        var unknownThinkingLevel = new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            GenerationConfig = new GenerationConfig { ThinkingConfig = new ThinkingConfig { ThinkingLevel = ThinkingLevel.Unknown } }
        };
        Assert.Throws<ArgumentException>(() => unknownThinkingLevel.Validate());

        var unknownFunctionCallingMode = new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            ToolConfig = new ToolConfig { FunctionCallingConfig = new FunctionCallingConfig { Mode = FunctionCallingMode.Unknown } }
        };
        Assert.Throws<ArgumentException>(() => unknownFunctionCallingMode.Validate());
    }

    [Fact]
    public void NullPropertiesInRequiredAndNonNullableModelFields_NormalizeToNonNullDefaults()
    {
        const string jsonWithNulls = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      { "inlineData": { "mimeType": null, "data": null } },
                      { "fileData": { "fileUri": null } },
                      { "functionCall": { "name": null } },
                      { "functionResponse": { "name": null } }
                    ]
                  },
                  "logprobsResult": {
                    "chosenCandidates": [
                      { "token": null, "logProbability": -0.5 }
                    ]
                  }
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize(jsonWithNulls, GeminiJsonContext.Default.GenerateContentResponse);
        Assert.NotNull(response);
        var parts = response.Candidates[0].Content!.Parts;
        Assert.Equal("", parts[0].InlineData!.MimeType);
        Assert.Empty(parts[0].InlineData!.Data);
        Assert.Equal("", parts[1].FileData!.FileUri);
        Assert.Equal("", parts[2].FunctionCall!.Name);
        Assert.Equal("", parts[3].FunctionResponse!.Name);
        Assert.Equal("", response.Candidates[0].LogprobsResult!.ChosenCandidates![0].Token);
    }

    [Fact]
    public void Validate_RejectsMultiplePartDataFields_EmptyBlob_EmptyFileUri_AndEmptyFunctionDeclarationName()
    {
        var multipleDataFields = new GenerateContentRequest
        {
            Contents =
            [
                new Content
                {
                    Role = Role.User,
                    Parts =
                    [
                        new Part
                        {
                            Text = "hello",
                            InlineData = new Blob { MimeType = "image/png", Data = [1, 2, 3] }
                        }
                    ]
                }
            ]
        };
        Assert.Throws<ArgumentException>(() => multipleDataFields.Validate());

        var emptyBlobData = new GenerateContentRequest
        {
            Contents =
            [
                new Content
                {
                    Role = Role.User,
                    Parts = [new Part { InlineData = new Blob { MimeType = "image/png", Data = [] } }]
                }
            ]
        };
        Assert.Throws<ArgumentException>(() => emptyBlobData.Validate());

        var emptyFileUri = new GenerateContentRequest
        {
            Contents =
            [
                new Content
                {
                    Role = Role.User,
                    Parts = [new Part { FileData = new FileData { FileUri = "   " } }]
                }
            ]
        };
        Assert.Throws<ArgumentException>(() => emptyFileUri.Validate());

        var nullFunctionResponsePayload = new GenerateContentRequest
        {
            Contents =
            [
                new Content
                {
                    Role = Role.User,
                    Parts = [new Part { FunctionResponse = new FunctionResponse { Name = "fn", Response = null } }]
                }
            ]
        };
        Assert.Throws<ArgumentException>(() => nullFunctionResponsePayload.Validate());

        var emptyFunctionDeclName = new GenerateContentRequest
        {
            Contents = [Content.FromText("Hi")],
            Tools = [new Tool { FunctionDeclarations = [new FunctionDeclaration { Name = "  " }] }]
        };
        Assert.Throws<ArgumentException>(() => emptyFunctionDeclName.Validate());
    }

    [Fact]
    public void GenerateContentResponse_IsBlocked_ReflectsPromptBlockReasonAndCandidateSafetyFinishReasons()
    {
        var normal = new GenerateContentResponse
        {
            Candidates = [new Candidate { FinishReason = FinishReason.Stop }]
        };
        Assert.False(normal.IsBlocked);

        var promptBlocked = new GenerateContentResponse
        {
            PromptFeedback = new PromptFeedback { BlockReason = BlockReason.Safety }
        };
        Assert.True(promptBlocked.IsBlocked);

        var candidateBlocked = new GenerateContentResponse
        {
            Candidates = [new Candidate { FinishReason = FinishReason.Safety }]
        };
        Assert.True(candidateBlocked.IsBlocked);
    }

    [Fact]
    public void NewSpecProperties_MediaResolution_HarmSeverity_AndSchemaKeywords_RoundTripCleanly()
    {
        var request = new GenerateContentRequest
        {
            Contents = [Content.FromText("Generate")],
            Store = false,
            GenerationConfig = new GenerationConfig
            {
                ResponseModalities = ["TEXT", "AUDIO"],
                MediaResolution = MediaResolution.High,
                EnableEnhancedCivicAnswers = true,
                ResponseMimeType = MimeTypes.ApplicationJson,
                ResponseSchema = new Schema
                {
                    Type = SchemaType.Object,
                    Title = "Result",
                    PropertyOrdering = ["score", "tags"],
                    Properties = new Dictionary<string, Schema>
                    {
                        ["score"] = new Schema { Type = SchemaType.Number, Minimum = 0.0, Maximum = 100.0 },
                        ["tags"] = new Schema
                        {
                            Type = SchemaType.Array,
                            MinItems = 1,
                            MaxItems = 5,
                            Items = new Schema
                            {
                                AnyOf = [new Schema { Type = SchemaType.String }, new Schema { Type = SchemaType.Integer }]
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(request, GeminiJsonContext.Default.GenerateContentRequest);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("store").GetBoolean());
        var genConfig = doc.RootElement.GetProperty("generationConfig");
        Assert.Equal("MEDIA_RESOLUTION_HIGH", genConfig.GetProperty("mediaResolution").GetString());
        Assert.True(genConfig.GetProperty("enableEnhancedCivicAnswers").GetBoolean());
        Assert.Equal(2, genConfig.GetProperty("responseModalities").GetArrayLength());

        var schemaElem = genConfig.GetProperty("responseSchema");
        Assert.Equal("Result", schemaElem.GetProperty("title").GetString());
        Assert.Equal(2, schemaElem.GetProperty("propertyOrdering").GetArrayLength());
        Assert.Equal(100.0, schemaElem.GetProperty("properties").GetProperty("score").GetProperty("maximum").GetDouble());
        Assert.Equal(5, schemaElem.GetProperty("properties").GetProperty("tags").GetProperty("maxItems").GetInt64());
        Assert.Equal(2, schemaElem.GetProperty("properties").GetProperty("tags").GetProperty("items").GetProperty("anyOf").GetArrayLength());

        const string safetyResponseJson = """
            {
              "candidates": [
                {
                  "index": 0,
                  "safetyRatings": [
                    {
                      "category": "HARM_CATEGORY_HATE_SPEECH",
                      "probability": "LOW",
                      "probabilityScore": 0.125,
                      "severity": "HARM_SEVERITY_LOW",
                      "severityScore": 0.25
                    },
                    {
                      "category": "HARM_CATEGORY_HARASSMENT",
                      "probability": "NEGLIGIBLE",
                      "severity": "FUTURE_SEVERITY_LEVEL"
                    }
                  ]
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize(safetyResponseJson, GeminiJsonContext.Default.GenerateContentResponse);
        Assert.NotNull(response);
        var ratings = response.Candidates[0].SafetyRatings;
        Assert.Equal(HarmSeverity.Low, ratings[0].Severity);
        Assert.Equal(0.125f, ratings[0].ProbabilityScore);
        Assert.Equal(0.25f, ratings[0].SeverityScore);
        Assert.Equal(HarmSeverity.Unknown, ratings[1].Severity);
    }
}
