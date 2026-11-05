using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Engines.TgWsProxy;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class HomeViewModel : PageViewModel
{
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _checkCts;
    private int _checkVersion;

    public HomeViewModel(MainViewModel shell)
        : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
        shell.Profiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasProfiles));
        };
        L.PropertyChanged += (_, _) => RefreshAll();
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            OnPropertyChanged(nameof(Elapsed));
            Services.Connections.RefreshGroupSelection();
        });
        UpdateClock();
    }

    private ConnectionStatus Status => Shell.Primary.Status;

    public bool IsConnected => Status == ConnectionStatus.Connected;

    public bool IsBusy => Status is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting or ConnectionStatus.Reconnecting;

    public bool IsError => Status == ConnectionStatus.Error;

    public bool IsEmpty => Shell.Profiles.Count == 0;

    public bool CanCheckConnection => IsConnected && !Services.IsMock && !NetworkSafetyGuard.IsLocked;

    public bool CanRunCheck => CanCheckConnection && !IsChecking;

    public string? CheckDisabledReason => !IsConnected
        ? null
        : Services.IsMock
            ? L["home.check.mock"]
            : NetworkSafetyGuard.IsLocked ? L["home.check.locked"] : null;

    public string StatusText => Status switch
    {
        ConnectionStatus.Connected => L["status.connected"],
        ConnectionStatus.Connecting => L["status.connecting"],
        ConnectionStatus.Disconnecting => L["status.disconnecting"],
        ConnectionStatus.Reconnecting => L["status.reconnecting"],
        ConnectionStatus.Error => L["status.error"],
        _ => L["status.disconnected"],
    };

    public string ButtonText => IsConnected || IsBusy || Shell.Primary.CleanupPending ? L["home.disconnect"] : L["home.connect"];

    public string Hint
    {
        get
        {
            if (IsError) return L["home.hint.error"];
            if (IsBusy) return L["home.hint.busy"];
            if (!IsConnected)
            {
                return Shell.SelectedProfile is null ? L["home.hint.noprofile"] : L["home.hint.ready"];
            }

            if (Shell.Primary.IsMock) return L["home.hint.mock"];
            if (Shell.Primary.Mode == ConnectionMode.Tun)
            {
                return L["home.hint.connected"];
            }

            return Shell.Primary.Engine switch
            {
                EngineKind.SingBox => Loc.T("home.hint.proxy", Shell.Primary.LocalProxyPort),
                EngineKind.Xray => Loc.T("home.hint.proxy.xray", Shell.Primary.LocalProxyPort, Bydlo.Core.Connection.ProxyPorts.HttpPort(Shell.Primary.LocalProxyPort, EngineKind.Xray)),
                _ => L["home.hint.connected"],
            };
        }
    }

    public string? Error => IsError && Shell.Primary.Error is { } error ? Loc.Error(error, Shell.Primary.ErrorLocalizationKey, Shell.Primary.ErrorLocalizationArguments) : null;

    /// <summary>Relay chain carrying the active connection, if any (issue #75).</summary>
    public bool IsChainActive => IsConnected && Shell.Primary.Chain is not null;

    public string? ActiveChainName => IsChainActive ? Shell.Primary.Chain?.Name : null;

    public string Elapsed => IsConnected && Shell.Primary.ConnectedSince is { } since ? Format.Duration(DateTimeOffset.Now - since) : "00:00";

    public string DownSpeed => Format.Speed(Shell.Primary.LastTraffic?.DownloadBytesPerSecond ?? 0);

    public string UpSpeed => Format.Speed(Shell.Primary.LastTraffic?.UploadBytesPerSecond ?? 0);

    public bool HasTraffic => Shell.Primary.LastTraffic is not null;

    public bool NoStatsEngine => IsConnected && Shell.Primary.LastTraffic is null;

    public string EngineLabel => Labels.Engine(Shell.Primary.Engine);

    public bool IsTun
    {
        get => Services.Settings.Mode == ConnectionMode.Tun;
        set
        {
            if (value == IsTun) return;
            Services.Settings.Mode = value ? ConnectionMode.Tun : ConnectionMode.LocalProxy;
            _ = Services.SaveSettingsAsync();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Hint));
            OnPropertyChanged(nameof(IsProxy));
            OnPropertyChanged(nameof(IsWholeDevice));
            OnPropertyChanged(nameof(ModeHint));
        }
    }

    public bool HasProfiles => Shell.Profiles.Count > 0;

    public bool HasLatencyResults => Shell.HasLatencyResults;

    public bool IsProxy
    {
        get => !IsTun;
        set
        {
            if (value) IsTun = false;
        }
    }

    public bool IsWholeDevice
    {
        get => IsTun;
        set
        {
            if (value) IsTun = true;
        }
    }

    public IReadOnlyList<string> RoutingPresetOptions => Shell.Routing.PresetOptions;

    public int RoutingPresetIndex
    {
        get => (int)Services.Settings.Routing.Preset;
        set
        {
            if (value is < 0 or > 2 || value == RoutingPresetIndex)
            {
                return;
            }

            Services.Settings.Routing.Preset = (RoutingPreset)value;
            OnPropertyChanged();
            _ = Shell.Guard(async () => await Services.SaveSettingsAsync());
        }
    }

    public override void OnShown() => OnPropertyChanged(nameof(RoutingPresetIndex));

    public string ModeHint => IsTun ? L["home.tun.hint"] : Loc.T("home.mode.proxy.hint", Services.Settings.LocalProxyPort);

    [ObservableProperty]
    private bool _showModeHint;

    [RelayCommand]
    private void ToggleModeHint() => ShowModeHint = !ShowModeHint;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _checkFailed;

    [ObservableProperty]
    private IpCheckResult? _checkResult;

    public bool HasCheckResult => CheckResult is not null;

    public bool HasCheckError => CheckFailed;

    public string? CheckError => CheckFailed ? L["home.check.error"] : null;

    public string CheckIp => CheckResult?.Ip ?? "—";

    public string CheckCountry
    {
        get
        {
            var result = CheckResult;
            if (result is null)
            {
                return "—";
            }

            if (!string.IsNullOrWhiteSpace(result.CountryCode))
            {
                try
                {
                    var localized = new RegionInfo(result.CountryCode).DisplayName;
                    if (!string.IsNullOrWhiteSpace(localized))
                    {
                        return localized;
                    }
                }
                catch (ArgumentException)
                {
                    // Some providers return non-ISO codes; fall back to the provider name below.
                }
            }

            return !string.IsNullOrWhiteSpace(result.CountryName) ? result.CountryName : L["home.check.unknown"];
        }
    }

    public string CheckLatency => CheckResult is null ? "—" : Format.Latency(CheckResult.Latency);

    public string CheckButtonText => IsChecking ? L["home.check.checking"] : L["home.check"];

    partial void OnIsCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunCheck));
        OnPropertyChanged(nameof(CheckButtonText));
    }

    partial void OnCheckFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckError));
        OnPropertyChanged(nameof(HasCheckError));
    }

    partial void OnCheckResultChanged(IpCheckResult? value)
    {
        OnPropertyChanged(nameof(HasCheckResult));
        OnPropertyChanged(nameof(CheckIp));
        OnPropertyChanged(nameof(CheckCountry));
        OnPropertyChanged(nameof(CheckLatency));
    }

    public bool ZapretOn => Shell.Zapret.CleanupPending || Shell.Zapret.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;

    public string ZapretState => ToolState(Shell.Zapret, L["status.stopped"]);

    public bool TgOn => Shell.Tg.CleanupPending || Shell.Tg.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;

    public string TgState => ToolState(Shell.Tg, L["status.stopped"]);

    [RelayCommand]
    private async Task ToggleConnection()
    {
        if (IsConnected || IsBusy || Shell.Primary.CleanupPending)
        {
            await Shell.Guard(() => Services.Connections.DisconnectAsync());
            return;
        }

        if (UseGroup)
        {
            await Shell.Guard(async () => await ConnectGroupAsync(SelectedGroup ?? throw ServerGroup.Invalid()));
            return;
        }

        if (Shell.SelectedChain is { } chainItem)
        {
            await Shell.Guard(async () =>
            {
                var chain = Services.Profiles.Chains.FirstOrDefault(c => c.Id == chainItem.Id)
                    ?? throw new EngineException("One of the chain servers no longer exists.", localizationKey: "error.chain.unknown");
                var hops = await Shell.Connections.ResolveChainAsync(chain);
                foreach (var w in Services.Connections.PreviewChainConflicts(hops.Select(h => h.Profile).ToList(), Services.EffectiveSettings).Where(i => i.Severity == ConflictSeverity.Warning))
                {
                    Shell.ShowToast(w.Message);
                }

                await Services.Connections.ConnectChainAsync(chain, hops, Services.EffectiveSettings);
            });
            return;
        }

        if (Shell.SelectedProfile is not { } item)
        {
            Shell.ShowToast(L["home.hint.noprofile"], isError: true);
            Shell.Navigate("connections");
            return;
        }

        foreach (var w in Services.Connections.PreviewConflicts(item.Profile, Services.EffectiveSettings).Where(i => i.Severity == ConflictSeverity.Warning))
        {
            Shell.ShowToast(w.Message);
        }

        await Shell.Guard(() => Services.Connections.ConnectAsync(item.Profile, Services.EffectiveSettings));
    }

    [RelayCommand]
    private async Task CheckConnection()
    {
        if (!CanCheckConnection || IsChecking)
        {
            return;
        }

        ResetCheck();
        var version = _checkVersion;
        var provider = Services.Settings.IpCheckProvider;
        using var cts = new CancellationTokenSource();
        _checkCts = cts;
        IsChecking = true;
        try
        {
            var result = await Services.ConnectionCheck.CheckAsync(
                provider,
                ConnectionCheckService.ResolveProxyPort(Shell.Primary.Engine, Shell.Primary.Mode, Shell.Primary.LocalProxyPort),
                cts.Token);

            if (version != _checkVersion || cts.IsCancellationRequested)
            {
                return;
            }

            CheckResult = result;
            CheckFailed = false;
            Services.Log.Info("check", $"External IP check via {provider} succeeded in {(int)result.Latency.TotalMilliseconds} ms.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (version != _checkVersion || cts.IsCancellationRequested)
            {
                return;
            }

            Services.Log.Warn("check", $"External IP check failed: {ex.Message}");
            CheckFailed = true;
        }
        finally
        {
            if (ReferenceEquals(_checkCts, cts))
            {
                _checkCts = null;
                IsChecking = false;
            }
        }
    }

    [RelayCommand]
    private async Task BestServer()
    {
        UseGroup = false;
        var best = BestServerPicker.PickBest(Shell.Profiles.Select(i => i.Profile), Shell.Latencies);
        if (best is null)
        {
            Shell.ShowToast(L["connections.latency.none"], isError: true);
            Shell.Navigate("connections");
            return;
        }

        if (Shell.Profiles.FirstOrDefault(i => i.Id == best.Id) is { } item)
        {
            Shell.SelectedProfile = item;
            Shell.SelectedChain = null;
        }

        if (!IsConnected && !IsBusy && !Shell.Primary.CleanupPending)
        {
            await ToggleConnectionCommand.ExecuteAsync(null);
            return;
        }

        await Shell.Guard(() => Services.Connections.ConnectAsync(best, Services.EffectiveSettings));
    }

    [RelayCommand]
    private Task ToggleZapret() => Shell.Tools.ToggleZapretCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task ToggleTg() => Shell.Tools.ToggleTgCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task CopyTgLink()
    {
        var secret = await Services.TgSecrets.GetOrCreateAsync();
        var s = Services.Settings.TgWsProxy;
        var link = TgLinkBuilder.Build(s.Host, s.Port, secret, s.FakeTlsDomain);
        if (Services.TopLevel()?.Clipboard is { } cb)
        {
            await cb.SetTextAsync(link);
            Shell.ShowToast(L["toast.copied"]);
        }
    }

    private string ToolState(SessionSnapshot s, string idle) => s.Status switch
    {
        ConnectionStatus.Connected => L["status.running"],
        ConnectionStatus.Connecting => L["status.starting"],
        ConnectionStatus.Disconnecting => L["status.stopping"],
        ConnectionStatus.Error => L["status.error"],
        _ => idle,
    };

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedChain) && Shell.SelectedChain is not null) { UseGroup = false; }
        if (e.PropertyName is nameof(MainViewModel.SelectedProfile) or nameof(MainViewModel.SelectedChain) or nameof(MainViewModel.IsMock))
        {
            ResetCheck();
        }

        if (e.PropertyName is nameof(MainViewModel.Primary) or nameof(MainViewModel.SelectedProfile) or nameof(MainViewModel.SelectedChain) or nameof(MainViewModel.IsMock)
            or nameof(MainViewModel.Zapret) or nameof(MainViewModel.Tg) or nameof(MainViewModel.HasLatencyResults))
        {
            RefreshAll();
        }
    }

    public void RefreshAll()
    {
        UpdateClock();
        if (!IsConnected)
        {
            ResetCheck();
        }

        foreach (var name in new[]
                 {
                     nameof(IsConnected), nameof(IsBusy), nameof(IsError), nameof(IsEmpty), nameof(StatusText), nameof(ButtonText), nameof(Hint), nameof(Error),
                     nameof(Elapsed), nameof(DownSpeed), nameof(UpSpeed), nameof(HasTraffic), nameof(NoStatsEngine), nameof(EngineLabel), nameof(ZapretOn), nameof(ZapretState), nameof(TgOn), nameof(TgState),
                     nameof(IsTun), nameof(IsProxy), nameof(IsWholeDevice), nameof(ModeHint), nameof(HasProfiles), nameof(CanCheckConnection), nameof(CanRunCheck), nameof(CheckDisabledReason), nameof(CheckButtonText),
                      nameof(CheckError), nameof(HasCheckError), nameof(CheckIp), nameof(CheckCountry), nameof(CheckLatency), nameof(HasCheckResult), nameof(HasLatencyResults),
                      nameof(IsChainActive), nameof(ActiveChainName), nameof(IsGroupActive), nameof(ActiveGroupServer),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private void UpdateClock()
    {
        if (IsConnected) { _clock.Start(); }
        else { _clock.Stop(); }
    }

    /// <summary>Clears the card when Settings changes the provider. Safe to call from the UI thread.</summary>
    public void ResetConnectionCheck() => ResetCheck();

    private void ResetCheck()
    {
        _checkVersion++;
        _checkCts?.Cancel();
        _checkCts = null;
        IsChecking = false;
        CheckResult = null;
        CheckFailed = false;
    }
}
