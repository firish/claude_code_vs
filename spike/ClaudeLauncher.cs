using System.Diagnostics;

namespace ClaudeCodeVs.Spike;

/// <summary>
/// Spawns the real `claude` CLI with the env vars that make it auto-connect to our WS server.
/// The two env vars are the entire handoff (build-plan.md §3, §7 task 0.6):
///   CLAUDE_CODE_SSE_PORT=&lt;port&gt;   ENABLE_IDE_INTEGRATION=true
/// </summary>
internal static class ClaudeLauncher
{
    public const string PortEnv = "CLAUDE_CODE_SSE_PORT";
    public const string EnableEnv = "ENABLE_IDE_INTEGRATION";

    /// <summary>Locate the CLI: PATH first, then the known install locations on this box.</summary>
    public static string? FindClaude()
    {
        var candidates = new List<string>();

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir, "claude"));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(home, ".local", "bin", "claude"));
        candidates.Add("/opt/homebrew/bin/claude");
        candidates.Add("/usr/local/bin/claude");

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>A copy/paste line so the user can run an interactive session against this server.</summary>
    public static string ManualRunHint(int port)
        => $"{EnableEnv}=true {PortEnv}={port} claude    # then type:  /ide";

    /// <summary>
    /// Headless probe: run `claude -p "<prompt>"` with IDE integration enabled and output captured.
    /// Lets us observe the real handshake (initialize / tools/list) without an interactive TTY.
    /// Returns null if the CLI can't be found.
    /// </summary>
    public static Process? StartHeadlessProbe(int port, string prompt, string workingDir)
    {
        var claude = FindClaude();
        if (claude is null)
        {
            Log.Warn("claude CLI not found; skipping headless probe");
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = claude,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);

        psi.Environment[EnableEnv] = "true";
        psi.Environment[PortEnv] = port.ToString();

        Log.Info($"launching headless probe: {claude} -p \"{prompt}\"  ({EnableEnv}=true {PortEnv}={port})");
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Event($"[claude stdout] {e.Data}"); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log.Event($"[claude stderr] {e.Data}"); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }
}
