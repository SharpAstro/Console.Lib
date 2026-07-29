using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Console.Lib.Inspector;

/// <summary>
/// JSON string escaping, by hand.
/// <para>
/// Deliberately not <c>JsonSerializer.Serialize</c>. This is a <c>dnx</c> tool, so it is a plausible
/// candidate for trimming or AOT one day, and both set
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> — at which point the generic overload throws at
/// runtime even for a plain string. That exact failure already cost a debugging cycle on the APP side of this
/// protocol, surfacing as a socket that closed the instant it was written to; there is no reason to leave it
/// armed on the driver side too.
/// </para>
/// <para>
/// It cannot borrow <c>DebugInspectorCore.Quote</c>: that lives behind <c>#if DEBUG</c>, so a published
/// DIR.Lib does not contain it. A local copy is also consistent with this project referencing nothing from
/// the framework.
/// </para>
/// </summary>
internal static class Json
{
    public static string Quote(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

/// <summary>One discovered debuggable terminal app.</summary>
/// <param name="Address">Where it replied from. NOT what the command connection uses — see
/// <see cref="InspectorSocketClient"/>.</param>
/// <param name="Kind">Its surface kind. Only a terminal speaks the verbs in this sidecar.</param>
public sealed record InspectorInstance(IPAddress Address, int TcpPort, string App, string Kind, int Pid, int Proto);

/// <summary>
/// Finds debuggable instances with a UDP multicast query, collecting the unicast replies.
///
/// <para>Replies whose <c>kind</c> is not a terminal are dropped. Discovery is one shared group, so a GPU app
/// on the same machine answers too — and offering it <c>screen</c> would be nonsense. This family has been
/// bitten by an unfiltered shared broadcast domain before, in LAN peer discovery.</para>
/// </summary>
public sealed class InspectorDiscoveryClient(IPAddress group, int port)
{
    private static readonly byte[] Query = Encoding.UTF8.GetBytes("{\"q\":\"dir-inspect\",\"proto\":1}");

    public async Task<IReadOnlyList<InspectorInstance>> DiscoverAsync(CancellationToken ct = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(Query, Query.Length, new IPEndPoint(group, port));

        var found = new List<InspectorInstance>();
        var seen = new HashSet<int>();

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            while (true)
            {
                var recv = await udp.ReceiveAsync(window.Token);
                if (TryParse(recv, out var instance) && IsTerminal(instance!.Kind) && seen.Add(instance.Pid))
                {
                    found.Add(instance);
                }
            }
        }
        catch (OperationCanceledException) { /* collection window elapsed */ }
        catch (SocketException) { /* transient; return what we have */ }

        return found;
    }

    /// <summary>
    /// Whether a reply is a terminal we can drive. <c>console</c> is accepted alongside <c>tui</c> because
    /// Console.Lib 4.4 shipped the older word for one afternoon; dropping it would make that build INVISIBLE
    /// to this sidecar rather than merely mislabelled, and a silent disappearance is the exact failure the
    /// kind field exists to prevent.
    /// </summary>
    private static bool IsTerminal(string kind) => kind is "tui" or "console";

    private static bool TryParse(UdpReceiveResult recv, out InspectorInstance? instance)
    {
        instance = null;
        try
        {
            using var doc = JsonDocument.Parse(recv.Buffer);
            var root = doc.RootElement;
            instance = new InspectorInstance(
                recv.RemoteEndPoint.Address,
                root.GetProperty("tcpPort").GetInt32(),
                root.TryGetProperty("app", out var a) ? a.GetString() ?? "?" : "?",
                root.TryGetProperty("kind", out var k) ? k.GetString() ?? "unknown" : "unknown",
                root.TryGetProperty("pid", out var p) ? p.GetInt32() : 0,
                root.TryGetProperty("proto", out var v) ? v.GetInt32() : 0);
            return true;
        }
        catch (JsonException) { return false; }
        catch (KeyNotFoundException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

/// <summary>
/// Sends one command over the app's TCP command server: newline-delimited JSON, one object each way.
/// A connection per call — the exchanges are tiny and infrequent, and a pooled connection would have to
/// survive the app exiting and restarting between tool calls, which is the common case while debugging.
/// </summary>
public sealed class InspectorSocketClient
{
    private int _id;

    public async Task<JsonElement> SendAsync(
        InspectorInstance target, string method, string? paramsJson, CancellationToken ct = default)
    {
        using var tcp = new TcpClient();

        // Loopback, NOT the address the discovery reply came from. The app's command server binds
        // IPAddress.Loopback by design, so it is only ever reachable locally — and a multicast reply can
        // easily arrive from some other adapter (a Hyper-V or WSL bridge), which is then the one address
        // guaranteed to refuse the connection.
        await tcp.ConnectAsync(IPAddress.Loopback, target.TcpPort, ct);

        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        var id = Interlocked.Increment(ref _id);
        await writer.WriteLineAsync(
            $"{{\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson ?? "{}"}}}".AsMemory(), ct);

        var line = await reader.ReadLineAsync(ct)
            ?? throw new InvalidOperationException("the app closed the connection without replying");

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
        {
            throw new InvalidOperationException($"{method}: {err.GetString()}");
        }
        return root.GetProperty("result").Clone();
    }
}
