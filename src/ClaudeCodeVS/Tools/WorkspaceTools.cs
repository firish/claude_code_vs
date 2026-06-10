using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Editor;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>getOpenEditors — the file documents currently open in VS, with their unsaved (dirty) state.</summary>
internal sealed class GetOpenEditorsTool : IIdeTool
{
    public string Name => "getOpenEditors";
    public string Description => "List all files currently open in Visual Studio editor tabs, including whether each has unsaved changes.";
    public JToken Schema => Schemas.Empty();

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var arr = new JArray();
        foreach (var doc in RunningDocuments.OpenDocuments())
        {
            arr.Add(new JObject
            {
                ["filePath"] = doc.Path,
                ["fileUrl"] = ToUri(doc.Path),
                ["isDirty"] = doc.IsDirty,
            });
        }
        Log.Info($"getOpenEditors -> {arr.Count} editor(s)");
        return new JObject { ["editors"] = arr };
    }

    internal static string? ToUri(string path)
    {
        try { return new Uri(path).AbsoluteUri; } catch { return null; }
    }
}

/// <summary>getWorkspaceFolders — the open solution/folder root(s).</summary>
internal sealed class GetWorkspaceFoldersTool : IIdeTool
{
    public string Name => "getWorkspaceFolders";
    public string Description => "Get the root folder(s) of the open Visual Studio solution or folder.";
    public JToken Schema => Schemas.Empty();

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var arr = new JArray();
        var sol = (IVsSolution?)await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolution));
        if (sol != null &&
            sol.GetSolutionInfo(out string dir, out _, out _) == VSConstants.S_OK &&
            !string.IsNullOrEmpty(dir))
        {
            dir = dir.TrimEnd('\\');
            arr.Add(new JObject
            {
                ["name"] = System.IO.Path.GetFileName(dir),
                ["path"] = dir,
                ["uri"] = GetOpenEditorsTool.ToUri(dir),
            });
        }
        Log.Info($"getWorkspaceFolders -> {arr.Count} folder(s)");
        return new JObject { ["folders"] = arr };
    }
}

/// <summary>checkDocumentDirty — whether a file is open and has unsaved changes.</summary>
internal sealed class CheckDocumentDirtyTool : IIdeTool
{
    public string Name => "checkDocumentDirty";
    public string Description => "Check whether a file is open in the editor and has unsaved (dirty) changes.";
    public JToken Schema => Schemas.WithFilePath();

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string? path = (string?)args["filePath"];
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("filePath is required");

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        bool? dirty = RunningDocuments.IsDirty(path!);
        return new JObject
        {
            ["isOpen"] = dirty.HasValue,
            ["isDirty"] = dirty ?? false,
        };
    }
}

/// <summary>saveDocument — save an open document if it has unsaved changes.</summary>
internal sealed class SaveDocumentTool : IIdeTool
{
    public string Name => "saveDocument";
    public string Description => "Save a file's unsaved changes in the Visual Studio editor.";
    public JToken Schema => Schemas.WithFilePath();

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string? path = (string?)args["filePath"];
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("filePath is required");

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        bool saved = RunningDocuments.Save(path!);
        Log.Info($"saveDocument: {path} -> {(saved ? "saved" : "not open")}");
        return "FILE_SAVED";
    }
}
