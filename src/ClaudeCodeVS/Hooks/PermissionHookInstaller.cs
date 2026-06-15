using System;
using System.IO;
using System.Linq;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Hooks;

/// <summary>
/// Installs the extension's hooks into a workspace's .claude/ folder: writes the embedded hook scripts
/// and merges entries into .claude/settings.json (preserving everything else; idempotent). Called from
/// the Launch command. Best-effort - never throws into the launch path.
/// - PreToolUse (Edit|Write|MultiEdit) -> the single-gate permission hook (our diff is the edit gate).
/// - Stop -> the usage hook (reports the transcript path so the panel can show token/cost stats).
/// </summary>
internal static class PermissionHookInstaller
{
    private const string PermissionScript = "vs-permission-hook.ps1";
    private const string UsageScript = "vs-usage-hook.ps1";
    private const int PermissionTimeoutSeconds = 86400; // 24h, so the diff can wait for an unattended user
    private const int UsageTimeoutSeconds = 10;

    private static string Command(string script) =>
        $"powershell -NoProfile -ExecutionPolicy Bypass -File .claude/{script}";

    public static void EnsureInstalled(string workspaceRoot)
    {
        try
        {
            var claudeDir = Path.Combine(workspaceRoot, ".claude");
            Directory.CreateDirectory(claudeDir);

            // 1) (Over)write the hook scripts from the embedded copies, so updates ship with the extension.
            File.WriteAllText(Path.Combine(claudeDir, PermissionScript), ReadEmbeddedScript(PermissionScript));
            File.WriteAllText(Path.Combine(claudeDir, UsageScript), ReadEmbeddedScript(UsageScript));

            // 2) Merge hook entries into .claude/settings.json, preserving any existing content.
            var settingsPath = Path.Combine(claudeDir, "settings.json");
            JObject root;
            if (File.Exists(settingsPath))
            {
                try { root = JObject.Parse(File.ReadAllText(settingsPath)); }
                catch (Exception e)
                {
                    Log.Warn($"hooks: couldn't parse {settingsPath}; leaving it alone ({e.Message})");
                    return;
                }
            }
            else
            {
                root = new JObject();
            }

            var hooks = root["hooks"] as JObject ?? new JObject();
            root["hooks"] = hooks;

            bool addedPre = EnsureHook(hooks, "PreToolUse", "Edit|Write|MultiEdit", PermissionScript, PermissionTimeoutSeconds);
            bool addedStop = EnsureHook(hooks, "Stop", matcher: null, UsageScript, UsageTimeoutSeconds);

            if (!addedPre && !addedStop)
            {
                Log.Info("hooks: PreToolUse + Stop already present; nothing to change");
                return;
            }

            File.WriteAllText(settingsPath, root.ToString(Formatting.Indented));
            Log.Info($"hooks: updated {settingsPath} (PreToolUse {(addedPre ? "ADDED" : "present")}, Stop {(addedStop ? "ADDED" : "present")})");
        }
        catch (Exception e)
        {
            Log.Warn($"hook install failed: {e.Message}");
        }
    }

    /// <summary>Add a hook for <paramref name="eventName"/> pointing at <paramref name="script"/> if not already present. Returns true if it changed settings.</summary>
    private static bool EnsureHook(JObject hooks, string eventName, string? matcher, string script, int timeoutSeconds)
    {
        var arr = hooks[eventName] as JArray ?? new JArray();
        hooks[eventName] = arr;
        if (AlreadyInstalled(arr, script)) return false;

        var entry = new JObject
        {
            ["hooks"] = new JArray(new JObject
            {
                ["type"] = "command",
                ["command"] = Command(script),
                ["timeout"] = timeoutSeconds,
            }),
        };
        if (matcher != null)
            entry["matcher"] = matcher;
        arr.Add(entry);
        return true;
    }

    private static bool AlreadyInstalled(JArray eventHooks, string script)
    {
        foreach (var entry in eventHooks.OfType<JObject>())
        {
            if (entry["hooks"] is not JArray hs) continue;
            foreach (var h in hs.OfType<JObject>())
            {
                var cmd = (string?)h["command"];
                if (cmd != null && cmd.IndexOf(script, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        return false;
    }

    private static string ReadEmbeddedScript(string scriptFileName)
    {
        var asm = typeof(PermissionHookInstaller).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(scriptFileName, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            throw new InvalidOperationException($"embedded hook script not found: {scriptFileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
