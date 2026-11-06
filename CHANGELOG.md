# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

The library is now much smaller: it favors plain code and the base class library over hand-written infrastructure and allocation tuning.

### Changed (Breaking)
- **`ChatOptions.Reasoning` throws `NotSupportedException`**, and nothing maps an effort to a model anymore. Set `ThinkingConfig` through `RawRepresentationFactory`.
- **`GeminiClientOptions.StreamingFormat` and `GeminiStreamingFormat` are removed**: streaming asks for Server-Sent Events unless `UseServerSentEvents` is `false`.
- **`ChatOptions.AdditionalProperties` are no longer read**: `ChatOptions.RawRepresentationFactory` is the one way to set Gemini-specific options. Its result is now modified, as its contract allows, so it must return a new instance on each call.
- **Citations on a streamed response or on a response with several texts arrive on an annotation-only `AIContent`**, with regions counted from the start of the response text. Updates already delivered are no longer changed. `segment.partIndex` is ignored: the API omits it and counts offsets across the response.
- **Sent parts are always rebuilt from `AIContent`**, so a change to the content takes effect. A `FunctionResultContent` no longer takes anything from an attached `RawRepresentation`.
- **`ToModelContent`, `ToModelContentAsync`, and `TryGetModelContent` keep part boundaries** instead of merging text, because Gemini forbids merging a signed part with an unsigned one.
- **A standalone `thoughtSignature` part stays a separate part when resent.** A signature that arrived on a text part still returns to that text after streaming aggregation.
- **`IEmbeddingGenerator` always uses `:batchEmbedContents`.** A `BatchEmbedContentsRequest` from `RawRepresentationFactory` is ignored.
- **Tool values go through System.Text.Json one by one**: dictionaries and sequences are walked element by element, so custom converters and `NumberHandling` now apply to what they hold. `TimeOnly` uses the System.Text.Json format, a `List<KeyValuePair<string, int>>` becomes an array of key/value objects, and a key/value sequence that is neither a dictionary nor described by metadata throws `InvalidOperationException`.
- **`HostedFileContent` in a chat message throws `NotSupportedException`** instead of being dropped. Use `UriContent` with the uploaded file's URI and media type.
- **`MALFORMED_FUNCTION_CALL`, `UNEXPECTED_TOOL_CALL`, and `TOO_MANY_TOOL_CALLS` no longer map to `ChatFinishReason.ToolCalls`**, even when the response carries function calls: `FinishReason` is `null`, and the original value stays on `RawRepresentation`.
- **Non-streaming calls are covered by `HttpClient.Timeout` alone**: `StreamIdleTimeout` applies only to streams, so a stalled body ends with `TaskCanceledException`, not `TimeoutException`.
- **`UsageDetails` always follow Gemini's definition**: input adds the tool prompt and output adds thoughts. The guess for gateways that report otherwise is gone.
- **`GeminiApiException.StatusCode` for an error sent with a 2xx status comes from the HTTP-style `code`**, or is 500. gRPC numbers and `status` names are no longer translated, and `ErrorStatus` still reports the name.
- **`GeminiApiException.IsTransient` is removed.** Decide on retries from `StatusCode` and `ErrorStatus`.
- **An error status on a stream reports `ErrorContent` as `null` when its body cannot be read in time or completely**, instead of failing with `TimeoutException` or keeping a partial body.
- **A stream and a single SSE event have no size limit anymore**, and clients this library creates no longer lower `HttpClient.MaxResponseContentBufferSize`, which limits non-streaming bodies only.
- **Whether a stream response is an event stream is decided by its body, not its `Content-Type`.** An SSE body with a wrong type now works. A body starting with `[` is read element by element, as the JSON array Gemini sends when `alt=sse` does not reach it. A body starting with `{` or `<` is read as one complete response, which fails with `GeminiApiException` unless it is a single JSON object. An error in such a body keeps its details.
- **A connection lost while reading a non-streaming body surfaces as `HttpRequestException` without `StatusCode`**, even for an error status, because the body is read as part of the request.
- **A message is no longer merged into the last content of a `RawRepresentationFactory` template** with the same role; it starts a new content.
- **Error bodies are always read as UTF-8**, whatever charset the response declares.
- **`retryDelay` is read only in the seconds form Google uses**, such as `"37s"`.
- **`VideoOffset` reads only the `"10.5s"` wire format**, not `{"seconds", "nanos"}` objects.
- **`GeminiClientOptions.ToString` hides the whole query string** of the endpoint.
- **Requests are no longer checked before sending.** `Validate` and its rules are gone: an invalid request is sent, and the API or gateway answers it with an error status. The same goes for a batch that mixes models, and for a model or endpoint containing an operation such as `:generateContent`.
- **`GeminiClientOptions.Timeout` is removed.** Clients this library creates use 5 minutes. Set `HttpClient.Timeout` on your own client, or use `.ConfigureHttpClient` on the builder `AddGeminiClient` returns. A `Gemini:Timeout` configuration key is now ignored.
- **`GeminiClientOptions.ToolSerializerOptions` and `IGeminiClient.ToolSerializerOptions` are removed.** Pass the options to `AsIChatClient`.
- **Option setters no longer check their values.** A value that cannot work fails where it is used, such as a negative `StreamIdleTimeout` on the first read.
- **A header value with a line break or NUL throws `ArgumentException`**, because .NET would send it as it is, and so do a header name .NET refuses, the same header set twice, and an empty key from `ApiKeyProvider`. Other header checks are gone: non-ASCII values fail when sent.
- **`ChatOptions.Seed` outside the 32-bit range throws `OverflowException`.**
- **A stream with only comments, a `[DONE]` marker, or events with empty `data` throws `GeminiApiException`** like an empty body.
- **An `error` event with data on a stream throws `GeminiApiException`**, also when its data is not in Google's shape. It used to read as an empty chunk.
- **A `FunctionResultContent` without its call throws `ArgumentException`**, because its function name is unknown. The call ID used to be sent as the function name. Synthesized call IDs no longer carry the function name.
- **`GeminiChatClient` and `GeminiEmbeddingGenerator` are internal.** `AsIChatClient` and `AsIEmbeddingGenerator` are the one way to create them.
- **`IGeminiClient.Endpoint` and `ProviderUri` in the chat and embedding metadata leave out the query string and user info**, which may hold credentials. The query string is still sent.
- **`Part.FromBytes(byte[], string)` is removed.** The `ReadOnlySpan<byte>` overload accepts a `byte[]` and copies it. A `null` array now gives an empty part.
- **Request bodies use the default JSON encoder**, which writes non-ASCII text as `\uXXXX` escapes. The content is the same, but Japanese text takes about twice the bytes.
- **The prompt overloads of `GenerateContentAsync`, `StreamGenerateContentAsync`, and `EmbedContentAsync` take `model` second**, like the request overloads. Positional arguments after the prompt or text change meaning.
- **An `error` value of any shape on a 2xx response throws `GeminiApiException`**, not only Google's object.
- **A streamed `FinishReason` comes only from the chunk that ends the response**, so a failure after a function call is no longer reported as `ToolCalls`. A stream without any finish reason reports none.

