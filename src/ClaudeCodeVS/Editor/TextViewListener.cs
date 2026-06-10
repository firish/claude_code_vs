using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ClaudeCodeVs.Editor;

/// <summary>
/// MEF editor extension: VS calls <see cref="TextViewCreated"/> for every document text view. We hook
/// each view's selection/focus events and forward to <see cref="SelectionService"/>, which powers the
/// selection tools and the selection_changed push. Discovered because the VSIX assembly is registered
/// as a MefComponent asset in the manifest.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class TextViewListener : IWpfTextViewCreationListener
{
    public void TextViewCreated(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        void OnSelectionChanged(object sender, System.EventArgs e) => SelectionService.Update(view);
        void OnGotFocus(object sender, System.EventArgs e) => SelectionService.Update(view);

        view.Selection.SelectionChanged += OnSelectionChanged;
        view.Caret.PositionChanged += (s, e) => SelectionService.Update(view);
        view.GotAggregateFocus += OnGotFocus;

        view.Closed += (s, e) =>
        {
            view.Selection.SelectionChanged -= OnSelectionChanged;
            view.GotAggregateFocus -= OnGotFocus;
        };
    }
}
