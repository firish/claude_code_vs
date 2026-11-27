using System;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeCodeVs.Sessions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeVs.Tests;

[TestClass]
public class SessionStoreTests
{
    private const string Workspace = @"D:\work\sample-repo";
    private TempDir _claude = null!;
    private string _projectDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _claude = new TempDir();
        SessionStore.ClaudeDirOverride = _claude.Path;
        _projectDir = _claude.Sub("projects", SessionStore.EncodeWorkspace(Workspace));
    }

    [TestCleanup]
    public void Cleanup()
    {
        SessionStore.ClaudeDirOverride = null;
        _claude.Dispose();
    }

    private string Transcript(string id, params string[] lines)
    {
        var path = Path.Combine(_projectDir, id + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    private static string User(string text) =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":" + Newtonsoft.Json.JsonConvert.ToString(text) + "}]},\"timestamp\":\"2026-11-27T10:00:00Z\"}";

    private static string UserWithReminder(string typed) =>
        "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"<system-reminder>ctx</system-reminder>\"},{\"type\":\"text\",\"text\":" + Newtonsoft.Json.JsonConvert.ToString(typed) + "}]}}";

    private static string Assistant(string text) =>
        "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":" + Newtonsoft.Json.JsonConvert.ToString(text) + "}]}}";

    [TestMethod]
    public void EncodeWorkspace_ReplacesEveryNonAlphanumeric()
    {
        Assert.AreEqual("D--work-sample-repo", SessionStore.EncodeWorkspace(Workspace));
    }

    [TestMethod]
    public void Title_PrefersCustomTitle_ThenAiTitle_ThenFirstPrompt()
    {
        Transcript("aaaaaaaa-0000-0000-0000-000000000001", User("first prompt"), "{\"type\":\"ai-title\",\"aiTitle\":\"AI name\"}", "{\"type\":\"custom-title\",\"customTitle\":\"Mine\"}");
        Transcript("aaaaaaaa-0000-0000-0000-000000000002", User("first prompt"), "{\"type\":\"ai-title\",\"aiTitle\":\"AI name\"}");
        Transcript("aaaaaaaa-0000-0000-0000-000000000003", UserWithReminder("typed text"), Assistant("ok"));

        var byId = SessionStore.List(Workspace).ToDictionary(s => s.Id, s => s.Title);

        Assert.AreEqual("Mine", byId["aaaaaaaa-0000-0000-0000-000000000001"]);
        Assert.AreEqual("AI name", byId["aaaaaaaa-0000-0000-0000-000000000002"]);
        Assert.AreEqual("typed text", byId["aaaaaaaa-0000-0000-0000-000000000003"]); // reminder block skipped
    }

    [TestMethod]
    public void MessagelessStub_IsNotListed()
    {
        Transcript("bbbbbbbb-0000-0000-0000-000000000001", "{\"type\":\"cost-state\"}", "{\"type\":\"ai-title\",\"aiTitle\":\"ghost\"}");
        Transcript("bbbbbbbb-0000-0000-0000-000000000002", User("real"));

        var ids = SessionStore.List(Workspace).Select(s => s.Id).ToList();

        CollectionAssert.AreEqual(new[] { "bbbbbbbb-0000-0000-0000-000000000002" }, ids);
    }

    [TestMethod]
    public void Rename_AppendsACustomTitle_ThatWins()
    {
        const string id = "cccccccc-0000-0000-0000-000000000001";
        var path = Transcript(id, User("hello"), "{\"type\":\"ai-title\",\"aiTitle\":\"Old\"}");

        Assert.IsTrue(SessionStore.Rename(Workspace, id, "  New name  "));

        Assert.AreEqual("New name", SessionStore.List(Workspace).Single().Title);
        StringAssert.Contains(File.ReadAllLines(path).Last(), "\"customTitle\":\"New name\"");
        Assert.IsFalse(SessionStore.Rename(Workspace, id, "   ")); // blank is refused
    }

    [TestMethod]
    public void Preview_ReturnsTheLastPromptAndAnswer()
    {
        const string id = "dddddddd-0000-0000-0000-000000000001";
        Transcript(id, User("first"), Assistant("first answer"), UserWithReminder("second"), Assistant("second answer"),
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"x\"}]}}");

        var (prompt, answer) = SessionStore.Preview(Workspace, id);

        Assert.AreEqual("second", prompt);
        Assert.AreEqual("second answer", answer);
    }

    [TestMethod]
    public void Delete_RemovesTranscript_Sidecar_FileHistory_AndSessionEnv()
    {
        const string id = "eeeeeeee-0000-0000-0000-000000000001";
        Transcript(id, User("x"));
        _claude.Sub("projects", SessionStore.EncodeWorkspace(Workspace), id, "subagents");
        _claude.Sub("file-history", id);
        _claude.Sub("session-env", id);
        var keep = Transcript("eeeeeeee-0000-0000-0000-000000000002", User("keep"));

        Assert.AreEqual(4, SessionStore.Delete(id));

        Assert.IsFalse(File.Exists(Path.Combine(_projectDir, id + ".jsonl")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_projectDir, id)));
        Assert.IsFalse(Directory.Exists(Path.Combine(_claude.Path, "file-history", id)));
        Assert.IsFalse(Directory.Exists(Path.Combine(_claude.Path, "session-env", id)));
        Assert.IsTrue(File.Exists(keep));
    }

    [TestMethod]
    public void Delete_RefusesIdsThatCouldEscapeTheFolder()
    {
        Assert.AreEqual(0, SessionStore.Delete(@"..\projects"));
        Assert.AreEqual(0, SessionStore.Delete(""));
    }
}
