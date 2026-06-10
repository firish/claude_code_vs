using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs;

/// <summary>
/// VS package entry point. Auto-loads when the shell finishes initializing (so the bridge server is
/// up regardless of whether a solution is open) and runs everything on a background-loadable async
/// init. All it owns is the <see cref="BridgeHost"/> lifetime; the real work lives there and in the
/// tool handlers. See build-plan.md §2.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuids.PackageString)]
// Register the extension folder as an assembly probe path. Our package assembly isn't strong-named,
// so the generated pkgdef uses "Assembly=" with no CodeBase; without a binding path, devenv's
// load-by-name (Activator.CreateInstance) can't locate ClaudeCodeVS.dll / ClaudeCodeVS.Protocol.dll
// and the package fails with "Could not load file or assembly 'ClaudeCodeVS' ...". This fixes that.
[ProvideBindingPath]
[ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class ClaudeCodeVsPackage : AsyncPackage
{
    private BridgeHost? _host;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);

        _host = new BridgeHost(this);
        await _host.StartAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _host?.Dispose();
        base.Dispose(disposing);
    }
}

internal static class PackageGuids
{
    public const string PackageString = "d9032717-8a83-4ab5-9b63-2fe9d9a78481";
}
