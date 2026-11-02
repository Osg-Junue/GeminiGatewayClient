namespace GeminiAPIWrapper.Models;

/// <summary>
/// Well-known IANA MIME type constants supported by Gemini multimodal inputs and outputs.
/// <para>
/// <see cref="Blob.MimeType"/> and <see cref="FileData.MimeType"/> accept any string so newly supported
/// media types or parameterized values (such as <c>audio/L16;codec=pcm;rate=24000</c>) work without library updates.
/// </para>
/// See <see href="https://ai.google.dev/gemini-api/docs/image-understanding"/>,
/// <see href="https://ai.google.dev/gemini-api/docs/audio"/>, and
/// <see href="https://ai.google.dev/gemini-api/docs/video-understanding"/>.
/// </summary>
public static class MimeTypes
{
    // Documents & text
    /// <summary>PDF document (<c>application/pdf</c>).</summary>
    public const string Pdf = "application/pdf";

    /// <summary>Plain text (<c>text/plain</c>).</summary>
    public const string TextPlain = "text/plain";

    /// <summary>HTML document (<c>text/html</c>).</summary>
    public const string TextHtml = "text/html";

    /// <summary>CSS stylesheet (<c>text/css</c>).</summary>
    public const string TextCss = "text/css";

    /// <summary>Markdown text (<c>text/md</c>).</summary>
    public const string TextMarkdown = "text/md";

    /// <summary>CSV comma-separated values (<c>text/csv</c>).</summary>
    public const string TextCsv = "text/csv";

    /// <summary>XML document (<c>text/xml</c>).</summary>
    public const string TextXml = "text/xml";

    /// <summary>JavaScript source (<c>text/javascript</c>).</summary>
    public const string TextJavaScript = "text/javascript";

    /// <summary>Python source (<c>text/x-python</c>).</summary>
    public const string TextPython = "text/x-python";

    /// <summary>JSON document (<c>application/json</c>).</summary>
    public const string ApplicationJson = "application/json";

    /// <summary>Enum classification output for <see cref="GenerationConfig.ResponseMimeType"/> (<c>text/x.enum</c>).</summary>
    public const string TextXEnum = "text/x.enum";

    /// <summary>Arbitrary binary data (<c>application/octet-stream</c>).</summary>
    public const string ApplicationOctetStream = "application/octet-stream";

    // Images
    /// <summary>PNG image (<c>image/png</c>).</summary>
    public const string ImagePng = "image/png";

    /// <summary>JPEG image (<c>image/jpeg</c>).</summary>
    public const string ImageJpeg = "image/jpeg";

    /// <summary>WebP image (<c>image/webp</c>).</summary>
    public const string ImageWebp = "image/webp";

    /// <summary>HEIC image (<c>image/heic</c>).</summary>
    public const string ImageHeic = "image/heic";

    /// <summary>HEIF image (<c>image/heif</c>).</summary>
    public const string ImageHeif = "image/heif";

    // Audio
    /// <summary>WAV audio (<c>audio/wav</c>).</summary>
    public const string AudioWav = "audio/wav";

    /// <summary>MP3 audio (<c>audio/mp3</c>).</summary>
    public const string AudioMp3 = "audio/mp3";

    /// <summary>MPEG audio (<c>audio/mpeg</c>).</summary>
    public const string AudioMpeg = "audio/mpeg";

    /// <summary>AIFF audio (<c>audio/aiff</c>).</summary>
    public const string AudioAiff = "audio/aiff";

    /// <summary>AAC audio (<c>audio/aac</c>).</summary>
    public const string AudioAac = "audio/aac";

    /// <summary>OGG Vorbis audio (<c>audio/ogg</c>).</summary>
    public const string AudioOgg = "audio/ogg";

    /// <summary>FLAC lossless audio (<c>audio/flac</c>).</summary>
    public const string AudioFlac = "audio/flac";

    // Video
    /// <summary>MP4 video (<c>video/mp4</c>).</summary>
    public const string VideoMp4 = "video/mp4";

    /// <summary>MPEG video (<c>video/mpeg</c>).</summary>
    public const string VideoMpeg = "video/mpeg";

    /// <summary>QuickTime MOV video (<c>video/mov</c>).</summary>
    public const string VideoMov = "video/mov";

    /// <summary>AVI video (<c>video/avi</c>).</summary>
    public const string VideoAvi = "video/avi";

    /// <summary>Flash Video (<c>video/x-flv</c>).</summary>
    public const string VideoFlv = "video/x-flv";

    /// <summary>MPG video (<c>video/mpg</c>).</summary>
    public const string VideoMpg = "video/mpg";

    /// <summary>WebM video (<c>video/webm</c>).</summary>
    public const string VideoWebm = "video/webm";

    /// <summary>Windows Media Video (<c>video/wmv</c>).</summary>
    public const string VideoWmv = "video/wmv";

    /// <summary>3GPP video (<c>video/3gpp</c>).</summary>
    public const string Video3gpp = "video/3gpp";
}
