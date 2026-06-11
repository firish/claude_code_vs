using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// The dockable "Claude Code" panel content (built in code to avoid XAML build wiring): a header with
/// connection status + a Launch button, and a scrolling live log mirroring the bridge output. Reads
/// <see cref="BridgeStatus"/> and updates on its events (marshaled to the WPF dispatcher, since logs
/// arrive on the background WS thread).
/// </summary>
internal sealed class ClaudeToolWindowControl : UserControl
{
    private readonly TextBlock _status;
    private readonly TextBox _log;
    private readonly CheckBox _autoAccept;

    public ClaudeToolWindowControl()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header: status text + Launch button.
        var header = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
        var launch = new Button
        {
            Content = "Launch Claude Code",
            Padding = new Thickness(10, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        launch.Click += (s, e) => { try { _ = BridgeStatus.LaunchAction?.Invoke(); } catch { /* logged elsewhere */ } };
        DockPanel.SetDock(launch, Dock.Right);

        _autoAccept = new CheckBox
        {
            Content = "Auto-accept (run wild)",
            ToolTip = "Apply edits without opening the diff. Resets when VS restarts.",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        _autoAccept.Checked += (s, e) => BridgeStatus.SetAutoAcceptEdits(true);
        _autoAccept.Unchecked += (s, e) => BridgeStatus.SetAutoAcceptEdits(false);
        DockPanel.SetDock(_autoAccept, Dock.Right);

        _status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8, 0),
        };
        header.Children.Add(launch);
        header.Children.Add(_autoAccept);
        header.Children.Add(_status); // last child fills
        Grid.SetRow(header, 0);

        // Log view.
        _log = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(4, 0, 4, 4),
        };
        Grid.SetRow(_log, 1);

        grid.Children.Add(header);
        grid.Children.Add(_log);
        Content = grid;

        foreach (var line in BridgeStatus.LogSnapshot())
            _log.AppendText(line + Environment.NewLine);
        _log.ScrollToEnd();
        UpdateStatus();

        BridgeStatus.Logged += OnLogged;
        BridgeStatus.Changed += OnChanged;
        Unloaded += (s, e) =>
        {
            BridgeStatus.Logged -= OnLogged;
            BridgeStatus.Changed -= OnChanged;
        };
    }

    // The WPF Dispatcher is the correct way to marshal into a WPF control from the background WS
    // thread; VSTHRD001 prefers JTF but doesn't apply to plain WPF controls.
#pragma warning disable VSTHRD001
    private void OnLogged(string line)
        => _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _log.AppendText(line + Environment.NewLine);
            _log.ScrollToEnd();
        }));

    private void OnChanged() => _ = Dispatcher.BeginInvoke(new Action(UpdateStatus));
#pragma warning restore VSTHRD001

    private void UpdateStatus()
    {
        if (_autoAccept.IsChecked != BridgeStatus.AutoAcceptEdits)
            _autoAccept.IsChecked = BridgeStatus.AutoAcceptEdits;

        if (BridgeStatus.Port is not int port)
        {
            _status.Text = "Claude Code bridge: starting…";
            return;
        }
        var state = BridgeStatus.Connected ? "● Connected" : "○ Waiting for CLI";
        var ws = string.IsNullOrEmpty(BridgeStatus.Workspace) ? "(no workspace)" : BridgeStatus.Workspace;
        _status.Text = $"{state}   ·   port {port}   ·   {ws}";
    }
}
