using System;
using System.IO;
using System.Linq;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Hooks;

/// <summary>
/// Installs the extension's hooks into a workspace's .claude/ folder: writes the embedded hook scripts
/// and merges entries into .claude/settings.local.json (preserving everything else; idempotent). Called from
/// the Launch command. Best-effort - never throws into the launch path.
/// - PreToolUse (Edit|Write|MultiEdit) -> the single-gate permission hook (our diff is the edit gate).
/// - Stop -> the usage hook (reports the transcript path so the panel can show token/cost stats).
/// - UserPromptSubmit -> the debug-context hook (injects live break state so Claude debugs smarter).
/// - Notification -> the notify hook (raises an in-IDE notification when Claude needs the user).
/// </summary>
internal static class PermissionHookInstaller
{
    private const string PermissionScript = "vs-permission-hook.ps1";
    private const string UsageScript = "vs-usage-hook.ps1";
    private const string DebugScript = "vs-debug-context-hook.ps1";
    private const string NotifyScript = "vs-notify-hook.ps1";
    private const int PermissionTimeoutSeconds = 86400; // 24h, so the diff can wait for an unattended user
    private const int UsageTimeoutSeconds = 10;
    private const int DebugTimeoutSeconds = 10;
    private const int NotifyTimeoutSeconds = 10;

    /// <summary>
    /// The registered hook command. ⚠️ The CLI executes hook commands through a POSIX shell (bash) even
    /// on Windows, and bash expands $-tokens inside double quotes BEFORE PowerShell runs - a first
    /// attempt using PowerShell variables ($s, $env:...) arrived at PowerShell with every $ eaten and
    /// parse-errored on each prompt (issue #17 live test). So the one-liner contains NO PowerShell
    /// $-syntax at all; the single $CLAUDE_PROJECT_DIR token is there FOR bash, which expands it to the
    /// workspace root (the CLI sets it for hooks - its own documented pattern). Resolution order:
    /// (1) cwd-relative .claude/&lt;script&gt; (CLI at the workspace root; also the working path if a shell
    ///     change ever stops expanding $CLAUDE_PROJECT_DIR - it then stays a literal that fails Test-Path);
    /// (2) the bash-expanded absolute path - fixes sessions started in a SUBFOLDER of the workspace;
    /// (3) only a genuinely absent script no-ops (exit 0), and install-on-connect re-materializes those.
    /// Stdin, stdout, and exit codes flow through unchanged when the script is found.
    /// </summary>
    private static string Command(string script) =>
        "powershell -NoProfile -ExecutionPolicy Bypass -Command "
        + $"\"if (Test-Path '.claude/{script}') {{ & '.claude/{script}' }} elseif (Test-Path '$CLAUDE_PROJECT_DIR/.claude/{script}') {{ & '$CLAUDE_PROJECT_DIR/.claude/{script}' }} else {{ exit 0 }}\"";

    public static void EnsureInstalled(string workspaceRoot)
    {
        try
        {
            var claudeDir = Path.Combine(workspaceRoot, ".claude");
            Directory.CreateDirectory(claudeDir);

            // 1) (Over)write the hook scripts from the embedded copies, so updates ship with the
            //    extension - unless the user took ownership by removing the marker line (issue #17).
            ManagedScripts.WriteIfManaged(Path.Combine(claudeDir, PermissionScript), ReadEmbeddedScript(PermissionScript));
            ManagedScripts.WriteIfManaged(Path.Combine(claudeDir, UsageScript), ReadEmbeddedScript(UsageScript));
            ManagedScripts.WriteIfManaged(Path.Combine(claudeDir, DebugScript), ReadEmbeddedScript(DebugScript));
            ManagedScripts.WriteIfManaged(Path.Combine(claudeDir, NotifyScript), ReadEmbeddedScript(NotifyScript));

            // Older versions wrote into the shared settings.json; our hooks are per-machine, so move them out.
            RemoveLegacyHooks(Path.Combine(claudeDir, "settings.json"));

            // 2) Merge hook entries into .claude/settings.local.json (personal, not committed), preserving any existing content.
            var settingsPath = Path.Combine(claudeDir, "settings.local.json");
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

            // Only assign when creating: re-assigning an already-parented JToken makes Json.NET CLONE it
            // into the parent, detaching this local reference - every later mutation then lands on the
            // orphan and silently never reaches the file (bit us adding the Notification hook to
            // workspaces with an existing settings.json).
            if (root["hooks"] is not JObject hooks)
            {
                hooks = new JObject();
                root["hooks"] = hooks;
            }

            bool addedPre = EnsureHook(hooks, "PreToolUse", "Edit|Write|MultiEdit", PermissionScript, PermissionTimeoutSeconds);
            bool addedStop = EnsureHook(hooks, "Stop", matcher: null, UsageScript, UsageTimeoutSeconds);
            bool addedDebug = EnsureHook(hooks, "UserPromptSubmit", matcher: null, DebugScript, DebugTimeoutSeconds);
            bool addedNotify = EnsureHook(hooks, "Notification", matcher: null, NotifyScript, NotifyTimeoutSeconds);

            if (!addedPre && !addedStop && !addedDebug && !addedNotify)
            {
                Log.Info("hooks: PreToolUse + Stop + UserPromptSubmit + Notification already present; nothing to change");
                return;
            }

            File.WriteAllText(settingsPath, root.ToString(Formatting.Indented));
            Log.Info($"hooks: updated {settingsPath} (PreToolUse {(addedPre ? "added/updated" : "present")}, Stop {(addedStop ? "added/updated" : "present")}, UserPromptSubmit {(addedDebug ? "added/updated" : "present")}, Notification {(addedNotify ? "added/updated" : "present")})");
        }
        catch (Exception e)
        {
            Log.Warn($"hook install failed: {e.Message}");
        }
    }

