using System.Net;
using Console.Lib.Inspector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Stdio MCP server bridging an agent to running Debug-build Console.Lib terminal apps. Spawned by the
// MCP client (e.g. Claude Code) as a child process via `dnx Console.Lib.Inspector --yes`.
//
// All logging goes to STDERR: stdout is the JSON-RPC channel and must stay clean. (The apps being driven
// have the same rule for the opposite reason -- their stdout is the terminal screen.)
//
// Discovery overrides: --group <multicast-ip> / --port <n>, or CONSOLE_INSPECTOR_GROUP / _PORT.
//
// NOTE: `System.Console` must be written out in full in this file. `using Console.Lib.Inspector` makes the
// bare name `Console` resolve to the NAMESPACE, which is the same collision Chess.Lib.File has with
// System.IO.File.

var group = IPAddress.Parse(GetOption("--group", "CONSOLE_INSPECTOR_GROUP") ?? "239.255.77.91");
var port = int.TryParse(GetOption("--port", "CONSOLE_INSPECTOR_PORT"), out var p) ? p : 47892;

// Headless protocol self-test, no MCP involved: discover, ping, read the screen, print, exit. This is what
// to run when the question is "does the bridge work", separately from "does the agent call it correctly".
if (Array.Exists(args, a => a == "selftest"))
{
    var discovery = new InspectorDiscoveryClient(group, port);
    var socket = new InspectorSocketClient();

    System.Console.Error.WriteLine($"discovering on {group}:{port} ...");
    var found = await discovery.DiscoverAsync();
    if (found.Count == 0)
    {
        System.Console.Error.WriteLine("FAIL: no instances found");
        return 1;
    }

    foreach (var i in found)
    {
        System.Console.Error.WriteLine($"  pid={i.Pid} app={i.App} kind={i.Kind} {i.Address}:{i.TcpPort}");
    }

    var target = found[0];
    System.Console.Error.WriteLine($"ping    -> {(await socket.SendAsync(target, "ping", null)).GetRawText()}");
    System.Console.Error.WriteLine($"size    -> {(await socket.SendAsync(target, "size", null)).GetRawText()}");
    System.Console.Error.WriteLine($"state   -> {(await socket.SendAsync(target, "appState", null)).GetRawText()}");

    var screen = await socket.SendAsync(target, "screen", null);
    if (screen.TryGetProperty("rows", out var rows))
    {
        var n = 0;
        foreach (var r in rows.EnumerateArray())
        {
            var text = r.GetString() ?? "";
            if (text.Trim().Length > 0) System.Console.Error.WriteLine($"  {n,3}|{text}|");
            n++;
        }
    }

    System.Console.Error.WriteLine("OK");
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new InspectorDiscoveryClient(group, port));
builder.Services.AddSingleton<InspectorSocketClient>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInstructions = """
            Console.Lib live TUI inspector — discover and drive running Debug-build terminal apps.

            The target app must be a DEBUG build with the inspector attached; it is compiled out of Release
            entirely. For chess: CHESS_INSPECTOR=1 with `dotnet run --project Chess.Console -c Debug`.

            Start with list_instances. Then:
              - screen / row / cell  read the terminal as TEXT. Assert on words, not pixels.
              - app_state            what the app thinks is happening. Usually the fastest answer.
              - input_log            what input actually arrived and what it changed. Best for input bugs.
              - key / keys / click / drag   drive it.
              - press / move / release      a drag ONE event at a time, so you can look
                                            at the app between them. `drag` cannot show you
                                            anything mid-gesture: it arrives all at once and
                                            an app that coalesces motion renders none of it.

            Two things worth knowing:
              - screen shows CELLS only. A Sixel image (a chess board) occupies cells that read blank, and
                cell reports kind=Image there. That is correct.
              - a chess move is FOUR keys: file letter, rank digit, twice. e2e4 = e, 2, e, 4.
            """;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;

static string? GetOption(string flag, string envVar)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == flag) return args[i + 1];
    }
    return Environment.GetEnvironmentVariable(envVar);
}
