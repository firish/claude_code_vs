using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Automation;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
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
    // Mid gray reads as secondary text on both dark and light themes.
    private static readonly Brush DimText = Freeze(Color.FromRgb(0x8A, 0x8A, 0x8A));
    // Translucent amber for the "tools didn't load" banner - reads on dark and light like the gray chips.
    private static readonly Brush WarnFill = Freeze(Color.FromArgb(30, 0xD0, 0x9A, 0x36));
    private static readonly Brush WarnBorder = Freeze(Color.FromArgb(96, 0xD0, 0x9A, 0x36));
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    private readonly Ellipse _dot;
    private readonly TextBlock _statusLine;
    private readonly TextBlock _endpointLine;
    private readonly Grid _usageGrid;
    private string? _usageSignature;
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
    private readonly CheckBox _usageToggle;
    private readonly RichTextBox _feed;
    private readonly WrapPanel _attachChips;
    private readonly Button _attachClear;
    private readonly TextBlock _attachSummary;
    private readonly DispatcherTimer _timer;
    private readonly FrameworkElement _editsCard;
    private readonly TextBlock _editsEmpty;
    private Edits.EditEntry[] _renderedEdits = Array.Empty<Edits.EditEntry>();
    private readonly ItemsControl _editsList;
    private readonly Border _activityCard;
    private readonly Ellipse _activityDot;
    private readonly TextBlock _activityHeader;
    private readonly TextBlock _activityPrompt;
    private readonly StackPanel _activityTools;
    private readonly TextBlock _activityTasksLabel;
    private readonly StackPanel _activityTasks;
    private readonly StackPanel _costRow;
    private readonly TextBlock _costText;
    private readonly Button _costButton;
    private bool _showCost;
    private readonly StackPanel _sessionsList;
    private readonly TextBox _sessionsFilter;
    private readonly FrameworkElement _sessionsCard;
    private readonly Border _tabActivity;
    private readonly Border _tabEdits;
    private readonly Border _tabSessions;
    private readonly RowDefinition _topRow;
    private readonly RowDefinition _tabsRow;

    private enum PanelTab { Activity, Edits, Sessions }

    /// <summary>The tab area never shrinks below this; the top section scrolls instead.</summary>
    private const double TabsMinHeight = 200;
    private const double SplitterHeight = 9;
    private int _sessionsLoadGen; // a slow listing must not overwrite a newer one

    public ClaudeToolWindowControl()
    {
        SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        FontScale.BindRoot(this);

        // Layout: [top section: status, toolbar, cards - scrolls when it doesn't fit]
        //         [splitter: drag to move the boundary, double-click to reset]
        //         [tabs: ACTIVITY / EDITS / SESSIONS - always at least TabsMinHeight tall]
        // The top row is Auto but capped (see ClampTopRow), so a tall top section scrolls instead of
        // pushing the tabs off the bottom of the panel.
        var root = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        _topRow = new RowDefinition { Height = GridLength.Auto };
        _tabsRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = TabsMinHeight };
        root.RowDefinitions.Add(_topRow);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(_tabsRow);
        var topStack = new StackPanel();

        // ---- Row 0: header (status pill) ----
        // No "Claude Code" text here: the tool window's own Caption (ClaudeToolWindow.cs) already shows
        // it in the tab/title bar, so a second copy here just duplicated the first line.
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        _dot = new Ellipse { Width = 9, Height = 9, Fill = DotIdle, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        _statusLine = Font(new TextBlock { VerticalAlignment = VerticalAlignment.Center }, 1.0);
        statusRow.Children.Add(_dot);
        statusRow.Children.Add(_statusLine);
        header.Children.Add(statusRow);

        _endpointLine = Font(new TextBlock { Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) }, 0.92);
        header.Children.Add(_endpointLine);

        // Subscription usage (5-hour / weekly / spend windows), relayed from the CLI's status line.
        // Collapsed until a session has reported it - API-key sessions never do.
        // One row per window: label | fill bar | "42% · resets 14:30" (built by RenderUsage).
        _usageGrid = new Grid
        {
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = Strings.UsageTooltip,
        };
        _usageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _usageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 40 });
        _usageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_usageGrid);
        Grid.SetRow(header, 0);

        // ---- Row 1: toolbar ----
        // One flat WrapPanel for all four buttons (no DockPanel right-docking): docking Clear/Output to
        // the right pre-claims width, which clipped the Launch buttons mid-text at narrow panel widths.
        // Wrapping keeps every button whole and reachable at any width; right-alignment wasn't worth that.
        // Icon buttons (VS's own KnownMonikers, themed) with the old labels as tooltips: one compact row
        // instead of two rows of wide text buttons and four checkboxes.
        var toolbar = new WrapPanel { Margin = new Thickness(0) };
        toolbar.Children.Add(MakeIconButton(KnownMonikers.Run, Strings.BtnLaunch, () => { _ = BridgeStatus.LaunchAction?.Invoke(); }));
        toolbar.Children.Add(MakeIconButton(KnownMonikers.Console, Strings.BtnExternalConsole + "\n" + Strings.TipExternalConsole,
            () => { _ = BridgeStatus.LaunchExternalAction?.Invoke(); }));
        toolbar.Children.Add(MakeIconButton(KnownMonikers.Output, Strings.BtnOutput, () => { try { BridgeStatus.ShowOutputAction?.Invoke(); } catch { } }));

        // The four toggles live in a popup under a gear button. The checkboxes themselves (and all their
        // state syncing in UpdateStatus) are unchanged - only where they sit. The gear carries an amber dot
        // while any SAFETY toggle is on, so a hidden "run wild" is never invisible.
        var toggles = new StackPanel { Margin = new Thickness(0) };
        _autoAccept = new CheckBox
        {
            Content = Strings.ToggleAutoAccept,
            ToolTip = Strings.TipAutoAccept,
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
            Content = Strings.ToggleAllowDrive,
            ToolTip = Strings.TipAllowDrive,
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
            Content = Strings.ToggleAllowCapture,
            ToolTip = Strings.TipAllowCapture,
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
            Content = Strings.ToggleNotify,
            ToolTip = Strings.TipNotify,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 2),
            IsChecked = BridgeStatus.NotifyEnabled,
        };
        _notify.Checked += (s, e) => BridgeStatus.SetNotifyEnabled(true);
        _notify.Unchecked += (s, e) => BridgeStatus.SetNotifyEnabled(false);
        _notify.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        toggles.Children.Add(_notify);

        // Usage row: our statusLine relays the CLI's subscription limits here. A convenience with a cost
        // (one PowerShell per status-line refresh), so it can be turned off - which also puts the user's
        // own status line back. Re-applied to the workspace right away; new sessions pick it up.
        _usageToggle = new CheckBox
        {
            Content = Strings.ToggleUsage,
            ToolTip = Strings.TipUsageToggle,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = BridgeStatus.UsageStatusLine,
        };
        _usageToggle.Checked += (s, e) => ApplyUsageToggle(true);
        _usageToggle.Unchecked += (s, e) => ApplyUsageToggle(false);
        _usageToggle.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        toggles.Children.Add(_usageToggle);

        foreach (var box in new[] { _autoAccept, _allowDrive, _allowCapture, _notify, _usageToggle })
            box.Margin = new Thickness(0, 3, 0, 3);
        toolbar.Children.Add(MakeSettingsButton(toggles));

        var toolbarStack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        toolbarStack.Children.Add(toolbar);
        Grid.SetRow(toolbarStack, 1);

        // ---- Row 2: "extra tools didn't load" banner (collapsed unless the PULL MCP servers failed) ----
        // Surfaces the otherwise-silent gap where the IDE WebSocket connected but vs-debug/vs-semantic/
        // tests never loaded (Claude launched outside the workspace, or project servers unapproved).
        var warnStack = new StackPanel();
        _toolsWarningTitle = Font(new TextBlock
        {
            Text = Strings.BannerConfigTitle,
            FontWeight = FontWeights.SemiBold,
            Foreground = WarnText,
            TextWrapping = TextWrapping.Wrap,
        }, 1.0);
        warnStack.Children.Add(_toolsWarningTitle);
        _toolsWarningText = Font(new TextBlock { Opacity = 0.85, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) }, 0.96);
        warnStack.Children.Add(_toolsWarningText);
        var warnButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        warnButtons.Children.Add(MakeButton(Strings.BtnRelaunch, () => { _ = BridgeStatus.RelaunchAction?.Invoke(); }));
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
        _editsLine = Font(new TextBlock { TextWrapping = TextWrapping.Wrap }, 1.0);
        _debugLine = Font(new TextBlock { Opacity = 0.9, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }, 1.0);
        _latestLine = Font(new TextBlock { Opacity = 0.9, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }, 1.0);
        _sessionLine = Font(new TextBlock { Opacity = 0.9, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }, 1.0);

        // Cost is an estimate, so it's gated behind a toggle rather than shown by default.
        _costButton = MakeButton(Strings.BtnShowCost, ToggleCost);
        _costText = Font(new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed }, 1.0);
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

        // ---- Row 4: live activity (collapsed until the first turn) ----
        // What Claude is doing right now: turn state + elapsed, the tool call in flight, its task list.
        // Fed by the transcript tail (Activity/TranscriptTail), not by a hook per tool call. This is
        // most of what a chat pane earns its space with DURING a turn, without building one.
        var activityHead = new StackPanel { Orientation = Orientation.Horizontal };
        _activityDot = new Ellipse { Width = 8, Height = 8, Fill = DotIdle, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 6, 0) };
        _activityHeader = Font(new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 1.0);
        activityHead.Children.Add(_activityDot);
        activityHead.Children.Add(_activityHeader);
        _activityPrompt = Font(new TextBlock { Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(15, 1, 0, 0) }, 0.92);
        _activityTools = new StackPanel { Margin = new Thickness(15, 3, 0, 0) };
        _activityTasksLabel = Font(new TextBlock { Opacity = 0.55, FontWeight = FontWeights.SemiBold, Margin = new Thickness(15, 6, 0, 2) }, 0.83);
        _activityTasks = new StackPanel { Margin = new Thickness(15, 0, 0, 0) };
        var activityStack = new StackPanel();
        activityStack.Children.Add(activityHead);
        activityStack.Children.Add(_activityPrompt);
        activityStack.Children.Add(_activityTools);
        activityStack.Children.Add(_activityTasksLabel);
        activityStack.Children.Add(_activityTasks);
        _activityCard = new Border
        {
            Background = Chip,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
            Child = activityStack,
        };
        Grid.SetRow(_activityCard, 4);

        // ---- Row 5: pending diffs (collapsed when none) ----
        _pendingText = Font(new TextBlock { TextWrapping = TextWrapping.Wrap }, 1.0);
        _pendingCard = new Border
        {
            Background = Chip,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
            Child = _pendingText,
        };
        Grid.SetRow(_pendingCard, 5);

        // ---- Edits this turn: the EDITS tab ----
        // Issue #44: the CLI prints "changed Foo.cs:1234" in the terminal and VS's terminal has no link
        // provider we can hook, so the jump targets live here instead. Scoped to the current turn (the
        // UserPromptSubmit hook marks the list stale, the next edit replaces it). One row per HUNK, which is
        // finer than the CLI's one per edit - a big turn easily reaches dozens, which is why the list lives
        // in its own scrolling tab instead of growing the panel's top section past the screen.
        _editsEmpty = Font(new TextBlock { Text = Strings.EditsEmpty, Opacity = 0.6, Margin = new Thickness(2, 2, 0, 2) }, 0.92);
        _editsList = new ItemsControl { Margin = new Thickness(0, 0, 0, 8) };
        var editsStack = new StackPanel();
        // The stats card (edit decisions, debugger use, tokens/cost) heads this tab rather than the panel's
        // top section: it is reference material, not something to keep in view while working.
        editsStack.Children.Add(statsCard);
        // Which turn's edits to show: the current one, or one of the last few (BridgeStatus.EditHistory).
        _editsTurnButton = MakeButton("", () => ShowTurnPicker());
        _editsTurnButton.HorizontalAlignment = HorizontalAlignment.Left;
        _editsTurnButton.Margin = new Thickness(0, 0, 0, 6);
        _editsTurnButton.ToolTip = Strings.TipEditsTurn;
        _editsTurnButton.Visibility = Visibility.Collapsed; // until RefreshEdits has a label and an earlier turn
        editsStack.Children.Add(_editsTurnButton);
        editsStack.Children.Add(_editsEmpty);
        editsStack.Children.Add(_editsList);
        _editsCard = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = editsStack };

        // ---- Row 7: attachments (drop/paste target + staged chips) ----
        // Screenshots can't be pasted into the CLI on Windows (open upstream gap), so the panel is the
        // paste/drop point: stage the file, then push an at_mentioned so the reference lands in the
        // CLI's composer with no path typing. Chips show what's staged; × removes, click re-mentions.
        var attachHint = Font(new TextBlock
        {
            Text = Strings.AttachHint,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
        }, 0.96);
        // WrapPanel, not a horizontal StackPanel, for the same reason as the toolbar above: at a docked
        // panel width the hint plus Paste/Compose/Clear overflow, and a StackPanel just runs off the edge
        // (the card grew a horizontal scrollbar and Compose sat half outside it). Wrapping keeps each
        // button whole and clickable at any width.
        var attachHeader = new WrapPanel();
        attachHeader.Children.Add(attachHint);
        var pasteBtn = MakeButton(Strings.BtnPaste, PasteFromClipboard);
        pasteBtn.Margin = new Thickness(6, 0, 6, 0);
        pasteBtn.ToolTip = Strings.TipPaste;
        attachHeader.Children.Add(pasteBtn);
        var composeBtn = MakeButton(Strings.BtnCompose, () => ComposeAndStage(""));
        composeBtn.Margin = new Thickness(0, 0, 6, 0);
        composeBtn.ToolTip = Strings.TipCompose;
        attachHeader.Children.Add(composeBtn);
        _attachClear = MakeButton(Strings.BtnClear, Attachments.AttachmentService.Clear);
        _attachClear.Visibility = Visibility.Collapsed;
        attachHeader.Children.Add(_attachClear);
        // A live "what will this cost" readout for the staged set - same estimate language as the cost row.
        _attachSummary = Font(new TextBlock
        {
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        }, 0.96);
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
        Grid.SetRow(attachCard, 7);

        // Ctrl+V anywhere in the panel = the Paste image button.
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, (s, e) => PasteFromClipboard()));

        // Tab switching from the keyboard. Ctrl+PageDown/PageUp always reach the panel; Ctrl+Tab only when VS
        // doesn't claim it first for its own window switcher.
        PreviewKeyDown += (s, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool back = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (key == Key.Tab) { CycleTab(back ? -1 : 1); e.Handled = true; }
            else if (key == Key.PageDown) { CycleTab(1); e.Handled = true; }
            else if (key == Key.PageUp) { CycleTab(-1); e.Handled = true; }
        };

        // ---- Row 8: sessions (past CLI sessions of this workspace; double-click = resume) ----
        // The CLI owns the session store (~/.claude/projects/...); we only list it and launch
        // `claude --resume <id>` / `--continue` in a new terminal. Collapsed by default: zero panel height
        // until the user wants it.
        var sessionsBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        sessionsBar.Children.Add(MakeButton(Strings.BtnSessionsNew, () => { _ = BridgeStatus.LaunchSessionAction?.Invoke(""); }));
        sessionsBar.Children.Add(MakeButton(Strings.BtnSessionsRefresh, RefreshSessions));
        // Filter by title; re-renders the last listing without touching the disk.
        _sessionsFilter = Font(new TextBox { Width = 160, Padding = new Thickness(3, 2, 3, 2), VerticalContentAlignment = VerticalAlignment.Center }, 0.96);
        _sessionsFilter.SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
        _sessionsFilter.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
        _sessionsFilter.SetResourceReference(TextBox.CaretBrushProperty, VsBrushes.WindowTextKey);
        _sessionsFilter.SetResourceReference(BorderBrushProperty, VsBrushes.ComboBoxBorderKey);
        var filterHint = Font(new TextBlock { Text = Strings.SessionsFilterHint, Opacity = 0.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }, 0.96);
        filterHint.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
        _sessionsFilter.TextChanged += (s, e) =>
        {
            filterHint.Visibility = _sessionsFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderSessions(_lastSessions, _lastActiveId);
        };
        var filterBox = new Grid { VerticalAlignment = VerticalAlignment.Center };
        filterBox.Children.Add(_sessionsFilter);
        filterBox.Children.Add(filterHint);
        sessionsBar.Children.Add(filterBox);
        _sessionsList = new StackPanel();
        var sessionsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _sessionsList,
        };
        var sessionsPane = new DockPanel { LastChildFill = true, Focusable = true, FocusVisualStyle = null };
        sessionsPane.PreviewKeyDown += OnSessionsKeyDown;
        DockPanel.SetDock(sessionsBar, Dock.Top);
        sessionsPane.Children.Add(sessionsBar);
        sessionsPane.Children.Add(sessionsScroll);

        // ---- Row 8: tabs - Activity (curated feed) and Sessions share the one stretchy row ----
        // A read-only RichTextBox, not a ListBox: the text can be selected across lines and copied
        // (Ctrl+C / context menu), which a list of TextBlocks can't do. One Paragraph per line keeps the
        // per-level colors. Wrapping is a context-menu toggle, off by default (ApplyFeedWrap).
        _feed = Font(new RichTextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(1),
            BorderBrush = Divider,
            Background = Brushes.Transparent,
            FontFamily = Mono,
            Padding = new Thickness(2),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Document = new FlowDocument { PagePadding = new Thickness(0) },
        }, 0.96);
        _feed.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        var feedMenu = MakeThemedMenu();
        var copyItem = new MenuItem { Header = Strings.FeedMenuCopy, Command = ApplicationCommands.Copy, CommandTarget = _feed };
        var selectAllItem = new MenuItem { Header = Strings.FeedMenuSelectAll, Command = ApplicationCommands.SelectAll, CommandTarget = _feed };
        feedMenu.Items.Add(copyItem);
        feedMenu.Items.Add(selectAllItem);
        var wrapItem = new MenuItem { Header = Strings.FeedMenuWrap, IsCheckable = true, IsChecked = BridgeStatus.FeedWrap };
        wrapItem.Click += (s, e) =>
        {
            BridgeStatus.SetFeedWrap(wrapItem.IsChecked);
            ApplyFeedWrap();
        };
        feedMenu.Items.Add(wrapItem);
        // Timestamps and the warnings-only filter re-render the whole feed from the log snapshot.
        var timeItem = new MenuItem { Header = Strings.FeedMenuTimestamps, IsCheckable = true };
        timeItem.Click += (s, e) => { BridgeStatus.SetFeedTimestamps(timeItem.IsChecked); ReloadFeed(); };
        feedMenu.Items.Add(timeItem);
        var warnItem = new MenuItem { Header = Strings.FeedMenuWarningsOnly, IsCheckable = true };
        warnItem.Click += (s, e) => { BridgeStatus.SetFeedWarningsOnly(warnItem.IsChecked); ReloadFeed(); };
        feedMenu.Items.Add(warnItem);
        var clearItem = new MenuItem { Header = Strings.BtnClear };
        clearItem.Click += (s, e) => ClearFeed();
        feedMenu.Items.Add(clearItem);
        feedMenu.Opened += (s, e) =>
        {
            wrapItem.IsChecked = BridgeStatus.FeedWrap;
            timeItem.IsChecked = BridgeStatus.FeedTimestamps;
            warnItem.IsChecked = BridgeStatus.FeedWarningsOnly;
        };
        _feed.ContextMenu = feedMenu;
        ApplyFeedWrap();

        // Hand-rolled tab strip rather than TabControl: the stock one ignores the VS theme in a tool
        // window; this is two themed headers with an accent underline, like the Output window's tabs.
        _tabActivity = MakeTabHeader(Strings.FeedLabel, () => SelectTab(PanelTab.Activity), out _tabActivityBadge);
        _tabEdits = MakeTabHeader(Strings.TabEdits, () => SelectTab(PanelTab.Edits), out _tabEditsBadge);
        _tabSessions = MakeTabHeader(Strings.SessionsHeader, () => SelectTab(PanelTab.Sessions), out _tabSessionsBadge);
        _tabActivityBadge.Text = "●";
        _tabSessionsBadge.Foreground = OpenDot;
        var tabStrip = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        tabStrip.Children.Add(_tabSessions);
        tabStrip.Children.Add(_tabEdits);
        tabStrip.Children.Add(_tabActivity);

        _sessionsCard = sessionsPane;
        var tabBody = new Grid();
        tabBody.Children.Add(_feed);
        tabBody.Children.Add(_editsCard);
        tabBody.Children.Add(sessionsPane);
        var tabHost = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(tabStrip, Dock.Top);
        tabHost.Children.Add(tabStrip);
        tabHost.Children.Add(tabBody);
        Grid.SetRow(tabHost, 2);
        // The last tab the user picked (SESSIONS, the first tab, on a fresh install); its content loads
        // once the panel is wired (Attach).
        SelectTab(Enum.TryParse(PanelSettings.GetString(PanelSettings.SelectedTabKey, ""), out PanelTab savedTab) ? savedTab : PanelTab.Sessions);
        _layoutRestored = true;

        topStack.Children.Add(header);
        topStack.Children.Add(toolbarStack);
        topStack.Children.Add(_toolsWarningCard);
        topStack.Children.Add(_activityCard);
        topStack.Children.Add(_pendingCard);
        topStack.Children.Add(attachCard);
        var topScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = topStack,
        };
        Grid.SetRow(topScroll, 0);

        // The boundary between the two is the user's to move; double-click gives it back to Auto,
        // and whichever mode is current (Auto or a dragged height) is what the next VS session starts in.
        var splitter = new GridSplitter
        {
            Height = SplitterHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeNS,
            ToolTip = Strings.TipSplitter,
            Template = SplitterTemplate(),
        };
        splitter.MouseDoubleClick += (s, e) =>
        {
            _topRow.Height = GridLength.Auto;
            _tabsRow.Height = new GridLength(1, GridUnitType.Star);
            ClampTopRow(root);
            PanelSettings.SetString(PanelSettings.TopHeightKey, "");
        };
        splitter.DragCompleted += (s, e) =>
        {
            ClampTopRow(root);
            // A double-click is also a zero-length drag: its second mouse-up lands here right after the
            // double-click handler restored Auto. Only a real move (which turns the row into a fixed
            // size) may persist a height, or Auto would never survive a restart.
            if (e.Canceled || _topRow.Height.IsAuto) return;
            // Remember the boundary (the top section's height) across VS restarts.
            PanelSettings.SetString(PanelSettings.TopHeightKey,
                _topRow.ActualHeight.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        };
        if (double.TryParse(PanelSettings.GetString(PanelSettings.TopHeightKey, ""), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var savedTop) && savedTop > 0)
            _topRow.Height = new GridLength(savedTop);
        Grid.SetRow(splitter, 1);

        root.Children.Add(topScroll);
        root.Children.Add(splitter);
        root.Children.Add(tabHost);
        root.SizeChanged += (s, e) => ClampTopRow(root);
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
        Activity.AgentActivity.Changed += OnChanged;
        Attachments.AttachmentService.Changed += OnAttachmentsChanged;
        BridgeStatus.SessionsChanged += OnSessionsChanged;
        VSColorTheme.ThemeChanged += OnThemeChanged;
        ApplyImageTheme();
        // Re-sync from current state (we may have missed events while hidden).
        ReloadFeed(markUnread: false);
        UpdateStatus();
        RefreshAttachments();
        if (_sessionsCard.Visibility == Visibility.Visible) RefreshSessions();
        _timer.Start();
    }

    private void Detach()
    {
        if (!_wired) return;
        _wired = false;
        BridgeStatus.Logged -= OnLogged;
        BridgeStatus.Changed -= OnChanged;
        Activity.AgentActivity.Changed -= OnChanged;
        Attachments.AttachmentService.Changed -= OnAttachmentsChanged;
        BridgeStatus.SessionsChanged -= OnSessionsChanged;
        VSColorTheme.ThemeChanged -= OnThemeChanged;
        _timer.Stop();
    }

    // The WPF Dispatcher is the correct way to marshal into a WPF control from the background WS
    // thread; VSTHRD001 prefers JTF but doesn't apply to plain WPF controls.
