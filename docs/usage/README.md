# Usage Guide

How to use `GeminiGatewayClient`, one topic per page. Every sample assumes the setup in [Getting Started](getting-started.md).

## Start Here

| Page | Covers |
|---|---|
| [Getting Started](getting-started.md) | Install, first request, the two APIs, what the library does not do |
| [Configuration](configuration.md) | `GeminiClientOptions`, gateways, models, `HttpClient`, dependency injection, resilience |

## Generating Content

| Page | Covers |
|---|---|
| [Text Generation](text-generation.md) | Prompts, requests, streaming, multi-turn history, safety settings, reading a response |
| [Structured Output](structured-output.md) | JSON Schema, `Schema`, enums |
| [Thinking](thinking.md) | `ThinkingConfig`, `ChatOptions.Reasoning`, thought summaries |
| [Multimodal Input](multimodal.md) | Inline bytes, file URIs, video clipping |

## Tools

| Page | Covers |
|---|---|
| [Function Calling](function-calling.md) | Building function calls by hand with `GeminiClient`, and automatic calling with `IChatClient` |
| [Grounding and Built-in Tools](grounding.md) | Google Search, URL Context, code execution, citations |

## Integration

| Page | Covers |
|---|---|
| [`IChatClient`](chat-client.md) | What maps to what, streaming, usage, persisting history, histories from other providers |
| [Embeddings](embeddings.md) | `EmbedContentAsync`, `BatchEmbedContentsAsync`, `IEmbeddingGenerator` |
| [Native AOT](native-aot.md) | Trimming and source-generated JSON for tools |

## Reference

| Page | Covers |
|---|---|
| [Errors and Exceptions](errors.md) | Every exception, every failure case, and how to tell them apart |
| [API Compatibility Matrix](../API_COMPATIBILITY.md) | Field-by-field mapping to the Gemini REST API |
| [Architecture](../ARCHITECTURE.md) | Class structure and sequence diagrams (Japanese) |
