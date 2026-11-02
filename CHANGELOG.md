# Changelog

All notable changes to this project are documented in this file.

## [2.0.0] - 2026-11-02

### Added
- **`Microsoft.Extensions.AI` (`IChatClient`) support**: `GeminiService` implements `IChatClient` directly, supporting `GetResponseAsync`, `GetStreamingResponseAsync`, tool/function declarations (`AIFunctionDeclaration` via `parametersJsonSchema`), parallel function call IDs (`FunctionCall.Id` / `FunctionResponse.Id`), structured JSON output (`responseJsonSchema`), `ChatOptions.ModelId` per-request model overrides, `ChatOptions.Reasoning` mapping, reasoning content (`TextReasoningContent` with `ProtectedData` thought signatures), and full token usage (`UsageDetails` including `ReasoningTokenCount` and `CachedInputTokenCount`).
- **`IHttpClientFactory` & Dependency Injection integration**: added `IServiceCollection.AddGeminiService(...)` in `GeminiAPIWrapper.DependencyInjection` returning `IHttpClientBuilder` for chaining resilience and handler pipelines.
- **Gateway-friendly authentication and transport options** on `GenerativeLanguageOptions`:
  - `KeyPrefix`: prepend a prefix such as `"Bearer "` to `ApiKey` in `KeyHeader`.
  - `AdditionalHeaders`: send extra request headers (e.g., `cf-aig-authorization` for Cloudflare AI Gateway or custom routing/tracing headers) alongside or instead of `KeyHeader`, with eager validation in the constructor.
  - `ApiKey` is now optional (`string?`); when `null` or empty, no key header is sent (for gateways that inject the Gemini API key upstream).
  - `Timeout`: configurable timeout for internally created `HttpClient` instances (defaults to 5 minutes for thinking models), backed by `SocketsHttpHandler` with a 2-minute `PooledConnectionLifetime`.
  - `StreamingFormat`: defaults to `GeminiStreamingFormat.ServerSentEvents` (`?alt=sse`) with `GeminiStreamingFormat.JsonArray` available when needed.
  - `AllowInsecureHttp`: blocks accidental cleartext `http://` credential transmission to non-loopback hosts unless explicitly enabled.
  - Redacted `ToString()`: `GenerativeLanguageOptions.ToString()` masks `ApiKey` and `AdditionalHeaders` values so options are never leaked in logs.
- **External `HttpClient` support**: `GeminiServiceBuilder.Build(GenerativeLanguageOptions, HttpClient)` accepts a caller-managed `HttpClient`. Request headers are attached per `HttpRequestMessage` so `HttpClient.DefaultRequestHeaders` is never mutated, and caller-provided `HttpClient` instances are not disposed when `GeminiService` is disposed.
- **Rich error parsing on `GeminiApiException`**: parses Google RPC `error.status` (`ErrorStatus`), `error.message` (`ErrorMessage`), and `RetryAfter` (from `google.rpc.RetryInfo.retryDelay` or the HTTP `Retry-After` header) while bounding raw `ErrorContent` to 64 KB.
- **Official Gemini API model alignment**:
  - `ThinkingConfig`: added `ThinkingLevel` (`Minimal`, `Low`, `Medium`, `High`) for Gemini 3+ models, made `ThinkingBudget` and `IncludeThoughts` nullable, and fixed the `includeThoughts` property name.
  - `GeminiResponse`: added `PromptFeedback` (`BlockReason`, `SafetyRatings`) and `ResponseId`, plus `GetBlockReason()` and `GetFinishReason()` extension methods.
  - `UsageMetadata`: added `ThoughtsTokenCount`, `CachedContentTokenCount`, and `ToolUsePromptTokenCount`.
  - `GenerationConfig.ResponseJsonSchema` and `FunctionDeclaration.ParametersJsonSchema` / `ResponseJsonSchema` for lossless standard JSON Schema support.
  - Lenient response enum deserialization (`LenientStringEnumConverter<T>`): unrecognized server enum values for `FinishReason`, `HarmCategory`, `HarmProbability`, and `BlockReason` map to `Unknown` instead of throwing `JsonException`.
- **NativeAOT & trim compatibility**: enabled `<IsAotCompatible>true</IsAotCompatible>` and removed runtime reflection (`GenerateOptionAttribute`).

### Changed (Breaking)
- **Target framework**: updated from `net9.0` to `net10.0`.
- **MIME types as strings**: changed `InlineData.MimeType` and `FileData.MimeType` from the `MimeType` enum to `string` and added the `MimeTypes` constants class so any IANA media type or parameterized MIME string (such as `audio/L16;codec=pcm;rate=24000`) works without enum limitations.
- **`ThinkingConfig` properties**: `ThinkingBudget` is now `int?` and `IncludeThoughts` is `bool?` so unset properties are omitted rather than sending `thinkingBudget: 0`.
- **Default streaming wire format**: `StreamGenerateAsync` now uses Server-Sent Events (`?alt=sse`) by default (`GeminiStreamingFormat.ServerSentEvents`). Set `StreamingFormat = GeminiStreamingFormat.JsonArray` for the legacy chunked JSON array format.
- **Non-loopback `http://` restriction**: `http://` endpoints now throw `ArgumentException` for non-loopback hosts unless `AllowInsecureHttp = true` is set on `GenerativeLanguageOptions`.
- **`IChatClient` function call arguments**: `FunctionCallContent.Arguments` values are now preserved as `JsonElement` instances rather than eagerly converted to primitive `long`/`double` values.
- **`GeminiService` is now `sealed`** and implements `IDisposable`.
- **`GetText()` behavior**: `GeminiResponseExtensions.GetText()` now safely returns `null` when `Candidates` or `Parts` is empty and concatenates all non-thought text parts in the first candidate.
- **English documentation**: translated all XML documentation comments, exception messages, package metadata, and `README.md` to English.

### Fixed
- Fixed `ArgumentNullException` in `GeminiClient` passing option values instead of parameter names.
- Fixed `GetText()` and `GetFunctionCall()` throwing `ArgumentOutOfRangeException` when a response candidate list or part list is empty (e.g., when blocked by safety filters).
- Fixed `EndpointBase` URLs with query strings (`?api-version=...`) producing invalid URLs when appending `:generateContent` or `:streamGenerateContent`.
- Fixed streaming `JsonException` errors not being wrapped in `GeminiApiException`.

## [1.0.3] - 2025-10-20
- Unified `Schema` class across request and tool configurations.

## [1.0.2] - 2025-10-20
- Fixed NuGet packaging metadata errors.

## [1.0.1] - 2025-10-20
- Removed unused references.

## [1.0.0] - 2025-10-20
- Initial release.
