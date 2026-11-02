using System.Net;
using System.Text;

namespace GeminiAPIWrapper.Tests;

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public RecordingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string contentType = "application/json",
        Action<HttpResponseMessage>? configureResponse = null)
    {
        _responder = (_, _) =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, contentType)
            };
            configureResponse?.Invoke(response);
            return Task.FromResult(response);
        };
    }

    public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        Requests.Add(request);
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return await _responder(request, cancellationToken);
    }
}

