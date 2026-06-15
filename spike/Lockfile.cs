using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeCodeVs.Spike;

/// <summary>
/// The lockfile is how the CLI discovers that "an IDE is here". It lives at
/// <c>~/.claude/ide/&lt;port&gt;.lock</c> - and the filename IS the port the WS server listens on.
/// See build-plan.md §3.
/// </summary>
internal sealed class Lockfile
{
    public int Port { get; }
    public string AuthToken { get; }
    public string Path { get; }

    private Lockfile(int port, string authToken, string path)
    {
        Port = port;
        AuthToken = authToken;
        Path = path;
    }

    private static string IdeDir =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "ide");

    /// <summary>
    /// Pick a free loopback port, then write the lockfile for it. We bind a throwaway
    /// TcpListener to port 0 (OS assigns a free port), read the assignment, and release it.
    /// There's a tiny TOCTOU window before the WS server grabs the port - fine for a spike.
    /// </summary>
    public static Lockfile CreateForFreePort(IReadOnlyList<string> workspaceFolders)
    {
        Directory.CreateDirectory(IdeDir);

        int port = PickFreePort();
        var token = Guid.NewGuid().ToString();
        var path = System.IO.Path.Combine(IdeDir, $"{port}.lock");

        var doc = new LockfileDoc
        {
            Pid = Environment.ProcessId,
            WorkspaceFolders = workspaceFolders.ToArray(),
            IdeName = "Visual Studio",
            Transport = "ws",
            // CLI uses this to pick `tasklist.exe` (Windows) vs `ps` for PID-liveness checks.
            // On this macOS dev box it must be false; the real VS extension sets it true.
            RunningInWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            AuthToken = token,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOpts));
        Log.Info($"wrote lockfile {path} (pid={doc.Pid}, token=<redacted>)");
        return new Lockfile(port, token, path);
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
                Log.Info($"deleted lockfile {Path}");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"could not delete lockfile {Path}: {e.Message}");
        }
    }

    /// <summary>
    /// On startup, remove lockfiles whose owning process is dead. A stale lockfile pointing at a
    /// dead WS server blocks reconnection (issue #5043). We ONLY delete dead-PID files - never
    /// another live IDE's (e.g. a running VS Code) lockfile.
    /// </summary>
    public static void ReapStale()
    {
        if (!Directory.Exists(IdeDir)) return;

        foreach (var file in Directory.EnumerateFiles(IdeDir, "*.lock"))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<LockfileDoc>(File.ReadAllText(file), JsonOpts);
                if (doc is null) continue;

                if (!IsProcessAlive(doc.Pid))
                {
                    File.Delete(file);
                    Log.Info($"reaped stale lockfile {System.IO.Path.GetFileName(file)} (dead pid {doc.Pid})");
                }
            }
            catch (Exception e)
            {
                Log.Warn($"skipping unreadable lockfile {System.IO.Path.GetFileName(file)}: {e.Message}");
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            // Throws ArgumentException if no such process. (On the real extension/Windows the CLI
            // itself does the liveness check via tasklist.exe; here we just self-clean our own dir.)
            using var _ = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int PickFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }
        finally
        {
            l.Stop();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    /// <summary>Exact on-disk schema. Property names are wire-critical - do not rename casually.</summary>
    private sealed class LockfileDoc
    {
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonPropertyName("workspaceFolders")] public string[] WorkspaceFolders { get; set; } = [];
        [JsonPropertyName("ideName")] public string IdeName { get; set; } = "Visual Studio";
        [JsonPropertyName("transport")] public string Transport { get; set; } = "ws";
        [JsonPropertyName("runningInWindows")] public bool RunningInWindows { get; set; }
        [JsonPropertyName("authToken")] public string AuthToken { get; set; } = "";
    }
}