#pragma warning disable VSTHRD001
    private void OnLogged(BridgeStatus.LogEntry entry)
        => _ = Dispatcher.BeginInvoke(new Action(() => AddFeedLine(entry, markUnread: true)));

    private void OnChanged() => _ = Dispatcher.BeginInvoke(new Action(UpdateStatus));

    // Only re-list while the Sessions tab is showing; selecting the tab lists afresh anyway.
    private void OnSessionsChanged() => _ = Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_sessionsCard.Visibility == Visibility.Visible) RefreshSessions();
    }));

    private void OnAttachmentsChanged() => _ = Dispatcher.BeginInvoke(new Action(RefreshAttachments));
#pragma warning restore VSTHRD001

    private void AddFeedLine(BridgeStatus.LogEntry entry, bool markUnread)
    {
        var level = entry.Level;
        // Raw JSON frames and notification noise stay in the Output pane; keep the panel readable.
        if (level == LogLevel.Frame || level == LogLevel.Event) return;
        if (entry.Time < _feedClearedAt) return;
        if (BridgeStatus.FeedWarningsOnly && level != LogLevel.Warn && level != LogLevel.Error) return;

        // A warning/error arriving while another tab is up marks the ACTIVITY tab.
        if (markUnread && (level == LogLevel.Warn || level == LogLevel.Error) && _feed.Visibility != Visibility.Visible)
            MarkActivityUnread(level == LogLevel.Error);

        // Fixed 24h stamp, culture-invariant like the log text itself (bug reports stay greppable).
        string text = BridgeStatus.FeedTimestamps
            ? entry.Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + entry.Text
            : entry.Text;
        var p = new Paragraph(new Run(text)) { Margin = new Thickness(0) };
        if (level == LogLevel.Error) p.Foreground = ErrText;
        else if (level == LogLevel.Warn) p.Foreground = WarnText;
        // Info lines inherit the RichTextBox's themed foreground.

        var blocks = _feed.Document.Blocks;
        blocks.Add(p);
        while (blocks.Count > 400) blocks.Remove(blocks.FirstBlock);

        // No-wrap needs the document as wide as its longest line (a FlowDocument has no "auto width").
        double width = MeasureFeedLine(text);
        if (width > _feedMaxLineWidth)
        {
            _feedMaxLineWidth = width;
            if (!BridgeStatus.FeedWrap) ApplyFeedWrap();
        }

        // Follow the tail, except while the user has text selected (they're about to copy it). A bulk
        // reload scrolls once at its end instead of once per line.
        if (!_feedBulkLoading && _feed.Selection.IsEmpty) _feed.ScrollToEnd();
    }

    private bool _feedBulkLoading;

    /// <summary>Clear = hide everything logged so far; a reload (filter/timestamps toggle, re-show)
    /// must not bring it back.</summary>
    private void ClearFeed()
    {
        _feedClearedAt = DateTime.Now;
        ResetFeed();
    }

    private DateTime _feedClearedAt = DateTime.MinValue;

    private void ResetFeed()
    {
        _feed.Document.Blocks.Clear();
        _feedMaxLineWidth = 0;
        ApplyFeedWrap();
    }

    /// <summary>Re-render the feed from the log snapshot (after a display option changed, or on re-show).</summary>
    private void ReloadFeed(bool markUnread = false)
    {
        ResetFeed();
        _feedBulkLoading = true;
        try
        {
            foreach (var entry in BridgeStatus.LogSnapshot())
                AddFeedLine(entry, markUnread);
        }
        finally { _feedBulkLoading = false; }
        _feed.ScrollToEnd();
    }

    private double _feedMaxLineWidth;

    /// <summary>
    /// Wrap on: the document follows the viewport width (PageWidth = auto), no horizontal scroll.
    /// Wrap off: the document is as wide as the longest line seen, with a horizontal scrollbar - lines
    /// stay one per row, which is easier to scan in a log.
    /// </summary>
    private void ApplyFeedWrap()
    {
        if (BridgeStatus.FeedWrap)
        {
            _feed.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _feed.Document.PageWidth = double.NaN;
        }
        else
        {
            _feed.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _feed.Document.PageWidth = Math.Max(100, _feedMaxLineWidth + 24);
        }
    }

    private double MeasureFeedLine(string text)
    {
        try
        {
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(_feed.FontFamily, _feed.FontStyle, _feed.FontWeight, _feed.FontStretch),
                _feed.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(_feed).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }
        catch { return 0; }
    }

    /// <summary>
    /// One row per usage window:  "5-hour  [■■■■□□□□□□]  42%  resets 14:30". Runs on every status tick (1s),
    /// which also drops a window the moment its reset time passes. ≥80% reads amber, ≥95% red.
    /// </summary>
    private void RenderUsage()
    {
        var usage = BridgeStatus.Limits;
        if (usage == null || !usage.HasCurrent || !BridgeStatus.UsageStatusLine)
        {
            _usageGrid.Visibility = Visibility.Collapsed;
            _usageSignature = null;
            return;
        }

        // Runs every second: rebuild only when what the rows say changes (values, a window expiring, or
        // the day rolling over, which turns "Sun 1:00" into "1:00").
        string Sig(UsageWindow? w) => w == null || !w.IsCurrent ? "-" : $"{Math.Round(w.Percent)}@{w.ResetsAt?.ToUnixTimeSeconds()}";
        var signature = $"{Sig(usage.FiveHour)}|{Sig(usage.SevenDay)}|{Sig(usage.Spend)}|{DateTime.Today:yyyyMMdd}";
        if (signature == _usageSignature) return;
        _usageSignature = signature;

        _usageGrid.Children.Clear();
        _usageGrid.RowDefinitions.Clear();
        void Add(UsageWindow? window, string label)
        {
            if (window == null || !window.IsCurrent) return;
            int row = _usageGrid.RowDefinitions.Count;
            _usageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Brush level = window.Percent >= 95 ? ErrText : window.Percent >= 80 ? WarnText : OpenDot;

            var name = Font(new TextBlock { Text = label, Margin = new Thickness(0, 1, 8, 1), VerticalAlignment = VerticalAlignment.Center }, 1.0);
            name.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
            Grid.SetRow(name, row);
            Grid.SetColumn(name, 0);
            _usageGrid.Children.Add(name);

            // Fill bar: two star columns in proportion used : remaining, on a translucent track.
            var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            double used = Math.Max(0.0001, window.Percent), left = Math.Max(0.0001, 100 - window.Percent);
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(used, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left, GridUnitType.Star) });
            var track = new Border { Background = Chip, CornerRadius = new CornerRadius(3) };
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            bar.Children.Add(new Border { Background = level, CornerRadius = new CornerRadius(3) });
            Grid.SetRow(bar, row);
            Grid.SetColumn(bar, 1);
            _usageGrid.Children.Add(bar);

            var value = Font(new TextBlock { VerticalAlignment = VerticalAlignment.Center }, 1.0);
            value.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
            var pct = new Run(Math.Round(window.Percent) + "%") { FontWeight = FontWeights.SemiBold };
            if (window.Percent >= 80) pct.Foreground = level;
            value.Inlines.Add(pct);
            if (window.ResetsAt is { } resets)
                value.Inlines.Add(new Run("  " + string.Format(Strings.UsageResetsFormat, ResetLabel(resets))) { Foreground = DimText });
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 2);
            _usageGrid.Children.Add(value);
        }
        Add(usage.FiveHour, Strings.UsageFiveHourLabel);
        Add(usage.SevenDay, Strings.UsageSevenDayLabel);
        Add(usage.Spend, Strings.UsageSpendLabel);
        _usageGrid.Visibility = Visibility.Visible;
    }

    /// <summary>Local time; today = just the time, otherwise the weekday too.</summary>
    private static string ResetLabel(DateTimeOffset resets)
    {
        var local = resets.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("t", UiCulture)
            : local.ToString("ddd ", UiCulture) + local.ToString("t", UiCulture);
    }

    /// <summary>VS's display language (as the strings use), for every date/time the panel formats.</summary>
    private static System.Globalization.CultureInfo UiCulture =>
        Strings.Culture ?? System.Globalization.CultureInfo.CurrentUICulture;

    private void UpdateStatus()
    {
        RenderUsage();
        _syncingToggles = true;
        try
        {
            // Run-wild reflects the CLI session's own mode (issue #17 follow-up): while the CLI
            // pre-approves edits (acceptEdits / auto / dontAsk / bypassPermissions - e.g. shift+tab in the
            // terminal), the checkbox shows checked and DISABLED - unchecking it could not re-gate
            // edits the user already approved at the CLI level, so the UI must not offer it. When the
            // session mode is default (or no session), the checkbox is the user's own bridge-side
            // toggle exactly as before. The checked-at-Launch direction starts the CLI in acceptEdits.
            if (BridgeStatus.CliEditsPreApproved)
            {
                _autoAccept.IsChecked = true;
                _autoAccept.IsEnabled = false;
                _autoAccept.ToolTip = string.Format(Strings.TipAutoAcceptLocked, BridgeStatus.CliPermissionMode);
            }
            else
            {
                _autoAccept.IsEnabled = true;
                _autoAccept.ToolTip = Strings.TipAutoAccept;
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
        if (_usageToggle.IsChecked != BridgeStatus.UsageStatusLine)
            _usageToggle.IsChecked = BridgeStatus.UsageStatusLine;

        // The toggles are tucked under the gear - flag it while anything that widens what Claude may do is on.
        bool anySafetyToggleOn = BridgeStatus.AutoAcceptEdits || BridgeStatus.CliEditsPreApproved
                                 || BridgeStatus.AllowDebuggerDrive || BridgeStatus.AllowScreenCapture;
        _gearBadge.Visibility = anySafetyToggleOn ? Visibility.Visible : Visibility.Collapsed;

        // Status pill.
        if (BridgeStatus.Port is not int port)
        {
            _dot.Fill = DotIdle;
            _statusLine.Text = Strings.StatusStarting;
            _endpointLine.Text = "";
        }
        else if (BridgeStatus.Connected)
        {
            _dot.Fill = DotConnected;
            var up = BridgeStatus.ConnectedSince is DateTime since ? "  ·  " + Uptime(since) : "";
            _statusLine.Text = Strings.StatusConnected + up;
            _endpointLine.Text = string.Format(Strings.EndpointFormat, port, Workspace());
        }
        else
        {
            _dot.Fill = DotWaiting;
            _statusLine.Text = Strings.StatusWaiting;
            _endpointLine.Text = string.Format(Strings.EndpointFormat, port, Workspace());
        }

        // Stats card.
        _editsLine.Text = string.Format(Strings.StatsEdits, BridgeStatus.EditsAccepted, BridgeStatus.EditsRejected);
        _debugLine.Text = string.Format(Strings.StatsDebug, BridgeStatus.DebugInspects, BridgeStatus.DebugDrives);

        // Tokens are always shown; cost (an estimate) sits behind a toggle. We show the latest call
        // and the cumulative session separately, since the transcript spans the whole conversation.
        var latest = BridgeStatus.Latest;
        var session = BridgeStatus.Session;
        var model = string.IsNullOrEmpty(BridgeStatus.Model) ? "" : "  ·  " + ShortModel(BridgeStatus.Model!);
        _latestLine.Text =
            string.Format(Strings.StatsLatest, Tok(latest.Input), Tok(latest.Output), Tok(latest.CacheRead));
        _sessionLine.Text =
            string.Format(Strings.StatsSession, Tok(session.Input), Tok(session.Output), Tok(session.CacheRead)) +
            (BridgeStatus.Turns > 0 ? string.Format(Strings.StatsTurns, BridgeStatus.Turns, model) : "");
        _latestLine.Opacity = BridgeStatus.HasUsage ? 0.9 : 0.55;
        _sessionLine.Opacity = BridgeStatus.HasUsage ? 0.9 : 0.55;

        if (BridgeStatus.HasUsage)
        {
            _costRow.Visibility = Visibility.Visible;
            _costButton.Content = _showCost ? Strings.BtnHideCost : Strings.BtnShowCost;
            _costText.Visibility = _showCost ? Visibility.Visible : Visibility.Collapsed;
            if (_showCost)
                _costText.Text = string.Format(Strings.CostFormat, session.CostUsd, latest.CostUsd);
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
            _pendingText.Text = string.Format(Strings.PendingFormat, string.Join(",  ", names));
            _pendingCard.Visibility = Visibility.Visible;
        }

        RefreshEdits();
        RefreshActivity();

        // The two-variant warning banner. Variant 1 ("hooks only"): a session's hook POSTs are reaching
        // the bridge but the IDE WebSocket never connected - claude was launched outside the extension,
        // and /ide from that terminal lights up the diff/selection channel. Variant 2 ("config not
        // loaded"): connected, but no /mcp handshake - the session never loaded the workspace's .claude
        // configuration at all. Only meaningful in their respective connection states.
        if (BridgeStatus.HooksOnlyWarning && !BridgeStatus.Connected)
        {
            _toolsWarningTitle.Text = Strings.BannerHooksOnlyTitle;
            _toolsWarningText.Text = Strings.BannerHooksOnlyText;
            _toolsWarningCard.Visibility = Visibility.Visible;
        }
        else if (BridgeStatus.ToolsWarning && BridgeStatus.Connected)
        {
            _toolsWarningTitle.Text = Strings.BannerConfigTitle;
            // {0} = the optional " (workspace path)" parenthetical after "the workspace".
            _toolsWarningText.Text = string.Format(Strings.BannerConfigText,
                string.IsNullOrEmpty(BridgeStatus.Workspace) ? "" : $" ({BridgeStatus.Workspace})");
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
                _ = Task.Run(async () =>
                {
                    await Attachments.AttachmentService.StageTextAsync(text);
                    FocusClaude(); // Attach = "I'm done, send it" - put Enter where it works
                });
        }
        catch (Exception ex)
        {
            Log.Warn($"attach: composer failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Focus the claude input, so Enter sends the reference just pushed. Called ONLY where the user's
    /// intent to send is unambiguous: a chip click (re-mention) and the composer's Attach. Raw
    /// paste/drop deliberately keeps focus in the panel - people stage several files in a row, and
    /// yanking focus after the first would fight them (see docs/QOL.md "Where focus goes").
    /// </summary>
    private static void FocusClaude()
    {
        var focus = BridgeStatus.FocusClaudeAction;
        if (focus != null) _ = focus();
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
    /// <summary>
    /// Render this turn's jump targets (issue #44): one row per changed hunk, newest last, each opening
    /// the file at the change through the same Navigator the openFile tool uses. Hidden entirely when
    /// there is nothing to show, so it costs no panel height in the common case.
    /// </summary>
    /// <summary>List the workspace's saved sessions off-thread (files can be large; titles are cached by
    /// size+mtime), then render on the dispatcher. Marks the session the hooks last reported as active.</summary>
    private void RefreshSessions()
    {
        int gen = ++_sessionsLoadGen;
        string? workspace = BridgeStatus.Workspace;
        string? activeId = Sessions.SessionStore.IdFromTranscriptPath(Activity.TranscriptTail.CurrentPath);
        _ = Task.Run(() =>
        {
            Sessions.ExternalSessions.Scan(); // so the Delete item knows about sessions other CLIs hold
            var list = Sessions.SessionStore.List(workspace);
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (gen != _sessionsLoadGen) return;
                RenderSessions(list, activeId);
            }));
        });
    }

    private System.Collections.Generic.List<Sessions.SessionInfo> _lastSessions = new();
    private string? _lastActiveId;
    private string? _selectedSessionId;
    private readonly System.Collections.Generic.List<(Sessions.SessionInfo Session, Border Row)> _sessionRows = new();
    private readonly System.Collections.Generic.Dictionary<string, string> _previewCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsSessionOpen(string id) =>
        Sessions.SessionWindows.Check(id) != Sessions.SessionWindows.Presence.NotOpen || Sessions.ExternalSessions.IsOpen(id);

    /// <summary>Render the (filtered) list, grouped Today / Yesterday / This week / Earlier.</summary>
    private void RenderSessions(System.Collections.Generic.List<Sessions.SessionInfo> list, string? activeId)
    {
        _lastSessions = list;
        _lastActiveId = activeId;
        _sessionsList.Children.Clear();
        _sessionRows.Clear();

        // Tab badge: how many of these sessions are open right now (here or in any other CLI). One registry
        // prune + one scan snapshot for the whole list, not a process check per session.
        var openIds = Sessions.SessionWindows.OpenIds();
        openIds.UnionWith(Sessions.ExternalSessions.LastScan());
        int open = list.Count(s => openIds.Contains(s.Id));
        _tabSessionsBadge.Text = "● " + open;
        _tabSessionsBadge.ToolTip = string.Format(Strings.SessionsOpenCountFormat, open);
        _tabSessionsBadge.Visibility = open > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (list.Count == 0)
        {
            _sessionsList.Children.Add(Font(new TextBlock { Text = Strings.SessionsEmpty, Opacity = 0.6, Margin = new Thickness(2, 2, 0, 2) }, 0.92));
            return;
        }

        var filter = _sessionsFilter.Text.Trim();
        var shown = filter.Length == 0
            ? list
            : list.Where(s => s.Title.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        if (shown.Count == 0)
        {
            _sessionsList.Children.Add(Font(new TextBlock { Text = Strings.SessionsNoMatch, Opacity = 0.6, Margin = new Thickness(2, 2, 0, 2) }, 0.92));
            return;
        }

        var today = DateTime.Today;
        string GroupOf(DateTime when) =>
            when.Date == today ? Strings.SessionsGroupToday
            : when.Date == today.AddDays(-1) ? Strings.SessionsGroupYesterday
            : when.Date > today.AddDays(-7) ? Strings.SessionsGroupThisWeek
            : Strings.SessionsGroupEarlier;
        string? group = null;
        foreach (var s in shown)
        {
            var g = GroupOf(s.LastWriteLocal);
            if (g != group)
            {
                group = g;
                var head = Font(new TextBlock { Text = g, FontWeight = FontWeights.SemiBold, Opacity = 0.55, Margin = new Thickness(4, _sessionRows.Count == 0 ? 2 : 8, 0, 2) }, 0.83);
                head.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
                _sessionsList.Children.Add(head);
            }
            var row = BuildSessionRow(s, activeId);
            _sessionRows.Add((s, row));
            _sessionsList.Children.Add(row);
        }
        if (_selectedSessionId != null && !_sessionRows.Any(r => r.Session.Id == _selectedSessionId))
            _selectedSessionId = null;
        PaintSessionSelection();
    }

    /// <summary>The selected row keeps a chip background; hover adds its own on top.</summary>
    private void PaintSessionSelection()
    {
        foreach (var (session, row) in _sessionRows)
            row.Background = session.Id == _selectedSessionId ? Chip : Brushes.Transparent;
    }

    private void SelectSession(Sessions.SessionInfo session)
    {
        _selectedSessionId = session.Id;
        PaintSessionSelection();
        _sessionsCard.Focus(); // so the keys (Enter / Delete / F2 / arrows) reach the pane
    }

    /// <summary>Keyboard on the SESSIONS pane: ↑/↓ select, Enter switch, Delete delete, F2 rename, F5 refresh.</summary>
    private void OnSessionsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
        {
            // In the filter box only ↓ is ours: it hands the keyboard to the (filtered) list.
            if (e.Key == Key.Down && _sessionRows.Count > 0)
            {
                SelectSession(_sessionRows[0].Session);
                e.Handled = true;
            }
            return;
        }
        var selected = _sessionRows.FindIndex(r => r.Session.Id == _selectedSessionId);
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                if (_sessionRows.Count == 0) return;
                int next = selected < 0 ? 0 : Math.Max(0, Math.Min(_sessionRows.Count - 1, selected + (e.Key == Key.Down ? 1 : -1)));
                SelectSession(_sessionRows[next].Session);
                _sessionRows[next].Row.BringIntoView();
                break;
            case Key.Enter when selected >= 0: ResumeSession(_sessionRows[selected].Session); break;
            case Key.Delete when selected >= 0:
                if (!IsSessionOpen(_sessionRows[selected].Session.Id)) DeleteSession(_sessionRows[selected].Session);
                break;
            case Key.F2 when selected >= 0: RenameSession(_sessionRows[selected].Session); break;
            case Key.F5: RefreshSessions(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Fill <paramref name="tip"/> with "You: … / Claude: …" from the transcript's tail. Keyed by id AND the
    /// transcript's last-write time, so a session that moved on shows its new last exchange. On a cache
    /// miss the tail (up to 2 MB) is read on a pool thread and the open tooltip is updated in place - the
    /// read must never stall the UI thread on hover.
    /// </summary>
    private void FillSessionPreview(Sessions.SessionInfo session, ToolTip tip)
    {
        var key = session.Id + "|" + session.LastWriteLocal.Ticks;
        if (_previewCache.TryGetValue(key, out var cached))
        {
            tip.Content = cached;
            return;
        }
        tip.Content = Strings.TipSessionResume;
        string? workspace = BridgeStatus.Workspace;
        _ = Task.Run(() =>
        {
            var (prompt, answer) = Sessions.SessionStore.Preview(workspace, session.Id);
            var text = Strings.TipSessionResume;
            if (prompt != null) text += "\n\n" + Strings.SessionPreviewYou + ": " + prompt;
            if (answer != null) text += "\n" + "Claude: " + answer;
#pragma warning disable VSTHRD001 // plain WPF dispatcher hop, as elsewhere in this control
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _previewCache[key] = text;
                tip.Content = text;
            }));
#pragma warning restore VSTHRD001
        });
    }

    private Border BuildSessionRow(Sessions.SessionInfo s, string? activeId)
    {
        {
            var session = s; // capture per row
            bool active = string.Equals(session.Id, activeId, StringComparison.OrdinalIgnoreCase);
            var title = Font(new TextBlock
            {
                Text = (active ? "● " : "") + session.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            }, 0.96);
            var when = Font(new TextBlock
            {
                Text = session.LastWriteLocal.ToString("g", UiCulture) + (active ? "  ·  " + Strings.SessionActive : ""),
                Opacity = 0.55,
            }, 0.83);
            var stack = new StackPanel();
            stack.Children.Add(title);
            stack.Children.Add(when);
            var tip = new ToolTip { Content = Strings.TipSessionResume };
            var row = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(4, 2, 4, 2),
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Child = stack,
            };
            // The tooltip grows a preview of the conversation's last exchange, read in the background on hover.
            row.ToolTipOpening += (a, b) => FillSessionPreview(session, tip);
            row.MouseEnter += (a, b) => row.Background = ChipHover;
            row.MouseLeave += (a, b) => row.Background = session.Id == _selectedSessionId ? Chip : Brushes.Transparent;
            row.MouseLeftButtonDown += (a, b) =>
            {
                SelectSession(session);
                if (b.ClickCount >= 2) ResumeSession(session);
            };
            row.MouseRightButtonDown += (a, b) => SelectSession(session);

            // Right-click menu. Switch is also the double-click; Rename writes the same record the CLI's
            // /rename and the VS Code extension do, so the new name shows up everywhere.
            var menu = MakeThemedMenu();
            var cont = new MenuItem { Header = Strings.SessionMenuSwitch };
            cont.Click += (a, b) => ResumeSession(session);
            menu.Items.Add(cont);

            var rename = new MenuItem { Header = Strings.SessionMenuRename };
            rename.Click += (a, b) => RenameSession(session);
            menu.Items.Add(rename);

            var delete = new MenuItem { Header = Strings.SessionMenuDelete };
            delete.Click += (a, b) => DeleteSession(session);
            ToolTipService.SetShowOnDisabled(delete, true);
            menu.Items.Add(delete);
            // An open session can't be deleted (its CLI would recreate the transcript): grey the item out,
            // evaluated each time the menu opens since sessions open and close while the list sits there.
            // "Open" = known to our window registry OR held by any running CLI (VS Code, another terminal):
            // deleting a session another client holds doesn't stick - it re-writes a stub on exit.
            void ApplyOpenState()
            {
                bool open = IsSessionOpen(session.Id);
                delete.IsEnabled = !open;
                delete.ToolTip = open ? Strings.SessionDeleteOpen : null;
            }
            menu.Opened += (a, b) =>
            {
                ApplyOpenState(); // instant, from the last process scan
                _ = Task.Run(() =>
                {
                    Sessions.ExternalSessions.Scan(); // then refresh it while the menu is up
                    _ = Dispatcher.BeginInvoke(new Action(() => { if (menu.IsOpen) ApplyOpenState(); }));
                });
            };
            row.ContextMenu = menu;

            return row;
        }
    }

    private void RenameSession(Sessions.SessionInfo session)
    {
        var name = RenameDialog.Prompt(session.Title);
        if (name == null) return;
        string? workspace = BridgeStatus.Workspace;
        _ = Task.Run(() =>
        {
            Sessions.SessionStore.Rename(workspace, session.Id, name);
            BridgeStatus.RaiseSessionsChanged(); // re-lists here and retitles the session's terminal tab
        });
    }

    private void DeleteSession(Sessions.SessionInfo session)
    {
        // The menu item is disabled for open sessions; re-check in case it opened while the menu was up.
        if (Sessions.SessionWindows.Check(session.Id) != Sessions.SessionWindows.Presence.NotOpen) return;

        int answer = VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider,
            string.Format(Strings.SessionDeleteConfirmFormat, session.Title), Strings.SessionDeleteTitle,
            Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_QUERY,
            Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
            Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
        if (answer != 1 /* IDOK */) return;

        _ = Task.Run(() =>
        {
            // Last check right before deleting: a CLI may have resumed it while the dialog was up.
            if (Sessions.ExternalSessions.Scan().Contains(session.Id))
                Protocol.Log.Warn($"sessions: not deleting {session.Id} - a running claude process has it open");
            else
                Sessions.SessionStore.Delete(session.Id);
            _ = Dispatcher.BeginInvoke(new Action(RefreshSessions));
        });
    }

    /// <summary>
    /// A context menu skinned with VS's command-bar theme brushes (the stock WPF menu is light Aero chrome
    /// in a dark IDE). Templates are built in code to match the rest of the panel; the items' template
    /// lives in the menu's own resources so every MenuItem added later picks it up.
    /// </summary>
    private static ContextMenu MakeThemedMenu()
    {

        // Menu frame
        var frame = new System.Windows.FrameworkElementFactory(typeof(Border));
        frame.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarMenuBackgroundGradientBrushKey);
        frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarMenuBorderBrushKey);
        frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        frame.SetValue(Border.PaddingProperty, new Thickness(3));
        var items = new System.Windows.FrameworkElementFactory(typeof(StackPanel));
        items.SetValue(Panel.IsItemsHostProperty, true);
        frame.AppendChild(items);
        var menuStyle = new Style(typeof(ContextMenu));
        menuStyle.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(ContextMenu)) { VisualTree = frame }));

        // Menu item
        var itemBorder = new System.Windows.FrameworkElementFactory(typeof(Border)) { Name = "bd" };
        itemBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        itemBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        itemBorder.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        itemBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        itemBorder.SetValue(Border.PaddingProperty, new Thickness(4, 5, 24, 5));
        // A fixed check column (blank unless IsChecked), so checkable items show their state and every
        // item's text lines up whether or not it is checkable.
        var row = new System.Windows.FrameworkElementFactory(typeof(StackPanel)) { Name = "txt" };
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        row.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.CommandBarTextActiveBrushKey);
        var check = new System.Windows.FrameworkElementFactory(typeof(TextBlock)) { Name = "chk" };
        check.SetValue(TextBlock.TextProperty, "✓");
        check.SetValue(WidthProperty, 16.0);
        check.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        check.SetValue(VisibilityProperty, Visibility.Hidden);
        row.AppendChild(check);
        var content = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        content.SetValue(MarginProperty, new Thickness(4, 0, 0, 0));
        row.AppendChild(content);
        itemBorder.AppendChild(row);
        var itemTemplate = new ControlTemplate(typeof(MenuItem)) { VisualTree = itemBorder };
        var isChecked = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true };
        isChecked.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "chk"));
        itemTemplate.Triggers.Add(isChecked);
        var hot = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hot.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarMenuItemMouseOverBrushKey), "bd"));
        hot.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarMenuItemMouseOverBorderBrushKey), "bd"));
        hot.Setters.Add(new Setter(TextElement.ForegroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarMenuItemMouseOverTextBrushKey), "txt"));
        itemTemplate.Triggers.Add(hot);
        // Added after "hot" so it wins: a disabled item is dimmed and never highlights.
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.45, "txt"));
        disabled.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent, "bd"));
        disabled.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent, "bd"));
        itemTemplate.Triggers.Add(disabled);
        var itemStyle = new Style(typeof(MenuItem));
        itemStyle.Setters.Add(new Setter(TemplateProperty, itemTemplate));
        itemStyle.Setters.Add(new Setter(FocusVisualStyleProperty, null));

        var menu = new ContextMenu { Style = menuStyle };
        menu.Resources.Add(typeof(MenuItem), itemStyle);
        return menu;
    }

    private static void ResumeSession(Sessions.SessionInfo session)
    {
        // The id becomes a command-line argument: accept only the CLI's own id alphabet.
        if (!System.Text.RegularExpressions.Regex.IsMatch(session.Id, "^[A-Za-z0-9-]+$")) return;
        _ = BridgeStatus.SwitchSessionAction?.Invoke(session.Id);
    }

    private void SelectTab(PanelTab tab)
    {
        _currentTab = tab;
        _feed.Visibility = tab == PanelTab.Activity ? Visibility.Visible : Visibility.Collapsed;
        _editsCard.Visibility = tab == PanelTab.Edits ? Visibility.Visible : Visibility.Collapsed;
        _sessionsCard.Visibility = tab == PanelTab.Sessions ? Visibility.Visible : Visibility.Collapsed;
        SetTabSelected(_tabActivity, tab == PanelTab.Activity);
        SetTabSelected(_tabEdits, tab == PanelTab.Edits);
        SetTabSelected(_tabSessions, tab == PanelTab.Sessions);
        if (tab == PanelTab.Activity) _tabActivityBadge.Visibility = Visibility.Collapsed; // read
        if (tab == PanelTab.Sessions && _wired) RefreshSessions();
        if (_layoutRestored) PanelSettings.SetString(PanelSettings.SelectedTabKey, tab.ToString());
    }

    private PanelTab _currentTab = PanelTab.Sessions;
    private bool _layoutRestored;
    private readonly TextBlock _tabActivityBadge;
    private readonly TextBlock _tabEditsBadge;
    private readonly TextBlock _tabSessionsBadge;
    private static readonly Brush OpenDot = Freeze(Color.FromRgb(0x3F, 0xB9, 0x50));

    /// <summary>Tab order as shown, for Ctrl+Tab / Ctrl+PageDown cycling.</summary>
    private static readonly PanelTab[] TabOrder = { PanelTab.Sessions, PanelTab.Edits, PanelTab.Activity };

    private void CycleTab(int direction)
    {
        int i = Array.IndexOf(TabOrder, _currentTab);
        SelectTab(TabOrder[(i + direction + TabOrder.Length) % TabOrder.Length]);
    }

    /// <summary>A warning or error was logged while ACTIVITY wasn't showing: dot the tab (red beats amber).</summary>
    private void MarkActivityUnread(bool error)
    {
        if (_tabActivityBadge.Visibility != Visibility.Visible || error)
            _tabActivityBadge.Foreground = error ? ErrText : WarnText;
        _tabActivityBadge.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Cap the top section so the tab area keeps at least <see cref="TabsMinHeight"/>: the top row is
    /// Auto (sized to its content) until the user drags the splitter, and an Auto row ignores the star
    /// row's MinHeight - without this cap, a tall top section pushed the tabs off the panel.
    /// </summary>
    private void ClampTopRow(Grid root)
    {
        double available = root.ActualHeight - TabsMinHeight - SplitterHeight;
        _topRow.MaxHeight = Math.Max(48, available);
    }

    /// <summary>A thin divider line centred in the splitter's grab area.</summary>
    private static ControlTemplate SplitterTemplate()
    {
        var grip = new System.Windows.FrameworkElementFactory(typeof(Border));
        grip.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var line = new System.Windows.FrameworkElementFactory(typeof(Border));
        line.SetValue(HeightProperty, 1.0);
        line.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        line.SetValue(Border.BackgroundProperty, Divider);
        grip.AppendChild(line);
        return new ControlTemplate(typeof(GridSplitter)) { VisualTree = grip };
    }

    private static void SetTabSelected(Border tab, bool selected)
    {
        tab.BorderThickness = new Thickness(0, 0, 0, 2);
        tab.Opacity = selected ? 1.0 : 0.6;
        if (selected) tab.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ControlLinkTextKey);
        else tab.BorderBrush = Brushes.Transparent;
    }

    /// <summary>A tab header: the upper-cased label plus a small badge (a count or a dot), hidden when empty.</summary>
    private static Border MakeTabHeader(string text, Action onClick, out TextBlock badge)
    {
        var label = Font(new TextBlock { Text = text.ToUpperInvariant(), FontWeight = FontWeights.SemiBold }, 0.96);
        label.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        badge = Font(new TextBlock { Margin = new Thickness(5, 0, 0, 0), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center }, 0.85);
        badge.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        var face = new StackPanel { Orientation = Orientation.Horizontal };
        face.Children.Add(label);
        face.Children.Add(badge);
        var tab = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            Child = face,
        };
        tab.MouseLeftButtonUp += (s, e) => onClick();
        return tab;
    }

    private Button _editsTurnButton = null!;
    /// <summary>The past turn being shown, or null for the current one. A reference, not an index: a new
    /// turn is inserted at the front of the history and would shift every index.</summary>
    private BridgeStatus.EditTurn? _editsTurn;
    private readonly System.Collections.Generic.HashSet<string> _collapsedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _editsDirty;

    private void RefreshEdits()
    {
        var current = BridgeStatus.TurnEdits;
        var history = BridgeStatus.EditHistory;
        if (_editsTurn != null && !history.Contains(_editsTurn)) _editsTurn = null; // aged out of the history

        // The tab badge always counts the CURRENT turn's changes, whatever turn the tab is showing.
        _tabEditsBadge.Text = current.Count.ToString();
        _tabEditsBadge.Visibility = current.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        System.Collections.Generic.IReadOnlyList<Edits.EditEntry> edits = _editsTurn == null ? current : _editsTurn.Rows;

        // The turn picker is updated BEFORE the unchanged-list early return below: with no edits yet the
        // list never "changes", and the button used to stay a blank square with only its tooltip.
        // It only appears once there is an earlier turn to pick.
        _editsTurnButton.Content = (_editsTurn == null
            ? string.Format(Strings.EditsTurnCurrentFormat, current.Count)
            : TurnLabel(_editsTurn)) + "  ▾";
        _editsTurnButton.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Runs on every status tick: rebuild only when the list really changed, so the tab's scroll
        // position (and a row under the mouse) survive the 1s refresh.
        if (!_editsDirty && edits.Count == _renderedEdits.Length
            && edits.Select((e, i) => ReferenceEquals(e, _renderedEdits[i])).All(same => same))
            return;
        _editsDirty = false;
        _renderedEdits = edits.ToArray();

        _editsList.Items.Clear();
        _editsEmpty.Visibility = edits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (edits.Count == 0) return;

        // A folder tree by path relative to the workspace; every folder and file collapsible.
        var tree = Edits.EditTree.Build(edits, BridgeStatus.Workspace);
        RenderEditFolder(tree, depth: 0);
    }

    private const double TreeIndent = 14;

    /// <summary>Folders first (alphabetical), then files; the root itself has no row.</summary>
    private void RenderEditFolder(Edits.EditTree.Folder folder, int depth)
    {
        foreach (var child in folder.Folders)
        {
            bool collapsed = _collapsedFiles.Contains(FolderKey(child.FullPath));
            _editsList.Items.Add(BuildEditFolderRow(child, collapsed, depth));
            if (!collapsed) RenderEditFolder(child, depth + 1);
        }
        foreach (var rows in folder.Files)
        {
            bool collapsed = _collapsedFiles.Contains(rows[0].FilePath);
            _editsList.Items.Add(BuildEditFileRow(rows, collapsed, depth));
            if (collapsed) continue;
            foreach (var e in rows)
                _editsList.Items.Add(BuildEditHunkRow(e, depth + 1));
        }
    }

    private static string FolderKey(string path) => "dir:" + path;

    /// <summary>A folder node: ▾/▸ + folder icon + its (compacted, shortened) path + the subtree's +A -R.</summary>
    private UIElement BuildEditFolderRow(Edits.EditTree.Folder folder, bool collapsed, int depth)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(depth * TreeIndent, 4, 0, 1),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            ToolTip = folder.FullPath,
        };
        var chevron = Font(new TextBlock { Text = collapsed ? "▸" : "▾", Width = 14, Opacity = 0.7 }, 0.96);
        chevron.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        var icon = new CrispImage
        {
            Moniker = collapsed ? KnownMonikers.FolderClosed : KnownMonikers.FolderOpened,
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var name = Font(new TextBlock { Text = Edits.EditTree.Shorten(folder.Name, 40) }, 0.96);
        name.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        var all = folder.AllRows().ToList();
        var summary = Font(new TextBlock
        {
            Text = "  " + new Edits.EditEntry { Added = all.Sum(r => r.Added), Removed = all.Sum(r => r.Removed) }.ChangeLabel,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        }, 0.88);
        summary.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        row.Children.Add(chevron);
        row.Children.Add(icon);
        row.Children.Add(name);
        row.Children.Add(summary);
        row.MouseLeftButtonUp += (s, e) =>
        {
            var key = FolderKey(folder.FullPath);
            if (!_collapsedFiles.Remove(key)) _collapsedFiles.Add(key);
            _editsDirty = true;
            RefreshEdits();
        };
        return row;
    }

    private static string TurnLabel(BridgeStatus.EditTurn turn)
    {
        var prompt = (turn.Prompt ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (prompt.Length > 40) prompt = prompt.Substring(0, 39) + "…";
        return string.Format(Strings.EditsTurnPastFormat, turn.Started.ToString("t", UiCulture), prompt, turn.Rows.Length);
    }

    private void ShowTurnPicker()
    {
        var history = BridgeStatus.EditHistory;
        var menu = MakeThemedMenu();
        var currentItem = new MenuItem
        {
            Header = string.Format(Strings.EditsTurnCurrentFormat, BridgeStatus.TurnEdits.Count),
            IsCheckable = true,
            IsChecked = _editsTurn == null,
        };
        currentItem.Click += (s, e) => { _editsTurn = null; _editsDirty = true; RefreshEdits(); };
        menu.Items.Add(currentItem);
        foreach (var turn in history)
        {
            var item = new MenuItem { Header = TurnLabel(turn), IsCheckable = true, IsChecked = ReferenceEquals(_editsTurn, turn) };
            item.Click += (s, e) => { _editsTurn = turn; _editsDirty = true; RefreshEdits(); };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = _editsTurnButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The file header of an EDITS group: ▾/▸ toggles the group; the menu opens the file or shows
    /// the file's whole change in this turn (first edit's "before" against the last edit's "after").</summary>
    private UIElement BuildEditFileRow(System.Collections.Generic.List<Edits.EditEntry> rows, bool collapsed, int depth)
    {
        var first = rows[0];
        int added = rows.Sum(r => r.Added), removed = rows.Sum(r => r.Removed);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(depth * TreeIndent, 4, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var chevron = Font(new TextBlock { Text = collapsed ? "▸" : "▾", Width = 14, Opacity = 0.7 }, 0.96);
        chevron.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        var name = Font(new TextBlock { Text = first.FileName, FontWeight = FontWeights.SemiBold, ToolTip = first.FilePath }, 0.96);
        name.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        var summary = Font(new TextBlock
        {
            Text = "  " + new Edits.EditEntry { Added = added, Removed = removed }.ChangeLabel,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        }, 0.88);
        summary.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        row.Children.Add(chevron);
        row.Children.Add(name);
        row.Children.Add(summary);
        row.MouseLeftButtonUp += (s, e) =>
        {
            if (!_collapsedFiles.Remove(first.FilePath)) _collapsedFiles.Add(first.FilePath);
            _editsDirty = true;
            RefreshEdits();
        };

        var menu = MakeThemedMenu();
        var open = new MenuItem { Header = Strings.EditsMenuOpen };
        open.Click += (s, e) => OpenEdit(first, wholeFile: true);
        menu.Items.Add(open);
        var diff = new MenuItem { Header = Strings.EditsMenuShowDiff, IsEnabled = rows.All(r => r.Snapshot != null) };
        diff.Click += (s, e) =>
        {
            // Whole-turn change of this file: before the first edit -> after the last one.
            var combined = new Edits.EditEntry
            {
                FilePath = first.FilePath,
                FileName = first.FileName,
                Snapshot = new Edits.EditSnapshot(rows[0].Snapshot!.Before, rows[rows.Count - 1].Snapshot!.After),
            };
            Edits.EditDiffViewer.Show(combined);
        };
        menu.Items.Add(diff);
        row.ContextMenu = menu;
        return row;
    }

    /// <summary>One changed region: click opens the file there; the menu also shows this edit's diff.</summary>
    private UIElement BuildEditHunkRow(Edits.EditEntry e, int depth)
    {
        string label = e.Rewritten
            ? string.Format(Strings.EditsRewrittenFormat, e.TotalLines)
            : string.Format(Strings.EditsLineFormat, e.LineLabel);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(depth * TreeIndent + 14, 1, 0, 1) };
        var link = Font(new TextBlock
        {
            Text = label,
            Cursor = Cursors.Hand,
            ToolTip = e.Rewritten
                ? string.Format(Strings.EditsTooltipRewritten, e.FilePath)
                : string.Format(Strings.EditsTooltipFormat, e.FilePath, e.StartLine + 1),
        }, 0.96);
        link.SetResourceReference(ForegroundProperty, VsBrushes.ControlLinkTextKey);
        link.MouseLeftButtonUp += (s, _) => OpenEdit(e, wholeFile: false);
        row.Children.Add(link);

        var change = e.ChangeLabel;
        if (change.Length > 0)
        {
            var badge = Font(new TextBlock
            {
                Text = "  " + change,
                Opacity = 0.5,
                VerticalAlignment = VerticalAlignment.Center,
            }, 0.88);
            badge.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
            row.Children.Add(badge);
        }

        var menu = MakeThemedMenu();
        var open = new MenuItem { Header = Strings.EditsMenuOpen };
        open.Click += (s, args) => OpenEdit(e, wholeFile: false);
        menu.Items.Add(open);
        var diff = new MenuItem { Header = Strings.EditsMenuShowDiff, IsEnabled = e.Snapshot != null };
        diff.Click += (s, args) => Edits.EditDiffViewer.Show(e);
        menu.Items.Add(diff);
        row.ContextMenu = menu;
        return row;
    }

    private static void OpenEdit(Edits.EditEntry e, bool wholeFile)
    {
        // Already on the UI thread here (a WPF input event), which is what Navigator requires.
        bool rangeless = wholeFile || e.Rewritten;
        bool opened = Editor.Navigator.OpenAt(e.FilePath, rangeless ? (int?)null : e.StartLine, rangeless ? (int?)null : e.EndLine);
        if (!opened)
            Protocol.Log.Warn($"could not open {e.FilePath} - moved or deleted since the edit");
    }

    /// <summary>
    /// Render the live-activity card: turn state with elapsed time (the 1s timer keeps it moving), the
    /// tool call(s) in flight or the last one done, and Claude's task list when the session has one.
    /// Hidden until the first turn so it costs no height before there is anything to say.
    /// </summary>
    private void RefreshActivity()
    {
        if (!Activity.AgentActivity.HasActivity)
        {
            _activityCard.Visibility = Visibility.Collapsed;
            return;
        }
        _activityCard.Visibility = Visibility.Visible;

        bool active = Activity.AgentActivity.TurnActive;
        var started = Activity.AgentActivity.TurnStartedAt;
        var ended = Activity.AgentActivity.TurnEndedAt;
        int calls = Activity.AgentActivity.ToolCallsThisTurn;

        _activityDot.Fill = active ? DotConnected : DotIdle;
        if (active)
        {
            _activityHeader.Text = Strings.ActivityWorking + (started is DateTime s ? "  ·  " + Uptime(s) : "");
        }
        else
        {
            string took = started is DateTime s2 && ended is DateTime e2 ? "  ·  " + Duration(e2 - s2) : "";
            _activityHeader.Text = Strings.ActivityFinished + took + "  ·  " + string.Format(Strings.ActivityToolCalls, calls);
        }

        var prompt = Activity.AgentActivity.PromptPreview;
        _activityPrompt.Visibility = string.IsNullOrEmpty(prompt) ? Visibility.Collapsed : Visibility.Visible;
        _activityPrompt.Text = prompt ?? "";

        // Tool lines: only meaningful mid-turn. Tool names, paths and commands are not localized.
        _activityTools.Children.Clear();
        if (active)
        {
            var tools = Activity.AgentActivity.ActiveTools;
            if (tools.Count > 0)
            {
                foreach (var t in tools)
                {
                    var running = DateTime.Now - t.StartedAt;
                    var age = running.TotalSeconds >= 5 ? $"   {Duration(running)}" : "";
                    _activityTools.Children.Add(ToolLine("⚙ " + t.Name, t.Summary + age, 1.0));
                }
            }
            else
            {
                var last = Activity.AgentActivity.LastCompleted;
                _activityTools.Children.Add(ToolLine(Strings.ActivityThinking,
                    last != null ? $"  ({last.Name} {last.Summary})" : "", 0.7));
            }
            _activityTools.Visibility = Visibility.Visible;
        }
        else
        {
            _activityTools.Visibility = Visibility.Collapsed;
        }

        // Task list (TaskCreate/TaskUpdate, or TodoWrite on older CLIs). Newer models don't get these
        // tools by default, so this block is simply absent for most sessions - not a bug.
        var tasks = Activity.AgentActivity.Tasks;
        _activityTasks.Children.Clear();
        if (tasks.Count == 0)
        {
            _activityTasksLabel.Visibility = Visibility.Collapsed;
            _activityTasks.Visibility = Visibility.Collapsed;
            return;
        }
        int done = 0;
        foreach (var t in tasks) if (t.Status == "completed") done++;
        _activityTasksLabel.Text = string.Format(Strings.ActivityTasksFormat, done, tasks.Count);
        _activityTasksLabel.Visibility = Visibility.Visible;
        _activityTasks.Visibility = Visibility.Visible;
        foreach (var t in tasks)
        {
            string glyph = t.Status switch { "completed" => "☑", "in_progress" => "◐", _ => "☐" };
            string text = t.Status == "in_progress" && !string.IsNullOrEmpty(t.ActiveForm) ? t.ActiveForm! : t.Subject;
            var row = Font(new TextBlock
            {
                Text = glyph + "  " + text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = t.Status == "completed" ? 0.5 : (t.Status == "in_progress" ? 1.0 : 0.8),
                Margin = new Thickness(0, 0, 0, 1),
            }, 0.96);
            row.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
            _activityTasks.Children.Add(row);
        }
    }

    private TextBlock ToolLine(string head, string detail, double opacity)
    {
        var tb = Font(new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = opacity,
        }, 0.96);
        tb.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        tb.Inlines.Add(new System.Windows.Documents.Run(head) { FontWeight = FontWeights.SemiBold });
        if (detail.Length > 0) tb.Inlines.Add(new System.Windows.Documents.Run("  " + detail) { FontFamily = Mono });
        return tb;
    }

    private static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{Math.Max(0, t.Seconds)}s";
    }

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
            _attachSummary.Text = string.Format(Strings.AttachSummaryFormat, Tok(estSum), estPartial ? "+" : "");

        foreach (var item in items)
        {
            var it = item; // capture per chip
            var est = it.EstTokens is long e ? "\n" + string.Format(Strings.ChipEstimateFormat, Tok(e)) : "";
            var toolNote = it.NeedsTool ? "\n" + Strings.ChipNeedsTool : "";
            var name = Font(new TextBlock
            {
                Text = (it.IsImage ? "🖼 " : it.NeedsTool ? "🧰 " : "📄 ") + it.FileName + it.RangeShort + (it.Sent ? "" : "  ⏳"),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = it.MentionPath + it.RangeLabel + est + toolNote + "\n" + (it.Sent
                    ? Strings.ChipClickRemention
                    : Strings.ChipStagedRetry),
            }, 0.96);
            name.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
            name.MouseLeftButtonUp += (s, e) => _ = Task.Run(async () =>
            {
                await Attachments.AttachmentService.ResendAsync(it);
                FocusClaude(); // chip click = explicit re-send intent
            });

            var close = Font(new TextBlock
            {
                Text = "✕",
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Opacity = 0.65,
                ToolTip = it.WasCopied ? Strings.ChipRemoveCopied : Strings.ChipRemove,
            }, 0.96);
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
        => string.IsNullOrEmpty(BridgeStatus.Workspace) ? Strings.NoWorkspace : BridgeStatus.Workspace!;

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

    /// <summary>Scales an element's FontSize relative to VS's Environment Font size (see <see cref="FontScale"/>).</summary>
    private static T Font<T>(T el, double ratio) where T : FrameworkElement
        => FontScale.Bind(el, typeof(ClaudeToolWindowControl), ratio);

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

    /// <summary>
    /// A VS-themed button: the shell's own button brushes (face / border / hover / pressed / disabled,
    /// so it follows every theme incl. high contrast), a 1px border, and real hover/pressed/focus states.
    /// The state brushes are set by template triggers on the named border - NOT on the button itself:
    /// a local Background on the Button outranks any trigger, which is why the old chip never reacted.
    /// </summary>
    /// <summary>Persist the usage toggle and re-apply the workspace's statusLine (off the UI thread - file IO).</summary>
    private void ApplyUsageToggle(bool on)
    {
        if (BridgeStatus.UsageStatusLine == on) return; // a programmatic sync from UpdateStatus
        BridgeStatus.SetUsageStatusLine(on);
        var workspace = BridgeStatus.Workspace;
        if (!string.IsNullOrEmpty(workspace))
            _ = Task.Run(() => Hooks.PermissionHookInstaller.EnsureInstalled(workspace!, on));
    }

    /// <summary>A toolbar button showing a VS image instead of text; the label becomes its tooltip.</summary>
    private static Button MakeIconButton(ImageMoniker moniker, string tooltip, Action onClick)
    {
        var b = MakeButton("", onClick);
        b.Content = new CrispImage { Moniker = moniker, Width = 16, Height = 16 };
        b.Padding = new Thickness(7, 4, 7, 4);
        b.ToolTip = tooltip;
        AutomationProperties.SetName(b, tooltip);
        return b;
    }

    private Ellipse _gearBadge = null!;

    /// <summary>
    /// The gear: opens a themed popup holding <paramref name="content"/> (the toggles). The popup lives in
    /// the button's subtree so it inherits the panel's font scale and resources.
    /// </summary>
    private Button MakeSettingsButton(Panel content)
    {
        var face = new Grid();
        face.Children.Add(new CrispImage { Moniker = KnownMonikers.Settings, Width = 16, Height = 16 });
        _gearBadge = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = WarnText,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -3, -5, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        face.Children.Add(_gearBadge);

        var frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(12, 6, 12, 6), Child = content };
        frame.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.CommandBarMenuBackgroundGradientBrushKey);
        frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.CommandBarMenuBorderBrushKey);
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = frame,
        };
        face.Children.Add(popup);

        // StaysOpen=false closes the popup on the mouse-down that is also this button's click - without
        // the guard that click would immediately reopen it, and the gear could never close its own popup.
        DateTime closedAt = DateTime.MinValue;
        popup.Closed += (s, e) => closedAt = DateTime.UtcNow;
        Button? button = null;
        button = MakeButton("", () =>
        {
            if (DateTime.UtcNow - closedAt < TimeSpan.FromMilliseconds(250)) return;
            popup.PlacementTarget = button;
            popup.IsOpen = true;
        });
        button.Content = face;
        button.Padding = new Thickness(7, 4, 7, 4);
        button.ToolTip = Strings.TipSettings;
        AutomationProperties.SetName(button, Strings.TipSettings);
        return button;
    }

    /// <summary>CrispImage picks light/dark variants from the background it sits on - tell it which.</summary>
    private void ApplyImageTheme()
    {
        var c = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
        ImageThemingUtilities.SetImageBackgroundColor(this, Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    private void OnThemeChanged(ThemeChangedEventArgs e) => ApplyImageTheme();

    private static Button MakeButton(string text, Action onClick)
    {
        var b = Font(new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(12, 3, 12, 3),
            MinHeight = 24,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1),
            FocusVisualStyle = null,
        }, 1.0);
        b.SetResourceReference(BackgroundProperty, CommonControlsColors.ButtonBrushKey);
        b.SetResourceReference(BorderBrushProperty, CommonControlsColors.ButtonBorderBrushKey);
        b.SetResourceReference(ForegroundProperty, CommonControlsColors.ButtonTextBrushKey);

        var border = new System.Windows.FrameworkElementFactory(typeof(Border)) { Name = "bd" };
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
        var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        // Later triggers win over earlier ones: hover < keyboard focus < pressed < disabled.
        template.Triggers.Add(StateTrigger(IsMouseOverProperty, CommonControlsColors.ButtonHoverBrushKey, CommonControlsColors.ButtonBorderHoverBrushKey));
        template.Triggers.Add(StateTrigger(IsKeyboardFocusedProperty, null, CommonControlsColors.ButtonBorderFocusedBrushKey));
        template.Triggers.Add(StateTrigger(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, CommonControlsColors.ButtonPressedBrushKey, CommonControlsColors.ButtonBorderPressedBrushKey));
        template.Triggers.Add(StateTrigger(IsEnabledProperty, CommonControlsColors.ButtonDisabledBrushKey, CommonControlsColors.ButtonBorderDisabledBrushKey, value: false));
        b.Template = template;

        b.Click += (s, e) => { try { onClick(); } catch { } };
        return b;
    }

    private static Trigger StateTrigger(DependencyProperty prop, object? backgroundKey, object? borderKey, bool value = true)
    {
        var t = new Trigger { Property = prop, Value = value };
        if (backgroundKey != null)
            t.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(backgroundKey), "bd"));
        if (borderKey != null)
            t.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(borderKey), "bd"));
        return t;
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
