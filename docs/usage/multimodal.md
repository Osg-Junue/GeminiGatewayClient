# Multimodal Input

A `Content` holds any mix of parts. `MimeTypes` lists common types. `Part.FromBytes` and `Part.FromUri` accept any non-empty string, while `DataContent` and `UriContent` need a well-formed media type.

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

Bytes travel as Base64 in the request, which makes it about a third larger than the file. The library sets no limit. [Google's guide](https://aistudio.google.com/docs/file-input-methods) gave 100 MB per request, or 50 MB for PDFs, when read on 2026-11-07. Prefer a file URI for anything larger, or for a file you send more than once.

## File URIs

Pass the URI that the Files API returned, or a public HTTPS URL:

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

- This library does not upload or register files. Do that with another tool.
- Pass the full URI. The API answered HTTP 400 to `files/abc123` when checked on 2026-11-07.
- A `gs://` URI cannot be used directly. The API answered HTTP 400 and asked for the file to be registered with the Files API first, so pass the URI that registration returns.
- The `mimeType` argument is optional. Without it, `fileData` has no `mimeType`.

## Video Clipping and Frame Rate

Set `VideoMetadata` on the part that holds the video. `StartOffset` and `EndOffset` clip it, and `Fps` sets how many frames per second are sampled:

```csharp
using GeminiGatewayClient.Models;

var video = Part.FromUri("https://generativelanguage.googleapis.com/v1beta/files/abc123", MimeTypes.VideoMp4);
video.VideoMetadata = new VideoMetadata
{
    StartOffset = new VideoOffset { Seconds = 10 },
    EndOffset = VideoOffset.FromTimeSpan(TimeSpan.FromSeconds(40.5)),
    Fps = 1
};

var request = new GenerateContentRequest
{
    Contents = [Content.FromParts(Role.User, video, Part.FromText("Summarize this clip."))]
};
```

`GenerationConfig.MediaResolution` sets the resolution of input images, video, and PDFs.

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

- The table applies to messages other than `System`. In a `System` message only text is sent, and other content, including `HostedFileContent`, is dropped without an error.
- A `DataContent` inside a function result is serialized as JSON, not sent as `inlineData`.

To clip a video, set `VideoMetadata` through `RawRepresentation`:

```csharp
using GeminiGatewayClient.Models;
using Microsoft.Extensions.AI;

var clip = new UriContent(new Uri("https://generativelanguage.googleapis.com/v1beta/files/abc123"), "video/mp4")
{
    RawRepresentation = new Part
    {
        VideoMetadata = new VideoMetadata { StartOffset = new VideoOffset { Seconds = 10 } }
    }
};
```

`RawRepresentation` is not serialized, so a saved history loses the clip settings.

## Media in a Response

Ask for other kinds of output with `GenerationConfig.ResponseModalities`, and only on a model that supports them. With `IChatClient`, set it through `RawRepresentationFactory`.

```csharp
using GeminiGatewayClient.Models;

var response = await client.GenerateContentAsync(
    "Draw a red circle.",
    generationConfig: new GenerationConfig { ResponseModalities = ["IMAGE"] });

foreach (var part in response.Candidates[0].Content?.Parts ?? [])
{
    if (part.InlineData is { } image)
    {
        Console.WriteLine($"{image.MimeType}: {image.Data.Length} bytes");
    }
}
```

`response.Text` holds text parts only, so it is `null` for an image-only response. With `IChatClient`, `Text` is an empty string.

With `IChatClient`, media comes back as `DataContent` or `UriContent`:

- A missing or invalid media type reads as `application/octet-stream`.
- An `inlineData` part with no data is skipped.
