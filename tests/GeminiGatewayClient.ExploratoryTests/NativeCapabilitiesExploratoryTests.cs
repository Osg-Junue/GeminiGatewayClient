using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiGatewayClient.ExploratoryTests.Infrastructure;
using GeminiGatewayClient.Models;
using Xunit.Abstractions;

namespace GeminiGatewayClient.ExploratoryTests;

/// <summary>
/// Exploratory tests for native <see cref="IGeminiClient"/> capabilities:
/// multimodal audio (PCM WAV), documents (PDF 1.4), image media resolution token scaling,
/// server-side Python code execution (<see cref="CodeExecutionTool"/>),
/// OpenAPI 3.03 structured schema output (<see cref="Schema"/>),
/// custom safety thresholds &amp; ratings (<see cref="SafetySetting"/>),
/// tool invocation suppression (<see cref="FunctionCallingMode.None"/>),
/// and context caching wire propagation (<see cref="GenerateContentRequest.CachedContent"/>).
/// </summary>
public sealed class NativeCapabilitiesExploratoryTests(ExploratoryTestFixture fixture, ITestOutputHelper output)
    : ExploratoryTestBase(fixture, output)
{
    [ExploratoryFact]
    public async Task Multimodal_InlineWavAudioBytes_RecognizesAudioSignal()
    {
        LogSection("TC-NAT-01: Multimodal Inline WAV Audio Bytes Recognition");
        using var client = CreateGeminiClient();

        var wavBytes = CreateSineWaveWav(sampleRate: 16000, frequency: 440.0, durationSeconds: 1.0);
        Output.WriteLine($"Generated WAV bytes: length={wavBytes.Length} (16kHz 16-bit Mono 440Hz Sine Wave)");

        var request = new GenerateContentRequest
        {
            Contents =
            [
                Content.FromParts(
                    Role.User,
                    Part.FromText("添付された音声データの内容を解析し、どのような音（または周波数、トーン、正弦波、ビープ音、長さなど）が記録されているかを説明してください。"),
                    Part.FromBytes(wavBytes, MimeTypes.AudioWav))
            ]
        };

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Text);
        Assert.NotEmpty(response.Text);

        var text = response.Text;
        var mentionsAudio = text.Contains("音", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("周波数", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("トーン", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("正弦波", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("ビープ", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("tone", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("sine", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("beep", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("sound", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("440", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("秒", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("second", StringComparison.OrdinalIgnoreCase);

        Assert.True(mentionsAudio, $"Expected response to describe audio characteristics, but got: {text}");
        Assert.NotNull(response.UsageMetadata);
        Assert.True(response.UsageMetadata.PromptTokenCount > 0, "Expected non-zero prompt token count for audio input.");
    }

    [ExploratoryFact]
    public async Task Multimodal_InlinePdfDocumentBytes_ExtractsStructuredContent()
    {
        LogSection("TC-NAT-02: Multimodal Inline PDF Document Bytes Extraction");
        using var client = CreateGeminiClient();

        const string confidentialCode = "ALPHA-7729";
        var pdfBytes = CreateMinimalPdf($"Gemini Gateway Client Specification v1.0, Confidential Code: {confidentialCode}");
        Output.WriteLine($"Generated minimal PDF bytes: length={pdfBytes.Length}");

        var request = new GenerateContentRequest
        {
            Contents =
            [
                Content.FromParts(
                    Role.User,
                    Part.FromText("添付された PDF ドキュメントを精査し、記載されている Confidential Code の値を抽出して答えてください。"),
                    Part.FromBytes(pdfBytes, MimeTypes.Pdf))
            ]
        };

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Text);
        Assert.Contains(confidentialCode, response.Text, StringComparison.Ordinal);
        Assert.NotNull(response.UsageMetadata);
        Assert.True(response.UsageMetadata.PromptTokenCount > 0, "Expected non-zero prompt token count for PDF document input.");
    }

    [ExploratoryFact]
    public async Task Multimodal_MediaResolution_LowVsMedium_DemonstratesDifferentialTokenConsumption()
    {
        LogSection("TC-NAT-03: MediaResolution Low vs Medium Token Consumption Differential");
        using var client = CreateGeminiClient();

        // Use a 512x512 image per Challenger 2 finding to ensure distinct multi-tile tokenization between Low and Medium
        var pngBytes = CreateSolidColorPng(512, 512, 100, 180, 240);
        Output.WriteLine($"Generated 512x512 PNG bytes: length={pngBytes.Length}");

        var promptText = "この画像の主要な色と形状を1文で簡潔に説明してください。";

        // 1. Request with MediaResolution.Low
        var lowRequest = new GenerateContentRequest
        {
            GenerationConfig = new GenerationConfig
            {
                MediaResolution = MediaResolution.Low
            },
            Contents =
            [
                Content.FromParts(
                    Role.User,
                    Part.FromText(promptText),
                    Part.FromBytes(pngBytes, MimeTypes.ImagePng))
            ]
        };

        var swLow = Stopwatch.StartNew();
        var lowResponse = await client.GenerateContentAsync(lowRequest);
        swLow.Stop();
        Output.WriteLine($"[MediaResolution.Low] PromptTokens={lowResponse.UsageMetadata?.PromptTokenCount}, CandidatesTokens={lowResponse.UsageMetadata?.CandidatesTokenCount} ({swLow.ElapsedMilliseconds} ms)");

        // 2. Request with MediaResolution.Medium
        var medRequest = new GenerateContentRequest
        {
            GenerationConfig = new GenerationConfig
            {
                MediaResolution = MediaResolution.Medium
            },
            Contents =
            [
                Content.FromParts(
                    Role.User,
                    Part.FromText(promptText),
                    Part.FromBytes(pngBytes, MimeTypes.ImagePng))
            ]
        };

        var swMed = Stopwatch.StartNew();
        var medResponse = await client.GenerateContentAsync(medRequest);
        swMed.Stop();
        Output.WriteLine($"[MediaResolution.Medium] PromptTokens={medResponse.UsageMetadata?.PromptTokenCount}, CandidatesTokens={medResponse.UsageMetadata?.CandidatesTokenCount} ({swMed.ElapsedMilliseconds} ms)");

        var lowTokens = lowResponse.UsageMetadata?.PromptTokenCount ?? 0;
        var medTokens = medResponse.UsageMetadata?.PromptTokenCount ?? 0;

        Output.WriteLine($"Token Comparison: Low={lowTokens} vs Medium={medTokens} (Delta={medTokens - lowTokens})");

        Assert.NotNull(lowResponse.Text);
        Assert.NotNull(medResponse.Text);
        Assert.True(lowTokens > 0, "Expected non-zero prompt tokens for Low resolution.");
        Assert.True(medTokens > 0, "Expected non-zero prompt tokens for Medium resolution.");
        Assert.True(
            lowTokens < medTokens,
            $"Expected MediaResolution.Low ({lowTokens} tokens) to consume fewer prompt tokens than MediaResolution.Medium ({medTokens} tokens).");
    }

    [ExploratoryFact]
    public async Task NativeTools_ServerSidePythonCodeExecution_ExecutesAlgorithmAndReturnsResult()
    {
        LogSection("TC-NAT-04: Native Server-Side Python Code Execution");
        using var client = CreateGeminiClient();

        // 1 to 100 multiples of 7: 7, 14, 21, ..., 98 -> sum is 735.
        // Prompt explicitly directs code execution per Challenger 1 finding.
        var prompt = "必ず提供された Python コード実行ツール（Code Execution）を使用してプログラムを実行し、結果を求めてください。1 から 100 までの整数のうち 7 の倍数の合計を計算する Python スクリプトを実行し、計算結果を出力してください。暗算や推論のみでの回答は禁止します。";

        var request = new GenerateContentRequest
        {
            Contents = [Content.FromText(prompt)],
            Tools =
            [
                new Tool
                {
                    CodeExecution = new CodeExecutionTool()
                }
            ]
        };

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Candidates);
        Assert.NotEmpty(response.Candidates);

        var candidate = response.Candidates[0];
        Assert.NotNull(candidate.Content?.Parts);
        var parts = candidate.Content.Parts;

        // Verify Part.ExecutableCode and Part.CodeExecutionResult
        var executableCodePart = parts.FirstOrDefault(p => p.ExecutableCode is not null);
        var codeResultPart = parts.FirstOrDefault(p => p.CodeExecutionResult is not null);

        Assert.NotNull(executableCodePart);
        Assert.NotNull(executableCodePart.ExecutableCode);
        Output.WriteLine($"  [ExecutableCode ({executableCodePart.ExecutableCode.Language})]:\n{executableCodePart.ExecutableCode.Code}");
        Assert.Equal(CodeExecutionLanguage.Python, executableCodePart.ExecutableCode.Language);
        Assert.False(string.IsNullOrWhiteSpace(executableCodePart.ExecutableCode.Code));

        Assert.NotNull(codeResultPart);
        Assert.NotNull(codeResultPart.CodeExecutionResult);
        Output.WriteLine($"  [CodeExecutionResult ({codeResultPart.CodeExecutionResult.Outcome})]:\n{codeResultPart.CodeExecutionResult.Output}");
        Assert.Equal(CodeExecutionOutcome.Ok, codeResultPart.CodeExecutionResult.Outcome);

        Assert.NotNull(response.Text);
        // The sum of multiples of 7 from 1 to 100 is 735.
        Assert.Contains("735", response.Text, StringComparison.Ordinal);
    }

    [ExploratoryFact]
    public async Task StructuredOutput_NativeOpenApiSchema_ValidatesAndDeserializesContract()
    {
        LogSection("TC-NAT-05: Native OpenAPI 3.03 Schema Structured Output");
        using var client = CreateGeminiClient();

        var schema = new Schema
        {
            Type = SchemaType.Object,
            Description = "Corporate entity profile",
            Properties = new Dictionary<string, Schema>
            {
                ["companyName"] = new() { Type = SchemaType.String, Description = "Official registered company name" },
                ["foundedYear"] = new() { Type = SchemaType.Integer, Description = "Year the company was founded (e.g. 1998)" },
                ["isPublic"] = new() { Type = SchemaType.Boolean, Description = "Whether the company is publicly traded" },
                ["headquarters"] = new() { Type = SchemaType.String, Description = "Headquarters city and state/country" }
            },
            Required = ["companyName", "foundedYear", "isPublic", "headquarters"]
        };

        var request = new GenerateContentRequest
        {
            GenerationConfig = new GenerationConfig
            {
                ResponseMimeType = MimeTypes.ApplicationJson,
                ResponseSchema = schema
            },
            Contents =
            [
                Content.FromText("Google LLC についての企業概要情報を指定スキーマに従って JSON で出力してください。")
            ]
        };

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Text);
        Output.WriteLine($"JSON Response:\n{response.Text}");

        var profile = JsonSerializer.Deserialize<CompanyProfile>(response.Text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(profile);
        Assert.Contains("Google", profile.CompanyName, StringComparison.OrdinalIgnoreCase);
        Assert.True(profile.FoundedYear is >= 1990 and <= 2005, $"Expected foundedYear around 1998, but got {profile.FoundedYear}");
        Assert.False(string.IsNullOrWhiteSpace(profile.Headquarters));
    }

    [ExploratoryFact]
    public async Task SafetySettings_CustomThresholds_ReturnsPopulatedSafetyRatings()
    {
        LogSection("TC-NAT-06: SafetySettings Custom Thresholds & Populated SafetyRatings");
        using var client = CreateGeminiClient();

        var request = new GenerateContentRequest
        {
            SafetySettings =
            [
                new SafetySetting
                {
                    Category = HarmCategory.Harassment,
                    Threshold = HarmBlockThreshold.BlockOnlyHigh
                },
                new SafetySetting
                {
                    Category = HarmCategory.HateSpeech,
                    Threshold = HarmBlockThreshold.BlockOnlyHigh
                },
                new SafetySetting
                {
                    Category = HarmCategory.SexuallyExplicit,
                    Threshold = HarmBlockThreshold.BlockOnlyHigh
                },
                new SafetySetting
                {
                    Category = HarmCategory.DangerousContent,
                    Threshold = HarmBlockThreshold.BlockOnlyHigh
                }
            ],
            Contents =
            [
                Content.FromText("おはようございます。業務開始時の親切で前向きな挨拶メッセージを1行作成してください。")
            ]
        };

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.False(response.IsBlocked, "Response should not be blocked for benign prompt.");
        Assert.NotNull(response.Candidates);
        Assert.NotEmpty(response.Candidates);

        var candidate = response.Candidates[0];
        Assert.NotNull(candidate.SafetyRatings);
        Assert.NotEmpty(candidate.SafetyRatings);

        Output.WriteLine("\n--- Candidate Safety Ratings ---");
        foreach (var rating in candidate.SafetyRatings)
        {
            Output.WriteLine($"  Category={rating.Category}, Probability={rating.Probability}, Blocked={rating.Blocked}");
            Assert.True(Enum.IsDefined(rating.Category), $"Invalid harm category: {rating.Category}");
            Assert.True(Enum.IsDefined(rating.Probability), $"Invalid harm probability: {rating.Probability}");
        }

        Assert.NotNull(response.Text);
        Assert.NotEmpty(response.Text);
    }

    [ExploratoryFact]
    public async Task ToolConfig_FunctionCallingModeNone_SuppressesDeclaredTools()
    {
        LogSection("TC-NAT-07: ToolConfig FunctionCallingMode.None Suppresses Declared Tools");
        using var client = CreateGeminiClient();

        using var paramsDoc = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "city": { "type": "string", "description": "City name" }
              },
              "required": ["city"]
            }
            """);

        var weatherTool = new Tool
        {
            FunctionDeclarations =
            [
                new FunctionDeclaration
                {
                    Name = "fetch_live_weather_report",
                    Description = "Fetches live weather reports and temperatures for any city worldwide.",
                    ParametersJsonSchema = paramsDoc.RootElement.Clone()
                }
            ]
        };

        var request = new GenerateContentRequest
        {
            Contents = [Content.FromText("東京の現在の天気を調べて教えてください。")],
            Tools = [weatherTool],
            ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig
                {
                    Mode = FunctionCallingMode.None
                }
            }
        };

        var sw = Stopwatch.StartNew();
        var response = await client.GenerateContentAsync(request);
        sw.Stop();

        LogResponseSummary(response, sw.Elapsed);

        Assert.NotNull(response.Candidates);
        Assert.NotEmpty(response.Candidates);

        var candidate = response.Candidates[0];
        var parts = candidate.Content?.Parts ?? [];

        // Verify that function calling was completely suppressed by Mode.None
        var functionCallParts = parts.Where(p => p.FunctionCall is not null).ToList();
        Assert.Empty(functionCallParts);

        Assert.NotNull(response.Text);
        Assert.NotEmpty(response.Text);
        Output.WriteLine($"Suppressed tool response text: {response.Text}");
    }

    [ExploratoryFact]
    public async Task ContextCaching_CachedContentWirePropagation_TransmitsResourceIdentifier()
    {
        LogSection("TC-NAT-08: Context Caching Wire Propagation (Resource Identifier Transmission)");
        using var client = CreateGeminiClient();

        const string cacheResourceName = "cachedContents/exploratory-nonexistent-cache-resource-12345";

        var request = new GenerateContentRequest
        {
            CachedContent = cacheResourceName,
            Contents =
            [
                Content.FromText("キャッシュされたドキュメントの主要なポイントを要約してください。")
            ]
        };

        Output.WriteLine($"Sending GenerateContentRequest with CachedContent='{cacheResourceName}'");

        var ex = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync(request));

        Output.WriteLine($"Caught expected GeminiApiException: StatusCode={ex.StatusCode}, Message={ex.Message}");

        // Invariant: Google API rejects non-existent or invalid cachedContent resource with HTTP 403 (PermissionDenied: not found), 404 (NotFound), or 400 (BadRequest)
        Assert.True(
            ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
            $"Expected HttpStatusCode.NotFound, BadRequest, or Forbidden, but received: {ex.StatusCode}");

        Assert.Contains("CachedContent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreateSineWaveWav(int sampleRate = 16000, double frequency = 440.0, double durationSeconds = 1.0, short amplitude = 16000)
    {
        var totalSamples = (int)(sampleRate * durationSeconds);
        var pcmDataSize = totalSamples * sizeof(short); // 16-bit mono = 2 bytes per sample

        using var ms = new MemoryStream(44 + pcmDataSize);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // RIFF chunk descriptor
        bw.Write("RIFF"u8);
        bw.Write((uint)(36 + pcmDataSize)); // ChunkSize = 36 + subchunk2Size
        bw.Write("WAVE"u8);

        // "fmt " sub-chunk
        bw.Write("fmt "u8);
        bw.Write((uint)16);         // Subchunk1Size for PCM = 16
        bw.Write((ushort)1);        // AudioFormat: 1 = PCM
        bw.Write((ushort)1);        // NumChannels: 1 = Mono
        bw.Write((uint)sampleRate); // SampleRate
        bw.Write((uint)(sampleRate * 1 * sizeof(short))); // ByteRate = SampleRate * NumChannels * BitsPerSample/8
        bw.Write((ushort)(1 * sizeof(short)));           // BlockAlign = NumChannels * BitsPerSample/8
        bw.Write((ushort)16);       // BitsPerSample: 16

        // "data" sub-chunk
        bw.Write("data"u8);
        bw.Write((uint)pcmDataSize);

        // Synthesize sine wave PCM samples
        for (var i = 0; i < totalSamples; i++)
        {
            var t = (double)i / sampleRate;
            var sampleValue = (short)(amplitude * Math.Sin(2.0 * Math.PI * frequency * t));
            bw.Write(sampleValue);
        }

        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] CreateMinimalPdf(string contentText)
    {
        // Escape parentheses and backslashes for PDF string literal
        var escapedText = contentText.Replace("\\", "\\\\", StringComparison.Ordinal)
                                     .Replace("(", "\\(", StringComparison.Ordinal)
                                     .Replace(")", "\\)", StringComparison.Ordinal);

        var contentStream = $"BT\n/F1 14 Tf\n50 700 Td\n({escapedText}) Tj\nET\n";
        var streamBytes = Encoding.ASCII.GetBytes(contentStream);

        using var ms = new MemoryStream();
        using var sw = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };

        var offsets = new List<long>();

        // Header
        sw.Write("%PDF-1.4\n");
        sw.Flush();

        // Object 1: Catalog
        offsets.Add(ms.Position);
        sw.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sw.Flush();

        // Object 2: Pages
        offsets.Add(ms.Position);
        sw.Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        sw.Flush();

        // Object 3: Page
        offsets.Add(ms.Position);
        sw.Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        sw.Flush();

        // Object 4: Contents
        offsets.Add(ms.Position);
        sw.Write($"4 0 obj\n<< /Length {streamBytes.Length} >>\nstream\n");
        sw.Flush();
        ms.Write(streamBytes, 0, streamBytes.Length);
        sw.Write("\nendstream\nendobj\n");
        sw.Flush();

        // Object 5: Font
        offsets.Add(ms.Position);
        sw.Write("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        sw.Flush();

        // xref table
        var xrefOffset = ms.Position;
        sw.Write("xref\n0 6\n");
        sw.Write("0000000000 65535 f\r\n");
        foreach (var off in offsets)
        {
            sw.Write($"{off:D10} 00000 n\r\n");
        }

        sw.Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        sw.Flush();

        return ms.ToArray();
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

    private sealed record CompanyProfile(
        [property: JsonPropertyName("companyName")] string CompanyName,
        [property: JsonPropertyName("foundedYear")] int FoundedYear,
        [property: JsonPropertyName("isPublic")] bool IsPublic,
        [property: JsonPropertyName("headquarters")] string Headquarters);
}
