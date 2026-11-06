# Multimodal Input

A `Content` holds any mix of parts. `MimeTypes` lists common types, and any string is accepted.

## Inline Bytes

```csharp
using GeminiGatewayClient.Models;

byte[] imageBytes = await File.ReadAllBytesAsync("diagram.png");

var request = new GenerateContentRequest
{
    Contents =
    [
        Content.FromParts(
            Role.User,
            Part.FromBytes(imageBytes, MimeTypes.ImagePng),
            Part.FromText("Describe this diagram."))
    ]
};

var response = await client.GenerateContentAsync(request);
Console.WriteLine(response.Text);
```

Bytes travel as Base64 in the request. Prefer a file URI for payloads over about 20 MB.

## File URIs

Pass a Files API URI or a Cloud Storage URI. This library does not upload files, so do that with another tool:

```csharp
using GeminiGatewayClient.Models;

var request = new GenerateContentRequest
{
    Contents =
    [
        Content.FromParts(
            Role.User,
            Part.FromUri("https://generativelanguage.googleapis.com/v1beta/files/abc123", MimeTypes.VideoMp4),
            Part.FromText("Summarize this video."))
    ]
};
```

## Video Clipping

Set `VideoMetadata` on the part that holds the video:

```csharp
using GeminiGatewayClient.Models;

var video = Part.FromUri("https://generativelanguage.googleapis.com/v1beta/files/abc123", MimeTypes.VideoMp4);
video.VideoMetadata = new VideoMetadata
{
    StartOffset = new VideoOffset { Seconds = 10 },
    EndOffset = new VideoOffset { Seconds = 40 },
    Fps = 1
};
```

## With `IChatClient`

| Content | Sent as |
|---|---|
| `DataContent` | `inlineData` |
| `UriContent` | `fileData` |
| `HostedFileContent` | Throws `NotSupportedException`. Use `UriContent` with the uploaded file's URI and media type |

```csharp
using Microsoft.Extensions.AI;

byte[] imageBytes = await File.ReadAllBytesAsync("diagram.png");

var message = new ChatMessage(ChatRole.User,
[
    new DataContent(imageBytes, "image/png"),
    new TextContent("Describe this diagram.")
]);

var response = await chatClient.GetResponseAsync([message]);
```

Media in a response comes back as `DataContent` or `UriContent`.
