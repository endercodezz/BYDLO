using System.Runtime.InteropServices;
using Bydlo.Core;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Security;
using Bydlo.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace Bydlo.App.Services;

/// <summary>
/// Velopack adapter. Everything Velopack sees is authenticated against the signed update catalog first,
/// because on Windows Velopack 1.2.161 may extract and replace Update.exe during
/// <c>DownloadUpdatesAsync</c>, so a check after the download would be too late (issue #411).
/// </summary>
internal sealed class PackagedAppUpdater(string dataDirectory, HttpClient http) : IAppUpdateBackend
{
    private readonly UpdateManager _manager = new(new GithubSource("https://github.com/endercodezz/BYDLO", null, false));
    private readonly UpdateCatalogVerifier _verifier = new();
    private TrustedUpdate? _trusted;

    public bool IsInstalled => !File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")) &&
        AutomaticAppUpdater.HasSeparateDataDirectory(AppContext.BaseDirectory, dataDirectory) && _manager.IsInstalled;

    /// <summary>Version Velopack will install on the next exit; the recovery journal records it (issue #412).</summary>
    public string? PendingVersion => _manager.UpdatePendingRestart?.Version?.ToString();

    public async Task<bool> DownloadLatestAsync(IProgress<int>? progress, CancellationToken ct)
    {
        NetworkSafetyGuard.EnsureAllowed("download application update");
        _trusted = null;
        var catalogJson = await new UpdateCatalogClient(http).DownloadAsync(ct).ConfigureAwait(false);
        var trusted = _verifier.Verify(catalogJson, UpdateCatalogVerifier.StableChannel, CurrentRid(), AppVersion.Current);
        var update = await _manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        if (update is null)
        {
            return false;
        }

        // Refuse before DownloadUpdatesAsync: the feed asset must be exactly the signed one.
        var asset = update.TargetFullRelease;
        trusted.EnsureMatches(asset.Version?.ToString(), asset.FileName, asset.Size, asset.SHA256);
        Action<int>? report = progress is null ? null : progress.Report;
        await _manager.DownloadUpdatesAsync(update, report, ct).ConfigureAwait(false);
        var pending = _manager.UpdatePendingRestart;
        if (pending is null)
        {
            return false;
        }

        // Re-check what Velopack is about to apply: a package swapped after the download must not install.
        trusted.EnsureMatches(pending.Version?.ToString(), pending.FileName, pending.Size, pending.SHA256);
        _trusted = trusted;
        return true;
    }

    public void ApplyOnExit()
    {
        NetworkSafetyGuard.EnsureAllowed("install application update");
        var pending = _manager.UpdatePendingRestart;
        if (_trusted is null || pending is null)
        {
            // Nothing verified in this session: keep the working version instead of applying an unknown package.
            return;
        }

        _trusted.EnsureMatches(pending.Version?.ToString(), pending.FileName, pending.Size, pending.SHA256);
        _manager.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
    }

    /// <summary>Portable RID of the running build, e.g. <c>win-x64</c>.</summary>
    private static string CurrentRid() => RuntimeInformation.RuntimeIdentifier.ToLowerInvariant();
}
