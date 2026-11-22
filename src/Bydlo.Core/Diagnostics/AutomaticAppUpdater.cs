using Bydlo.Core.Models;

namespace Bydlo.Core.Diagnostics;

/// <summary>Package adapters perform I/O; the policy is independently testable without running an updater.</summary>
public interface IAppUpdateBackend
{
    bool IsInstalled { get; }

    /// <summary>Version of the package that installs on the next exit, if one is downloaded (issue #412).</summary>
    string? PendingVersion { get; }

    Task<bool> DownloadLatestAsync(IProgress<int>? progress, CancellationToken ct);
    void ApplyOnExit();
}

public sealed class AutomaticAppUpdater(Func<IAppUpdateBackend> createBackend, TimeProvider? timeProvider = null)
{
    public static bool HasSeparateDataDirectory(string applicationDirectory, string dataDirectory)
    {
        var application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return !data.Equals(application, comparison) &&
            !data.StartsWith(application + Path.DirectorySeparatorChar, comparison);
    }

    private IAppUpdateBackend? _backend;
    private bool _busy;
    private DateTimeOffset? _lastEmptyCheck;

    /// <summary>
    /// How this copy was installed (issue #413). Detected once at startup by <see cref="AppInstallLocator"/>;
    /// tests and the headless UI verification override it.
    /// </summary>
    public AppInstallKind InstallKind { get; set; } = AppInstallKind.Unknown;

    /// <summary>Who owns updates for this copy. Only a Velopack install may update itself.</summary>
    public AppUpdateOwner Owner => AppUpdateOwnership.OwnerOf(InstallKind);

    /// <summary>
    /// Local recovery protocol (issue #412). When it is set, a package is applied only after the journal and
    /// the user-data snapshot exist, and a failed confirmation stops automatic updates.
    /// </summary>
    public UpdateRecovery? Recovery { get; set; }

    /// <summary>Single user-visible state shared by the release check and the packaged updater.</summary>
    public AppUpdateState State { get; private set; }

    /// <summary>Newest version reported by the release check, if any.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>
    /// Whether this copy can update itself at all. Archives, portable layouts, package-manager installs
    /// and unpackaged builds answer false, and the UI must say so instead of leaving the opt-in switch on
    /// (issues #456, #413).
    /// </summary>
    public bool IsAvailable()
    {
        if (Owner != AppUpdateOwner.Velopack)
        {
            return false;
        }
        try
        {
            return (_backend ??= createBackend()).IsInstalled;
        }
        catch (Exception)
        {
            // A capability probe must never break the settings page; any failure means "cannot update".
            return false;
        }
    }

    /// <summary>
    /// Records a release found by the notification check. A downloaded package keeps its state, so the
    /// notification never hides a package that is already waiting for the next exit.
    /// </summary>
    public void MarkAvailable(string? version)
    {
        AvailableVersion = version;
        if (State is AppUpdateState.Idle or AppUpdateState.Available)
        {
            State = version is null ? AppUpdateState.Idle : AppUpdateState.Available;
        }
    }

    /// <summary>Reflects the opt-in switch: a downloaded package waits while the switch is off.</summary>
    public void SetOptIn(bool enabled)
    {
        if (enabled)
        {
            if (State == AppUpdateState.Deferred)
            {
                State = AppUpdateState.Ready;
            }
        }
        else if (State == AppUpdateState.Ready)
        {
            State = AppUpdateState.Deferred;
        }
    }

    /// <summary>
    /// Postpones installation without discarding the downloaded package. Used when cleanup failed or the
    /// exit was aborted, so the package is never applied over a half-stopped session.
    /// </summary>
    public void Defer()
    {
        if (State == AppUpdateState.Ready)
        {
            State = AppUpdateState.Deferred;
        }
    }

