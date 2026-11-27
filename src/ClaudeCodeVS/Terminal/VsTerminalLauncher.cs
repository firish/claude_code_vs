using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.ServiceBroker;

namespace ClaudeCodeVs.Terminal;

/// <summary>
/// Launches `claude` inside VS's own native Terminal tool window (the engine behind View > Terminal)
/// instead of an external cmd.exe console. `Microsoft.VisualStudio.Terminal.dll` (the ITerminalService
/// types) has no NuGet package, so it's loaded by reflection from the install dir at runtime - same idea
/// as Testing/TestRunner.cs's TestWindow integration, ship zero of its DLLs in the .vsix. The brokered-
/// service plumbing itself (IBrokeredServiceContainer/IServiceBroker/ServiceRpcDescriptor) IS a normal
/// compile-time reference - it's a transitive dependency of the VS SDK package already.
///
/// This surface is undocumented (no Learn page, no NuGet package) so it could change or vanish across a
/// VS update: every failure path here logs and returns false rather than throwing, so BridgeHost always
/// has the external cmd.exe console as a safety net.
/// </summary>
internal static class VsTerminalLauncher
{
    private const string AsmName = "Microsoft.VisualStudio.Terminal.dll";

    /// <summary>How long the native-terminal attempt may take before the external console takes over.</summary>
    private const int LaunchTimeoutMs = 10_000;

    /// <summary>How long the cached "Claude Code" profile stays registered after a 17.x create, so the
    /// tab's late-starting pty still finds it (see TryLaunchCoreAsync's finally).</summary>
    private const int LegacyProfileLingerMs = 30_000;

    public static async Task<bool> TryLaunchAsync(string? workingDirectory, int ssePort, CancellationToken ct, string? claudeArgs = null, string? tabName = null)
    {
        try
        {
            // A stalled brokered-service acquisition (classically: ServiceHub still spinning up on a cold
            // VS start) must not leave the Launch button dead - the fallback only ever runs on *failure*,
            // not on a hang. Race the attempt against a hard timeout; if it loses, the linked token has
            // already cancelled it (so a late completion aborts instead of opening a second terminal).
            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(LaunchTimeoutMs);
            Task<bool> attempt = TryLaunchCoreAsync(workingDirectory, ssePort, timeoutCts.Token, claudeArgs, tabName);
            _ = attempt.ContinueWith(t => { _ = t.Exception; timeoutCts.Dispose(); }, TaskScheduler.Default);

            // The grace over CancelAfter lets a cancellation-honoring call surface as a clean
            // OperationCanceledException inside the core first; this race only decides when the
            // call ignores its token outright.
            if (await Task.WhenAny(attempt, Task.Delay(LaunchTimeoutMs + 2_000, ct)) != attempt)
            {
                Log.Warn($"Native VS terminal: no response within {LaunchTimeoutMs / 1000}s (ServiceHub not ready?) - falling back to external console.");
                return false;
            }
            return await attempt;
        }
        catch (Exception e)
        {
            Log.Warn($"Native VS terminal launch failed ({e.GetType().Name}: {e.Message}) - falling back to external console.");
            return false;
        }
    }

