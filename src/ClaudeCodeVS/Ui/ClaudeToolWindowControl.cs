using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// The dockable "Claude Code" panel (built in code, no XAML): a themed header with a status pill, a
/// toolbar (Launch / run-wild / clear / open Output), a stats card (edit decisions + token/cost), a
/// pending-diff strip, and a curated activity feed. Colors come from VS theme brushes so it tracks
/// dark/light automatically. Raw protocol frames stay in the Output pane; the feed shows only curated
/// lines. Reads <see cref="BridgeStatus"/> and updates on its events (marshaled to the WPF dispatcher,
/// since logs arrive on the background WS thread).
/// </summary>
internal sealed class ClaudeToolWindowControl : UserControl
{
    // Neutral translucent grays read correctly on both dark and light themes (they lighten/darken
    // relative to whatever the themed background is), so we don't need separate per-theme assets.
    private static readonly Brush Chip = Freeze(Color.FromArgb(26, 128, 128, 128));
    private static readonly Brush ChipHover = Freeze(Color.FromArgb(56, 128, 128, 128));
    private static readonly Brush Divider = Freeze(Color.FromArgb(40, 128, 128, 128));
    private static readonly Brush DotConnected = Freeze(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush DotWaiting = Freeze(Color.FromRgb(0xD7, 0xA5, 0x3D));
    private static readonly Brush DotIdle = Freeze(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static readonly Brush ErrText = Freeze(Color.FromRgb(0xE0, 0x6C, 0x5C));
    private static readonly Brush WarnText = Freeze(Color.FromRgb(0xD0, 0x9A, 0x36));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    private readonly Ellipse _dot;
    private readonly TextBlock _statusLine;
    private readonly TextBlock _endpointLine;
    private readonly TextBlock _editsLine;
    private readonly TextBlock _latestLine;
    private readonly TextBlock _sessionLine;
    private readonly Border _pendingCard;
    private readonly TextBlock _pendingText;
    private readonly CheckBox _autoAccept;
    private readonly ListBox _feed;
    private readonly DispatcherTimer _timer;
    private readonly StackPanel _costRow;
    private readonly TextBlock _costText;
    private readonly Button _costButton;
    private bool _showCost;

    public ClaudeToolWindowControl()
    {
        SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var root = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        for (int i = 0; i < 5; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // feed

        // ---- Row 0: header (title + status pill) ----
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = "Claude Code", FontSize = 15, FontWeight = FontWeights.SemiBold });

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        _dot = new Ellipse { Width = 9, Height = 9, Fill = DotIdle, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        _statusLine = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        statusRow.Children.Add(_dot);
        statusRow.Children.Add(_statusLine);
        header.Children.Add(statusRow);

        _endpointLine = new TextBlock { FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        header.Children.Add(_endpointLine);
        Grid.SetRow(header, 0);

        // ---- Row 1: toolbar ----
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right);
        right.Children.Add(MakeButton("Clear", () => _feed!.Items.Clear()));
        right.Children.Add(MakeButton("Output", () => { try { BridgeStatus.ShowOutputAction?.Invoke(); } catch { } }));

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(MakeButton("Launch Claude Code", () => { _ = BridgeStatus.LaunchAction?.Invoke(); }));
        _autoAccept = new CheckBox
        {
            Content = "Auto-accept (run wild)",
            ToolTip = "Apply edits without opening the diff. Resets when VS restarts.",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        _autoAccept.Checked += (s, e) => BridgeStatus.SetAutoAcceptEdits(true);
        _autoAccept.Unchecked += (s, e) => BridgeStatus.SetAutoAcceptEdits(false);
        _autoAccept.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey); // else label is black-on-dark
        left.Children.Add(_autoAccept);

        toolbar.Children.Add(right);
        toolbar.Children.Add(left);
        Grid.SetRow(toolbar, 1);

        // ---- Row 2: stats card ----
        _editsLine = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _latestLine = new TextBlock { FontSize = 12, Opacity = 0.9, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        _sessionLine = new TextBlock { FontSize = 12, Opacity = 0.9, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

        // Cost is an estimate, so it's gated behind a toggle rather than shown by default.
        _costButton = MakeButton("≈ Show est. cost", ToggleCost);
        _costText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
        _costRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        _costRow.Children.Add(_costButton);
        _costRow.Children.Add(_costText);

        var statsStack = new StackPanel();
        statsStack.Children.Add(_editsLine);
        statsStack.Children.Add(_latestLine);
        statsStack.Children.Add(_sessionLine);
        statsStack.Children.Add(_costRow);
        var statsCard = new Border
        {
            Background = Chip,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = statsStack,
        };
        Grid.SetRow(statsCard, 2);

        // ---- Row 3: pending diffs (collapsed when none) ----
        _pendingText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _pendingCard = new Border
        {
            Background = Chip,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
            Child = _pendingText,
        };
        Grid.SetRow(_pendingCard, 3);

        // ---- Row 4: feed label ----
        var feedLabel = new TextBlock { Text = "ACTIVITY", FontSize = 10, FontWeight = FontWeights.SemiBold, Opacity = 0.55, Margin = new Thickness(2, 0, 0, 4) };
        Grid.SetRow(feedLabel, 4);

        // ---- Row 5: curated activity feed ----
        _feed = new ListBox
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Divider,
            Background = Brushes.Transparent,
            ItemContainerStyle = FlatItemStyle(),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_feed, ScrollBarVisibility.Auto);
        _feed.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Grid.SetRow(_feed, 5);

        root.Children.Add(header);
        root.Children.Add(toolbar);
        root.Children.Add(statsCard);
        root.Children.Add(_pendingCard);
        root.Children.Add(feedLabel);
        root.Children.Add(_feed);
        Content = root;

        // 1s tick keeps the "connected for N" readout live without an event per second.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (s, e) => UpdateStatus();

        // Subscribe on Loaded / unsubscribe on Unloaded (symmetric). VS fires Unloaded whenever the
        // tool window is hidden, tab-switched, or re-docked, so subscribing once in the ctor and
        // unsubscribing on Unloaded would leave the panel permanently frozen after the first hide.
        Loaded += (s, e) => Attach();
        Unloaded += (s, e) => Detach();
    }

    private bool _wired;

    private void Attach()
    {
        if (_wired) return;
        _wired = true;
        BridgeStatus.Logged += OnLogged;
        BridgeStatus.Changed += OnChanged;
        // Re-sync from current state (we may have missed events while hidden).
        _feed.Items.Clear();
        foreach (var entry in BridgeStatus.LogSnapshot())
            AddFeedLine(entry.Level, entry.Text);
        UpdateStatus();
        _timer.Start();
    }

    private void Detach()
    {
        if (!_wired) return;
        _wired = false;
        BridgeStatus.Logged -= OnLogged;
        BridgeStatus.Changed -= OnChanged;
        _timer.Stop();
    }

    // The WPF Dispatcher is the correct way to marshal into a WPF control from the background WS
    // thread; VSTHRD001 prefers JTF but doesn't apply to plain WPF controls.
#pragma warning disable VSTHRD001
    private void OnLogged(LogLevel level, string line)
        => _ = Dispatcher.BeginInvoke(new Action(() => AddFeedLine(level, line)));

    private void OnChanged() => _ = Dispatcher.BeginInvoke(new Action(UpdateStatus));
#pragma warning restore VSTHRD001

    private void AddFeedLine(LogLevel level, string text)
    {
        // Raw JSON frames and notification noise stay in the Output pane; keep the panel readable.
        if (level == LogLevel.Frame || level == LogLevel.Event) return;

        var tb = new TextBlock { Text = text, FontFamily = Mono, FontSize = 11.5, TextWrapping = TextWrapping.NoWrap };
        if (level == LogLevel.Error) tb.Foreground = ErrText;
        else if (level == LogLevel.Warn) tb.Foreground = WarnText;
        else tb.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);

        _feed.Items.Add(tb);
        while (_feed.Items.Count > 400) _feed.Items.RemoveAt(0);
        _feed.ScrollIntoView(tb);
    }

    private void UpdateStatus()
    {
        if (_autoAccept.IsChecked != BridgeStatus.AutoAcceptEdits)
            _autoAccept.IsChecked = BridgeStatus.AutoAcceptEdits;

        // Status pill.
        if (BridgeStatus.Port is not int port)
        {
            _dot.Fill = DotIdle;
            _statusLine.Text = "Starting…";
            _endpointLine.Text = "";
        }
        else if (BridgeStatus.Connected)
        {
            _dot.Fill = DotConnected;
            var up = BridgeStatus.ConnectedSince is DateTime since ? "  ·  " + Uptime(since) : "";
            _statusLine.Text = "Connected" + up;
            _endpointLine.Text = $"port {port}  ·  {Workspace()}";
        }
        else
        {
            _dot.Fill = DotWaiting;
            _statusLine.Text = "Waiting for CLI";
            _endpointLine.Text = $"port {port}  ·  {Workspace()}";
        }

        // Stats card.
        _editsLine.Text = $"Edits   ✓ {BridgeStatus.EditsAccepted} accepted    ✗ {BridgeStatus.EditsRejected} rejected";

        // Tokens are always shown; cost (an estimate) sits behind a toggle. We show the latest call
        // and the cumulative session separately, since the transcript spans the whole conversation.
        var latest = BridgeStatus.Latest;
        var session = BridgeStatus.Session;
        var model = string.IsNullOrEmpty(BridgeStatus.Model) ? "" : "  ·  " + ShortModel(BridgeStatus.Model!);
        _latestLine.Text =
            $"Latest    ↑ {Tok(latest.Input)} in   ↓ {Tok(latest.Output)} out   ⚡ {Tok(latest.CacheRead)} cached";
        _sessionLine.Text =
            $"Session   ↑ {Tok(session.Input)} in   ↓ {Tok(session.Output)} out   ⚡ {Tok(session.CacheRead)} cached" +
            (BridgeStatus.Turns > 0 ? $"  ·  {BridgeStatus.Turns} turns{model}" : "");
        _latestLine.Opacity = BridgeStatus.HasUsage ? 0.9 : 0.55;
        _sessionLine.Opacity = BridgeStatus.HasUsage ? 0.9 : 0.55;

        if (BridgeStatus.HasUsage)
        {
            _costRow.Visibility = Visibility.Visible;
            _costButton.Content = _showCost ? "Hide cost" : "≈ Show est. cost";
            _costText.Visibility = _showCost ? Visibility.Visible : Visibility.Collapsed;
            if (_showCost)
                _costText.Text = $"≈ ${session.CostUsd:0.00} session  ·  ${latest.CostUsd:0.00} latest  (estimate)";
        }
        else
        {
            _costRow.Visibility = Visibility.Collapsed;
        }

        // Pending diffs.
        var pending = BridgeStatus.PendingSnapshot();
        if (pending.Count == 0)
        {
            _pendingCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var p in pending) names.Add(System.IO.Path.GetFileName(p));
            _pendingText.Text = $"⏳ Awaiting your review:  {string.Join(",  ", names)}";
            _pendingCard.Visibility = Visibility.Visible;
        }
    }

    private void ToggleCost()
    {
        _showCost = !_showCost;
        UpdateStatus();
    }

    private static string Workspace()
        => string.IsNullOrEmpty(BridgeStatus.Workspace) ? "(no workspace)" : BridgeStatus.Workspace!;

    private static string Tok(long n)
    {
        if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.0") + "M";
        if (n >= 1_000) return (n / 1_000.0).ToString("0.0") + "k";
        return n.ToString();
    }

    private static string Uptime(DateTime since)
    {
        var t = DateTime.Now - since;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{Math.Max(0, t.Seconds)}s";
    }

    private static string ShortModel(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("opus")) return "opus";
        if (m.Contains("sonnet")) return "sonnet";
        if (m.Contains("haiku")) return "haiku";
        return model;
    }

    /// <summary>A flat, non-selectable list item (so the feed reads like a log, not a selectable list).</summary>
    private static Style FlatItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(2, 0, 2, 0)));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        var template = new ControlTemplate(typeof(ListBoxItem));
        var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = cp;
        style.Setters.Add(new Setter(TemplateProperty, template));
        return style;
    }

    /// <summary>A flat, theme-aware button: a rounded chip that lightens on hover, themed text.</summary>
    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 3, 10, 3),
            FontSize = 12,
            Cursor = Cursors.Hand,
            Background = Chip,
            BorderThickness = new Thickness(0),
        };
        b.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var border = new System.Windows.FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty, ChipHover));
        template.Triggers.Add(hover);
        b.Template = template;

        b.Click += (s, e) => { try { onClick(); } catch { } };
        return b;
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
