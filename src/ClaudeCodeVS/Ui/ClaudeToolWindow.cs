using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVs.Ui;

/// <summary>The dockable "Claude Code" tool window. Hosts <see cref="ClaudeToolWindowControl"/>.</summary>
[Guid("6999373e-bcbd-45c4-8a00-04d22d62bb36")]
public sealed class ClaudeToolWindow : ToolWindowPane
{
    public ClaudeToolWindow() : base(null)
    {
        Caption = "Claude Code";
        Content = new ClaudeToolWindowControl();
    }
}