    private static async Task<bool> TryLaunchCoreAsync(string? workingDirectory, int ssePort, CancellationToken ct, string? claudeArgs, string? tabName)
    {
        try
        {
            var terminalServiceType = FindType("Microsoft.VisualStudio.Terminal.ITerminalService");
            var windowOptionsType = FindType("Microsoft.VisualStudio.Terminal.TerminalWindowOptions");
            var profileConfigType = FindType("Microsoft.VisualStudio.Terminal.ProfileConfig");
            if (terminalServiceType == null || profileConfigType == null)
            {
                Log.Warn("Native VS terminal: Microsoft.VisualStudio.Terminal types not found (VS edition/version mismatch) - falling back to external console.");
                return false;
            }
            // TerminalWindowOptions only exists on the 18.x (VS 2026) surface. Its absence means the
            // VS 2022 (17.x) contract, where CreateTerminalAsync takes name/profile/cwd as plain args
            // (issue #12 follow-up: same service, older shape - dumped headlessly from 17.14's DLL).

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            // The acquisition below can stall long past our timeout on a cold ServiceHub - after which
            // the fallback has already launched the external console. Explicit gates from here down keep
            // a late-running attempt from opening a SECOND terminal (seen live 2026-08-06: 10s stall ->
            // external console AND a late native tab, two sessions).
            ct.ThrowIfCancellationRequested();

            // TWO acquisition routes, newest first. VS 18.9 (18.9.12105.275, 2026-08-12) REMOVED
            // TerminalServiceDescriptors and moved the terminal off the brokered-service bus onto a
            // classic VS service: SVsTerminalService -> IVsTerminalService.CreateTerminalService().
            // Older VS - including every 17.x - only has the brokered descriptor. ITerminalService
            // itself is unchanged on both, so everything downstream of here is shared.
            // On 17.x a THIRD route comes first: an in-proc TerminalService built on the package's own
            // TerminalManager (see TryGetInProcService - the brokered proxy loses the profile in transit).
            bool viaBroker = false;
            object? terminalService = await TryGetViaVsServiceAsync(ct);
            if (terminalService == null && windowOptionsType == null)
            {
                terminalService = TryGetInProcService(ref terminalServiceType, ref profileConfigType);
            }
            if (terminalService == null)
            {
                terminalService = await TryGetViaBrokerAsync(terminalServiceType, ct);
                viaBroker = terminalService != null;
            }
            if (terminalService == null)
            {
                Log.Warn("Native VS terminal: could not acquire ITerminalService via SVsTerminalService (18.9+) or the brokered descriptor (older) - falling back to external console.");
                return false;
            }

            object? profile = null;
            object? terminalPackage = null; // set when the profile was cached in-proc (VS 2022 route)
            bool deferCleanup = false;
            try
            {
                // Last gate before side effects (AddCachedProfile / the create call): the proxy
                // acquisition may have ignored its token during a cold start.
                ct.ThrowIfCancellationRequested();

                // ProfileConfig's shape could drift across a VS update - select the 4-arg ctor explicitly
                // rather than trusting GetConstructors() ordering, and fail loudly into the fallback.
                var profileCtor = profileConfigType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 4);
                if (profileCtor == null)
                {
                    Log.Warn("Native VS terminal: ProfileConfig has no 4-arg constructor (VS update changed the shape) - falling back to external console.");
                    return false;
                }

                // /K keeps the window open after claude exits (parity with today's cmd.exe path); env vars are
                // baked into an inline `set` chain since TerminalWindowOptions has no EnvironmentVariables property.
                string args = $"/K set ENABLE_IDE_INTEGRATION=true&&set CLAUDE_CODE_SSE_PORT={ssePort}&&claude"
                            + (string.IsNullOrEmpty(claudeArgs) ? "" : " " + claudeArgs)
                            + (Ui.BridgeStatus.AutoAcceptEdits ? " --permission-mode acceptEdits" : ""); // run-wild at launch: see BridgeHost.LaunchClaudeAsync
                // DisplayName doubles as the tab caption on 17.x (GetToolWindowTitle reads the profile's name).
                string name = CaptionOf(string.IsNullOrWhiteSpace(tabName) ? "Claude Code" : tabName!);
                // A ROOTED path, not "cmd.exe": the 17.x pty host resolves a bare name through its own PATH
                // lookup (Pty.Net GetAppOnPath over the env block VS hands it), and with our profile that spawned
                // nothing at all - a blank tab and a pty service with no child process (seen live on 17.10).
                // VS's own built-in profiles all carry absolute paths, which is the shape that works everywhere.
                string shell = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec && File.Exists(comSpec)
                    ? comSpec
                    : Path.Combine(Environment.SystemDirectory, "cmd.exe");
                profile = profileCtor.Invoke(new object?[] { name, shell, args, false });

                // TerminalWindowOptions.Profile alone is ignored unless the profile is first registered with the
                // service - without this, the terminal silently falls back to the user's default shell.
                // On the brokered route (VS 2022) the service's AddCachedProfile(ITerminalProfile) is a no-op in
                // practice: an interface-typed parameter can't be deserialized on the far side, the notification
                // dies silently, and the tab opens the DEFAULT profile (seen live on 17.10: a bare Developer
                // PowerShell). The create call only carries the profile's Id, which the window then looks up in
                // the package's cache - so register it in-proc, straight on TerminalWindowPackage.
                if (viaBroker)
                    terminalPackage = TryCacheProfileInProc(profile, add: true);
                if (terminalPackage == null)
                    PickMethod(terminalServiceType, "AddCachedProfile", m => m.GetParameters().Length == 1)?
                        .Invoke(terminalService, new object?[] { profile });

                // Same overload tolerance as GetProxyAsync, plus parameter-ORDER tolerance: pick the
                // 2-arg overload taking (options, token) in either order and build the call to match.
                var create = windowOptionsType == null ? null : PickMethod(terminalServiceType, "CreateTerminalWindowAsync", m =>
                {
                    var p = m.GetParameters();
                    return p.Length == 2
                        && p.Any(x => x.ParameterType == typeof(CancellationToken))
                        && p.Any(x => x.ParameterType.IsAssignableFrom(windowOptionsType));
                });

                object? guidResult;
                string surface;
                var framesBefore = SnapshotToolFrames();
                if (create != null)
                {
                    object options = Activator.CreateInstance(windowOptionsType!)!; // ctor already defaults Focus/AllowUserInput/AutoResize=true
                    windowOptionsType!.GetProperty("Name")?.SetValue(options, name);
                    windowOptionsType.GetProperty("WorkingDirectory")?.SetValue(options, workingDirectory);
                    windowOptionsType.GetProperty("Profile")?.SetValue(options, profile);
                    windowOptionsType.GetProperty("Focus")?.SetValue(options, true);
                    windowOptionsType.GetProperty("AllowUserInput")?.SetValue(options, true);

                    object?[] callArgs = create.GetParameters()[0].ParameterType == typeof(CancellationToken)
                        ? new object?[] { ct, options }
                        : new object?[] { options, ct };
                    guidResult = await UnwrapAsync(create.Invoke(terminalService, callArgs));
                    surface = "18.x surface";
                }
                else
                {
                    // VS 2022 (17.x) legacy surface: CreateTerminalAsync(ct, name, ProfileConfig, cwd).
                    // Prefer the exact-ProfileConfig overload over its ITerminalProfile twin.
                    // Prefer the 3-arg (no working directory) overload. In 17.10 the 4-arg one takes a
                    // renderer path that creates the pseudo console BEFORE the tab has been laid out, so it
                    // asks ConPTY for a 0x0 console, gets E_INVALIDARG ("Value does not fall within the
                    // expected range", from ServiceHubPtyProxy.InitPtyAsync - found in a devenv heap snapshot)
                    // and leaves a blank tab. The 3-arg one is the "+" button's path: window first, then the
                    // pty at the window's real size, started in the open folder/solution - our workspace.
                    bool withCwd = false;
                    var createLegacy = PickMethod(terminalServiceType, "CreateTerminalAsync", m =>
                    {
                        var p = m.GetParameters();
                        return p.Length == 3
                            && p[0].ParameterType == typeof(CancellationToken)
                            && p[1].ParameterType == typeof(string)
                            && p[2].ParameterType == profileConfigType;
                    });
                    if (createLegacy == null)
                    {
                        withCwd = true;
                        createLegacy = PickMethod(terminalServiceType, "CreateTerminalAsync", m =>
                        {
                            var p = m.GetParameters();
                            return p.Length == 4
                                && p[0].ParameterType == typeof(CancellationToken)
                                && p[1].ParameterType == typeof(string)
                                && p[2].ParameterType == profileConfigType
                                && p[3].ParameterType == typeof(string);
                        });
                    }
                    object?[] LegacyArgs() => withCwd
                        ? new object?[] { ct, name, profile, workingDirectory }
                        : new object?[] { ct, name, profile };
                    if (createLegacy == null)
                    {
                        Log.Warn("Native VS terminal: neither CreateTerminalWindowAsync (18.x) nor a compatible CreateTerminalAsync (17.x) found - falling back to external console.");
                        return false;
                    }
                    // On a cold VS 2022 (17.10, verified) the first create after startup can fail server-side
                    // with RemoteInvocationException "Value does not fall within the expected range", and the
                    // identical call succeeds a moment later - so retry once before giving up on the native tab.
                    try
                    {
                        guidResult = await UnwrapAsync(createLegacy.Invoke(terminalService, LegacyArgs()));
                    }
                    catch (Exception first) when (!ct.IsCancellationRequested)
                    {
                        var cause = first is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : first;
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                        // The failure can come AFTER the tab was created (seen live: a retry left two tabs).
                        // If one appeared, keep it - it runs our profile - and just go without its guid.
                        if (SnapshotToolFrames().Any(f => !framesBefore.Contains(f)))
                        {
                            Log.Event($"Native VS terminal: create reported {cause.GetType().Name} ({cause.Message}) but the tab exists - keeping it.");
                            guidResult = Guid.Empty;
                        }
                        else
                        {
                            Log.Event($"Native VS terminal: first create failed ({cause.GetType().Name}: {cause.Message}) - retrying once.");
                            await Task.Delay(1500, ct);
                            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                            guidResult = await UnwrapAsync(createLegacy.Invoke(terminalService, LegacyArgs()));
                        }
                    }
                    surface = "17.x legacy surface";
                    deferCleanup = guidResult is Guid; // see the finally block: 17.x resolves the profile late

                    // The old surface has no Focus option; ShowAsync is its bring-to-front. Best-effort.
                    if (guidResult is Guid g && g != Guid.Empty)
                    {
                        try
                        {
                            await UnwrapAsync(PickMethod(terminalServiceType, "ShowAsync",
                                    m => m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(Guid))?
                                .Invoke(terminalService, new object?[] { g, ct }));
                        }
                        catch { /* focus is cosmetic */ }
                    }
                }

                if (guidResult is not Guid terminalGuid)
                {
                    Log.Warn("Native VS terminal: terminal creation returned no guid - falling back to external console.");
                    return false;
                }

                // Remember the tab so the context actions can refocus it (ShowAsync) after they push
                // an @ reference - otherwise Enter goes to the editor, not the composer (the #33 trap).
                _lastTerminal = terminalGuid;
                // ITerminalService has no rename, so find the tab's window frame (the tool frame that wasn't
                // there before the call) - its caption is what the Sessions card retitles later.
                _lastFrame = await FindNewToolFrameAsync(framesBefore, ct);
                if (_lastFrame == null)
                    Log.Event("Native VS terminal: couldn't identify the new tab's window frame - its caption won't follow the session title.");
                Log.Info($"Launched Claude Code in VS's native Terminal window ({surface}, port {ssePort}, cwd '{workingDirectory ?? "(default)"}').");
                return true;
            }
            finally
            {
                // The cached profile is only needed while the terminal resolves it (VS doesn't use it for tab
                // restore either - restored tabs come back as the default shell), then it's deregistered so
                // "Claude Code" never accumulates in the terminal's profile dropdown.
                // On 17.x the cleanup waits a while as a safety margin (the tab may re-read its profile while
                // it finishes initializing); 18.x resolves it inside the create call.
                var svc = terminalService;
                var prof = profile;
                var pkg = terminalPackage;
                bool ownsProxy = viaBroker;
                void Cleanup()
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    try
                    {
                        if (prof != null && (pkg == null || TryCacheProfileInProc(prof, add: false) == null))
                            PickMethod(terminalServiceType, "RemoveCachedProfile", m => m.GetParameters().Length == 1)?
                                .Invoke(svc, new object?[] { prof });
                    }
                    catch { /* best-effort cleanup; the profile cache is cosmetic */ }

                    // Brokered-service proxies must be disposed - each GetProxyAsync hands out a live RPC
                    // client. The 18.9+ SVsTerminalService route hands back a service the SHELL owns, so
                    // disposing that one would tear down state we don't own.
                    if (ownsProxy) (svc as IDisposable)?.Dispose();
                }

                if (deferCleanup || !ThreadHelper.CheckAccess())
                    _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        if (deferCleanup) await Task.Delay(LegacyProfileLingerMs);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        Cleanup();
                    });
                else
                    Cleanup();
            }
        }
        catch (OperationCanceledException)
        {
            // Lost the race to the timeout: the external-console fallback already took over. The gates
            // above guarantee no terminal was created on this path.
            Log.Info("Native VS terminal attempt aborted after the external-console fallback took over (no second terminal opened).");
            return false;
        }
        catch (Exception e)
        {
            Log.Warn($"Native VS terminal launch failed ({e.GetType().Name}: {e.Message}) - falling back to external console.");
            return false;
        }
    }

    /// <summary>The docked claude tab from the most recent native launch (Guid.Empty = none this session).</summary>
    private static Guid _lastTerminal;

    /// <summary>The tab created by the most recent successful native launch (read right after TryLaunchAsync).</summary>
    public static Guid LastTerminal => _lastTerminal;

    private static IVsWindowFrame? _lastFrame;

    /// <summary>The window frame hosting that tab, or null when it couldn't be identified.</summary>
    public static IVsWindowFrame? LastFrame => _lastFrame;

    private const int CaptionMax = 40;

    /// <summary>
    /// The tab marker: a filled heavy asterisk, Claude's spark. A tab caption is plain text (no color, no
    /// inline image), and the frame icon only renders in some tab layouts - so a glyph is what reliably shows.
    /// </summary>
    private const string Marker = "✱ ";

    private static string CaptionOf(string title)
    {
        title = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (title.Length > CaptionMax) title = title.Substring(0, CaptionMax - 1) + "…";
        return Marker + title;
    }

    /// <summary>Bring a terminal tab forward by its window frame - for a tab whose guid we never got.</summary>
    public static async Task<bool> TryShowFrameAsync(IVsWindowFrame frame, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        try { return ErrorHandler.Succeeded(frame.Show()); }
        catch { return false; }
    }

    /// <summary>Retitle a terminal tab. UI thread. False when the frame is gone (tab closed).</summary>
    public static bool TrySetCaption(IVsWindowFrame frame, string title)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return ErrorHandler.Succeeded(frame.SetProperty((int)__VSFPROPID.VSFPROPID_Caption, CaptionOf(title)));
        }
        catch { return false; }
    }

    private static object? _inProcService;

    /// <summary>
    /// VS 2022 (17.x) route: build the terminal service IN-PROC instead of taking the brokered proxy.
    /// Over the broker, CreateTerminalAsync's profile argument never arrives intact (two 4-arg overloads,
    /// ProfileConfig and ITerminalProfile, share one RPC name; the server ends up with no profile and
    /// substitutes the default one) and AddCachedProfile(ITerminalProfile) can't be deserialized at all -
    /// so every tab opened as a bare Developer PowerShell (seen live on 17.10). The package itself builds
    /// its brokered service as <c>new TerminalService(this.terminalManager)</c> (from the 17.10 IL), so we
    /// do exactly that, and every call then runs on real objects. The service is cached (its ctor hooks
    /// manager events). Returns null on any mismatch, leaving the broker as the fallback. UI thread.
    /// </summary>
    private static object? TryGetInProcService(ref Type serviceInterface, ref Type profileConfigType)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (_inProcService == null)
            {
                if (Package.GetGlobalService(typeof(SVsShell)) is not IVsShell shell) return null;
                var guid = TerminalPackageGuid;
                if (!ErrorHandler.Succeeded(shell.LoadPackage(ref guid, out var vsPackage)) || vsPackage == null) return null;
                object pkg = vsPackage;

                object? manager = null;
                for (var t = pkg.GetType(); t != null && manager == null; t = t.BaseType)
                    manager = t.GetField("terminalManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(pkg);
                var implType = pkg.GetType().Assembly.GetType("Microsoft.VisualStudio.Terminal.TerminalService", false);
                var ctor = manager == null ? null : implType?.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.IsInstanceOfType(manager));
                if (ctor == null)
                {
                    Log.Event("Native VS terminal: in-proc TerminalService not constructible (package shape changed) - using the brokered service.");
                    return null;
                }
                _inProcService = ctor.Invoke(new[] { manager });
            }

            var iface = _inProcService.GetType().GetInterfaces()
                .FirstOrDefault(i => i.FullName == "Microsoft.VisualStudio.Terminal.ITerminalService");
            if (iface == null) { _inProcService = null; return null; }
            // Use the types of the assembly devenv actually loaded, so reflection calls bind to this instance.
            serviceInterface = iface;
            profileConfigType = iface.Assembly.GetType("Microsoft.VisualStudio.Terminal.ProfileConfig", false) ?? profileConfigType;
            Log.Event("Native VS terminal: using the in-proc terminal service (17.x).");
            return _inProcService;
        }
        catch (Exception e)
        {
            Log.Event($"Native VS terminal: in-proc terminal service failed ({e.GetType().Name}: {e.Message}) - using the brokered service.");
            _inProcService = null;
            return null;
        }
    }

    /// <summary>The Terminal's VS package (from its pkgdef, same on 17.x and 18.x).</summary>
    private static readonly Guid TerminalPackageGuid = new("E632CA91-B170-401A-AC2E-6B83FFDC3C10");

    /// <summary>
    /// Add (or remove) <paramref name="profile"/> in TerminalWindowPackage's cached-profile list directly,
    /// in-proc, bypassing the brokered service whose interface-typed AddCachedProfile can't carry it.
    /// Returns the package on success, null if the package or its method isn't reachable. UI thread.
    /// </summary>
    private static object? TryCacheProfileInProc(object profile, bool add)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Package.GetGlobalService(typeof(SVsShell)) is not IVsShell shell) return null;
            var guid = TerminalPackageGuid;
            if (!ErrorHandler.Succeeded(shell.LoadPackage(ref guid, out var vsPackage)) || vsPackage == null) return null;
            object pkg = vsPackage; // a managed package in this AppDomain comes back as the object itself
            string name = add ? "AddCachedProfile" : "RemoveCachedProfile";
            var method = pkg.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == name
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.IsInstanceOfType(profile));
            if (method == null)
            {
                Log.Event($"Native VS terminal: {pkg.GetType().FullName} has no {name}(profile) - using the service route.");
                return null;
            }
            method.Invoke(pkg, new[] { profile });
            return pkg;
        }
        catch (Exception e)
        {
            Log.Event($"Native VS terminal: in-proc profile {(add ? "registration" : "removal")} failed ({e.GetType().Name}: {e.Message}).");
            return null;
        }
    }

    private static HashSet<object> SnapshotToolFrames()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var set = new HashSet<object>(); // RCWs of one COM object are one .NET object, so default equality is identity
        try
        {
            if (Package.GetGlobalService(typeof(SVsUIShell)) is not IVsUIShell shell) return set;
            if (!ErrorHandler.Succeeded(shell.GetToolWindowEnum(out var frames)) || frames == null) return set;
            var one = new IVsWindowFrame[1];
            while (frames.Next(1, one, out uint fetched) == VSConstants.S_OK && fetched == 1)
                if (one[0] != null) set.Add(one[0]);
        }
        catch { /* best-effort: no snapshot = no retitling */ }
        return set;
    }

    /// <summary>The tool frame that appeared since <paramref name="before"/>; polls briefly since the terminal
    /// may materialize its window a moment after CreateTerminalWindowAsync returns. Null if not exactly one.</summary>
    private static async Task<IVsWindowFrame?> FindNewToolFrameAsync(HashSet<object> before, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var added = SnapshotToolFrames().Where(f => !before.Contains(f)).OfType<IVsWindowFrame>().ToList();
            if (added.Count == 1) return added[0];
            if (added.Count > 1) return null; // ambiguous - rather no retitle than the wrong window's
            await Task.Delay(200, ct);
        }
        return null;
    }

    /// <summary>
    /// Bring the docked claude terminal to the front WITH keyboard focus, so Enter sends what a
    /// context action just pushed into the composer. Best-effort: false when no native tab was
    /// launched this session (external console, /ide-connected terminal, restored tab) or the
    /// service/ShowAsync isn't reachable - callers fall back or no-op, never error.
    /// </summary>
    public static async Task<bool> TryFocusAsync(CancellationToken ct, Guid? terminal = null)
    {
        var target = terminal ?? _lastTerminal;
        if (target == Guid.Empty) return false;
        try
        {
            var terminalServiceType = FindType("Microsoft.VisualStudio.Terminal.ITerminalService");
            if (terminalServiceType == null) return false;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            bool viaBroker = false;
            object? svc = await TryGetViaVsServiceAsync(ct);
            if (svc == null && _inProcService != null)
            {
                // A tab created through the in-proc service (17.x) is only known to that service's manager.
                var unusedProfileType = terminalServiceType;
                svc = TryGetInProcService(ref terminalServiceType, ref unusedProfileType);
            }
            if (svc == null)
            {
                svc = await TryGetViaBrokerAsync(terminalServiceType, ct);
                viaBroker = svc != null;
            }
            if (svc == null) return false;

            try
            {
                var show = PickMethod(terminalServiceType, "ShowAsync",
                    m => m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(Guid));
                if (show == null) return false;
                await UnwrapAsync(show.Invoke(svc, new object?[] { target, ct }));
                return true;
            }
            finally
            {
                if (viaBroker) (svc as IDisposable)?.Dispose(); // 18.9+ service is shell-owned (see #32)
            }
        }
        catch (Exception e)
        {
            Log.Event($"focus terminal failed (harmless): {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// VS 18.9+ route: the terminal is a classic VS service again. Both the service marker
    /// (SVsTerminalService) and the interface (IVsTerminalService, whose sole member is
    /// ITerminalService CreateTerminalService()) live in the reflection-loaded assembly, so the whole
    /// hop is reflection - we can't name either type at compile time. Returns null (not throws) on any
    /// miss so the caller can try the older brokered route.
    /// </summary>
    private static async Task<object?> TryGetViaVsServiceAsync(CancellationToken ct)
    {
        try
        {
            var markerType = FindType("Microsoft.VisualStudio.Terminal.SVsTerminalService");
            var vsServiceType = FindType("Microsoft.VisualStudio.Terminal.IVsTerminalService");
            if (markerType == null || vsServiceType == null) return null;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            object? svc = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(markerType);
            if (svc == null) return null;
            ct.ThrowIfCancellationRequested();

            return PickMethod(vsServiceType, "CreateTerminalService", m => m.GetParameters().Length == 0)?
                .Invoke(svc, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Log.Warn($"Native VS terminal: SVsTerminalService route unavailable ({e.GetType().Name}: {e.Message}) - trying the brokered route.");
            return null;
        }
    }

    /// <summary>
    /// Pre-18.9 route: ITerminalService came off the brokered-service bus, addressed by the static
    /// TerminalServiceDescriptors.TerminalServiceDescriptor. VS 18.9 deleted that type outright, so
    /// this is now the VS 2022 (17.x) / older-18.x path only. Returns null (not throws) on any miss.
    /// </summary>
    private static async Task<object?> TryGetViaBrokerAsync(Type terminalServiceType, CancellationToken ct)
    {
        try
        {
            var descriptorsType = FindType("Microsoft.VisualStudio.Terminal.TerminalServiceDescriptors");
            var descriptor = descriptorsType?
                .GetProperty("TerminalServiceDescriptor", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null) as ServiceRpcDescriptor;
            if (descriptor == null) return null;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var containerObj = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsBrokeredServiceContainer));
            if (containerObj is not IBrokeredServiceContainer container) return null;
            IServiceBroker broker = container.GetFullAccessServiceBroker();
            // GetServiceAsync above takes NO token and can stall on a cold ServiceHub; gate before the
            // proxy handout so a late completion never opens a terminal the fallback already replaced.
            ct.ThrowIfCancellationRequested();

            // T = ITerminalService is only known as a reflected Type, so the generic call needs
            // MakeGenericMethod. Overload-tolerant lookup: a bare GetMethod("GetProxyAsync") throws
            // AmbiguousMatchException the moment a ServiceHub.Framework servicing update adds an
            // overload - issue #12, seen on VS2022 AND VS2026 (the assembly ships to both). Select the
            // exact shape we call instead.
            var getProxyOpen = PickMethod(typeof(IServiceBroker), "GetProxyAsync", m =>
            {
                if (!m.IsGenericMethodDefinition) return false;
                var p = m.GetParameters();
                return p.Length == 3
                    && typeof(ServiceRpcDescriptor).IsAssignableFrom(p[0].ParameterType)
                    && p[1].ParameterType == typeof(ServiceActivationOptions)
                    && p[2].ParameterType == typeof(CancellationToken);
            });
            if (getProxyOpen == null) return null;

            var getProxy = getProxyOpen.MakeGenericMethod(terminalServiceType);
            return await UnwrapAsync(
                getProxy.Invoke(broker, new object?[] { descriptor, default(ServiceActivationOptions), ct }));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Log.Warn($"Native VS terminal: brokered route unavailable ({e.GetType().Name}: {e.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Overload-tolerant method lookup. Type.GetMethod(name) THROWS AmbiguousMatchException the moment
    /// a VS/ServiceHub update adds an overload of a member we address by name (issue #12) - and this
    /// whole class talks to assemblies that update underneath us. Enumerate by name, filter by the
    /// call shape we intend, and among survivors prefer the fewest parameters, so a richer overload
    /// added later never breaks the existing call.
    /// </summary>
    private static MethodInfo? PickMethod(Type type, string name, Func<MethodInfo, bool>? where = null)
    {
        IEnumerable<MethodInfo> candidates = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name == name);
        if (where != null) candidates = candidates.Where(where);
        return candidates.OrderBy(m => m.GetParameters().Length).FirstOrDefault();
    }

    // ---------------- reflection plumbing (mirrors Testing/TestRunner.cs) ----------------

    private static readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

    private static Type? FindType(string fullName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var t = a.GetType(fullName, false); if (t != null) return t; } catch { }
        }
        try
        {
            string? ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            if (ide == null) return null;
            string path = Path.Combine(ide, "CommonExtensions", "Microsoft", "Terminal", AsmName);
            if (!File.Exists(path)) return null;
            if (!_loaded.TryGetValue(path, out var asm)) { asm = Assembly.LoadFrom(path); _loaded[path] = asm; }
            return asm.GetType(fullName, false);
        }
        catch { return null; }
    }

    private static async Task<object?> UnwrapAsync(object? ret)
    {
        if (ret == null) return null;
        var rt = ret.GetType();
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ret = rt.GetMethod("AsTask")!.Invoke(ret, null);
        if (ret is Task task)
        {
            await task.ConfigureAwait(true);
            var rp = task.GetType().GetProperty("Result");
            return rp != null && rp.PropertyType != typeof(void) ? rp.GetValue(task) : null;
        }
        return ret;
    }
}
