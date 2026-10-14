using System.Diagnostics;
using System.Globalization;
using System.Net;
using Console.Lib.Inspector;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// The sidecar's reader of DIR.Lib's per-user inspector registry, against descriptor files written in the
/// format <c>inspector-registry.md</c> specifies, in a temporary folder of each test's own. Never the real
/// per-user folder: what is registered there depends on what happens to be running on the machine.
///
/// <para>The live process a fixture can name is this test process itself, so a "live" descriptor carries our
/// own pid and start time, a reused pid is our pid with a different start time, and a dead pid is one with no
/// process at all.</para>
/// </summary>
public sealed class InspectorRegistryTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "console-lib-inspector-registry-tests", Guid.NewGuid().ToString("N"));

    private static readonly DateTime OurStartUtc = ReadOurStartUtc();

    public InspectorRegistryTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* best-effort; it is under the temp folder */ }
    }

    // -----------------------------------------------------------------------
    // Reading one folder
    // -----------------------------------------------------------------------

    [Fact]
    public void Read_LiveEntry_IsListedAtLoopbackWithItsPort()
    {
        Register(Environment.ProcessId, 53817, kind: "tui", app: "Chess", processStartUtc: OurStartUtc,
            extras: ",\"title\":\"Chess - game 3\"");

        var found = InspectorRegistry.Read(_folder);

        var instance = found.ShouldHaveSingleItem();
        instance.Address.ShouldBe(IPAddress.Loopback);
        instance.Address.ToString().ShouldBe("127.0.0.1");
        instance.TcpPort.ShouldBe(53817);
        instance.Pid.ShouldBe(Environment.ProcessId);
        instance.App.ShouldBe("Chess");
        instance.Kind.ShouldBe("tui");
        instance.Proto.ShouldBe(1);
    }

    /// <summary>A crash skips the writer's delete. The file stays (readers never delete) but is not listed.</summary>
    [Fact]
    public void Read_DeadPid_IsDroppedAndNotDeleted()
    {
        var pid = UnusedPid();
        var path = Register(pid, 50001, kind: "tui", processStartUtc: OurStartUtc);

        InspectorRegistry.Read(_folder).ShouldBeEmpty();

        File.Exists(path).ShouldBeTrue("a reader must never delete; the writers sweep");
    }

    /// <summary>
    /// The pid is running, but it is not the process that registered: it started an hour away from the recorded
    /// start. That is what a crashed app's pid looks like once the OS hands it to something else.
    /// </summary>
    [Fact]
    public void Read_ReusedPid_IsDroppedAndNotDeleted()
    {
        var path = Register(Environment.ProcessId, 50002, kind: "tui", processStartUtc: OurStartUtc.AddHours(-1));

        InspectorRegistry.Read(_folder).ShouldBeEmpty();

        File.Exists(path).ShouldBeTrue("a reader must never delete; the writers sweep");
    }

    /// <summary>Pins the 2 s tolerance from both sides, so a rewrite cannot tighten it to exact equality.</summary>
    [Theory]
    [InlineData(1.0, true)]
    [InlineData(-1.0, true)]
    [InlineData(3.0, false)]
    [InlineData(-3.0, false)]
    public void Read_StartTimeWithinTwoSeconds_IsTheSameProcess(double offsetSeconds, bool listed)
    {
        Register(Environment.ProcessId, 50003, kind: "tui", processStartUtc: OurStartUtc.AddSeconds(offsetSeconds));

        InspectorRegistry.Read(_folder).Count.ShouldBe(listed ? 1 : 0);
    }

    /// <summary>The spec allows a descriptor without <c>processStart</c> (a platform that will not say); the pid decides.</summary>
    [Fact]
    public void Read_WithoutProcessStart_ThePidAloneDecides()
    {
        Register(Environment.ProcessId, 50004, kind: "tui", processStartUtc: null);
        Register(UnusedPid(), 50005, kind: "tui", processStartUtc: null);

        InspectorRegistry.Read(_folder).ShouldHaveSingleItem().TcpPort.ShouldBe(50004);
    }

    [Fact]
    public void Read_HalfWrittenOrMalformedFiles_AreSkippedWithoutFailingTheListing()
    {
        var pid = Environment.ProcessId;
        File.WriteAllText(Path.Combine(_folder, $"{pid}-50010.json"), "{\"app\":\"Half\",\"kind\":\"tui\",\"tcpPort\":500");
        File.WriteAllText(Path.Combine(_folder, $"{pid}-50011.json"), "");
        File.WriteAllText(Path.Combine(_folder, $"{pid}-50012.json"), "[1,2,3]");
        File.WriteAllText(Path.Combine(_folder, $"{pid}-50013.json"), $"{{\"app\":\"NoPort\",\"kind\":\"tui\",\"pid\":{pid}}}");
        File.WriteAllText(Path.Combine(_folder, $"{pid}-50014.json"), "{\"app\":\"NoPid\",\"kind\":\"tui\",\"tcpPort\":50014}");
        File.WriteAllText(Path.Combine(_folder, $"{pid}-50015.json"), $"{{\"tcpPort\":\"50015\",\"pid\":{pid}}}");
        Register(pid, 50016, kind: "tui", processStartUtc: OurStartUtc);

        InspectorRegistry.Read(_folder).ShouldHaveSingleItem().TcpPort.ShouldBe(50016);
    }

    /// <summary>
    /// An app that exits between the folder listing and the read deletes its file under the reader. That is a
    /// race no test can schedule, so the reader's seam deletes the file at exactly that moment.
    /// </summary>
    [Fact]
    public void Read_FileThatVanishesBeforeItIsRead_IsSkippedWithoutFailingTheListing()
    {
        var vanishing = Register(Environment.ProcessId, 50020, kind: "tui", processStartUtc: OurStartUtc);
        Register(Environment.ProcessId, 50021, kind: "tui", processStartUtc: OurStartUtc);

        var found = InspectorRegistry.Read(_folder, beforeOpen: path =>
        {
            if (path == vanishing) File.Delete(path);
        });

        found.ShouldHaveSingleItem().TcpPort.ShouldBe(50021);
    }

    /// <summary>The writer's in-progress temp file, and any name it did not produce, are never read.</summary>
    [Fact]
    public void Read_OnlyDescriptorNamesAreRead()
    {
        var pid = Environment.ProcessId;
        var valid = Descriptor(pid, 50030, "tui", "App", OurStartUtc, "");
        File.WriteAllText(Path.Combine(_folder, $"{pid}-50030.tmp"), valid);
        File.WriteAllText(Path.Combine(_folder, "notes.json"), valid);
        File.WriteAllText(Path.Combine(_folder, $"x{pid}-50030.json"), valid);
        File.WriteAllText(Path.Combine(_folder, $"{pid}-50030.json.bak"), valid);

        InspectorRegistry.Read(_folder).ShouldBeEmpty();
    }

    [Fact]
    public void Read_MissingFolderOrNoFolder_IsAnEmptyRegistry()
    {
        InspectorRegistry.Read(Path.Combine(_folder, "does-not-exist")).ShouldBeEmpty();
        InspectorRegistry.Read(null).ShouldBeEmpty();
    }

    [Fact]
    public void DefaultDirectory_IsDirLibInspectorUnderLocalApplicationData()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (root.Length == 0)
        {
            InspectorRegistry.DefaultDirectory.ShouldBeNull();
            return;
        }
        InspectorRegistry.DefaultDirectory.ShouldBe(Path.Combine(root, "DIR.Lib", "inspector"));
    }

    // -----------------------------------------------------------------------
    // Discovery: the registry, then multicast, merged
    // -----------------------------------------------------------------------

    /// <summary>
    /// The registry answers when the multicast query cannot even be sent. Port 0 makes the send fail (Windows
    /// rejects the address, Linux the port), standing in for a machine with no network; if a platform ever
    /// accepted it, nothing listens there and the query collects nothing, so the assertions hold either way.
    /// A non-terminal kind in the registry is filtered exactly as a multicast reply is.
    /// </summary>
    [Fact]
    public async Task Discover_ReadsTheRegistryWhenTheQueryFails_AndKeepsTerminalKindsOnly()
    {
        Register(Environment.ProcessId, 50040, kind: "tui", processStartUtc: OurStartUtc);

        // A second core in a GPU app sharing the folder. It has our pid too (the only live one a test owns), so
        // it is filtered by kind before the (address, pid) key is ever taken.
        Register(Environment.ProcessId, 50041, kind: "sdl", processStartUtc: OurStartUtc);

        var client = new InspectorDiscoveryClient(IPAddress.Parse("239.255.77.91"), 0) { RegistryDirectory = _folder };
        var found = await client.DiscoverAsync(TestContext.Current.CancellationToken);

        var instance = found.ShouldHaveSingleItem();
        instance.Kind.ShouldBe("tui");
        instance.Address.ShouldBe(IPAddress.Loopback);
        instance.TcpPort.ShouldBe(50040);
        instance.Pid.ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public async Task Discover_RegistryWithOnlyANonTerminalKind_FindsNothing()
    {
        Register(Environment.ProcessId, 50042, kind: "sdl", processStartUtc: OurStartUtc);

        var client = new InspectorDiscoveryClient(IPAddress.Parse("239.255.77.91"), 0) { RegistryDirectory = _folder };

        (await client.DiscoverAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    private static readonly IPAddress OurLanAddress = IPAddress.Parse("192.168.1.5");
    private static readonly IPAddress OtherMachine = IPAddress.Parse("192.168.1.77");

    private static bool IsThisMachine(IPAddress address) => IPAddress.IsLoopback(address) || address.Equals(OurLanAddress);

    /// <summary>
    /// An 11.21 app with LAN discovery on is in the registry AND answers the query, from whichever local adapter
    /// the query reached. That is one instance: the registry's entry is kept, the reply dropped.
    /// </summary>
    [Fact]
    public void Merge_TheSameLocalInstanceFoundBothWays_IsListedOnceFromTheRegistry()
    {
        var registered = new InspectorInstance(IPAddress.Loopback, 53817, "Chess", "tui", 4100, 1);
        var reply = new InspectorInstance(OurLanAddress, 53817, "Chess", "tui", 4100, 1);

        var merged = InspectorDiscoveryClient.Merge([registered], [reply], IsThisMachine);

        merged.ShouldHaveSingleItem().ShouldBeSameAs(registered);
    }

    /// <summary>
    /// The address is in the key because pids are per machine: an app on another machine with the same pid as a
    /// local one is a different instance. A repeated reply from it is still one instance.
    /// </summary>
    [Fact]
    public void Merge_AnotherMachineSharingAPid_IsKept_AndItsRepeatedReplyIsNot()
    {
        var registered = new InspectorInstance(IPAddress.Loopback, 53817, "Chess", "tui", 4100, 1);
        var remote = new InspectorInstance(OtherMachine, 61000, "Chess", "tui", 4100, 1);
        var remoteAgain = new InspectorInstance(OtherMachine, 61000, "Chess", "tui", 4100, 1);

        var merged = InspectorDiscoveryClient.Merge([registered], [remote, remoteAgain], IsThisMachine);

        merged.ShouldBe([registered, remote]);
    }

    /// <summary>
    /// A build older than 11.21 answers the query and never registers: kept, with the address it replied from.
    /// Kinds are filtered on the replies as on the registry; the legacy <c>console</c> kind still counts.
    /// </summary>
    [Fact]
    public void Merge_ReplyOnlyInstancesAndKinds()
    {
        var registered = new InspectorInstance(IPAddress.Loopback, 53817, "Chess", "tui", 4100, 1);
        var olderBuild = new InspectorInstance(OurLanAddress, 52000, "Old", "tui", 4200, 1);
        var console44 = new InspectorInstance(OurLanAddress, 52001, "Older", "console", 4300, 1);
        var gpuApp = new InspectorInstance(OurLanAddress, 52002, "Viewer", "sdl", 4400, 1);
        var unknown = new InspectorInstance(OtherMachine, 52003, "?", "unknown", 4500, 1);

        var merged = InspectorDiscoveryClient.Merge(
            [registered], [gpuApp, olderBuild, unknown, console44], IsThisMachine);

        merged.ShouldBe([registered, olderBuild, console44]);
        merged[1].Address.ShouldBe(OurLanAddress);
    }

    /// <summary>Two replies from different adapters of this machine are one local instance.</summary>
    [Fact]
    public void Merge_RepliesFromTwoLocalAdapters_AreOneInstance()
    {
        var viaLan = new InspectorInstance(OurLanAddress, 52000, "Old", "tui", 4200, 1);
        var viaLoopback = new InspectorInstance(IPAddress.Loopback, 52000, "Old", "tui", 4200, 1);

        InspectorDiscoveryClient.Merge([], [viaLan, viaLoopback], IsThisMachine)
            .ShouldHaveSingleItem().ShouldBeSameAs(viaLan);
    }

    // -----------------------------------------------------------------------
    // Fixtures
    // -----------------------------------------------------------------------

    /// <summary>
    /// Writes a descriptor the way the writer does: one line, UTF-8 without a BOM, named
    /// <c>&lt;pid&gt;-&lt;tcpPort&gt;.json</c>, <c>processStart</c> in ISO 8601 round-trip UTC.
    /// </summary>
    private string Register(int pid, int port, string kind, DateTime? processStartUtc, string app = "App", string extras = "")
    {
        var path = Path.Combine(_folder, $"{pid}-{port}.json");
        File.WriteAllText(path, Descriptor(pid, port, kind, app, processStartUtc, extras));
        return path;
    }

    private static string Descriptor(int pid, int port, string kind, string app, DateTime? processStartUtc, string extras)
    {
        var processStart = processStartUtc is { } s
            ? $",\"processStart\":\"{s.ToString("O", CultureInfo.InvariantCulture)}\""
            : "";
        return $"{{\"app\":\"{app}\",\"kind\":\"{kind}\",\"tcpPort\":{port},\"pid\":{pid},\"proto\":1{processStart}{extras}}}";
    }

    private static DateTime ReadOurStartUtc()
    {
        using var self = Process.GetCurrentProcess();
        return DateTime.SpecifyKind(self.StartTime.ToUniversalTime(), DateTimeKind.Utc);
    }

    /// <summary>A pid with no process behind it. Windows pids are multiples of 4, so the step keeps to real ones.</summary>
    private static int UnusedPid()
    {
        for (var pid = 4_000_000; pid > 0; pid -= 4)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return pid;
            }
        }
        throw new InvalidOperationException("every candidate pid is running");
    }
}