### Added
- `GeminiClientOptions.UseServerSentEvents`, for a gateway that rejects `alt=sse`. With `false`, the client sends what version 1.0.3 sent and reads the JSON array Gemini answers with.
- `GeminiClient.CreateDefaultHandler()` is public, so a caller-supplied `HttpClient` can follow no redirects and store no cookies, like the clients this library creates.
- `GeminiChatPropertyNames.SignatureCompanion`, which marks a signature that belongs to the preceding streamed text.
- A Native AOT smoke test that CI publishes with warnings as errors and runs as a native binary.

### Fixed
- A thought part with data of its own, such as an image or code, keeps that data in the chat history.
- Streaming works when a gateway does not forward `alt=sse`, as in version 1.0.3, which read the JSON array Gemini sends in that case.
- The handler this library creates no longer stores cookies, which one caller's gateway session could pass to the next caller.
- A function schema without top-level `properties`, such as `additionalProperties` or `allOf` alone, is no longer dropped.
- An embedding model name with a reserved character, such as `@`, is no longer percent-encoded in the request body.
- A Google error sent with a 2xx status keeps its status and retry delay when wrapped in an array, or when sent as plain JSON after keep-alive comments.
- A streamed 2xx body without events, such as plain text from a gateway, is kept in `ErrorContent`.
- A byte order mark split across reads no longer hides a JSON error on a stream.
- `ToModelContent`, `ToModelContentAsync`, and `TryGetModelContent` drop empty text next to other data on a part, which Gemini rejects when resent.
- A `Tool` inside a `RawRepresentationFactory` template is no longer changed when `ChatOptions.Tools` adds functions, so a shared instance does not collect them across calls.
- The error body of a stream is read only up to 64 KB. A longer one leaves `ErrorContent` as `null` and keeps the status.
- A tool result that is a custom read-only dictionary is written as a JSON object when type metadata describes it, instead of a list of key/value pairs.
- `citationMetadata` from the Gemini Developer API is no longer lost: `citationSources` is read as well as Vertex AI's `citations`.
- Streaming reports `ChatFinishReason.ToolCalls` when a later chunk carries `STOP` after a function call, matching non-streaming responses.
- A part with an unparsable MIME type no longer fails the whole response; its content uses `application/octet-stream`.
- Request bodies are sent with `Content-Length` instead of chunked encoding, which some gateways reject.
- Two functions with the same name in `ChatOptions.Tools` are declared once, keeping the first, instead of being sent twice.
- A `null` entry in `ChatOptions.Tools` throws `ArgumentException` before anything is sent, instead of `NullReferenceException`.

