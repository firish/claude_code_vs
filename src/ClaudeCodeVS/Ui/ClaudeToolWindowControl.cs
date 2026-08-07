using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    // Translucent amber for the "tools didn't load" banner - reads on dark and light like the gray chips.
    private static readonly Brush WarnFill = Freeze(Color.FromArgb(30, 0xD0, 0x9A, 0x36));
    private static readonly Brush WarnBorder = Freeze(Color.FromArgb(96, 0xD0, 0x9A, 0x36));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    private readonly Ellipse _dot;
    private readonly TextBlock _statusLine;
    private readonly TextBlock _endpointLine;
    private readonly TextBlock _editsLine;
    private readonly TextBlock _debugLine;
    private readonly TextBlock _latestLine;
    private readonly TextBlock _sessionLine;
    private readonly Border _pendingCard;
    private readonly TextBlock _pendingText;
    private readonly Border _toolsWarningCard;
    private readonly TextBlock _toolsWarningTitle;
    private readonly TextBlock _toolsWarningText;
    private readonly CheckBox _autoAccept;
    private readonly CheckBox _allowDrive;
    private readonly CheckBox _allowCapture;
    private readonly CheckBox _notify;
    private readonly ListBox _feed;
    private readonly WrapPanel _attachChips;
    private readonly Button _attachClear;
    private readonly TextBlock _attachSummary;
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
        for (int i = 0; i < 7; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // feed

        // ---- Row 0: header (status pill) ----
        // No "Claude Code" text here: the tool window's own Caption (ClaudeToolWindow.cs) already shows
        // it in the tab/title bar, so a second copy here just duplicated the first line.
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

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
        // One flat WrapPanel for all four buttons (no DockPanel right-docking): docking Clear/Output to
        // the right pre-claims width, which clipped the Launch buttons mid-text at narrow panel widths.
        // Wrapping keeps every button whole and reachable at any width; right-alignment wasn't worth that.
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        toolbar.Children.Add(MakeButton("Launch Claude Code", () => { _ = BridgeStatus.LaunchAction?.Invoke(); }));
        var launchExternal = MakeButton("External console", () => { _ = BridgeStatus.LaunchExternalAction?.Invoke(); });
        launchExternal.ToolTip = "Launch Claude Code in a separate console window instead of the docked terminal. Unlike the docked tab, it survives closing Visual Studio.";
        toolbar.Children.Add(launchExternal);
        toolbar.Children.Add(MakeButton("Clear", () => _feed!.Items.Clear()));
        toolbar.Children.Add(MakeButton("Output", () => { try { BridgeStatus.ShowOutputAction?.Invoke(); } catch { } }));

        // The toggles get their own WRAPPING row: four checkboxes stopped fitting beside the buttons at
        // a typically-docked panel width (they were simply invisible until the panel was widened), and a
        // WrapPanel keeps every one reachable at any size. Each also carries a what-it-does tooltip.
        var toggles = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        _autoAccept = new CheckBox
        {
            Content = "Auto-accept (run wild)",
            ToolTip = "Apply edits without opening the diff. Resets when VS restarts.",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 2),
        };
        // The guard keeps PROGRAMMATIC check-state changes (reflecting the CLI's session mode in
        // UpdateStatus) from being recorded as the user's own run-wild preference.
        _autoAccept.Checked += (s, e) => { if (!_syncingToggles) BridgeStatus.SetAutoAcceptEdits(true); };
        _autoAccept.Unchecked += (s, e) => { if (!_syncingToggles) BridgeStatus.SetAutoAcceptEdits(false); };
        _autoAccept.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey); // else label is black-on-dark
        toggles.Children.Add(_autoAccept);

        // Phase 3 gate: lets Claude continue/step/set-breakpoints while paused. Default OFF, resets each
        // session (same in-memory safety model as auto-accept) - model-controlled execution is opt-in.
        _allowDrive = new CheckBox
        {
            Content = "Allow Claude to drive debugger",
            ToolTip = "Let Claude continue/step and set breakpoints while paused. Resets when VS restarts.",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 2),
        };
        _allowDrive.Checked += (s, e) => BridgeStatus.SetAllowDebuggerDrive(true);
        _allowDrive.Unchecked += (s, e) => BridgeStatus.SetAllowDebuggerDrive(false);
        _allowDrive.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        toggles.Children.Add(_allowDrive);

        // Capture gate: lets Claude screenshot the debuggee / a window by title / the screen into the
        // attachment tray. Same in-memory opt-in model as the drive toggle - what Claude can SEE of your
        // desktop is a safety decision, so it is never left on across sessions.
        _allowCapture = new CheckBox
        {
            Content = "Allow screen capture",
            ToolTip = "Let Claude capture the debugged app's window, a window by title (e.g. your browser), or the screen as image attachments. Every capture is logged and staged as a visible chip. Resets when VS restarts.",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 2),
        };
        _allowCapture.Checked += (s, e) => BridgeStatus.SetAllowScreenCapture(true);
        _allowCapture.Unchecked += (s, e) => BridgeStatus.SetAllowScreenCapture(false);
        _allowCapture.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        toggles.Children.Add(_allowCapture);

        // Notifications: an InfoBar (+ taskbar flash when VS is in the background) when Claude finishes
        // a turn or needs input. A convenience, not a safety gate, so unlike the two above it defaults ON.
        _notify = new CheckBox
        {
            Content = "Notify",
            ToolTip = "Show a notification bar (and flash the taskbar when VS is in the background) when Claude finishes responding or needs your input.",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 2),
            IsChecked = BridgeStatus.NotifyEnabled,
        };
        _notify.Checked += (s, e) => BridgeStatus.SetNotifyEnabled(true);
        _notify.Unchecked += (s, e) => BridgeStatus.SetNotifyEnabled(false);
        _notify.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        toggles.Children.Add(_notify);

        var toolbarStack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        toolbar.Margin = new Thickness(0);
        toolbarStack.Children.Add(toolbar);
        toolbarStack.Children.Add(toggles);
        Grid.SetRow(toolbarStack, 1);

        // ---- Row 2: "extra tools didn't load" banner (collapsed unless the PULL MCP servers failed) ----
        // Surfaces the otherwise-silent gap where the IDE WebSocket connected but vs-debug/vs-semantic/
        // tests never loaded (Claude launched outside the workspace, or project servers unapproved).
        var warnStack = new StackPanel();
        _toolsWarningTitle = new TextBlock
        {
            Text = "⚠  Workspace hooks & tools didn't load for this session",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = WarnText,
            TextWrapping = TextWrapping.Wrap,
        };
        warnStack.Children.Add(_toolsWarningTitle);
        _toolsWarningText = new TextBlock { FontSize = 11.5, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        warnStack.Children.Add(_toolsWarningText);
        var warnButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        warnButtons.Children.Add(MakeButton("Relaunch Claude Code", () => { _ = BridgeStatus.LaunchAction?.Invoke(); }));
        warnStack.Children.Add(warnButtons);
        _toolsWarningCard = new Border
        {
            Background = WarnFill,
            BorderBrush = WarnBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
            Child = warnStack,
        };
        Grid.SetRow(_toolsWarningCard, 2);

        // ---- Row 3: stats card ----
        _editsLine = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _debugLine = new TextBlock { FontSize = 12, Opacity = 0.9, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
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
        statsStack.Children.Add(_debugLine);
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
        Grid.SetRow(statsCard, 3);

        // ---- Row 4: pending diffs (collapsed when none) ----
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
        Grid.SetRow(_pendingCard, 4);

        // ---- Row 5: attachments (drop/paste target + staged chips) ----
        // Screenshots can't be pasted into the CLI on Windows (open upstream gap), so the panel is the
        // paste/drop point: stage the file, then push an at_mentioned so the reference lands in the
        // CLI's composer with no path typing. Chips show what's staged; × removes, click re-mentions.
        var attachHint = new TextBlock
        {
            Text = "📎  Attach — drop images/files here, or",
            FontSize = 11.5,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var attachHeader = new StackPanel { Orientation = Orientation.Horizontal };
        attachHeader.Children.Add(attachHint);
        var pasteBtn = MakeButton("Paste", PasteFromClipboard);
        pasteBtn.Margin = new Thickness(6, 0, 6, 0);
        pasteBtn.ToolTip = "Paste from the clipboard: a screenshot (Win+Shift+S), copied files, or copied text (opens in the composer to review/edit, then attaches as .txt). Ctrl+V in the panel works too.";
        attachHeader.Children.Add(pasteBtn);
        var composeBtn = MakeButton("Compose", () => ComposeAndStage(""));
        composeBtn.Margin = new Thickness(0, 0, 6, 0);
        composeBtn.ToolTip = "Write multi-line text in an editor (line breaks, code, whatever), then attach it as a .txt with an @ reference in the Claude composer.";
        attachHeader.Children.Add(composeBtn);
        _attachClear = MakeButton("Clear", Attachments.AttachmentService.Clear);
        _attachClear.Visibility = Visibility.Collapsed;
        attachHeader.Children.Add(_attachClear);
        // A live "what will this cost" readout for the staged set - same estimate language as the cost row.
        _attachSummary = new TextBlock
        {
            FontSize = 11.5,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _attachSummary.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        attachHeader.Children.Add(_attachSummary);

        _attachChips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };

        var attachStack = new StackPanel();
        attachStack.Children.Add(attachHeader);
        attachStack.Children.Add(_attachChips);
        var attachCard = new Border
        {
            Background = Chip,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 8),
            AllowDrop = true,
            Child = attachStack,
        };
        attachCard.DragOver += OnAttachDragOver;
        attachCard.Drop += OnAttachDrop;
        Grid.SetRow(attachCard, 5);

        // Ctrl+V anywhere in the panel = the Paste image button.
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, (s, e) => PasteFromClipboard()));

        // ---- Row 6: feed label ----
        var feedLabel = new TextBlock { Text = "ACTIVITY", FontSize = 10, FontWeight = FontWeights.SemiBold, Opacity = 0.55, Margin = new Thickness(2, 0, 0, 4) };
        Grid.SetRow(feedLabel, 6);

        // ---- Row 7: curated activity feed ----
        _feed = new ListBox
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Divider,
            Background = Brushes.Transparent,
            ItemContainerStyle = FlatItemStyle(),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_feed, ScrollBarVisibility.Auto);
        _feed.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Grid.SetRow(_feed, 7);

        root.Children.Add(header);
        root.Children.Add(toolbarStack);
        root.Children.Add(_toolsWarningCard);
        root.Children.Add(statsCard);
        root.Children.Add(_pendingCard);
        root.Children.Add(attachCard);
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
    private bool _syncingToggles; // true while UpdateStatus mirrors state INTO the checkboxes

    private void Attach()
    {
        if (_wired) return;
        _wired = true;
        BridgeStatus.Logged += OnLogged;
        BridgeStatus.Changed += OnChanged;
        Attachments.AttachmentService.Changed += OnAttachmentsChanged;
        // Re-sync from current state (we may have missed events while hidden).
        _feed.Items.Clear();
        foreach (var entry in BridgeStatus.LogSnapshot())
            AddFeedLine(entry.Level, entry.Text);
        UpdateStatus();
        RefreshAttachments();
        _timer.Start();
    }

    private void Detach()
    {
        if (!_wired) return;
        _wired = false;
        BridgeStatus.Logged -= OnLogged;
        BridgeStatus.Changed -= OnChanged;
        Attachments.AttachmentService.Changed -= OnAttachmentsChanged;
        _timer.Stop();
    }

    // The WPF Dispatcher is the correct way to marshal into a WPF control from the background WS
    // thread; VSTHRD001 prefers JTF but doesn't apply to plain WPF controls.
