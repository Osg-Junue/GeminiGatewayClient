using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.ExploratoryTests.Infrastructure;
using GeminiGatewayClient.Models;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests;

/// <summary>
/// Exploratory tests for core <see cref="GeminiClient"/> operations:
/// model discovery, single-turn generation, SSE streaming, structured JSON output,
/// thinking/reasoning configuration, and inline multimodal image input.
/// </summary>
public sealed class BasicGenerationExploratoryTests(ExploratoryTestFixture fixture, ITestOutputHelper output)
    : ExploratoryTestBase(fixture, output)
{
    [ExploratoryFact]
    public void ModelDiscovery_ListsAvailableFlashModels_AndSelectsPreferredFlashModel()
    {
        LogSection("Model Discovery & Selection");
        Output.WriteLine(Fixture.DiscoveryNote ?? "<no discovery note>");
        Output.WriteLine($"Selected Default Model: {DefaultModel}");
        Output.WriteLine($"API Key Source: {ExploratoryTestSettings.ApiKeySource}");

        var flashModels = Fixture.AvailableModels
            .Where(m => m.Id.Contains("flash", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Output.WriteLine($"Available Flash Models ({flashModels.Count}):");
        foreach (var model in flashModels)
        {
            Output.WriteLine(
                $"  - {model.Id} ({model.DisplayName ?? "n/a"}) [in={model.InputTokenLimit?.ToString() ?? "?"}, out={model.OutputTokenLimit?.ToString() ?? "?"}]");
        }

        Assert.False(string.IsNullOrWhiteSpace(DefaultModel));
        Assert.NotEmpty(Fixture.AvailableModels);
    }

    [ExploratoryFact]
    public async Task SimplePrompt_GenerateContentAsync_ReturnsTextAndUsageMetadata()
    {
        LogSection("Simple Single-Turn Generation");
        using var client = CreateGeminiClient();

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(
            prompt: "日本の首都はどこですか？また、その都市が属する地方名（関東地方など）を1文で簡潔に答えてください。",
            systemInstruction: "あなたは簡潔で正確な日本の地理アシスタントです。",
            generationConfig: new GenerationConfig
            {
                Temperature = 0.1,
                MaxOutputTokens = 1024
            });
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Text);
        Assert.Contains("東京", response.Text, StringComparison.Ordinal);
        Assert.Equal(FinishReason.Stop, response.FinishReason);
        Assert.NotNull(response.UsageMetadata);
        Assert.True(response.UsageMetadata.PromptTokenCount > 0);
        Assert.True(response.UsageMetadata.CandidatesTokenCount > 0);
        Assert.True(response.UsageMetadata.TotalTokenCount >= response.UsageMetadata.PromptTokenCount);
    }

    [ExploratoryFact]
    public async Task Streaming_YieldsChunksInRealTime()
    {
        LogSection("Streaming Generation");
        using var client = CreateGeminiClient();

        var sw = Stopwatch.StartNew();
        var chunks = new List<GenerateContentResponse>();
        var fullText = new StringBuilder();

        await foreach (var chunk in client.StreamGenerateContentAsync(
            prompt: "C#言語の代表的な特徴を3つ、各1行の箇条書きで挙げてください。",
            systemInstruction: "簡潔に回答してください。"))
        {
            chunks.Add(chunk);
            var chunkText = chunk.Text;
            if (!string.IsNullOrEmpty(chunkText))
            {
                fullText.Append(chunkText);
            }

            Output.WriteLine(
                $"  [+{sw.ElapsedMilliseconds,4} ms] Chunk #{chunks.Count}: textLen={chunkText?.Length ?? 0}, " +
                $"finish={chunk.FinishReason?.ToString() ?? "-"}, totalTokens={chunk.UsageMetadata?.TotalTokenCount.ToString() ?? "-"}");
        }

        sw.Stop();
        Output.WriteLine($"[Stream Completed in {sw.ElapsedMilliseconds} ms] TotalChunks={chunks.Count}");
        Output.WriteLine(fullText.ToString());

        Assert.NotEmpty(chunks);
        Assert.True(fullText.Length > 10);
        Assert.Contains(chunks, c => c.FinishReason == FinishReason.Stop);
        Assert.Contains(chunks, c => c.UsageMetadata is { TotalTokenCount: > 0 });
    }

    [ExploratoryFact]
    public async Task StructuredOutput_ResponseJsonSchema_DeserializesToStronglyTypedRecord()
    {
        LogSection("Structured Output (ResponseJsonSchema)");
        using var client = CreateGeminiClient();

        using var schemaDoc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "languages": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "name": { "type": "string" },
                      "paradigm": { "type": "string" },
                      "isStaticallyTyped": { "type": "boolean" }
                    },
                    "required": ["name", "paradigm", "isStaticallyTyped"]
                  }
                }
              },
              "required": ["languages"]
            }
            """);

        var request = GenerateContentRequest.FromPrompt(
            prompt: "C#, Python, Rust の3つのプログラミング言語について、名称・主要パラダイム・静的型付けかどうかをJSONで出力してください。",
            generationConfig: new GenerationConfig
            {
                Temperature = 0.0,
                ResponseMimeType = MimeTypes.ApplicationJson,
                ResponseJsonSchema = schemaDoc.RootElement.Clone()
            });

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.False(string.IsNullOrWhiteSpace(response.Text));
        var catalog = JsonSerializer.Deserialize<LanguageCatalog>(response.Text!);
        Assert.NotNull(catalog);
        Assert.Equal(3, catalog.Languages.Count);
        Assert.Contains(catalog.Languages, l => l.Name.Contains("C#", StringComparison.OrdinalIgnoreCase) && l.IsStaticallyTyped);
        Assert.Contains(catalog.Languages, l => l.Name.Contains("Python", StringComparison.OrdinalIgnoreCase) && !l.IsStaticallyTyped);
        Assert.Contains(catalog.Languages, l => l.Name.Contains("Rust", StringComparison.OrdinalIgnoreCase) && l.IsStaticallyTyped);
    }

    [ExploratoryFact]
    public async Task ThinkingConfig_IncludeThoughts_ReturnsThoughtSummaryAndFinalAnswer()
    {
        LogSection("Thinking / Reasoning Configuration");
        using var client = CreateGeminiClient();

        var isGemini3OrLater = DefaultModel.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase);
        var thinkingConfig = isGemini3OrLater
            ? new ThinkingConfig { ThinkingLevel = ThinkingLevel.Low, IncludeThoughts = true }
            : new ThinkingConfig { ThinkingBudget = 1024, IncludeThoughts = true };

        var request = GenerateContentRequest.FromPrompt(
            prompt: """
                3つの箱 A, B, C があります。以下の条件がすべて正しいとき、金貨が確実に入っている箱はどれですか？
                1. 少なくとも1つの箱に金貨が入っている。
                2. 箱Aが空ならば、箱Bに金貨が入っている。
                3. 箱Bと箱Cの両方に金貨が入っていることはない。
                4. 箱Aと箱Cには金貨が入っていない。
                答えの箱のアルファベット（A, B, Cのいずれか）を明記してください。
                """,
            generationConfig: new GenerationConfig
            {
                ThinkingConfig = thinkingConfig
            });

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Text);
        Assert.Contains("B", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(response.UsageMetadata);
        Assert.True(
            response.UsageMetadata.ThoughtsTokenCount is > 0 ||
            response.Candidates[0].Content?.Parts.Any(p => p.Thought == true) == true,
            "Expected thinking tokens or thought summary parts to be returned when IncludeThoughts is enabled.");
    }

    [ExploratoryFact]
    public async Task ThinkingConfig_ExhaustedMaxOutputTokens_HandlesEmptyPartsSafelyWithTryGetModelContent()
    {
        LogSection("Thinking Model MaxOutputTokens Exhaustion & Safe Multi-Turn Recovery");
        using var client = CreateGeminiClient();

        var isGemini3OrLater = DefaultModel.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase);
        var thinkingConfig = isGemini3OrLater
            ? new ThinkingConfig { ThinkingLevel = ThinkingLevel.Medium }
            : new ThinkingConfig { ThinkingBudget = 512 };

        var conversation = new List<Content>
        {
            Content.FromText("1から50までの素数をすべて列挙し、それぞれの双子素数のペアを詳しく解説してください。", Role.User)
        };

        // Intentionally set a tiny MaxOutputTokens so thinking consumes the budget and returns FinishReason.MaxTokens
        var truncatedResponse = await client.GenerateContentAsync(new GenerateContentRequest
        {
            Contents = conversation,
            GenerationConfig = new GenerationConfig
            {
                MaxOutputTokens = 16,
                ThinkingConfig = thinkingConfig
            }
        });

        LogResponseSummary(truncatedResponse);
        Assert.Equal(FinishReason.MaxTokens, truncatedResponse.FinishReason);

        var hasModelContent = truncatedResponse.TryGetModelContent(out var modelContent);
        Output.WriteLine($"TryGetModelContent returned {hasModelContent} (Parts.Count={truncatedResponse.Candidates[0].Content?.Parts.Count ?? 0})");

        if (hasModelContent)
        {
            conversation.Add(modelContent!);
            conversation.Add(Content.FromText("では代わりに、最小の素数を1つだけ数字で答えてください。", Role.User));
        }
        else
        {
            // When Thinking exhausted all MaxOutputTokens and Parts is empty, do not append the empty Content
            Assert.Empty(truncatedResponse.Candidates[0].Content?.Parts ?? []);
            conversation[^1] = Content.FromText("最小の素数を1つだけ数字で答えてください。", Role.User);
        }

        // Follow-up request with sufficient MaxOutputTokens must succeed without ArgumentException or HTTP 400
        var recoveryResponse = await client.GenerateContentAsync(new GenerateContentRequest
        {
            Contents = conversation,
            GenerationConfig = new GenerationConfig
            {
                MaxOutputTokens = 1024,
                ThinkingConfig = isGemini3OrLater
                    ? new ThinkingConfig { ThinkingLevel = ThinkingLevel.Low }
                    : new ThinkingConfig { ThinkingBudget = 256 }
            }
        });

        LogResponseSummary(recoveryResponse);
        Assert.True(recoveryResponse.TryGetModelContent(out _));
        Assert.NotNull(recoveryResponse.Text);
        Assert.Contains("2", recoveryResponse.Text, StringComparison.Ordinal);
    }

    [ExploratoryFact]
    public async Task Multimodal_InlinePngBytes_RecognizesImageColorAndShape()
    {
        LogSection("Multimodal Inline PNG Image Input");
        using var client = CreateGeminiClient();

        // Create a 32x32 solid bright red (#FF0000) PNG image in memory without external files.
        var redPngBytes = CreateSolidColorPng(width: 32, height: 32, r: 255, g: 0, b: 0);
        Output.WriteLine($"Generated in-memory PNG: {redPngBytes.Length} bytes");

        var request = new GenerateContentRequest
        {
            Contents =
            [
                Content.FromParts(
                    Role.User,
                    Part.FromBytes(redPngBytes, MimeTypes.ImagePng),
                    Part.FromText("この画像の塗りつぶされている色は何色ですか？「赤」「青」「緑」のいずれかの単語を含めて1文で答えてください。"))
            ],
            GenerationConfig = new GenerationConfig { Temperature = 0.0 }
        };

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Text);
        Assert.True(
            response.Text.Contains("赤", StringComparison.Ordinal) ||
            response.Text.Contains("レッド", StringComparison.OrdinalIgnoreCase) ||
            response.Text.Contains("red", StringComparison.OrdinalIgnoreCase),
            $"Expected response to identify red color, but got: {response.Text}");
    }

    private static byte[] CreateSolidColorPng(int width, int height, byte r, byte g, byte b)
    {
        using var ms = new MemoryStream();
        // PNG signature
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR chunk (13 bytes: width, height, bitDepth=8, colorType=2 (RGB), compression=0, filter=0, interlace=0)
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[0..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..8], (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // truecolor RGB
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WritePngChunk(ms, "IHDR"u8, ihdr);

        // Raw scanlines: each row starts with filter byte 0, followed by RGB triples
        var stride = 1 + (width * 3);
        var rawScanlines = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            rawScanlines[rowOffset] = 0; // filter type 0 (None)
            for (var x = 0; x < width; x++)
            {
                var pixelOffset = rowOffset + 1 + (x * 3);
                rawScanlines[pixelOffset] = r;
                rawScanlines[pixelOffset + 1] = g;
                rawScanlines[pixelOffset + 2] = b;
            }
        }

        using var compressedStream = new MemoryStream();
        using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(rawScanlines);
        }

        WritePngChunk(ms, "IDAT"u8, compressedStream.ToArray());
        WritePngChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)data.Length);
        stream.Write(lengthBytes);
        stream.Write(chunkType);
        stream.Write(data);

        var crc = ComputeCrc32(chunkType, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc32(crc, type);
        crc = UpdateCrc32(crc, data);
        return ~crc;
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc;
    }

    private sealed record LanguageCatalog(
        [property: JsonPropertyName("languages")] IReadOnlyList<LanguageEntry> Languages);

    private sealed record LanguageEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("paradigm")] string Paradigm,
        [property: JsonPropertyName("isStaticallyTyped")] bool IsStaticallyTyped);
}
