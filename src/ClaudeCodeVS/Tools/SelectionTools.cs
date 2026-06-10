using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Editor;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// getCurrentSelection — the active editor's current selection, read from <see cref="SelectionService"/>
/// (kept current by the MEF text-view listener). No UI-thread hop needed: we read a cached snapshot.
/// </summary>
internal sealed class GetCurrentSelectionTool : IIdeTool
{
    public string Name => "getCurrentSelection";
    public string Description => "Get the current text selection in the active Visual Studio editor.";
    public JToken Schema => Schemas.Empty();

    public Task<object> InvokeAsync(JToken arguments, CancellationToken ct)
        => Task.FromResult<object>(SelectionService.CurrentAsJson());
}

/// <summary>getLatestSelection — the last non-empty selection (falls back to current when none seen).</summary>
internal sealed class GetLatestSelectionTool : IIdeTool
{
    public string Name => "getLatestSelection";
    public string Description => "Get the most recent non-empty text selection from the Visual Studio editor.";
    public JToken Schema => Schemas.Empty();

    public Task<object> InvokeAsync(JToken arguments, CancellationToken ct)
        => Task.FromResult<object>(SelectionService.LatestAsJson());
}
