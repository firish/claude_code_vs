using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVs.Ui;

/// <summary>A tiny themed modal asking for a session's new name. UI thread only.</summary>
internal sealed class RenameDialog : DialogWindow
{
    private readonly TextBox _box;

    private RenameDialog(string current)
    {
        Title = Strings.RenameTitle;
        Width = 460;
        Height = 160;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var prompt = new TextBlock { Text = Strings.RenamePrompt, Margin = new Thickness(0, 0, 0, 6) };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Grid.SetRow(prompt, 0);

        _box = new TextBox { Text = current, Padding = new Thickness(4) };
        _box.SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
        _box.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
        _box.SetResourceReference(TextBox.CaretBrushProperty, VsBrushes.WindowTextKey);
        _box.SetResourceReference(BorderBrushProperty, VsBrushes.ComboBoxBorderKey);
        Grid.SetRow(_box, 1);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var ok = new Button { Content = Strings.BtnRenameOk, IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 2, 8, 2) };
        var cancel = new Button { Content = Strings.BtnCancel, IsCancel = true, MinWidth = 80, Padding = new Thickness(8, 2, 8, 2) };
        ok.Click += (s, e) => { DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);

        grid.Children.Add(prompt);
        grid.Children.Add(_box);
        grid.Children.Add(buttons);
        Content = grid;

        Loaded += (s, e) => { _box.Focus(); _box.SelectAll(); };
    }

    /// <summary>The new name, or null when cancelled / left blank.</summary>
    public static string? Prompt(string current)
    {
        var dlg = new RenameDialog(current);
        var text = dlg.ShowDialog() == true ? dlg._box.Text.Trim() : null;
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
