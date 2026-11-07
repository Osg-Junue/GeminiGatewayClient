# Usage Guide

How to use `GeminiGatewayClient`, one topic per page.

Unless a sample shows its own setup, it assumes `client` and `chatClient` from [Getting Started](getting-started.md), with `using GeminiGatewayClient;`. A sample may continue the one before it on the same page, and `ct` stands for your `CancellationToken`.

## Start Here

| Page | Covers |
|---|---|
| [Getting Started](getting-started.md) | Install, first request, the two APIs, what the library does not do |
| [Configuration](configuration.md) | `GeminiClientOptions`, gateways, credential rotation, Vertex AI, models, `HttpClient`, dependency injection, resilience |

## Generating Content

| Page | Covers |
|---|---|
| [Text Generation](text-generation.md) | Prompts, requests, streaming, multi-turn history, safety settings, reading a response |
| [Structured Output](structured-output.md) | JSON Schema, `Schema`, enums |
| [Thinking](thinking.md) | `ThinkingConfig`, `ChatOptions.Reasoning`, thought summaries, thought signatures |
| [Multimodal Input](multimodal.md) | Inline bytes, file URIs, video clipping, media in a response |

## Tools

| Page | Covers |
|---|---|
| [Function Calling](function-calling.md) | Building function calls by hand with `GeminiClient`, and automatic calling with `IChatClient`, which needs the `Microsoft.Extensions.AI` package |
| [Grounding and Built-in Tools](grounding.md) | Google Search, URL Context, code execution, citations |

## Integration

| Page | Covers |
|---|---|
| [`IChatClient`](chat-client.md) | What maps to what, streaming, token usage, persisting history, histories from other providers |
| [Embeddings](embeddings.md) | `EmbedContentAsync`, `BatchEmbedContentsAsync`, `IEmbeddingGenerator` |
| [Native AOT](native-aot.md) | Publishing with Native AOT, source-generated JSON for your own types and tools |

## Reference

| Page | Covers |
|---|---|
| [Errors and Exceptions](errors.md) | The exceptions the library raises, when each failure happens, and how to tell them apart |
| [API Compatibility Matrix](../API_COMPATIBILITY.md) | Mapping of the Gemini REST API to library types |
| [Architecture](../ARCHITECTURE.md) | Class structure and sequence diagrams (Japanese) |
| [Changelog](../../CHANGELOG.md) | What changed in each version |
