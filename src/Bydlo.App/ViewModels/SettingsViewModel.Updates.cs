using System.Globalization;
using Bydlo.App.Services;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Security;
using Bydlo.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>BYDLO and engine update checks via GitHub Releases (issue #19, unified states in #413).</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _autoUpdateApp;

    private CancellationTokenSource? _automaticUpdateCheck;

    partial void OnAutoUpdateAppChanged(bool value)
    {
        Services.Settings.AutoUpdateApp = value;
        // A downloaded package waits while the switch is off and is never applied behind the user's back.
        Services.AutomaticUpdates.SetOptIn(value);
        if (!value) { _automaticUpdateCheck?.Cancel(); }
        OnPropertyChanged(nameof(CanDownloadUpdate));
        OnPropertyChanged(nameof(UpdateStatus));
        Persist();
    }

    [ObservableProperty]
    private bool _checkForUpdates;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    private int _updateProgress;

    /// <summary>Result of the last release or engine check; the packaged update state wins when it is further along.</summary>
    private string _updateCheckStatus = "";

    // Why the signed catalog refused the last update (issue #411); shown instead of the generic error.
    private string? _updateFailureStatus;

    /// <summary>
    /// Single status line. The packaged update state is authoritative, so a release notification can never
    /// replace "downloaded, installs on exit" with "new version available" (issue #413). A failed confirmation
    /// of the previous version wins over both, because automatic updates are paused (issue #412).
    /// </summary>
    public string UpdateStatus => Services.UpdateRecovery.AutoApplyBlocked
        ? L["settings.update.recovery.blocked"]
        : Services.AutomaticUpdates.State switch
        {
            AppUpdateState.Downloading => L["settings.update.downloading"],
            AppUpdateState.Ready => L["settings.update.ready"],
            AppUpdateState.Deferred => L["settings.update.deferred"],
            AppUpdateState.Failed => _updateFailureStatus ?? L["settings.update.error"],
            AppUpdateState.Available when Services.AutomaticUpdates.AvailableVersion is { } version =>
                Loc.T("settings.update.available", version),
            _ => _updateCheckStatus,
        };

    /// <summary>Secondary line: last check time or live progress.</summary>
    [ObservableProperty]
    private string _updateDetail = "";

    /// <summary>Manual update instructions for a copy the built-in updater does not own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateGuidance))]
    private string _updateGuidanceText = "";

    public bool HasUpdateGuidance => UpdateGuidanceText.Length > 0;

    /// <summary>True only for a Velopack install: the built-in updater can own this copy.</summary>
    public bool SupportsAutomaticUpdates => Services.AutomaticUpdates.Owner == AppUpdateOwner.Velopack;

    /// <summary>False for archives, portable layouts, package-manager installs and Mock Mode.</summary>
    [ObservableProperty]
    private bool _isAutoUpdateAvailable = true;

    [ObservableProperty]
    private string? _updateUrl;

    public bool HasUpdateUrl => UpdateUrl is not null;

    public bool CanDownloadUpdate => IsAutoUpdateAvailable && AutoUpdateApp && ConsentAutomaticUpdates &&
        !IsDownloadingUpdate && !IsCheckingUpdates;

    /// <summary>A manual check also reads GitHub, so it needs the same consent (issue #438).</summary>
    public bool CanCheckUpdates => ConsentAutomaticUpdates && !IsCheckingUpdates;

    partial void OnUpdateUrlChanged(string? value) => OnPropertyChanged(nameof(HasUpdateUrl));

    partial void OnIsDownloadingUpdateChanged(bool value) => OnPropertyChanged(nameof(CanDownloadUpdate));

    partial void OnIsCheckingUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownloadUpdate));
        OnPropertyChanged(nameof(CanCheckUpdates));
    }

    partial void OnIsAutoUpdateAvailableChanged(bool value) => OnPropertyChanged(nameof(CanDownloadUpdate));

    partial void OnCheckForUpdatesChanged(bool value)
    {
        Services.Settings.CheckForUpdates = value;
        Persist();
    }

    /// <summary>Refreshes availability, the manual guidance and the last-check line; safe to call on every startup.</summary>
    public void RefreshUpdateState()
    {
        var blocked = Services.IsMock || NetworkSafetyGuard.IsLocked;
        OnPropertyChanged(nameof(SupportsAutomaticUpdates));
        IsAutoUpdateAvailable = SupportsAutomaticUpdates && !blocked && Services.AutomaticUpdates.IsAvailable();
        // Only mention the kept package when the installer really left one next to the app (issue #412).
        var recoveryGuidance = Services.UpdateRecovery.PreviousPackageAvailable
            ? "settings.update.recovery.previous"
            : "settings.update.recovery.manual";
        UpdateGuidanceText = Services.UpdateRecovery.AutoApplyBlocked
            ? L[recoveryGuidance]
            : SupportsAutomaticUpdates
                ? (blocked ? L["settings.update.auto.unavailable"] : "")
                : L[UpdateGuidance.KeyFor(Services.AutomaticUpdates.InstallKind, Services.AptRepositoryConfigured)];
        RefreshUpdateDetail();
        OnPropertyChanged(nameof(UpdateStatus));
    }

    private void SetUpdateFailureStatus(string value)
    {
        _updateCheckStatus = value;
        _updateFailureStatus = value;
        OnPropertyChanged(nameof(UpdateStatus));
    }

    private void SetUpdateCheckStatus(string value)
    {
        _updateCheckStatus = value;
        _updateFailureStatus = null;
        OnPropertyChanged(nameof(UpdateStatus));
    }

    private void RefreshUpdateDetail()
    {
        if (!ConsentAutomaticUpdates)
        {
            UpdateDetail = L["settings.update.detail.consent"];
            return;
        }
        if (!IsAutoUpdateAvailable)
        {
            UpdateDetail = "";
            return;
        }
        var last = Services.Settings.LastAutomaticUpdateCheck ?? Services.Settings.LastUpdateCheck;
        UpdateDetail = last is { } value
            ? Loc.T("settings.update.detail.checked", value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture))
            : L["settings.update.detail.never"];
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        if (IsCheckingUpdates)
        {
            return;
        }
        await Shell.Guard(async () =>
        {
            if (Services.IsMock || NetworkSafetyGuard.IsLocked)
            {
                throw new InvalidOperationException(L["settings.update.locked"]);
            }
            if (!ConsentAutomaticUpdates)
            {
                throw new InvalidOperationException(L["settings.update.detail.consent"]);
            }
            IsCheckingUpdates = true;
            UpdateUrl = null;
            SetUpdateCheckStatus(L["settings.update.checking"]);
            try
            {
                var client = new GitHubUpdateClient();
                var release = await client.CheckBydloAsync(Version, Services.ShutdownToken);
                Services.Settings.LastUpdateCheck = DateTimeOffset.UtcNow;
                await Services.SaveSettingsAsync();
                if (release is null)
                {
                    Services.AutomaticUpdates.MarkAvailable(null);
                    SetUpdateCheckStatus(await CheckEnginesAsync(client));
                }
                else
                {
                    UpdateUrl = string.IsNullOrWhiteSpace(release.HtmlUrl)
                        ? "https://github.com/endercodezz/BYDLO/releases"
                        : release.HtmlUrl;
                    Services.AutomaticUpdates.MarkAvailable(release.Tag);
                    SetUpdateCheckStatus(Loc.T("settings.update.available", release.Tag));
                    Services.Settings.PendingUpdate = release;
                    await Services.SaveSettingsAsync();
                    Shell.ShowReleaseNotification(release);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                SetUpdateCheckStatus(L["settings.update.error"]);
            }
            finally
            {
                IsCheckingUpdates = false;
                RefreshUpdateDetail();
            }
        });
    }

    /// <summary>Explicit download: bypasses the daily window so the user never waits a day for a release.</summary>
    [RelayCommand]
    private async Task DownloadUpdate()
    {
        if (IsDownloadingUpdate || IsCheckingUpdates)
        {
            return;
        }
        await Shell.Guard(async () =>
        {
            if (Services.IsMock || NetworkSafetyGuard.IsLocked)
            {
                throw new InvalidOperationException(L["settings.update.locked"]);
            }
            if (!IsAutoUpdateAvailable)
            {
                throw new InvalidOperationException(L["settings.update.auto.unavailable"]);
            }
            if (!ConsentAutomaticUpdates)
            {
                throw new InvalidOperationException(L["settings.update.detail.consent"]);
            }
            IsDownloadingUpdate = true;
            UpdateProgress = 0;
            SetUpdateCheckStatus(L["settings.update.downloading"]);
            UpdateDetail = Loc.T("settings.update.detail.progress", 0);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Services.ShutdownToken);
            _automaticUpdateCheck = cancellation;
            try
            {
                var progress = new Progress<int>(value =>
                {
                    UpdateProgress = value;
                    UpdateDetail = Loc.T("settings.update.detail.progress", value);
                });
                var ready = await Services.AutomaticUpdates.DownloadNowAsync(
                    Services.Settings, false, progress, cancellation.Token);
                SetUpdateCheckStatus(ready ? L["settings.update.ready"] : L["settings.update.uptodate"]);
                await Services.SaveSettingsAsync();
            }
            catch (UpdateTrustException ex)
            {
                // The signed catalog refused this update: nothing was installed and the working version stays.
                SetUpdateFailureStatus(L[UpdateTrustMessages.Key(ex.Failure)]);
                Services.Log.Warn("update", $"Update refused: {ex.Failure}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                SetUpdateCheckStatus(L["settings.update.error"]);
            }
            finally
            {
                _automaticUpdateCheck = null;
                IsDownloadingUpdate = false;
                UpdateProgress = 0;
                RefreshUpdateDetail();
            }
        });
    }

    private async Task<string> CheckEnginesAsync(GitHubUpdateClient client)
    {
        var pending = new List<string>();
        foreach (var entry in Services.Lock.Engines)
        {
            if (entry.Id.Equals("tg-ws-proxy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                var release = await client.CheckEngineAsync(entry.Upstream, entry.Version, Services.ShutdownToken);
                if (release is not null)
                {
                    pending.Add($"{entry.Id} {release.Tag}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return L["settings.update.error"];
            }
        }
        return pending.Count == 0 ? L["settings.update.uptodate"] : Loc.T("settings.update.engines", string.Join(", ", pending));
    }

    /// <summary>Scheduled check: at most once per 24 hours, only when enabled and allowed.</summary>
    public async Task CheckForUpdatesIfDueAsync()
    {
        RefreshUpdateState();
        if (!ConsentAutomaticUpdates || !Services.Settings.AutoUpdateApp ||
            Services.IsMock || NetworkSafetyGuard.IsLocked || IsCheckingUpdates)
        {
            return;
        }
        IsCheckingUpdates = true;
        using var automaticCancellation = CancellationTokenSource.CreateLinkedTokenSource(Services.ShutdownToken);
        _automaticUpdateCheck = automaticCancellation;
        try
        {
            var progress = new Progress<int>(value =>
            {
                UpdateProgress = value;
                UpdateDetail = Loc.T("settings.update.detail.progress", value);
            });
            if (await Services.AutomaticUpdates.DownloadIfDueAsync(Services.Settings,
                    Services.IsMock || NetworkSafetyGuard.IsLocked, progress, automaticCancellation.Token))
            {
                SetUpdateCheckStatus(L["settings.update.ready"]);
            }
            if (!Services.Settings.AutoUpdateApp || automaticCancellation.IsCancellationRequested) { return; }
            var release = await new StartupUpdateChecker(new GitHubUpdateClient()).CheckAsync(
                Services.Settings, Version, Services.IsMock || NetworkSafetyGuard.IsLocked, automaticCancellation.Token);
            await Services.SaveSettingsAsync();
            // The notification only records the version; a downloaded package keeps its ready state.
            Services.AutomaticUpdates.MarkAvailable(release?.Tag);
            if (release is not null)
            {
                UpdateUrl = string.IsNullOrWhiteSpace(release.HtmlUrl)
                    ? "https://github.com/endercodezz/BYDLO/releases"
                    : release.HtmlUrl;
                SetUpdateCheckStatus(Loc.T("settings.update.available", release.Tag));
                Shell.ShowReleaseNotification(release);
            }
        }
        catch (UpdateTrustException ex)
        {
            // A scheduled download that fails verification must say so instead of looking like a network error.
            SetUpdateFailureStatus(L[UpdateTrustMessages.Key(ex.Failure)]);
            Services.Log.Warn("update", $"Update refused: {ex.Failure}.");
        }
        catch (Exception ex)
        {
            Services.Log.Warn("update", ex.GetType().Name);
        }
        finally
        {
            _automaticUpdateCheck = null;
            IsCheckingUpdates = false;
            RefreshUpdateDetail();
        }
    }
}
