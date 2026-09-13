using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pol33.Proxy.Tests.Hosting;

/// <summary>
/// Why two response-side paths are left off <c>DownstreamWriteTimeoutSeconds</c>, established against
/// a real Kestrel host and a real socket rather than by reading the framework.
/// </summary>
/// <remarks>
/// <para><see cref="InferenceHttpForwarder"/> bounds every response-body write, but two writes sit
/// outside that loop: <c>Response.StartAsync</c>, which commits the headers of a streaming response,
/// and the small gateway error body the router writes when a forward failed before the response
/// started. Both run on a token that a stalled consumer never cancels, so the question is whether
/// either can stay pending while a client holds the connection open and refuses to read.</para>
///
/// <para>They cannot, and the reason is structural rather than incidental. An ASP.NET Core response
/// write completes once the bytes are in Kestrel's output pipe; it is deferred only when the pipe
/// already holds more unsent bytes than <c>KestrelServerLimits.MaxResponseBufferSize</c> (64 KB by
/// default, and this gateway does not change it). Both paths write well under that into an *empty*
/// pipe: <c>StartAsync</c> runs before the body phase, and the error body is written only under
/// <c>!Response.HasStarted</c> — and nothing can be in the pipe while the response has not started,
/// because the first byte to reach it is what starts the response.</para>
///
/// <para><see cref="LargeBodyWrite_ForAClientThatNeverReads_IsDeferredByBackpressure"/> is the control.
/// Without it the other two facts would be worthless: they would hold equally in a harness where
/// backpressure never engages at all.</para>
///
/// <para><c>MinResponseDataRate</c> is disabled throughout, because the premise under test is a host
/// whose response data-rate guard cannot be the thing that rescues a stalled write. Kestrel is the
/// only host this gateway runs under — the container entry point is <c>dotnet 33pol.App.dll</c>.</para>
/// </remarks>
public sealed class KestrelResponseBackpressureTests : IAsyncLifetime
{
    private const int ChunkBytes = 16 * 1024;
    private const int LargeWriteChunkCap = 2_048;

    private readonly TaskCompletionSource _responseStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _errorBodyWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication _app = null!;
    private string _origin = null!;
    private int _largeWriteChunks;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            // The condition under test: no data-rate guard to end a stalled write for us.
            options.Limits.MinResponseDataRate = null;
            // 127.0.0.1 rather than ListenLocalhost: Kestrel refuses dynamic port binding on the
            // localhost alias, and the test needs a free port it did not have to guess.
            options.Listen(System.Net.IPAddress.Loopback, 0);
        });

        _app = builder.Build();
        _app.Run(async context =>
        {
            switch (context.Request.Path.Value)
            {
                case "/start":
                    // Exactly what the forwarder does for a streaming response, in the same order and
                    // before any body byte. DisableBuffering is part of that sequence and is included
                    // deliberately: it clears the per-request response data rate, so omitting it would
                    // have tested a path the gateway never takes.
                    context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                    context.Response.Headers.CacheControl = "no-cache";
                    await context.Response.StartAsync(context.RequestAborted);
                    _responseStarted.TrySetResult();
                    break;

                case "/error":
                    // Exactly what the router does under !HasStarted: a small JSON error body.
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"error":{"code":"upstream_error","message":"Backend stopped sending the response body."}}""",
                        context.RequestAborted);
                    _errorBodyWritten.TrySetResult();
                    break;

                case "/large":
                    var chunk = new byte[ChunkBytes];
                    for (var i = 0; i < LargeWriteChunkCap; i++)
                    {
                        await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
                        Interlocked.Increment(ref _largeWriteChunks);
                    }

                    break;
            }
        });

        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        _origin = new Uri(address).Authority;
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(TimeSpan.FromSeconds(5));
        await _app.DisposeAsync();
    }

    /// <summary>
    /// The header commit of a streaming response completes for a client that never reads a byte, so
    /// it cannot strand a forward the way an unbounded body write could.
    /// </summary>
    [Fact]
    public async Task ResponseStartAsync_ForAClientThatNeverReads_Completes()
    {
        using var client = await ConnectAndRequestAsync("/start");

        await _responseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The gateway error body is written only while the response has not started, which means the
    /// output pipe is necessarily empty — nothing can be in it without having started the response.
    /// A few hundred bytes into an empty pipe completes regardless of the consumer.
    /// </summary>
    [Fact]
    public async Task SmallErrorBodyWrite_ForAClientThatNeverReads_Completes()
    {
        using var client = await ConnectAndRequestAsync("/error");

        await _errorBodyWritten.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The control. Backpressure does engage on this host for a client that stops reading, which is
    /// what makes the two facts above evidence rather than an artefact of a harness that never
    /// applies any. It is also the fault <c>DownstreamWriteTimeoutSeconds</c> exists to bound.
    /// </summary>
    [Fact]
    public async Task LargeBodyWrite_ForAClientThatNeverReads_IsDeferredByBackpressure()
    {
        using var client = await ConnectAndRequestAsync("/large");

        // Both assertions are one-sided, so neither can fail merely because the machine is slow: a
        // loaded host makes fewer writes land, not more. The cap is 32 MB against a 64 KB output pipe
        // and a socket send buffer of at most a few megabytes, so reaching it would mean backpressure
        // never engaged at all.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var landed = Volatile.Read(ref _largeWriteChunks);

        landed.Should().BeGreaterThan(0, "writes must reach the output pipe before it fills");
        landed.Should().BeLessThan(
            LargeWriteChunkCap,
            "a client that never reads must stop the writes, which is the stall the per-write deadline bounds");
    }

    /// <summary>
    /// A client that sends a request and then never reads the response, keeping the socket open. The
    /// receive side is simply never touched.
    /// </summary>
    private async Task<TcpClient> ConnectAndRequestAsync(string path)
    {
        var host = _origin.Split(':')[0];
        var port = int.Parse(_origin.Split(':')[1]);
        var client = new TcpClient();
        await client.ConnectAsync(host, port);
        var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {_origin}\r\n\r\n");
        await client.GetStream().WriteAsync(request);
        await client.GetStream().FlushAsync();
        return client;
    }
}