### Removed
- Allocation-focused code, allocation assertions, and the benchmark project.
- Hand-written SSE parsing, the idle watchdog, and buffer pooling, replaced by `System.Net.ServerSentEvents`, a small stream wrapper, and standard buffers.

## [3.0.0] - 2026-11-05

### Changed (Breaking)
- **Null elements in `ChatOptions.RawRepresentationFactory` templates now fail validation**: a `null` in a template's `Contents`, `Content.Parts`, `Tools`, `Tool.FunctionDeclarations`, `SafetySettings`, or `Schema.AnyOf` now throws `ArgumentException` before anything is sent, matching a `null` passed directly on a request. Except for array-backed `Parts`, these elements were previously removed silently while cloning the template. `null` values in server responses are still tolerated.
- **Lost connections while reading a response body no longer surface as a bare `IOException`** (`HttpIOException`), which contradicted the documented exception contract. Successful responses and streams now throw `HttpRequestException`, with the original failure in `InnerException` and the status in `StatusCode`. Non-2xx responses still throw `GeminiApiException`, built from whatever part of the error body could be decoded, so `StatusCode`, `IsTransient`, and `RetryAfter` are kept. Code catching `IOException` must change.
- **Exception types for cyclic or very deep tool values**: self-referencing or deeply nested collections in tool results and arguments can now surface as `InvalidOperationException` or `JsonReaderException` instead of `JsonException`, unless `JsonSerializerOptions.ReferenceHandler` is set.

### Changed
- **Request bodies keep non-ASCII text as UTF-8**: text in the Basic Multilingual Plane is no longer written as `\uXXXX` escapes, which reduces Japanese conversation bodies by about 40%. The JSON is semantically unchanged, HTML-sensitive ASCII such as `<` and `&` stays escaped, and supplementary characters such as emoji stay escaped.
- **`Blob.MimeType` and `Blob.Data` are no longer `required`**: a response omitting either key used to fail the whole response, including its valid parts. Requests are still validated, and through `IChatClient` a replayed part without a MIME type is sent as `application/octet-stream`.
- **Request mapping and validation**:
  - Request validation no longer allocates a path string per content or part, and formats paths only on failure. `Schema` validation follows the same approach but still allocates for its visited set and enumerators.
  - Replayed function-call arguments are compared by raw bytes before falling back to `JsonElement.DeepEquals`, which removes most of the cost of re-sending large arguments on every turn.
  - `ToolJsonConverter.ToDictionary` pre-sizes its result, and plain sequences in tool values skip a redundant serializer pass.
- **Response handling**:
  - Bodies with a known `Content-Length` no longer grow and copy the buffer for the final read.
  - `GeminiStreamReader` now uses a single SSE line-processing path and `SearchValues<byte>` newline scanning.
  - Grounding chunk tracking uses a 64-bit mask, and model collections are allocated lazily.

### Fixed
- `UriContent` and `Uri` tool values were sent unescaped (`a%20b` became `a b`), which also prevented replaying the original `fileData` part. They are now sent exactly as supplied.
- Parts without a chat mapping, such as `toolCall` and `toolResponse`, were dropped by `IChatClient`. They are now kept as `AIContent` and replayed on the next turn.
- Single-use sequences returned as tool results were serialized as an empty array, because they were enumerated more than once. They are now enumerated once.
- A `FunctionResultContent` with a `Part` attached as `RawRepresentation` re-sent the attached response even after `Result` changed. It now sends the current result while keeping the attached name, id, and metadata.
- `ChatRequestMapper` threw `NullReferenceException` for a template whose `Contents` ended with `null`.
- Buffered responses starting with a UTF-8 byte order mark failed to parse, which also hid the details of error bodies.
- Explicit citation resolution ignored `partIndex == 0`.

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
