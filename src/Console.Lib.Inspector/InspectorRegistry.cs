using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Console.Lib.Inspector;

/// <summary>
/// Reads the per-user inspector registry: the folder of descriptor files a DEBUG app writes from DIR.Lib 11.21
/// on, where it used to answer multicast by default. A file needs no socket, so this works with no network at
/// all, and it finds an app that binds nothing but loopback (and so never raised a firewall prompt).
///
/// <para>The contract is DIR.Lib's <c>src/DIR.Lib/Diagnostics/inspector-registry.md</c>. This project references
/// nothing from DIR.Lib (the writer is <c>#if DEBUG</c> and never ships), so the rules are restated here, and
/// SdlVulkan.Renderer's sidecar implements the same ones:</para>
/// <list type="bullet">
/// <item>Only <c>&lt;pid&gt;-&lt;tcpPort&gt;.json</c> names are read. The writer's <c>.tmp</c> is never looked at.</item>
/// <item>A file that vanishes or cannot be opened (its app just exited, or a rewrite is mid-rename) is skipped,
/// as is one that does not parse or lacks <c>pid</c> or <c>tcpPort</c>. Nothing about one file fails the listing.</item>
/// <item>A file whose pid is not running is dropped (a crash skips the writer's delete), and so is one whose pid
/// IS running but started more than <see cref="StartTimeTolerance"/> away from the recorded
/// <c>processStart</c>: that pid was handed to a later process.</item>
/// <item>Nothing is ever deleted. The writers sweep; a reader that deleted could race a live app's rewrite.</item>
/// </list>
/// </summary>
internal static class InspectorRegistry
{
    /// <summary>
    /// How far the recorded and the observed start of a pid may differ and still be the same process. The
    /// writer's own value: generous, because what it guards against (a reused pid) is separated from the
    /// original by the dead app's whole lifetime, while Linux derives a start time from the boot time, which
    /// moves if the clock is stepped.
    /// </summary>
    internal static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <c>DIR.Lib/inspector</c> under <see cref="Environment.SpecialFolder.LocalApplicationData"/>, computed the way
    /// the writer computes it rather than spelled out per OS. Null where the OS reports no per-user data folder,
    /// in which case nothing can have registered.
    /// </summary>
    public static string? DefaultDirectory
        => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } root
            ? Path.Combine(root, "DIR.Lib", "inspector")
            : null;

    /// <summary>
    /// Every live instance registered in <paramref name="directory"/>, of every kind (the caller filters, as it
    /// does a multicast reply), each at <see cref="IPAddress.Loopback"/>. A missing folder is an empty registry.
    /// </summary>
    /// <param name="beforeOpen">Called with each candidate path just before it is opened. The seam a test uses to
    /// make a file vanish between the listing and the read, which is otherwise a race it cannot schedule.</param>
    public static IReadOnlyList<InspectorInstance> Read(string? directory, Action<string>? beforeOpen = null)
    {
        if (directory is null)
        {
            return [];
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // DirectoryNotFoundException is an IOException: no folder, nothing registered.
            return [];
        }

        var found = new List<InspectorInstance>();
        foreach (var file in files)
        {
            if (!IsDescriptorName(Path.GetFileName(file)))
            {
                continue;
            }

            beforeOpen?.Invoke(file);
            if (TryReadAllBytes(file) is { } bytes
                && TryParse(bytes, out var instance, out var processStartUtc)
                && IsLive(instance.Pid, processStartUtc))
            {
                found.Add(instance);
            }
        }
        return found;
    }

    /// <summary>
    /// Whether a file name has the descriptor shape, <c>&lt;pid&gt;-&lt;tcpPort&gt;.json</c> with both decimal.
    /// Anything else in the folder belongs to someone else, or to a later format.
    /// </summary>
    internal static bool IsDescriptorName(string name)
    {
        const string extension = ".json";
        if (!name.EndsWith(extension, StringComparison.Ordinal))
        {
            return false;
        }

        var stem = name.AsSpan(0, name.Length - extension.Length);
        var dash = stem.IndexOf('-');
        return dash > 0
            && int.TryParse(stem[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && int.TryParse(stem[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// The file's bytes, or null if it cannot be read now. Opened sharing <see cref="FileShare.Delete"/> so the
    /// app's exit can delete the file under us (<c>File.ReadAllBytes</c> shares only <c>Read</c>, which would
    /// block that), and closed at once: on Windows any open handle blocks the writer's rename, whatever it shares.
    /// </summary>
    private static byte[]? TryReadAllBytes(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // FileNotFoundException (the app exited between the listing and here) is an IOException, and so is a
            // sharing violation. UnauthorizedAccessException is a file Windows has marked for deletion.
            return null;
        }
    }

    /// <summary>
    /// Parses one descriptor. False unless it is a JSON object with a usable <c>pid</c> and <c>tcpPort</c>; the
    /// other members default as a multicast reply's do. <paramref name="processStartUtc"/> is null when the
    /// member is absent or unreadable, which leaves the pid alone to decide liveness, as the writer's sweep does.
    /// </summary>
    internal static bool TryParse(
        byte[] json, [NotNullWhen(true)] out InspectorInstance? instance, out DateTime? processStartUtc)
    {
        instance = null;
        processStartUtc = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Int(root, "pid") is not { } pid || pid <= 0
                || Int(root, "tcpPort") is not { } tcpPort || tcpPort is <= 0 or > IPEndPoint.MaxPort)
            {
                return false;
            }

            instance = new InspectorInstance(
                IPAddress.Loopback,
                tcpPort,
                Str(root, "app") ?? "?",
                Str(root, "kind") ?? "unknown",
                pid,
                Int(root, "proto") ?? 0);
            processStartUtc = root.TryGetProperty("processStart", out var s)
                && s.ValueKind == JsonValueKind.String
                && s.TryGetDateTimeOffset(out var started)
                    ? started.UtcDateTime
                    : null;
            return true;
        }
        catch (JsonException)
        {
            // Half-written, or not JSON at all. The writer renames a finished temp file into place, so this is
            // a file someone else put here, but it is skipped rather than failing the listing either way.
            return false;
        }
    }

    /// <summary>
    /// The liveness rule: the pid is running and, when the descriptor recorded a start time, it is that same
    /// process rather than a later one given the same pid. When the start time cannot be read (another user's
    /// or an elevated process: access denied) the pid alone decides.
    /// </summary>
    internal static bool IsLive(int pid, DateTime? processStartUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }
            if (processStartUtc is not { } recorded)
            {
                return true;
            }
            return (process.StartTime.ToUniversalTime() - recorded).Duration() <= StartTimeTolerance;
        }
        catch (ArgumentException) { return false; }          // no process with that pid
        catch (InvalidOperationException) { return false; }  // it exited while being asked
        catch (Exception) { return true; }                    // cannot tell (access denied): keep it
    }

    private static int? Int(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v)
            ? v
            : null;

    private static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
