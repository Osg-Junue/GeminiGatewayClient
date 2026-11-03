# Changelog

All notable changes to this project are documented in this file.

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
