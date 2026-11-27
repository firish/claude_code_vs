using System;
using System.IO;
using ClaudeCodeVs.Hooks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tests;

/// <summary>The installer's statusLine handling: wrap the user's own status line, keep it working, give it back.</summary>
[TestClass]
public class StatusLineInstallTests
{
    private TempDir _workspace = null!;
    private TempDir _userConfig = null!;
    private string? _savedConfigDir;
    private string Claude => Path.Combine(_workspace.Path, ".claude");
    private string Local => Path.Combine(Claude, "settings.local.json");
    private string Inner => Path.Combine(Claude, "vs-statusline-inner.json");

    [TestInitialize]
    public void Setup()
    {
        _workspace = new TempDir();
        _userConfig = new TempDir();
        Directory.CreateDirectory(Claude);
        // The user-level settings come from CLAUDE_CONFIG_DIR - never the real ~/.claude in a test.
        _savedConfigDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _userConfig.Path);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _savedConfigDir);
        _workspace.Dispose();
        _userConfig.Dispose();
    }

    private JObject LocalJson() => JObject.Parse(File.ReadAllText(Local));
    private static bool IsOurs(JToken? statusLine) => ((string?)statusLine?["command"] ?? "").Contains("vs-statusline.ps1");

    [TestMethod]
    public void ProjectStatusLine_IsWrapped_WithItsPadding()
    {
        File.WriteAllText(Path.Combine(Claude, "settings.json"), "{ \"statusLine\": { \"type\": \"command\", \"command\": \"echo project\", \"padding\": 2 } }");

        PermissionHookInstaller.EnsureInstalled(_workspace.Path);

        var statusLine = LocalJson()["statusLine"];
        Assert.IsTrue(IsOurs(statusLine));
        Assert.AreEqual(2, (int)statusLine!["padding"]!);
        var inner = JObject.Parse(File.ReadAllText(Inner));
        Assert.AreEqual("echo project", (string?)inner["command"]);
        Assert.AreEqual("project", (string?)inner["source"]);
        Assert.IsTrue(File.Exists(Path.Combine(Claude, "vs-statusline.ps1")));
    }

    [TestMethod]
    public void UserStatusLine_IsUsed_WhenTheProjectHasNone()
    {
        File.WriteAllText(Path.Combine(_userConfig.Path, "settings.json"), "{ \"statusLine\": { \"type\": \"command\", \"command\": \"echo user\" } }");

        PermissionHookInstaller.EnsureInstalled(_workspace.Path);

        Assert.AreEqual("user", (string?)JObject.Parse(File.ReadAllText(Inner))["source"]);
    }

    [TestMethod]
    public void SecondInstall_ChangesNothing()
    {
        PermissionHookInstaller.EnsureInstalled(_workspace.Path);
        var before = File.ReadAllText(Local);

        PermissionHookInstaller.EnsureInstalled(_workspace.Path);

        Assert.AreEqual(before, File.ReadAllText(Local));
        Assert.IsFalse(File.Exists(Inner)); // no status line of the user's to pass through
    }

    [TestMethod]
    public void LocalStatusLine_IsRemembered_AndRestoredWhenTurnedOff()
    {
        File.WriteAllText(Local, "{ \"statusLine\": { \"type\": \"command\", \"command\": \"echo mine\", \"padding\": 1 } }");

        PermissionHookInstaller.EnsureInstalled(_workspace.Path);
        Assert.IsTrue(IsOurs(LocalJson()["statusLine"]));
        Assert.AreEqual("local", (string?)JObject.Parse(File.ReadAllText(Inner))["source"]);

        PermissionHookInstaller.EnsureInstalled(_workspace.Path, usageStatusLine: false);

        var restored = LocalJson()["statusLine"]!;
        Assert.AreEqual("echo mine", (string?)restored["command"]);
        Assert.AreEqual(1, (int)restored["padding"]!);
        Assert.IsFalse(File.Exists(Inner));
    }

    [TestMethod]
    public void TurnedOff_WithNoLocalOriginal_RemovesTheKey()
    {
        PermissionHookInstaller.EnsureInstalled(_workspace.Path);
        Assert.IsTrue(IsOurs(LocalJson()["statusLine"]));

        PermissionHookInstaller.EnsureInstalled(_workspace.Path, usageStatusLine: false);

        Assert.IsNull(LocalJson()["statusLine"]);
        Assert.IsNotNull(LocalJson()["hooks"]); // the hooks themselves stay
    }
}
