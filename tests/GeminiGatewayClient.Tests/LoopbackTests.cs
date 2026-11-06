using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace GeminiGatewayClient.Tests;

/// <summary>
/// Behavior that only a real socket shows: what the default handler does with redirects and cookies, and how time passes
/// on a live stream.
/// </summary>
public class LoopbackTests
{
    private const string Chunk = """{"candidates":[{"content":{"role":"model","parts":[{"text":"hi"}]}}]}""";

    [Fact]
    public async Task Cookie_SetForOneCaller_DoesNotReachTheNextOne()
    {
        var cookies = new List<string?>();
        using var server = new LoopbackServer(async context =>
        {
            cookies.Add(context.Request.Headers["Cookie"]);
            context.Response.AppendHeader("Set-Cookie", "gw-session=tenantA; Path=/");
            await context.WriteAsync(Chunk);
        });
        var keys = new Queue<string>(["tenantA", "tenantB"]);
        using var client = new GeminiClient(new GeminiClientOptions
        {
            Endpoint = server.Uri,
            Model = "m",
            ApiKeyProvider = _ => ValueTask.FromResult(keys.Dequeue())
        });

        await client.GenerateContentAsync("a");
        await client.GenerateContentAsync("b");

        Assert.Equal([null, null], cookies);
    }

    public static TheoryData<string> ClientsWithTheDefaultHandler => ["owned", "caller", "dependency injection"];

    [Theory]
    [MemberData(nameof(ClientsWithTheDefaultHandler))]
    public async Task Redirect_ToAnotherHost_NeverCarriesTheKeyThere(string kind)
    {
        var otherHostCalls = 0;
        using var otherHost = new LoopbackServer(context =>
        {
            Interlocked.Increment(ref otherHostCalls);
            return context.WriteAsync(Chunk);
        });
        using var gateway = new LoopbackServer(context =>
        {
            context.Response.StatusCode = 307;
            context.Response.RedirectLocation = otherHost.Uri + "/moved";
            return context.WriteAsync(string.Empty);
        });
        void Configure(GeminiClientOptions options)
        {
            options.Endpoint = gateway.Uri;
            options.Model = "m";
            options.ApiKey = "secret-key";
        }

        var options = new GeminiClientOptions();
        Configure(options);
        using var provider = new ServiceCollection().Also(services => services.AddGeminiClient(Configure)).BuildServiceProvider();
        using var owned = new GeminiClient(options);
        using var caller = new GeminiClient(options, new HttpClient(GeminiClient.CreateDefaultHandler()));
        var client = kind switch
        {
            "owned" => owned,
            "caller" => caller,
            _ => provider.GetRequiredService<IGeminiClient>()
        };

        var error = await Assert.ThrowsAsync<GeminiApiException>(() => client.GenerateContentAsync("hi"));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, error.StatusCode);
        Assert.Equal(0, otherHostCalls);
    }

    [Fact]
    public async Task StalledStream_FailsWithTheIdleTimeout()
    {
        using var server = new LoopbackServer(async context =>
        {
            var body = context.StartStream();
            await body.SendAsync($"data: {Chunk}\n\n");
            await Task.Delay(Timeout.Infinite);
        });
        using var client = new GeminiClient(new GeminiClientOptions { Endpoint = server.Uri, Model = "m", StreamIdleTimeout = TimeSpan.FromMilliseconds(400) });
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in client.StreamGenerateContentAsync("hi", cancellationToken: guard.Token))
            {
            }
        });
    }

    [Fact]
    public async Task SlowHealthyStream_OutlivesTheRequestTimeout()
    {
        using var server = new LoopbackServer(async context =>
        {
            var body = context.StartStream();
            for (var i = 0; i < 6; i++)
            {
                await body.SendAsync($"data: {Chunk}\n\n");
                await Task.Delay(300);
            }
        });
        using var client = new GeminiClient(
            new GeminiClientOptions { Endpoint = server.Uri, Model = "m", StreamIdleTimeout = TimeSpan.FromSeconds(10) },
            new HttpClient(GeminiClient.CreateDefaultHandler()) { Timeout = TimeSpan.FromSeconds(1) });
        var received = 0;

        await foreach (var _ in client.StreamGenerateContentAsync("hi"))
        {
            received++;
        }

        Assert.Equal(6, received);
    }

    [Fact]
    public async Task JsonArrayStream_DeliversEachChunkAsItArrives()
    {
        var firstChunkSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new LoopbackServer(async context =>
        {
            var body = context.StartStream("application/json");
            await body.SendAsync($"[{Chunk}");
            await firstChunkSeen.Task;
            await body.SendAsync($",\r\n{Chunk}]");
        });
        using var client = new GeminiClient(new GeminiClientOptions { Endpoint = server.Uri, Model = "m" });
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var received = 0;

        await foreach (var _ in client.StreamGenerateContentAsync("hi", cancellationToken: guard.Token))
        {
            received++;
            firstChunkSeen.TrySetResult();
        }

        Assert.Equal(2, received);
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public LoopbackServer(Func<HttpListenerContext, Task> handle)
        {
            var port = GetFreePort();
            Uri = new Uri($"http://localhost:{port}/v1beta");
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _ = AcceptAsync(handle);
        }

        public Uri Uri { get; }

        public void Dispose() => _listener.Abort();

        private static int GetFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task AcceptAsync(Func<HttpListenerContext, Task> handle)
        {
            try
            {
                while (true)
                {
                    var context = await _listener.GetContextAsync();
                    _ = RespondAsync(context, handle);
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // The test is over.
            }
        }

        private static async Task RespondAsync(HttpListenerContext context, Func<HttpListenerContext, Task> handle)
        {
            try
            {
                await handle(context);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // The client went away, as a test of a stalled stream expects.
            }
        }
    }
}

internal static class LoopbackExtensions
{
    public static async Task WriteAsync(this HttpListenerContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    public static StreamWriterBody StartStream(this HttpListenerContext context, string mediaType = "text/event-stream")
    {
        context.Response.ContentType = mediaType;
        context.Response.SendChunked = true;
        return new StreamWriterBody(context.Response.OutputStream);
    }

    public static ServiceCollection Also(this ServiceCollection services, Action<ServiceCollection> configure)
    {
        configure(services);
        return services;
    }
}

internal sealed class StreamWriterBody(Stream stream)
{
    public async Task SendAsync(string text)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        await stream.FlushAsync();
    }
}