#pragma warning disable VSTHRD001
    private void OnLogged(LogLevel level, string line)
        => _ = Dispatcher.BeginInvoke(new Action(() => AddFeedLine(level, line)));

    private void OnChanged() => _ = Dispatcher.BeginInvoke(new Action(UpdateStatus));

    private void OnAttachmentsChanged() => _ = Dispatcher.BeginInvoke(new Action(RefreshAttachments));
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
        _syncingToggles = true;
        try
        {
            // Run-wild reflects the CLI session's own mode (issue #17 follow-up): while the CLI
            // pre-approves edits (acceptEdits / bypassPermissions, e.g. shift+tab auto-accept in the
            // terminal), the checkbox shows checked and DISABLED - unchecking it could not re-gate
            // edits the user already approved at the CLI level, so the UI must not offer it. When the
            // session mode is default (or no session), the checkbox is the user's own bridge-side
            // toggle exactly as before. The checked-at-Launch direction starts the CLI in acceptEdits.
            if (BridgeStatus.CliEditsPreApproved)
            {
                _autoAccept.IsChecked = true;
                _autoAccept.IsEnabled = false;
                _autoAccept.ToolTip = $"Edits are pre-approved by the CLI session (permission mode '{BridgeStatus.CliPermissionMode}'). Change it in the terminal (shift+tab), or start a new session.";
            }
            else
            {
                _autoAccept.IsEnabled = true;
                _autoAccept.ToolTip = "Apply edits without opening the diff (and launch new sessions in acceptEdits). Resets when VS restarts.";
                if (_autoAccept.IsChecked != BridgeStatus.AutoAcceptEdits)
                    _autoAccept.IsChecked = BridgeStatus.AutoAcceptEdits;
            }

            if (_allowDrive.IsChecked != BridgeStatus.AllowDebuggerDrive)
                _allowDrive.IsChecked = BridgeStatus.AllowDebuggerDrive;
            if (_allowCapture.IsChecked != BridgeStatus.AllowScreenCapture)
                _allowCapture.IsChecked = BridgeStatus.AllowScreenCapture;
        }
        finally
        {
            _syncingToggles = false;
        }
        if (_notify.IsChecked != BridgeStatus.NotifyEnabled)
            _notify.IsChecked = BridgeStatus.NotifyEnabled;

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
        _debugLine.Text = $"Debugger   {BridgeStatus.DebugInspects} inspected   ·   {BridgeStatus.DebugDrives} driven";

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

        // The two-variant warning banner. Variant 1 ("hooks only"): a session's hook POSTs are reaching
        // the bridge but the IDE WebSocket never connected - claude was launched outside the extension,
        // and /ide from that terminal lights up the diff/selection channel. Variant 2 ("config not
        // loaded"): connected, but no /mcp handshake - the session never loaded the workspace's .claude
        // configuration at all. Only meaningful in their respective connection states.
        if (BridgeStatus.HooksOnlyWarning && !BridgeStatus.Connected)
        {
            _toolsWarningTitle.Text = "⚠  A Claude session is running, but not connected to Visual Studio";
            _toolsWarningText.Text =
                "Its hooks are reaching this workspace's bridge (token stats update), but the session was " +
                "launched outside the extension and never connected the IDE channel - so edits won't open " +
                "the review diff and selection isn't shared. Run /ide in that Claude terminal and pick " +
                "Visual Studio (works from any folder inside the workspace), or relaunch from here. The " +
                "vs-debug / vs-semantic tools additionally need the session started at the workspace root.";
            _toolsWarningCard.Visibility = Visibility.Visible;
        }
        else if (BridgeStatus.ToolsWarning && BridgeStatus.Connected)
        {
            _toolsWarningTitle.Text = "⚠  Workspace hooks & tools didn't load for this session";
            _toolsWarningText.Text =
                "Claude connected, but this session never loaded the workspace's .claude configuration - " +
                "so the edit-review diff, notifications, and the vs-debug / vs-semantic / test tools are " +
                "all inactive. Usually Claude was started outside (or in a subfolder of) the workspace" +
                (string.IsNullOrEmpty(BridgeStatus.Workspace) ? "" : $" ({BridgeStatus.Workspace})") +
                ", so its project directory doesn't include our hooks - or the project MCP servers weren't " +
                "approved. Relaunch from here (pins the right folder), or start claude at the workspace " +
                "root; approve the vs-debug / vs-semantic servers if prompted.";
            _toolsWarningCard.Visibility = Visibility.Visible;
        }
        else
        {
            _toolsWarningCard.Visibility = Visibility.Collapsed;
        }
    }

    private void ToggleCost()
    {
        _showCost = !_showCost;
        UpdateStatus();
    }

    // ---- Attachments: drop/paste -> stage -> at_mentioned chip in the CLI composer ----

    private static void OnAttachDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnAttachDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths is { Length: > 0 })
                    _ = Task.Run(() => Attachments.AttachmentService.StageFilesAsync(paths));
            }
            else if (e.Data.GetDataPresent(DataFormats.Bitmap) && e.Data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            {
                // Encode on the UI thread (BitmapSource is thread-affine); file IO + send hop off it.
                var png = EncodePng(bmp);
                _ = Task.Run(() => Attachments.AttachmentService.StageImageBytesAsync(png));
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText) && e.Data.GetData(DataFormats.UnicodeText) is string dropped)
            {
                // Dragged TEXT (a selection from an editor, a browser, anywhere) opens in the composer
                // for review/edit, then attaches as a .txt.
                ComposeAndStage(dropped);
            }
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"attach: drop failed: {ex.Message}");
        }
    }

    /// <summary>The panel is the paste point for screenshots - the CLI can't take them on Windows.</summary>
    private void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource bmp)
            {
                var png = EncodePng(bmp);
                _ = Task.Run(() => Attachments.AttachmentService.StageImageBytesAsync(png));
            }
            else if (Clipboard.ContainsFileDropList())
            {
                var paths = new System.Collections.Generic.List<string>();
                foreach (var p in Clipboard.GetFileDropList())
                    if (!string.IsNullOrEmpty(p)) paths.Add(p!);
                if (paths.Count > 0)
                    _ = Task.Run(() => Attachments.AttachmentService.StageFilesAsync(paths));
            }
            else if (Clipboard.ContainsText() && Clipboard.GetText() is string text && !string.IsNullOrWhiteSpace(text))
            {
                // Text opens in the composer PRE-FILLED (review/edit before it becomes a file - pastes
                // often need a trim or a line break), then attaches as a .txt with a chip, a token
                // estimate, and an @-mention.
                ComposeAndStage(text);
            }
            else
            {
                Log.Warn("attach: clipboard has no image, files, or text - copy something first (Win+Shift+S for a screenshot).");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"attach: paste failed: {ex.Message}");
        }
    }

    /// <summary>Open the multi-line composer (optionally pre-filled) and stage the result as a .txt attachment.</summary>
    private static void ComposeAndStage(string initialText)
    {
        try
        {
            var text = ComposeDialog.Prompt(initialText); // modal, UI thread; null = cancelled/empty
            if (text != null)
                _ = Task.Run(() => Attachments.AttachmentService.StageTextAsync(text));
        }
        catch (Exception ex)
        {
            Log.Warn($"attach: composer failed: {ex.Message}");
        }
    }

    private static byte[] EncodePng(BitmapSource bmp)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Re-render the staged-attachment chips (called on the dispatcher via OnAttachmentsChanged).</summary>
    private void RefreshAttachments()
    {
        var items = Attachments.AttachmentService.Snapshot();
        _attachChips.Children.Clear();
        _attachChips.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _attachClear.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Tray total: the estimated token cost of reading everything staged (images by Anthropic's
        // (w×h)/750 formula, text by bytes/4). "+" flags items with no estimate (e.g. PDFs).
        long estSum = 0;
        bool estPartial = false;
        foreach (var i in items)
        {
            if (i.EstTokens is long t) estSum += t; else estPartial = true;
        }
        _attachSummary.Visibility = items.Count > 0 && estSum > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (estSum > 0)
            _attachSummary.Text = $"≈ {Tok(estSum)}{(estPartial ? "+" : "")} tok when read";

        foreach (var item in items)
        {
            var it = item; // capture per chip
            var est = it.EstTokens is long e ? $"\n≈ {Tok(e)} tokens when read (estimate)" : "";
            var toolNote = it.NeedsTool ? "\nNot a format Claude reads directly - it will use a script/tool on it." : "";
            var name = new TextBlock
            {
                Text = (it.IsImage ? "🖼 " : it.NeedsTool ? "🧰 " : "📄 ") + it.FileName + (it.Sent ? "" : "  ⏳"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = it.MentionPath + est + toolNote + (it.Sent
                    ? "\nClick to @-mention it again."
                    : "\nStaged - sends when Claude connects. Click to retry."),
            };
            name.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
            name.MouseLeftButtonUp += (s, e) => _ = Task.Run(() => Attachments.AttachmentService.ResendAsync(it));

            var close = new TextBlock
            {
                Text = "✕",
                FontSize = 11.5,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Opacity = 0.65,
                ToolTip = it.WasCopied ? "Remove (deletes the staged copy)" : "Remove from the tray",
            };
            close.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
            close.MouseLeftButtonUp += (s, e) => Attachments.AttachmentService.Remove(it);

            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            inner.Children.Add(name);
            inner.Children.Add(close);
            _attachChips.Children.Add(new Border
            {
                Background = Chip,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 6, 4),
                Child = inner,
            });
        }
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
