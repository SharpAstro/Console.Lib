using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Console.Lib.Inspector;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// The sidecar's command client presents the per-run token the way DIR.Lib's inspector core expects it (a
/// top-level <c>token</c> string), against a loopback listener each test controls. Every wait has a deadline.
/// Tests that touch the process environment share one collection so they cannot race each other.
/// </summary>
[Collection("InspectorTokenEnvironment")]
public sealed class InspectorClientTokenTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static InspectorInstance Instance(int port) =>
        new(IPAddress.Loopback, port, "fake", "tui", 1, 1);

    /// <summary>Accepts one connection, records its request line, answers with <paramref name="reply"/>.</summary>
    private static (int Port, Task<string> Request, TcpListener Listener) Serve(string reply)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var request = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(Deadline);
            using var client = await listener.AcceptTcpClientAsync(cts.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            var line = await reader.ReadLineAsync(cts.Token) ?? "";
            await writer.WriteLineAsync(reply.AsMemory(), cts.Token);
            return line;
        });
        return (port, request, listener);
    }

    [Fact]
    public async Task Explicit_token_is_sent_as_a_top_level_field()
    {
        var (port, request, listener) = Serve("{\"id\":1,\"result\":{\"ok\":true}}");
        using var stop = new Stop(listener);
        using var cts = new CancellationTokenSource(Deadline);

        var result = await new InspectorSocketClient().SendAsync(Instance(port), "ping", null, cts.Token, "s3cret");

        result.GetProperty("ok").GetBoolean().ShouldBeTrue();
        using var doc = JsonDocument.Parse(await request);
        doc.RootElement.GetProperty("token").GetString().ShouldBe("s3cret");
        doc.RootElement.GetProperty("method").GetString().ShouldBe("ping");
    }

    [Fact]
    public async Task Token_defaults_to_the_environment_variable()
    {
        var before = Environment.GetEnvironmentVariable(InspectorSocketClient.TokenVariable);
        Environment.SetEnvironmentVariable(InspectorSocketClient.TokenVariable, "from-env");
        try
        {
            var (port, request, listener) = Serve("{\"id\":1,\"result\":{}}");
            using var stop = new Stop(listener);
            using var cts = new CancellationTokenSource(Deadline);

            await new InspectorSocketClient().SendAsync(Instance(port), "ping", null, cts.Token);

            using var doc = JsonDocument.Parse(await request);
            doc.RootElement.GetProperty("token").GetString().ShouldBe("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(InspectorSocketClient.TokenVariable, before);
        }
    }

    [Fact]
    public async Task Missing_token_refusal_is_a_clear_error()
    {
        var before = Environment.GetEnvironmentVariable(InspectorSocketClient.TokenVariable);
        Environment.SetEnvironmentVariable(InspectorSocketClient.TokenVariable, null);
        try
        {
            var (port, request, listener) = Serve("{\"id\":1,\"error\":\"!unauthorized\"}");
            using var stop = new Stop(listener);
            using var cts = new CancellationTokenSource(Deadline);

            var ex = await Should.ThrowAsync<InvalidOperationException>(
                () => new InspectorSocketClient().SendAsync(Instance(port), "ping", null, cts.Token));

            ex.Message.ShouldContain("needs a token");
            ex.Message.ShouldContain("DIR_INSPECTOR_TOKEN");
            (await request).ShouldNotContain("\"token\"");
        }
        finally
        {
            Environment.SetEnvironmentVariable(InspectorSocketClient.TokenVariable, before);
        }
    }

    [Fact]
    public async Task Wrong_token_refusal_says_the_token_is_wrong()
    {
        var (port, _, listener) = Serve("{\"id\":1,\"error\":\"!unauthorized\"}");
        using var stop = new Stop(listener);
        using var cts = new CancellationTokenSource(Deadline);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => new InspectorSocketClient().SendAsync(Instance(port), "ping", null, cts.Token, "nope"));

        ex.Message.ShouldContain("token is wrong");
    }

    private sealed class Stop(TcpListener listener) : IDisposable
    {
        public void Dispose() => listener.Stop();
    }
}
