using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
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

    /// <summary>
    /// A JSON object from name/value pairs. A null value is written as <c>null</c> rather than omitted, which
    /// the app reads the same as an absent field — so an optional parameter needs no second call shape.
    /// </summary>
    public static string Obj(params (string Name, object? Value)[] fields)
    {
        var sb = new StringBuilder("{");
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(Quote(fields[i].Name)).Append(':').Append(Value(fields[i].Value));
        }
        return sb.Append('}').ToString();
    }

    /// <summary>
    /// One value. The accepted set is closed on purpose: an unsupported type throws HERE, at the call site
    /// that added it, rather than silently producing something the app cannot parse.
    /// </summary>
    private static string Value(object? value) => value switch
    {
        null => "null",
        string s => Quote(s),
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        // InvariantCulture is what stops a comma decimal separator producing JSON that parses as two values
        // on a machine with a European locale.
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException(
            $"{value.GetType().Name} is not a supported inspector parameter type; add it to Json.Value"),
    };
}

/// <summary>One discovered debuggable terminal app.</summary>
/// <param name="Address">Where it replied from, or loopback for an instance found in the registry. NOT what the
/// command connection uses: see <see cref="InspectorSocketClient"/>.</param>
/// <param name="Kind">Its surface kind. Only a terminal speaks the verbs in this sidecar.</param>
public sealed record InspectorInstance(IPAddress Address, int TcpPort, string App, string Kind, int Pid, int Proto);

/// <summary>
/// Finds debuggable instances: first in the per-user file registry (<see cref="InspectorRegistry"/>), then with
/// a UDP multicast query, collecting the unicast replies.
///
/// <para>Both, because each finds something the other cannot. From DIR.Lib 11.21 an app registers a file and
/// does not answer multicast unless asked to, since the multicast bind is what raises a Windows Firewall prompt
/// per executable path. Builds older than that answer multicast and never register, and an app on another
/// machine can only be reached by the query. The registry needs no network, so a failed query costs only what
/// the query would have found.</para>
///
/// <para>Instances whose <c>kind</c> is not a terminal are dropped. Discovery is one shared group, and one shared
/// registry folder, so a GPU app on the same machine is found too; offering it <c>screen</c> would be nonsense.
/// This family has been bitten by an unfiltered shared broadcast domain before, in LAN peer discovery.</para>
/// </summary>
public sealed class InspectorDiscoveryClient(IPAddress group, int port)
{
    private static readonly byte[] Query = Encoding.UTF8.GetBytes("{\"q\":\"dir-inspect\",\"proto\":1}");

    /// <summary>
    /// The registry folder read before the query. A test points it at a temporary folder, so it neither touches
    /// nor depends on the apps actually registered for the user running it.
    /// </summary>
    internal string? RegistryDirectory { get; init; } = InspectorRegistry.DefaultDirectory;

    public async Task<IReadOnlyList<InspectorInstance>> DiscoverAsync(CancellationToken ct = default)
    {
        var registered = InspectorRegistry.Read(RegistryDirectory);
        var replies = await QueryAsync(ct);

        var local = LocalAddresses();
        return Merge(registered, replies, a => IPAddress.IsLoopback(a) || local.Contains(a));
    }

    /// <summary>
    /// The registry's instances, then the replies, keeping terminal kinds only and the first of each
    /// (address, pid).
    /// <para>
    /// The address is in the key because an app on another machine can have the same pid as one here. A reply
    /// from THIS machine is keyed as loopback, though, whatever adapter it came from: that is the address the
    /// app's registry entry carries, and the reply's source is whichever local interface the query happened to
    /// reach it on (a LAN address, a Hyper-V or WSL bridge). Keyed on the raw source, an 11.21 app with LAN
    /// discovery on would be listed twice. The entry kept for it is the registry's, which was checked for a
    /// reused pid; the reply's own address is left as it was, only its key changes.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<InspectorInstance> Merge(
        IEnumerable<InspectorInstance> registered,
        IEnumerable<InspectorInstance> replies,
        Func<IPAddress, bool> isThisMachine)
    {
        var found = new List<InspectorInstance>();
        var seen = new HashSet<(IPAddress Address, int Pid)>();
        foreach (var instance in registered.Concat(replies))
        {
            var key = (isThisMachine(instance.Address) ? IPAddress.Loopback : instance.Address, instance.Pid);
            if (IsTerminal(instance.Kind) && seen.Add(key))
            {
                found.Add(instance);
            }
        }
        return found;
    }

    /// <summary>
    /// Every reply to one multicast query within the collection window, of every kind. Empty, not a failure,
    /// when there is no network to send on: the registry has already answered for this machine.
    /// </summary>
    private async Task<List<InspectorInstance>> QueryAsync(CancellationToken ct)
    {
        var replies = new List<InspectorInstance>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(Query, Query.Length, new IPEndPoint(group, port));

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromMilliseconds(500));
            while (true)
            {
                var recv = await udp.ReceiveAsync(window.Token);
                if (TryParse(recv, out var instance))
                {
                    replies.Add(instance!);
                }
            }
        }
        catch (OperationCanceledException) { /* collection window elapsed */ }
        catch (SocketException) { /* no network to send on, or transient; return what we have */ }

        return replies;
    }

    /// <summary>
    /// This machine's unicast addresses, so a reply can be told apart from one sent by another machine. Empty if
    /// the OS will not enumerate its interfaces, which leaves loopback as the only address known to be local.
    /// </summary>
    private static HashSet<IPAddress> LocalAddresses()
    {
        var addresses = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    addresses.Add(unicast.Address);
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
        return addresses;
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
