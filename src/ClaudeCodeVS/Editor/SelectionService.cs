using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Editor;

/// <summary>
/// Central, process-wide selection state. The MEF <see cref="TextViewListener"/> feeds it whenever a
/// caret/selection moves or an editor gains focus; the selection tools read from it and the debounced
/// <c>selection_changed</c> notification is pushed from here. Kept static because the MEF view
/// listener is composed by VS's editor — separate from our package — and both need the same instance.
/// </summary>
internal static class SelectionService
{
    private static readonly object Gate = new();
    private static SelectionInfo _current = SelectionInfo.Empty;
    private static SelectionInfo? _lastNonEmpty;

    private static IdeWebSocketServer? _server;
    private static JoinableTaskFactory? _jtf;
    private static CancellationTokenSource? _debounce;

    /// <summary>Wire the broadcast sink once the bridge server is up (called from BridgeHost).</summary>
    public static void Attach(IdeWebSocketServer server, JoinableTaskFactory jtf)
    {
        _server = server;
        _jtf = jtf;
    }

    public static JToken CurrentAsJson()
    {
        lock (Gate) return _current.ToJson();
    }

    public static JToken LatestAsJson()
    {
        lock (Gate) return (_lastNonEmpty ?? _current).ToJson();
    }

    /// <summary>Record a fresh selection from a focused view and (debounced) push selection_changed.</summary>
    public static void Update(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var info = SelectionInfo.FromView(view);
        if (info is null) return;

        lock (Gate)
        {
            _current = info;
            if (!info.IsEmpty) _lastNonEmpty = info;
        }

        DebouncedBroadcast(info);
    }

    private static void DebouncedBroadcast(SelectionInfo info)
    {
        var server = _server;
        var jtf = _jtf;
        if (server is null || jtf is null || !server.HasConnections) return;

        // Coalesce rapid caret moves (CLAUDE.md gotcha: debounce or you flood the socket).
        _debounce?.Cancel();
        var cts = new CancellationTokenSource();
        _debounce = cts;

        jtf.RunAsync(async () =>
        {
            try
            {
                await Task.Delay(150, cts.Token);
                await server.BroadcastNotificationAsync("selection_changed", info.ToJson(), cts.Token);
            }
            catch (OperationCanceledException) { /* superseded by a newer selection */ }
            catch (Exception e) { Log.Warn($"selection_changed push failed: {e.Message}"); }
        }).FileAndForget("claudecodevs/selectionChanged");
    }
}

/// <summary>Immutable snapshot of one selection, in LSP-shaped (0-based) coordinates.</summary>
internal sealed class SelectionInfo
{
    public static readonly SelectionInfo Empty = new("", null, 0, 0, 0, 0);

    public string Text { get; }
    public string? FilePath { get; }
    public int StartLine { get; }
    public int StartChar { get; }
    public int EndLine { get; }
    public int EndChar { get; }

    public bool IsEmpty => Text.Length == 0;

    public SelectionInfo(string text, string? filePath, int startLine, int startChar, int endLine, int endChar)
    {
        Text = text;
        FilePath = filePath;
        StartLine = startLine;
        StartChar = startChar;
        EndLine = endLine;
        EndChar = endChar;
    }

    public static SelectionInfo? FromView(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (view is null || view.IsClosed) return null;

        var span = view.Selection.StreamSelectionSpan.SnapshotSpan;
        var snapshot = span.Snapshot;

        var startLine = snapshot.GetLineFromPosition(span.Start.Position);
        var endLine = snapshot.GetLineFromPosition(span.End.Position);

        string? path = null;
        if (view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
            path = doc?.FilePath;

        return new SelectionInfo(
            span.GetText(),
            path,
            startLine.LineNumber,
            span.Start.Position - startLine.Start.Position,
            endLine.LineNumber,
            span.End.Position - endLine.Start.Position);
    }

    public JToken ToJson()
    {
        return new JObject
        {
            ["success"] = true,
            ["text"] = Text,
            ["filePath"] = FilePath,
            ["fileUrl"] = FilePath is not null ? new Uri(FilePath).AbsoluteUri : null,
            ["selection"] = new JObject
            {
                ["start"] = new JObject { ["line"] = StartLine, ["character"] = StartChar },
                ["end"] = new JObject { ["line"] = EndLine, ["character"] = EndChar },
            },
        };
    }
}
