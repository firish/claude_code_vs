using System.Windows;
using System.Windows.Controls;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// A tiny modal dialog asking why the user is rejecting a change. The text is fed back to Claude as
/// the PreToolUse hook's permissionDecisionReason, so Claude reconsiders with the feedback. Built in
/// code (no XAML). Must be shown on the UI thread.
/// </summary>
internal sealed class ReasonDialog : Window
{
    private readonly TextBox _box;

    private ReasonDialog(string fileName)
    {
        Title = "Reject change";
        Width = 480;
        Height = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var prompt = new TextBlock
        {
            Text = $"Tell Claude what to change about {fileName} (optional):",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(prompt, 0);

        _box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(_box, 1);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var ok = new Button { Content = "Send to Claude", IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 2, 8, 2) };
        var cancel = new Button { Content = "Just reject", IsCancel = true, MinWidth = 90, Padding = new Thickness(8, 2, 8, 2) };
        ok.Click += (s, e) => { DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);

        grid.Children.Add(prompt);
        grid.Children.Add(_box);
        grid.Children.Add(buttons);
        Content = grid;

        Loaded += (s, e) => _box.Focus();
    }

    /// <summary>Prompt for a reject reason. Returns the text (possibly empty), or null if cancelled.</summary>
    public static string? Prompt(string fileName)
    {
        var dlg = new ReasonDialog(fileName);
        return dlg.ShowDialog() == true ? dlg._box.Text : null;
    }
}