    /// <summary>Scheduled download: at most once per 24 hours after a successful download.</summary>
    public async Task<bool> DownloadIfDueAsync(AppSettings settings, bool blocked,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!AllowsUpdates(settings) || blocked || _busy) { return false; }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (settings.LastAutomaticUpdateCheck is { } last && now - last < TimeSpan.FromHours(24)) { return false; }
        // An empty check only rate-limits re-polling of the feed; a release published soon after is picked up within the hour.
        if (_lastEmptyCheck is { } empty && now - empty < TimeSpan.FromHours(1)) { return false; }
        return await DownloadAsync(settings, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Explicit user request: ignores the daily window but keeps every other guard.</summary>
    public Task<bool> DownloadNowAsync(AppSettings settings, bool blocked,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // An explicit request is the only way out of a failed-confirmation block (issue #412).
        Recovery?.ClearBlock();
        if (!AllowsUpdates(settings) || blocked || _busy) { return Task.FromResult(false); }
        return DownloadAsync(settings, progress, ct);
    }

    /// <summary>
    /// The packaged update needs both the explicit GitHub consent and the download opt-in (issue #438).
    /// Revoking either one stops future downloads and blocks a package that is already staged.
    /// A failed confirmation of the previous version also stops automatic updates (issue #412).
    /// </summary>
    private bool AllowsUpdates(AppSettings settings) =>
        settings.AutoUpdateApp && FeatureConsent.From(settings).Allows(OptionalFeature.AutomaticUpdates) &&
        Recovery?.AutoApplyBlocked != true;

    private async Task<bool> DownloadAsync(AppSettings settings, IProgress<int>? progress, CancellationToken ct)
    {
        _busy = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            // A package-manager or portable copy is never written by the built-in updater (issue #413).
            if (Owner != AppUpdateOwner.Velopack) { return false; }
            var backend = _backend ??= createBackend();
            if (!backend.IsInstalled) { return false; }
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            State = AppUpdateState.Downloading;
            bool downloaded;
            try { downloaded = await backend.DownloadLatestAsync(progress, ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Cancellation records nothing: closing the window mid-download must not consume any window (issue #456).
                State = AvailableVersion is null ? AppUpdateState.Idle : AppUpdateState.Available;
                throw;
            }
            catch (Exception)
            {
                // A failed check is rate-limited like an empty one, so an offline feed is not re-polled on every call.
                _lastEmptyCheck = now;
                State = AppUpdateState.Failed;
                throw;
            }
            // Only a successful download starts the 24h lock.
            if (downloaded) { settings.LastAutomaticUpdateCheck = now; }
            else { _lastEmptyCheck = now; }
            if (!downloaded)
            {
                State = AvailableVersion is null ? AppUpdateState.Idle : AppUpdateState.Available;
                return false;
            }
            // A package without a recovery journal and snapshot is never applied (issue #412).
            if (Recovery is not null && !Recovery.Stage(backend.PendingVersion))
            {
                State = AppUpdateState.Deferred;
                return false;
            }
            // Turning the opt-in off during the download keeps the package but never applies it.
            State = settings.AutoUpdateApp && !ct.IsCancellationRequested
                ? AppUpdateState.Ready
                : AppUpdateState.Deferred;
            return State == AppUpdateState.Ready;
        }
        finally { _busy = false; }
    }

    /// <summary>
    /// Hands a downloaded package to the installer. Called only on a real exit, after cleanup succeeded;
    /// hiding the window in the tray never reaches this method.
    /// </summary>
    public AppUpdateApplyResult ApplyOnExit(AppSettings settings, bool blocked)
    {
        if (!AllowsUpdates(settings) || blocked || _busy || Owner != AppUpdateOwner.Velopack)
        {
            return AppUpdateApplyResult.Nothing;
        }
        if (State != AppUpdateState.Ready)
        {
            return State == AppUpdateState.Deferred ? AppUpdateApplyResult.Deferred : AppUpdateApplyResult.Nothing;
        }
        try
        {
            // Without a durable journal and snapshot the package stays downloaded (issue #412).
            if (Recovery is not null && (!Recovery.CanApply(_backend!.PendingVersion) || !Recovery.MarkApplying()))
            {
                return AppUpdateApplyResult.Deferred;
            }
            _backend!.ApplyOnExit();
        }
        catch (Exception)
        {
            State = AppUpdateState.Failed;
            return AppUpdateApplyResult.Failed;
        }
        State = AppUpdateState.Idle;
        return AppUpdateApplyResult.Applied;
    }
}