    /// <summary>Strips our hook entries (matched by script name) from the legacy shared settings.json,
    /// pruning emptied containers; leaves the file untouched if nothing of ours is there.</summary>
    private static void RemoveLegacyHooks(string legacyPath)
    {
        try
        {
            if (!File.Exists(legacyPath)) return;
            JObject root;
            try { root = JObject.Parse(File.ReadAllText(legacyPath), new JsonLoadSettings { CommentHandling = CommentHandling.Load }); }
            catch { return; }
            if (root["hooks"] is not JObject hooks) return;

            // Rewriting would silently drop the user's comments (issue #49) - leave such a file alone.
            if (root.DescendantsAndSelf().Any(t => t.Type == JTokenType.Comment))
            {
                Log.Warn($"hooks: {legacyPath} contains comments; not rewriting it, remove our old hook entries from it manually");
                return;
            }

            var scripts = new[] { PermissionScript, UsageScript, DebugScript, NotifyScript };
            bool changed = false;
            foreach (var prop in hooks.Properties().ToList())
            {
                if (prop.Value is not JArray arr) continue;
                foreach (var entry in arr.OfType<JObject>().ToList())
                {
                    if (entry["hooks"] is not JArray hs) continue;
                    foreach (var h in hs.OfType<JObject>().ToList())
                    {
                        var cmd = (string?)h["command"];
                        if (cmd != null && scripts.Any(s => cmd.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            h.Remove();
                            changed = true;
                        }
                    }
                    if (hs.Count == 0) entry.Remove();
                }
                if (arr.Count == 0) prop.Remove();
            }
            if (!changed) return;
            if (hooks.Count == 0) root.Remove("hooks");

            if (root.Count == 0) File.Delete(legacyPath);
            else File.WriteAllText(legacyPath, root.ToString(Formatting.Indented));
            Log.Info($"hooks: migrated our entries out of {legacyPath}");
        }
        catch (Exception e)
        {
            Log.Warn($"hooks: legacy settings.json cleanup failed: {e.Message}");
        }
    }

    /// <summary>
    /// Add a hook for <paramref name="eventName"/> pointing at <paramref name="script"/>, or MIGRATE an
    /// existing entry whose command drifted from the current form (e.g. the pre-1.14.4 unguarded
    /// "-File .claude/x.ps1" shape). Returns true if it changed settings.
    /// </summary>
    private static bool EnsureHook(JObject hooks, string eventName, string? matcher, string script, int timeoutSeconds)
    {
        // Same clone-on-reparent hazard as above: keep the existing array in place, only assign a new one.
        if (hooks[eventName] is not JArray arr)
        {
            arr = new JArray();
            hooks[eventName] = arr;
        }

        var existing = FindOurHook(arr, script);
        if (existing != null)
        {
            var want = Command(script);
            if (string.Equals((string?)existing["command"], want, StringComparison.Ordinal)) return false;
            // Migrate IN PLACE: setting a value property on the EXISTING object is safe - it's
            // re-parenting a token into a new parent that clones (the 1.11.0 lesson), not this.
            existing["command"] = want;
            return true;
        }

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

    /// <summary>The inner hook object whose command references our script, or null. Match is by script
    /// name so every historical command shape (old -File form, new guarded form) is found and owned.</summary>
    private static JObject? FindOurHook(JArray eventHooks, string script)
    {
        foreach (var entry in eventHooks.OfType<JObject>())
        {
            if (entry["hooks"] is not JArray hs) continue;
            foreach (var h in hs.OfType<JObject>())
            {
                var cmd = (string?)h["command"];
                if (cmd != null && cmd.IndexOf(script, StringComparison.OrdinalIgnoreCase) >= 0)
                    return h;
            }
        }
        return null;
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
