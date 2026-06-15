using ClaudeCodeVs.Spike;

// ---------------------------------------------------------------------------
// Phase 0 protocol spike entry point. Three modes:
//
//   dotnet run --project spike                 interactive: serve + press a/r to accept/reject diffs
//   dotnet run --project spike -- --self-test  automated: spin up server + run the protocol client
//   dotnet run --project spike -- --probe-cli  launch the real `claude -p` and watch the handshake
//
// Flags: --auto-accept-diffs (openDiff resolves itself), --workspace <path>.
// ---------------------------------------------------------------------------

bool selfTest = args.Contains("--self-test");
bool probeCli = args.Contains("--probe-cli");
bool autoAccept = args.Contains("--auto-accept-diffs") || selfTest || probeCli;
string workspace = GetOption(args, "--workspace") ?? Directory.GetCurrentDirectory();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Lockfile lifecycle, step 1: reap stale (dead-PID) lockfiles before we add ours. (task 0.7)
Lockfile.ReapStale();

// Pick a free port + write our lockfile. (tasks 0.2)
var lockfile = Lockfile.CreateForFreePort(new[] { workspace });

// Delete the lockfile no matter how we exit (clean exit, Ctrl+C, or process kill backstop).
AppDomain.CurrentDomain.ProcessExit += (_, _) => lockfile.Delete();

// Wire the tool registry + MCP dispatcher + WS server. (tasks 0.1, 0.3, 0.4, 0.5)
var decisions = new DiffDecisions();
var tools = new ToolRegistry(new IIdeTool[]
{
    new OpenFileTool(),
    new OpenDiffTool(decisions, autoAccept),
    new GetCurrentSelectionTool(),
    new GetDiagnosticsTool(),
});
var mcp = new McpServer(tools);
var server = new IdeWebSocketServer(lockfile.Port, lockfile.AuthToken, mcp);

var serverTask = Task.Run(() => server.RunAsync(cts.Token));

int exitCode = 0;
try
{
    if (selfTest)
    {
        await WaitUntilListeningAsync(cts.Token);
        bool ok = await TestClient.RunAsync(lockfile.Path, cts.Token);
        exitCode = ok ? 0 : 1;
        cts.Cancel();
    }
    else if (probeCli)
    {
        await WaitUntilListeningAsync(cts.Token);
        await RunCliProbeAsync(lockfile.Port, workspace, cts.Token);
        cts.Cancel();
    }
    else
    {
        RunInteractive(decisions, lockfile.Port, cts);
        await serverTask; // run until Ctrl+C
    }
}
catch (OperationCanceledException) { /* normal shutdown */ }
finally
{
    cts.Cancel();
    try { await serverTask; } catch { /* swallow shutdown noise */ }
    lockfile.Delete(); // lifecycle step 2: explicit delete on exit (task 0.7)
}

return exitCode;

// ---------------------------------------------------------------------------

// Block until the WS server has actually bound the port (Start() is sync, but RunAsync is on
// another task - a brief poll avoids a connect race in the automated modes).
async Task WaitUntilListeningAsync(CancellationToken ct)
{
    for (int i = 0; i < 50 && !ct.IsCancellationRequested; i++)
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            await probe.ConnectAsync("127.0.0.1", lockfile.Port, ct);
            return;
        }
        catch
        {
            await Task.Delay(50, ct);
        }
    }
}

void RunInteractive(DiffDecisions decisions, int port, CancellationTokenSource cts)
{
    Log.Info("");
    Log.Info("Interactive mode. The server is up and the lockfile is written.");
    Log.Info($"To connect the real CLI in another terminal:");
    Log.Info($"    {ClaudeLauncher.ManualRunHint(port)}");
    Log.Info("When Claude proposes an edit, an openDiff will appear here.");
    Log.Info("Keys:  [a] accept diff   [r] reject diff   [q] quit");
    Log.Info("");

    if (Console.IsInputRedirected)
    {
        Log.Warn("stdin is redirected; key controls disabled. Use --auto-accept-diffs or Ctrl+C.");
        return;
    }

    _ = Task.Run(() =>
    {
        while (!cts.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true).KeyChar;
            switch (char.ToLowerInvariant(key))
            {
                case 'a':
                    Log.Info(decisions.ResolveOldest(true) ? "accepted pending diff" : "no pending diff");
                    break;
                case 'r':
                    Log.Info(decisions.ResolveOldest(false) ? "rejected pending diff" : "no pending diff");
                    break;
                case 'q':
                    cts.Cancel();
                    return;
            }
        }
    });
}

async Task RunCliProbeAsync(int port, string workingDir, CancellationToken ct)
{
    Log.Info("=== CLI probe: launching the real claude headless and watching for the handshake ===");
    var proc = ClaudeLauncher.StartHeadlessProbe(
        port,
        prompt: "Reply with the single word: connected. Do not use any tools.",
        workingDir);

    if (proc is null) return;

    // Give the CLI time to start, connect, and run the handshake; then tear down.
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(40));
    try
    {
        await proc.WaitForExitAsync(timeout.Token);
        Log.Info($"claude exited with code {proc.ExitCode}");
    }
    catch (OperationCanceledException)
    {
        Log.Warn("CLI probe timed out; killing claude");
        try { proc.Kill(entireProcessTree: true); } catch { }
    }
}

static string? GetOption(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
