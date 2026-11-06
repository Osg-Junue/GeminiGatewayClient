# Gemini API Compatibility Reference

This document maps the official [Google Gemini REST API (`generativelanguage.googleapis.com`)](https://ai.google.dev/api) specification to `GeminiGatewayClient` types and properties.

## Table of Contents

1. [REST Endpoints Coverage](#1-rest-endpoints-coverage)
2. [`GenerateContentRequest` Fields](#2-generatecontentrequest-fields)
3. [`GenerationConfig` & `ThinkingConfig` Fields](#3-generationconfig--thinkingconfig-fields)
4. [`Content` & `Part` Multimodal / Union Types](#4-content--part-multimodal--union-types)
5. [`Tools`, `ToolConfig` & Function Calling](#5-tools-toolconfig--function-calling)
6. [`GenerateContentResponse`, `Candidate`, `GroundingMetadata` & `UsageMetadata`](#6-generatecontentresponse-candidate-groundingmetadata--usagemetadata)
7. [`EmbedContent` & `BatchEmbedContents` Models](#7-embedcontent--batchembedcontents-models)
8. [`Microsoft.Extensions.AI` (`IChatClient` & `IEmbeddingGenerator`) Mapping](#8-microsoftextensionsai-ichatclient--iembeddinggenerator-mapping)
9. [Forward Compatibility & Wire Enums](#9-forward-compatibility--wire-enums)

---

## 1. REST Endpoints Coverage

`GeminiGatewayClient` is focused on stateless runtime endpoints (`:generateContent`, `:streamGenerateContent`, `:embedContent`, `:batchEmbedContents`)—the endpoints routed through API gateways in production applications.

| Official Gemini REST Endpoint | Official Doc Reference | Status | `GeminiGatewayClient` API |
|---|---|---|---|
| `POST /{version}/models/{model}:generateContent` | [Generating content](https://ai.google.dev/api/generate-content#method:-models.generatecontent) | ✅ Supported | `IGeminiClient.GenerateContentAsync`<br>`IChatClient.GetResponseAsync` |
| `POST /{version}/models/{model}:streamGenerateContent?alt=sse` | [Streaming content](https://ai.google.dev/api/generate-content#method:-models.streamgeneratecontent) | ✅ Supported | `IGeminiClient.StreamGenerateContentAsync`<br>`IChatClient.GetStreamingResponseAsync` |
| `POST /{version}/models/{model}:embedContent` | [Embeddings](https://ai.google.dev/api/embeddings#method:-models.embedcontent) | ✅ Supported | `IGeminiClient.EmbedContentAsync` |
| `POST /{version}/models/{model}:batchEmbedContents` | [Batch embeddings](https://ai.google.dev/api/embeddings#method:-models.batchembedcontents) | ✅ Supported | `IGeminiClient.BatchEmbedContentsAsync`<br>`IEmbeddingGenerator<string, Embedding<float>>.GenerateAsync` |
| `POST /{version}/tunedModels/{model}:generateContent` | [Tuned models](https://ai.google.dev/api/tuning) | ✅ Supported | Pass `"tunedModels/{id}"` as `Model` |
| `POST /{version}/tunedModels/{model}:streamGenerateContent` | [Tuned models](https://ai.google.dev/api/tuning) | ✅ Supported | Pass `"tunedModels/{id}"` as `Model` |
| Consuming uploaded files (`fileData.fileUri`) | [Files API](https://ai.google.dev/api/files) | ✅ Supported | `Part.FromUri(fileUri, mimeType)` / `FileData` |
| Consuming cached context (`cachedContent`) | [Context caching](https://ai.google.dev/api/caching) | ✅ Supported | `GenerateContentRequest.CachedContent` |
| `GET /{version}/models` (`models.list` / `models.get`) | [Models](https://ai.google.dev/api/models) | ➖ Out of scope | Management endpoint (not included in gateway runtime client) |
| `POST /{version}/models/{model}:countTokens` | [Tokens](https://ai.google.dev/api/tokens) | ➖ Out of scope | Token usage is returned in `GenerateContentResponse.UsageMetadata` |
| `POST /upload/{version}/files`, `cachedContents` CRUD, `batches` | [Files](https://ai.google.dev/api/files) / [Batch](https://ai.google.dev/api/batch-mode) | ➖ Out of scope | Storage/batch management endpoints |
| `BidiGenerateContent` (WebSockets) | [Live API](https://ai.google.dev/api/live) | ➖ Out of scope | Stateful WebSocket protocol |

---

## 2. `GenerateContentRequest` Fields

Reference: [`models.generateContent` Request Body](https://ai.google.dev/api/generate-content#request-body)

| JSON Wire Field | Official Type | Status | C# Property (`GenerateContentRequest`) | Notes |
|---|---|---|---|---|
| `contents` | `Content[]` | ✅ Supported | `Contents` (`IList<Content>`) | Required; validated non-empty before sending |
| `tools` | `Tool[]` | ✅ Supported | `Tools` (`IList<Tool>?`) | Functions, Google Search, Code Execution, URL Context |
| `toolConfig` | `ToolConfig` | ✅ Supported | `ToolConfig` (`ToolConfig?`) | Controls `FunctionCallingConfig` (`Mode`, `AllowedFunctionNames`) |
| `safetySettings` | `SafetySetting[]` | ✅ Supported | `SafetySettings` (`IList<SafetySetting>?`) | Per-category `HarmBlockThreshold` and `HarmBlockMethod` |
| `systemInstruction` | `Content` | ✅ Supported | `SystemInstruction` (`Content?`) | System prompt (`Role` omitted) |
| `generationConfig` | `GenerationConfig` | ✅ Supported | `GenerationConfig` (`GenerationConfig?`) | Sampling, schema, modalities, and thinking parameters |
| `cachedContent` | `string` | ✅ Supported | `CachedContent` (`string?`) | Format: `cachedContents/{id}` |
| `store` | `boolean` | ✅ Supported | `Store` (`bool?`) | Optional conversation storage flag |
| `labels` | `map<string, string>` | ✅ Supported | `Labels` (`IDictionary<string, string>?`) | **Vertex AI endpoints only** (rejected with HTTP 400 on Google AI Studio) |
| *(Any future field)* | `any` | ✅ Preserved | `ExtensionData` (`[JsonExtensionData]`) | Round-tripped without loss |

---

## 3. `GenerationConfig` & `ThinkingConfig` Fields

Reference: [`GenerationConfig`](https://ai.google.dev/api/generate-content#generationconfig) and [`ThinkingConfig`](https://ai.google.dev/api/generate-content#ThinkingConfig)

| JSON Wire Field | Official Type | Status | C# Property (`GenerationConfig`) | Notes |
|---|---|---|---|---|
| `stopSequences` | `string[]` | ✅ Supported | `StopSequences` (`IList<string>?`) | Up to 5 stop sequences |
| `responseMimeType` | `string` | ✅ Supported | `ResponseMimeType` (`string?`) | Use `MimeTypes.ApplicationJson`, `MimeTypes.TextPlain`, etc. |
| `responseSchema` | `Schema` | ✅ Supported | `ResponseSchema` (`Schema?`) | OpenAPI 3.0 subset (`SchemaType`, `Properties`, `Items`, `AnyOf`, `PropertyOrdering`, etc.) |
| `responseJsonSchema` | `Value` (JSON Schema) | ✅ Supported | `ResponseJsonSchema` (`JsonElement?`) | Standard JSON Schema payload (Gemini 2.5+); mutually exclusive with `ResponseSchema` |
| `responseModalities` | `Modality[]` | ✅ Supported | `ResponseModalities` (`IList<string>?`) | E.g., `["TEXT"]`, `["TEXT", "IMAGE"]`, `["AUDIO"]` |
| `candidateCount` | `integer` | ✅ Supported | `CandidateCount` (`int?`) | Number of generated candidates |
| `maxOutputTokens` | `integer` | ✅ Supported | `MaxOutputTokens` (`int?`) | Maximum output tokens |
| `temperature` | `number` | ✅ Supported | `Temperature` (`double?`) | Sampling randomness (`[0.0, 2.0]`) |
| `topP` | `number` | ✅ Supported | `TopP` (`double?`) | Nucleus sampling probability |
| `topK` | `integer` | ✅ Supported | `TopK` (`int?`) | Top-K token sampling |
| `seed` | `integer` | ✅ Supported | `Seed` (`int?`) | Best-effort deterministic seed |
| `presencePenalty` | `number` | ✅ Supported | `PresencePenalty` (`double?`) | Penalizes already-used tokens |
| `frequencyPenalty` | `number` | ✅ Supported | `FrequencyPenalty` (`double?`) | Penalizes repeated tokens by frequency |
| `responseLogprobs` | `boolean` | ✅ Supported | `ResponseLogprobs` (`bool?`) | Enables token log-probabilities in response |
| `logprobs` | `integer` | ✅ Supported | `Logprobs` (`int?`) | Number of top candidate tokens per step (`[0, 20]`) |
| `enableEnhancedCivicAnswers` | `boolean` | ✅ Supported | `EnableEnhancedCivicAnswers` (`bool?`) | Enhanced civic integrity mode |
| `mediaResolution` | `MediaResolution` | ✅ Supported | `MediaResolution` (`MediaResolution?`) | `Low`, `Medium`, `High` |
| `audioTimestamp` | `boolean` | ✅ Supported | `AudioTimestamp` (`bool?`) | **Vertex AI endpoints only** |
| `thinkingConfig.includeThoughts` | `boolean` | ✅ Supported | `ThinkingConfig.IncludeThoughts` (`bool?`) | Returns thought summaries (`Part.Thought == true`) |
| `thinkingConfig.thinkingBudget` | `integer` | ✅ Supported | `ThinkingConfig.ThinkingBudget` (`int?`) | Gemini 2.5 token budget (`-1` dynamic, `0` off, `> 0` cap) |
| `thinkingConfig.thinkingLevel` | `ThinkingLevel` | ✅ Supported | `ThinkingConfig.ThinkingLevel` (`ThinkingLevel?`) | Gemini 3+ qualitative tier (`Minimal`, `Low`, `Medium`, `High`) |
| `speechConfig` / `imageConfig` | `object` | 🔄 Via `ExtensionData` | `GenerationConfig.ExtensionData` | Can be passed and read via `[JsonExtensionData]` |

---

## 4. `Content` & `Part` Multimodal / Union Types

Reference: [`Content`](https://ai.google.dev/api/caching#Content) and [`Part`](https://ai.google.dev/api/caching#Part)

| JSON Wire Field | Official Type | Status | C# Property / Factory (`Part`) | Notes |
|---|---|---|---|---|
| `text` | `string` | ✅ Supported | `Part.Text` / `Part.FromText(text)` | Text or thought summary content |
| `thought` | `boolean` | ✅ Supported | `Part.Thought` (`bool?`) | `true` when the part contains internal reasoning |
| `thoughtSignature` | `string` (bytes) | ✅ Supported | `Part.ThoughtSignature` (`string?`) | Opaque base64 signature required when replaying Gemini 3 turns |
| `inlineData` | `Blob` | ✅ Supported | `Part.InlineData` / `Part.FromBytes(bytes, mimeType)` | `Blob.Data` (`byte[]`) is Base64-encoded automatically |
| `fileData` | `FileData` | ✅ Supported | `Part.FileData` / `Part.FromUri(fileUri, mimeType)` | References Files API (`files/...`) or Cloud Storage URIs |
| `functionCall` | `FunctionCall` | ✅ Supported | `Part.FunctionCall` (`Id`, `Name`, `Args`) | Model-requested function invocation |
| `functionResponse` | `FunctionResponse` | ✅ Supported | `Part.FunctionResponse` / `Part.FromFunctionResponse(...)` | Tool execution output (`Id`, `Name`, `Response`) |
| `executableCode` | `ExecutableCode` | ✅ Supported | `Part.ExecutableCode` (`Language`, `Code`) | Server-side Python code generated by the model |
| `codeExecutionResult` | `CodeExecutionResult` | ✅ Supported | `Part.CodeExecutionResult` (`Outcome`, `Output`) | Result of server-side code execution |
| `videoMetadata` | `VideoMetadata` | ✅ Supported | `Part.VideoMetadata` (`StartOffset`, `EndOffset`, `Fps`) | `VideoOffset` serializes to protobuf duration strings (`"12.5s"`) |

---

## 5. `Tools`, `ToolConfig` & Function Calling

Reference: [`Tool`](https://ai.google.dev/api/caching#Tool) and [`FunctionDeclaration`](https://ai.google.dev/api/caching#FunctionDeclaration)

| JSON Wire Field | Official Type | Status | C# Type / Property | Notes |
|---|---|---|---|---|
| `functionDeclarations` | `FunctionDeclaration[]` | ✅ Supported | `Tool.FunctionDeclarations` | Supports both `Parameters` (`Schema`) and `ParametersJsonSchema` (`JsonElement`), plus `Response` / `ResponseJsonSchema` |
| `googleSearch` | `GoogleSearch` | ✅ Supported | `Tool.GoogleSearch` (`GoogleSearchTool?`) | Grounding with Google Search (Gemini 2.0+) |
| `codeExecution` | `CodeExecution` | ✅ Supported | `Tool.CodeExecution` (`CodeExecutionTool?`) | Server-side Python sandbox execution |
| `urlContext` | `UrlContext` | ✅ Supported | `Tool.UrlContext` (`UrlContextTool?`) | Grounding with URL Context |
| `functionCallingConfig.mode` | `Mode` | ✅ Supported | `FunctionCallingConfig.Mode` | `Auto`, `Any`, `None`, `Validated` |
| `functionCallingConfig.allowedFunctionNames` | `string[]` | ✅ Supported | `FunctionCallingConfig.AllowedFunctionNames` | Restricts callable functions when `Mode` is `Any` or `Validated` |

---

## 6. `GenerateContentResponse`, `Candidate`, `GroundingMetadata` & `UsageMetadata`

Reference: [`GenerateContentResponse`](https://ai.google.dev/api/generate-content#response-body)

| JSON Wire Field | Official Type | Status | C# Type / Property | Notes |
|---|---|---|---|---|
| `candidates` | `Candidate[]` | ✅ Supported | `GenerateContentResponse.Candidates` | Also exposes convenience `response.Text`, `response.FunctionCalls`, `response.FinishReason` |
| `promptFeedback` | `PromptFeedback` | ✅ Supported | `GenerateContentResponse.PromptFeedback` | `BlockReason` (`BlockReason`), `SafetyRatings`; shortcut at `response.BlockReason` |
| `usageMetadata` | `UsageMetadata` | ✅ Supported | `GenerateContentResponse.UsageMetadata` | `PromptTokenCount`, `CachedContentTokenCount`, `CandidatesTokenCount`, `ToolUsePromptTokenCount`, `ThoughtsTokenCount`, `TotalTokenCount` |
| `modelVersion` | `string` | ✅ Supported | `GenerateContentResponse.ModelVersion` | Model version identifier used for generation |
| `responseId` | `string` | ✅ Supported | `GenerateContentResponse.ResponseId` | Unique response identifier |
| `candidates[].content` | `Content` | ✅ Supported | `Candidate.Content` | Generated model message parts |
| `candidates[].finishReason` | `FinishReason` | ✅ Supported | `Candidate.FinishReason` | All 19 official values (`Stop`, `MaxTokens`, `Safety`, `MalformedFunctionCall`, `UnexpectedToolCall`, etc.) + `Unknown` fallback |
| `candidates[].safetyRatings` | `SafetyRating[]` | ✅ Supported | `Candidate.SafetyRatings` | `Category`, `Probability`, `ProbabilityScore`, `Severity`, `SeverityScore`, `Blocked` |
| `candidates[].citationMetadata` | `CitationMetadata` | ✅ Supported | `Candidate.CitationMetadata` | `Citations` (`StartIndex`, `EndIndex`, `Uri`, `Title`, `License`, `PublicationDate`) |
| `candidates[].groundingMetadata` | `GroundingMetadata` | ✅ Supported | `Candidate.GroundingMetadata` | `WebSearchQueries`, `SearchEntryPoint` (`RenderedContent`, `SdkBlob`), `GroundingChunks` (`Web`, `RetrievedContext`), `GroundingSupports` (`Segment`, `GroundingChunkIndices`, `ConfidenceScores`) |
| `candidates[].urlContextMetadata` | `UrlContextMetadata` | ✅ Supported | `Candidate.UrlContextMetadata` | `UrlMetadata` (`RetrievedUrl`, `UrlRetrievalStatus`) |
| `candidates[].logprobsResult` | `LogprobsResult` | ✅ Supported | `Candidate.LogprobsResult` | `TopCandidates`, `ChosenCandidates` (`Token`, `TokenId`, `LogProbability`) |
| `candidates[].avgLogprobs` | `number` | ✅ Supported | `Candidate.AvgLogprobs` | Average log probability |
| `candidates[].tokenCount` | `integer` | ✅ Supported | `Candidate.TokenCount` | Token count for the candidate |
| `candidates[].finishMessage` | `string` | ✅ Supported | `Candidate.FinishMessage` | Detailed stop description |

---

## 7. `EmbedContent` & `BatchEmbedContents` Models

Reference: [`Embeddings`](https://ai.google.dev/api/embeddings)

| JSON Wire Field | Official Type | Status | C# Type / Property | Notes |
|---|---|---|---|---|
| `model` | `string` | ✅ Supported | `EmbedContentRequest.Model` | Normalized automatically to `"models/{model}"` on each item in `BatchEmbedContentsAsync` |
| `content` | `Content` | ✅ Supported | `EmbedContentRequest.Content` | Convenience factory `EmbedContentRequest.FromText(...)` |
| `taskType` | `TaskType` | ✅ Supported | `EmbedContentRequest.TaskType` | `RetrievalQuery`, `RetrievalDocument`, `SemanticSimilarity`, `Classification`, `Clustering`, `QuestionAnswering`, `FactVerification`, `CodeRetrievalQuery` + `Unknown` |
| `title` | `string` | ✅ Supported | `EmbedContentRequest.Title` | Validated only when `TaskType` is `RetrievalDocument` |
| `outputDimensionality` | `integer` | ✅ Supported | `EmbedContentRequest.OutputDimensionality` | Positive reduced vector dimension |
| `embedding.values` | `number[]` | ✅ Supported | `EmbedContentResponse.Embedding.Values` (`IList<float>`) | Dense float embedding vector |
| `requests` / `embeddings` | `EmbedContentRequest[]` / `ContentEmbedding[]` | ✅ Supported | `BatchEmbedContentsRequest.Requests` / `BatchEmbedContentsResponse.Embeddings` | Batch embedding request and response |

---

## 8. `Microsoft.Extensions.AI` (`IChatClient` & `IEmbeddingGenerator`) Mapping

`GeminiChatClient` (created via `client.AsIChatClient()` or resolved from DI) and `GeminiEmbeddingGenerator` (created via `client.AsIEmbeddingGenerator()` or resolved from DI) map `Microsoft.Extensions.AI` abstractions to Gemini wire models as follows:

| `Microsoft.Extensions.AI` Type | Gemini Wire Mapping |
|---|---|
| `ChatRole.System` | Merged into `GenerateContentRequest.SystemInstruction` |
| `ChatRole.User` / `ChatRole.Tool` | `Content` with `Role.User` |
| `ChatRole.Assistant` | `Content` with `Role.Model` |
| `TextContent` | `Part.Text` (preserves trailing `ThoughtSignature` via `AdditionalProperties`) |
| `AIContent.Annotations` (`CitationAnnotation`) | On the `TextContent` of a single-text response, otherwise on an annotation-only `AIContent`. Mapped from `Candidate.CitationMetadata` and `Candidate.GroundingMetadata` (`GroundingChunks` + `GroundingSupports`), converting UTF-8 byte offsets into UTF-16 `char` offsets on `TextSpanAnnotatedRegion` |
| `ChatResponse.AdditionalProperties["gemini.webSearchQueries"]` | Mapped from `Candidate.GroundingMetadata.WebSearchQueries` |
| `TextReasoningContent` | `Part` with `Thought = true`, `Text`, and `ThoughtSignature` (`ProtectedData`) |
| `DataContent` | `Part.InlineData` (`Blob` with `MimeType` and `Data` bytes) |
| `UriContent` | `Part.FileData` (`FileData` with `FileUri` and `MimeType`) |
| `FunctionCallContent` | `Part.FunctionCall` (preserves `ThoughtSignature` and synthesized ID markers in `AdditionalProperties`) |
| `FunctionResultContent` | `Part.FunctionResponse` (correlated with the preceding `FunctionCallContent` name and call ID) |
| `UsageContent` / `UsageDetails` | Mapped from `UsageMetadata` (emitted once as a single trailing update in streaming to avoid cumulative double-counting) |
| `AIFunctionDeclaration` | `FunctionDeclaration` (`Name`, `Description`, `ParametersJsonSchema`, `ResponseJsonSchema`) |
| `HostedWebSearchTool` | `Tool { GoogleSearch = new GoogleSearchTool() }` |
| `HostedCodeInterpreterTool` | `Tool { CodeExecution = new CodeExecutionTool() }` |
| `ChatToolMode.Auto` / `None` / `RequireAny` | `FunctionCallingMode.Auto` / `None` / `Any` (with `AllowedFunctionNames` when `RequiredFunctionName` is specified) |
| `ChatResponseFormatText` | `GenerationConfig.ResponseMimeType = "text/plain"` |
| `ChatResponseFormatJson` | `GenerationConfig.ResponseMimeType = "application/json"` + `ResponseJsonSchema` |
| `ChatOptions.Reasoning` | Mapped to `ThinkingConfig` (`ThinkingLevel` on Gemini 3+, `ThinkingBudget` on Gemini 2.5, `IncludeThoughts` from `ReasoningOutput`) |
| `ChatOptions.RawRepresentationFactory` | Accepts a `GenerateContentRequest` template, which the client modifies, so return a new instance on each call |
| `IEmbeddingGenerator<string, Embedding<float>>` | Always calls `:batchEmbedContents`; maps `EmbeddingGenerationOptions.ModelId`, `Dimensions` (`outputDimensionality`), and `RawRepresentationFactory` (`EmbedContentRequest`) |

---

## 9. Forward Compatibility & Wire Enums

To prevent deserialization crashes when Google adds new fields or enum values to the Gemini API:

1. **`[JsonExtensionData]` on every DTO**: All request and response model classes include `IDictionary<string, JsonElement>? ExtensionData`, ensuring unknown JSON properties survive round-trip serialization. Through `IChatClient`, a `Part` with such properties that maps to no other chat content (for example `toolCall` or `toolResponse`) is surfaced as a plain `AIContent` whose `RawRepresentation` is the part, and is replayed unchanged on the next turn. A part that would fail request validation is ignored instead. `RawRepresentation` is not serialized, so these parts, including any `thoughtSignature` they carry, do not survive persisting chat history as JSON.
2. **`LenientStringEnumConverter<TEnum>` on all 16 wire enums**:
   - `FinishReason`, `BlockReason`, `HarmCategory`, `HarmProbability`, `HarmSeverity`, `HarmBlockThreshold`, `HarmBlockMethod`, `Role`, `SchemaType`, `ThinkingLevel`, `MediaResolution`, `FunctionCallingMode`, `CodeExecutionLanguage`, `CodeExecutionOutcome`, `UrlRetrievalStatus`, and `TaskType`.
   - Known members use official Google Protobuf enum numbers (`Unspecified = 0` where defined); unrecognized string or integer tokens deserialize to `Unknown` (`-1`) instead of throwing `JsonException`.
   - Outbound validation (`GenerateContentRequest.Validate()`, `EmbedContentRequest.Validate()`, `BatchEmbedContentsRequest.Validate()`) rejects `Unknown` on request enums before an HTTP call is made (while allowing response-originated `CodeExecutionLanguage.Unknown` and `CodeExecutionOutcome.Unknown` for multi-turn history replay).
