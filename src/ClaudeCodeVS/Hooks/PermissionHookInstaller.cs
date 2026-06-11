using System;
using System.IO;
using System.Linq;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Hooks;

/// <summary>
/// Installs the single-gate PreToolUse hook into a workspace's .claude/ folder: writes the embedded
/// hook script and merges a PreToolUse entry into .claude/settings.json (preserving everything else;
/// idempotent). Called from the Launch command. Best-effort — never throws into the launch path.
/// </summary>
internal static class PermissionHookInstaller
{
    private const string ScriptFileName = "vs-permission-hook.ps1";
    private const string HookCommand = "powershell -NoProfile -ExecutionPolicy Bypass -File .claude/vs-permission-hook.ps1";
    private const int HookTimeoutSeconds = 86400; // 24h, so the diff can wait for an unattended user

    public static void EnsureInstalled(string workspaceRoot)
    {
        try
        {
            var claudeDir = Path.Combine(workspaceRoot, ".claude");
            Directory.CreateDirectory(claudeDir);

            // 1) (Over)write the hook script from the embedded copy, so updates ship with the extension.
            File.WriteAllText(Path.Combine(claudeDir, ScriptFileName), ReadEmbeddedScript());

            // 2) Merge a PreToolUse hook into .claude/settings.json, preserving any existing content.
            var settingsPath = Path.Combine(claudeDir, "settings.json");
            JObject root;
            if (File.Exists(settingsPath))
            {
                try { root = JObject.Parse(File.ReadAllText(settingsPath)); }
                catch (Exception e)
                {
                    Log.Warn($"single-gate: couldn't parse {settingsPath}; leaving it alone ({e.Message})");
                    return;
                }
            }
            else
            {
                root = new JObject();
            }

            var hooks = root["hooks"] as JObject ?? new JObject();
            root["hooks"] = hooks;
            var pre = hooks["PreToolUse"] as JArray ?? new JArray();
            hooks["PreToolUse"] = pre;

            if (AlreadyInstalled(pre)) return;

            pre.Add(new JObject
            {
                ["matcher"] = "Edit|Write|MultiEdit",
                ["hooks"] = new JArray(new JObject
                {
                    ["type"] = "command",
                    ["command"] = HookCommand,
                    ["timeout"] = HookTimeoutSeconds,
                }),
            });

            File.WriteAllText(settingsPath, root.ToString(Formatting.Indented));
            Log.Info($"single-gate: installed PreToolUse hook in {settingsPath}");
        }
        catch (Exception e)
        {
            Log.Warn($"single-gate hook install failed: {e.Message}");
        }
    }

    private static bool AlreadyInstalled(JArray preToolUse)
    {
        foreach (var entry in preToolUse.OfType<JObject>())
        {
            if (entry["hooks"] is not JArray hs) continue;
            foreach (var h in hs.OfType<JObject>())
            {
                var cmd = (string?)h["command"];
                if (cmd != null && cmd.IndexOf(ScriptFileName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        return false;
    }

    private static string ReadEmbeddedScript()
    {
        var asm = typeof(PermissionHookInstaller).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ScriptFileName, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            throw new InvalidOperationException("embedded permission hook script not found");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
