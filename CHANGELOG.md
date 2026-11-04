# Changelog

All notable changes to this project are documented in this file.

## [2.1.0] - 2026-11-04

### Added
- **Google Search grounding & URL context support (#10)**:
  - Added strongly-typed `GroundingMetadata` (`WebSearchQueries`, `SearchEntryPoint`, `GroundingChunks`, `GroundingSupports`, `GroundingSegment`) and `UrlContextMetadata` (`UrlMetadata`, `UrlRetrievalStatus`) on `Candidate`, plus `UrlContextTool` (`Tool.UrlContext`).
  - Mapped `CitationMetadata` and `GroundingMetadata` onto `Microsoft.Extensions.AI` `TextContent.Annotations` (`CitationAnnotation` with UTF-8 byte -> UTF-16 `char` span conversion across streaming and non-streaming responses) and exposed `WebSearchQueries` in `ChatResponse.AdditionalProperties[GeminiChatPropertyNames.WebSearchQueries]`.
- **Embeddings (`:embedContent`, `:batchEmbedContents`) & `IEmbeddingGenerator<string, Embedding<float>>` (#10)**:
  - Added `EmbedContentRequest`, `BatchEmbedContentsRequest`, `EmbedContentResponse`, `BatchEmbedContentsResponse`, `ContentEmbedding`, and `TaskType` models registered in `GeminiJsonContext` for Native AOT.
  - Added `IGeminiClient.EmbedContentAsync`, `IGeminiClient.BatchEmbedContentsAsync`, `IGeminiClient.DefaultEmbeddingModel`, and `GeminiClientOptions.EmbeddingModel` with automatic `models/{model}` wire normalization.
  - Added `GeminiEmbeddingGenerator` (`IEmbeddingGenerator<string, Embedding<float>>`), `client.AsIEmbeddingGenerator()`, and DI registration in `services.AddGeminiClient(...)`.
- **Standard exception constructors & `ChatOptions.AdditionalProperties` forwarding (#11, #12)**:
  - Added CA1032-compliant constructors on `GeminiApiException`.
  - Forwarded known generation/request options (`cachedContent`, `store`, `labels`, `candidateCount`, `responseLogprobs`, `logprobs`, `enableEnhancedCivicAnswers`, `audioTimestamp`, `mediaResolution`, `responseModalities`, `thinkingConfig`, etc.) and unknown extension properties from `ChatOptions.AdditionalProperties` in `ChatOptionsMapper` / `ChatRequestMapper`.

### Changed
- **Streaming, mapping, validation & DI hardening (#8, #9, #11, #12)**:
  - Linked caller `CancellationToken` and `[EnumeratorCancellation]` token in streaming pipelines, eliminated chunk-boundary `StreamIdleWatchdog` timer races, and applied per-chunk watchdog tracking to non-2xx error body reads.
  - Drained split non-array HTTP 200 error payloads in `JsonArray` streaming, enforced 64 MB per-element bounds, fixed numeric/boolean literal delimiter consumption, and excluded 2xx status codes from in-stream error mapping.
  - Preserved `ThoughtSignature` across multi-chunk streaming updates (`TextContent` and `TextReasoningContent`) and persisted history round-trips (`RawRepresentation == null`), while excluding `Thought` parts from `StreamGenerateContentTextAsync`.
  - Added reflection-free Native AOT serialization for non-generic `IDictionary` and typed `IEnumerable<KeyValuePair<string, TValue>>` in `ToolJsonConverter`.
  - Prevented token double-counting of `ToolUsePromptTokenCount` and `ThoughtsTokenCount` in `ChatResponseMapper.MapUsage` and backfilled `TotalTokenCount` when omitted.
  - Aligned all 16 wire enums with official Google Protobuf field numbers (`Unknown = -1`), tightened pre-flight `Validate()` checks (`Role.Unknown`, `SchemaType.Unknown`, `ExecutableCode` / `CodeExecutionResult` oneof exclusivity, `FunctionResponse` JSON object validation), and switched `VideoOffsetJsonConverter` to exact 64-bit integer nanosecond arithmetic.
  - Rejected relative path segments (`.` / `..`) in `GeminiEndpoint.ToResourcePath`, rejected RFC 9110 control characters in `GeminiRequestHeaders`, and redacted `Endpoint` query/userinfo/fragment in `GeminiClientOptions.ToString()`.
  - Registered `GeminiClient`, `IGeminiClient`, `IChatClient`, and `IEmbeddingGenerator` as singletons with `.ValidateOnStart()` in `AddGeminiClient(...)` while supporting late `IGeminiClient` test/mock overrides.

## [2.0.3] - 2026-11-04

### Changed
- Pre-sized `List<T>` and `HashSet<T>` capacities across `ChatResponseMapper`, `ChatRequestMapper`, `ChatOptionsMapper`, and `GeminiRequestHeaders` to reduce array reallocations and GC pressure on hot mapping paths (#5).
- Avoided lazy `List<Part>` allocation when inspecting empty `Candidate.Content` instances in `ChatResponseMapper` (`Content.RawParts`) and lazily allocated `systemParts` in `ChatRequestMapper`.

## [2.0.2] - 2026-11-03

### Changed
- Toned down marketing-heavy wording in `README.md` while keeping implementation-backed claims and explicit scope.

## [2.0.1] - 2026-11-03

### Changed
- Clarified static key vs. dynamic `ApiKeyProvider` validation structure in `GeminiRequestHeaders.Create`.
- Expanded XML documentation exception contracts on `IGeminiClient` and `GeminiClientExtensions` (`GeminiApiException`, `TimeoutException`, `OperationCanceledException`, `HttpRequestException`, `InvalidOperationException`).
- Added failure mode and exception boundary tables, production deployment checklist, and API scope/compatibility notes to `README.md` and `docs/USAGE.md`.

## [2.0.0] - 2026-11-02

### Added
- **Unified `GeminiClient` & `IGeminiClient` architecture**:
  - Added `IGeminiClient` for unit testing and dependency injection (`Endpoint`, `DefaultModel`, `GenerateContentAsync`, `StreamGenerateContentAsync`).
  - Added `GeminiClient` with convenience constructor `new GeminiClient(apiKey, model)` and full options constructor `new GeminiClient(GeminiClientOptions, HttpClient?)`.
  - Added `GeminiClientExtensions` (`GenerateContentAsync(prompt, ...)`, `StreamGenerateContentAsync(prompt, ...)`, and `AsIChatClient(...)`).
- **Gateway-first `GeminiClientOptions`**:
  - Separated `Endpoint` (`Uri`, defaults to `https://generativelanguage.googleapis.com/v1beta`) and `Model` (`string?`, supports bare IDs like `"gemini-2.5-flash"` and resource paths like `"models/..."` or `"tunedModels/..."`).
  - Added `ApiKeyProvider` (`Func<CancellationToken, ValueTask<string>>?`) for per-request dynamic OAuth/JWT token rotation.
  - Added `ApiKeyHeader` (default `"x-goog-api-key"`), `ApiKeyPrefix` (e.g. `"Bearer "`), and `AdditionalHeaders` with eager CRLF/duplicate/content-header validation.
  - Added `StreamIdleTimeout` (default 2 minutes) to detect stalled SSE / JSON-array streams via a per-chunk read watchdog and throw `TimeoutException`.
  - Added `Timeout` (default 5 minutes), `AllowInsecureHttp`, `StreamingFormat` (`ServerSentEvents` default or `JsonArray`), and credential-redacting `ToString()`.
- **`IOptions<GeminiClientOptions>` & `IHttpClientFactory` DI integration**:
  - `IServiceCollection.AddGeminiClient(Action<GeminiClientOptions>?)` in `namespace Microsoft.Extensions.DependencyInjection` registers `IValidateOptions<GeminiClientOptions>`, configures `HttpClient.Timeout` from `GeminiClientOptions.Timeout`, sets `SocketsHttpHandler.AllowAutoRedirect = false`, and registers `GeminiClient`, `IGeminiClient`, and `IChatClient`.
- **`Microsoft.Extensions.AI` (`IChatClient`) adapter (`GeminiChatClient`)**:
  - Dedicated `GeminiChatClient` (also created via `client.AsIChatClient()`) with modular request, options, response, reasoning-effort, and tool-JSON mappers.
  - Emits a single trailing `UsageContent` update in streaming to prevent cumulative token double-counting.
  - Preserves function-call `thoughtSignature` and synthesized call ID markers in `FunctionCallContent.AdditionalProperties` (`GeminiChatPropertyNames`) so persisted chat history without `RawRepresentation` replays accurately.
  - Model-aware `ReasoningEffort` mapping (`thinkingBudget` for Gemini 2.5 vs `thinkingLevel` for Gemini 3+).
- **Rich error handling (`GeminiApiException`)**:
  - Added `IsTransient` (`408`, `429`, `500`, `502`, `503`, `504`), `ErrorStatus`, `ErrorMessage`, `RetryAfter`, and in-stream `{"error": ...}` detection.
- **Official Gemini API model alignment (`namespace GeminiGatewayClient.Models`)**:
  - `GenerateContentRequest` (with `FromPrompt`, `SystemInstruction` as `Content?`) and `GenerateContentResponse` (with `[JsonIgnore]` convenience properties `Text`, `FunctionCalls`, `FinishReason`, `BlockReason`).
  - `Blob` (replacing `InlineData`) with `byte[] Data` automatically serialized as Base64.
  - Factory methods `Part.FromText`, `Part.FromBytes`, `Part.FromUri`, `Part.FromFunctionResponse` and `Content.FromText`, `Content.FromParts`.
  - `ThinkingConfig` (`ThinkingLevel`, nullable `ThinkingBudget` and `IncludeThoughts`), `GenerationConfig.ResponseJsonSchema`, `FunctionDeclaration.ParametersJsonSchema` / `ResponseJsonSchema`, and `LenientStringEnumConverter<T>`.
- **NativeAOT & trim compatibility**: enabled `<IsAotCompatible>true</IsAotCompatible>` and `<EnableTrimAnalyzer>true</EnableTrimAnalyzer>` with unified `GeminiJsonContext`.

### Changed (Breaking)
- **Renamed package, assembly, and root namespace from `GeminiAPIWrapper` to `GeminiGatewayClient`**.
- **Target framework**: updated from `net9.0` to `net10.0`.
- **Replaced `GeminiService` / `GeminiServiceBuilder` / `GenerativeLanguageOptions` / `GeminiResponseExtensions`**:
  - Use `GeminiClient` (or `IGeminiClient`), `GeminiClientOptions`, `services.AddGeminiClient(...)`, `client.AsIChatClient()`, and direct properties on `GenerateContentResponse` (`response.Text`, `response.FunctionCalls`, `response.FinishReason`, `response.BlockReason`).
- **Consolidated namespaces**:
  - Core client types (`GeminiClient`, `IGeminiClient`, `GeminiClientOptions`, `GeminiStreamingFormat`, `GeminiApiException`, `GeminiChatClient`, `GeminiChatPropertyNames`, `GeminiClientExtensions`) live in root `namespace GeminiGatewayClient`.
  - Request/response DTOs live in `namespace GeminiGatewayClient.Models` (`GenerateContentRequest`, `GenerateContentResponse`, `Blob`, `SafetyRating`, etc.).
  - DI extension `AddGeminiClient` lives in `namespace Microsoft.Extensions.DependencyInjection`.
- **Removed `SystemInstruction` wrapper class**: `GenerateContentRequest.SystemInstruction` now uses `Content?` directly (`Role` is nullable on `Content`).
- **MIME types and `Blob.Data`**: `GenerationConfig.ResponseMimeType`, `Blob.MimeType`, and `FileData.MimeType` are `string` (use `MimeTypes` constants); `Blob.Data` is `byte[]` instead of a manual Base64 `string`.
- **`FunctionCallingMode` casing**: renamed enum members from `AUTO`/`ANY`/`NONE`/`VALIDATED` to PascalCase `Auto`/`Any`/`None`/`Validated`.

## [1.0.3] - 2025-10-20
- Unified `Schema` class across request and tool configurations.

## [1.0.2] - 2025-10-20
- Fixed NuGet packaging metadata errors.

## [1.0.1] - 2025-10-20
- Removed unused references.

## [1.0.0] - 2025-10-20
- Initial release.
