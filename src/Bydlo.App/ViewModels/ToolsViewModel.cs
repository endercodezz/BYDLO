using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Engines;
using Bydlo.Engines.Installation;
using Bydlo.Engines.TgWsProxy;
using Bydlo.Engines.Zapret;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class EngineRow(EngineDescriptor descriptor) : ObservableObject
{
    public EngineDescriptor Descriptor { get; } = descriptor;

    public string Name => Descriptor.DisplayName;

    public string License => Descriptor.License;

    public string Upstream => Descriptor.Upstream;

    public string? LockId => Descriptor.Kind == EngineKind.Zapret && PlatformInfo.Current.Os != OsFamily.Windows ? "zapret-native" : EngineLocator.LockId(Descriptor.Kind);

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private bool _canInstall;

    [ObservableProperty]
    private string? _version;
}

public sealed partial class ToolsViewModel : PageViewModel
{
    public ToolsViewModel(MainViewModel shell)
        : base(shell)
    {
        var s = Services.Settings;
        _zapretPath = s.Zapret.InstallPath ?? "";
        _gameFilter = s.Zapret.GameFilter;
        _tgHost = s.TgWsProxy.Host;
        _tgPort = s.TgWsProxy.Port;
        _tgDcIps = string.Join('\n', s.TgWsProxy.DcIps);
        _tgFakeTls = s.TgWsProxy.FakeTlsDomain;
        _tgCfFallback = s.TgWsProxy.CfProxyFallback;
        _tgVerbose = s.TgWsProxy.Verbose;
        _tgCfDomains = string.Join('\n', s.TgWsProxy.CfProxyDomains);
        _tgProxyProtocol = s.TgWsProxy.ProxyProtocol;
        _tgProxyProtocolSources = string.Join('\n', s.TgWsProxy.ProxyProtocolTrustedSources);
        LoadSelection();
        shell.PropertyChanged += OnShellChanged;
        L.PropertyChanged += (_, _) =>
        {
            if (_selectionReport is not null && !IsSelecting) { ShowSelectionReport(_selectionReport); }
            OnPropertyChanged(nameof(ZapretError));
            OnPropertyChanged(nameof(TgError));
            RefreshZapret();
            _ = RefreshTgAsync();
        };
        Services.Log.EntriesAdded += (_, batch) => Dispatcher.UIThread.Post(() => OnLogs(batch));
    }

    public static IReadOnlyList<string> GameFilterModes { get; } = ["disabled", "all", "tcp", "udp"];

    public static IReadOnlyList<ZapretUserList> UserLists { get; } = Enum.GetValues<ZapretUserList>();

    public bool CanEditZapretLists => !Services.UsesPrivilegedHelper;

    public ObservableCollection<ZapretStrategy> Strategies { get; } = [];

    public ObservableCollection<EngineRow> Engines { get; } = [];

    public ObservableCollection<string> ZapretLog { get; } = [];

    public ObservableCollection<string> TgLog { get; } = [];

    public bool IsWindows => PlatformInfo.Current.Os == OsFamily.Windows;

    [ObservableProperty]
    private bool _openVpnDriverConsent;

    [RelayCommand]
    private async Task InstallOpenVpnDriver()
    {
        if (!OpenVpnDriverConsent) { return; }
        if (Services.IsMock || Bydlo.Core.Security.NetworkSafetyGuard.IsLocked)
        { Shell.ShowToast(L["tools.openvpn.driver.mock"], isError: true); return; }
        if (Services.Connections.Primary.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting)
        { Shell.ShowToast(L["tools.openvpn.driver.stop"], isError: true); return; }
        IsInstalling = true;
        try
        {
            if (await Shell.Guard(() => Services.OpenVpnDriver.InstallAsync(OpenVpnDriverConsent)))
            { OpenVpnDriverConsent = false; Shell.ShowToast(L["tools.openvpn.driver.done"]); }
        }
        finally { IsInstalling = false; await RefreshEnginesAsync(); }
    }

    public bool CanInstallZapret => Services.Installer.HasAsset(IsWindows ? "zapret" : "zapret-native", PlatformInfo.Current);

    public bool SupportsGameFilter => PlatformInfo.Current.Os is OsFamily.Windows or OsFamily.Linux;

    public string ZapretPlatformNote => L[PlatformInfo.Current.Os == OsFamily.MacOS ? "tools.zapret.macos" : "tools.zapret.native"];

    // ---------- Zapret ----------

    [ObservableProperty]
    private string _zapretPath;

    [ObservableProperty]
    private ZapretStrategy? _selectedStrategy;

    [ObservableProperty]
    private string _gameFilter;

    [ObservableProperty]
    private ZapretUserList _selectedList = ZapretUserList.General;

    [ObservableProperty]
    private string _listText = "";

    [ObservableProperty]
    private string _zapretInfo = "";

    [ObservableProperty]
    private bool _isInstalling;

    public bool ZapretRunning => Shell.Zapret.CleanupPending || Shell.Zapret.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;

    public string ZapretStatus => StatusOf(Shell.Zapret);

    public string? ZapretError => Shell.Zapret.Status == ConnectionStatus.Error ? Loc.Error(Shell.Zapret.Error ?? "", Shell.Zapret.ErrorLocalizationKey, Shell.Zapret.ErrorLocalizationArguments) : null;

    /// <summary>Zapret failed only because of missing elevation: offer a one-click restart here.</summary>
    public bool ZapretNeedsElevation => Shell.Zapret.Status == ConnectionStatus.Error
        && Shell.Zapret.ErrorLocalizationKey == "error.engine.zapret.admin";

    /// <summary>Restart button is useful only where the OS supports relaunch (Windows, non-elevated).</summary>
    public bool ShowRestartElevated => ZapretNeedsElevation
        && !Services.Privileges.IsElevated
        && Services.Privileges.CanRestartElevated;

    public bool ShowZapretHelperSetup => ZapretNeedsElevation
        && !Services.Privileges.IsElevated
        && Shell.Settings.HelperSupported && !Shell.Settings.HelperInstalled;

    // ---------- TgWsProxy ----------

    [ObservableProperty]
    private string _tgHost;

    [ObservableProperty]
    private int _tgPort;

    [ObservableProperty]
    private string _tgDcIps;

    [ObservableProperty]
    private string _tgFakeTls;

    [ObservableProperty]
    private bool _tgCfFallback;

    [ObservableProperty]
    private bool _tgVerbose;

    [ObservableProperty]
    private string _tgCfDomains;

    [ObservableProperty]
    private bool _tgProxyProtocol;

    [ObservableProperty]
    private string _tgProxyProtocolSources;

    [ObservableProperty]
    private string _tgLink = "";

    [ObservableProperty]
    private string _tgRuntimeInfo = "";

    public bool CanOpenInTelegram => Shell.Tg.Status == ConnectionStatus.Connected && !Shell.Tg.CleanupPending;
    public bool TgRunning => Shell.Tg.CleanupPending || Shell.Tg.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;

    public string TgStatus => StatusOf(Shell.Tg);

    public string? TgError => Shell.Tg.Status == ConnectionStatus.Error ? Loc.Error(Shell.Tg.Error ?? "", Shell.Tg.ErrorLocalizationKey) : null;

    public override void OnShown()
    {
        // Capture the saved id before RefreshZapret fills the combo with a fallback:
        // the startup check must test the remembered strategy, never the default.
        var savedAtOpen = Services.Settings.Zapret.Strategy;
        if (!IsSelecting) { RefreshZapret(); }
        _ = RefreshTgAsync();
        _ = RefreshEnginesAsync();
        MaybeRunStartupCheck(savedAtOpen);
    }

    public void ReloadSettings()
    {
        _loadingSettings = true;
        try
        {
            var s = Services.Settings;
            ZapretPath = s.Zapret.InstallPath ?? "";
            GameFilter = s.Zapret.GameFilter;
            SelectedStrategy = null;
            TgHost = s.TgWsProxy.Host;
            TgPort = s.TgWsProxy.Port;
            TgDcIps = string.Join('\n', s.TgWsProxy.DcIps);
            TgFakeTls = s.TgWsProxy.FakeTlsDomain;
            TgCfFallback = s.TgWsProxy.CfProxyFallback;
            TgVerbose = s.TgWsProxy.Verbose;
            TgCfDomains = string.Join('\n', s.TgWsProxy.CfProxyDomains);
            TgProxyProtocol = s.TgWsProxy.ProxyProtocol;
            TgProxyProtocolSources = string.Join('\n', s.TgWsProxy.ProxyProtocolTrustedSources);
        }
        finally { _loadingSettings = false; }
    }

    private bool _loadingSettings;

    /// <summary>Set while RefreshZapret assigns the combo; the change handler must not persist it.</summary>
    private bool _refreshingZapret;

    // ---------- Zapret commands ----------

    [RelayCommand]
    private async Task ToggleZapret()
    {
        if (ZapretRunning)
        {
            await Shell.Guard(() => Services.Connections.StopToolAsync(ConnectionManager.ZapretKey));
            return;
        }

        SaveZapretSettings();
        await Shell.Guard(() => Services.Connections.StartZapretAsync(Services.EffectiveSettings));
        if (Services.Connections.Zapret.ErrorLocalizationKey == "error.engine.zapret.admin")
        {
            // Also expose recovery when activation came from the tray.
            Shell.Navigate("tools");
        }
    }

    [RelayCommand]
    private async Task InstallZapretHelper()
    {
        if (!ShowZapretHelperSetup || !Shell.Settings.CanManageHelper) { return; }
        var installed = await Shell.Settings.InstallHelperForZapretAsync();
        RefreshListEditing();
        OnPropertyChanged(nameof(ShowZapretHelperSetup));
        if (installed && Services.UsesPrivilegedHelper)
        {
            await ToggleZapret();
        }
    }

    [RelayCommand]
    private Task RestartElevated() => Shell.Settings.RestartElevatedCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task BrowseZapret()
    {
        var storage = Services.TopLevel()?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L["tools.zapret.choose"] });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            ZapretPath = path;
            SaveZapretSettings();
            RefreshZapret();
        }
    }

    [RelayCommand]
    private async Task InstallZapret()
    {
        if (ZapretRunning)
        {
            Shell.ShowToast(L["tools.zapret.stopupdate"], isError: true);
            return;
        }

        IsInstalling = true;
        try
        {
            await Shell.Guard(async () =>
            {
                var previous = Services.Zapret.ResolveInstallation(Services.Settings);
                var install = await Services.ZapretUpdater.InstallOrUpdateAsync(previous);
                ZapretPath = "";
                Services.Settings.Zapret.InstallPath = null;
                await Services.SaveSettingsAsync();
                Shell.ShowToast(Loc.T("tools.zapret.installed", install.Version ?? "?"));
            });
        }
        finally
        {
            IsInstalling = false;
            RefreshZapret();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditZapretLists))]
    private void SaveList()
    {
        // Recheck here as well: helper installation can change while Tools is open.
        if (!CanEditZapretLists)
        {
            Shell.ShowToast(L["tools.zapret.lists.helper"], isError: true);
            return;
        }

        if (Services.Zapret.ResolveInstallation(Services.Settings) is { IsValid: true } install)
        {
            install.WriteUserList(SelectedList, ListText);
            ListText = install.ReadUserList(SelectedList);
            Shell.ShowToast(ZapretRunning ? L["tools.zapret.saved.restart"] : L["toast.saved"]);
        }
    }

    partial void OnSelectedListChanged(ZapretUserList value) => LoadList();

    partial void OnSelectedStrategyChanged(ZapretStrategy? value)
    {
        // RefreshZapret fills the combo programmatically (saved or default); that must never persist.
        if (_refreshingZapret || _loadingSettings) { return; }
        SaveZapretSettings();
    }

    partial void OnGameFilterChanged(string value) => SaveZapretSettings();

    private void RefreshZapret()
    {
        RefreshListEditing();
        var install = Services.Zapret.ResolveInstallation(Services.Settings);
        if (install is null || !install.IsValid)
        {
            Strategies.Clear();
            ZapretInfo = CanInstallZapret ? L["tools.zapret.missing"] : L["tools.zapret.platform"];
            return;
        }

        var current = Services.Settings.Zapret.Strategy;
        Strategies.ReplaceWith(install.Strategies.Select(s => s.FileName.StartsWith("native-", StringComparison.Ordinal)
            ? s with { DisplayName = L["tools.zapret." + s.FileName + (PlatformInfo.Current.Os == OsFamily.MacOS ? ".macos" : "")] } : s));
        _refreshingZapret = true;
        try
        {
            SelectedStrategy = Strategies.FirstOrDefault(s => s.FileName == current) ?? Strategies.FirstOrDefault(s => s.FileName == ZapretEngine.DefaultStrategy(install));
        }
        finally { _refreshingZapret = false; }
        ZapretInfo = Loc.T("tools.zapret.found", install.Version ?? "?", Strategies.Count, install.Root);
        LoadList();
    }

    private void LoadList()
    {
        if (Services.Zapret.ResolveInstallation(Services.Settings) is { IsValid: true } install)
        {
            ListText = install.ReadUserList(SelectedList);
        }
    }

    private void SaveZapretSettings()
    {
        if (_loadingSettings) { return; }
        var z = Services.Settings.Zapret;
        z.InstallPath = string.IsNullOrWhiteSpace(ZapretPath) ? null : ZapretPath.Trim();
        z.Strategy = SelectedStrategy?.FileName ?? z.Strategy;
        z.GameFilter = GameFilter;
        _ = Services.SaveSettingsAsync();
    }

    // ---------- TgWsProxy commands ----------

    [RelayCommand]
    private async Task ToggleTg()
    {
        if (TgRunning)
        {
            await Shell.Guard(() => Services.Connections.StopToolAsync(ConnectionManager.TgWsProxyKey));
            return;
        }

        if (!SaveTgSettings())
        {
            return;
        }

        await Services.TgSecrets.GetOrCreateAsync();
        await Shell.Guard(() => Services.Connections.StartTgWsProxyAsync(Services.EffectiveSettings));
        await RefreshTgAsync();
    }

    [RelayCommand]
    private async Task CopyTgLinkToClipboard()
    {
        await RefreshTgAsync();
        if (Services.TopLevel()?.Clipboard is { } cb)
        {
            await cb.SetTextAsync(TgLink);
            Shell.ShowToast(L["toast.copied"]);
        }
    }

    [RelayCommand]
    private async Task OpenInTelegram()
    {
        await Shell.Guard(async () =>
        {
            var session = await Services.Connections.GetRunningTgWsProxyAsync(Services.ShutdownToken);
            if (session is not { } active) { return; }
            var link = TgLinkBuilder.Build(active.Settings.Host, active.Settings.Port, active.Secret, active.Settings.FakeTlsDomain);
            var opened = Services.TopLevel()?.Launcher is { } launcher && await launcher.LaunchUriAsync(new Uri(link));
            if (!opened) { Shell.ShowToast(L["tools.tg.notelegram"], isError: true); }
            await RefreshTgAsync();
        });
    }

    [RelayCommand]
    private async Task RegenerateSecret()
    {
        await Services.TgSecrets.RegenerateAsync();
        await RefreshTgAsync();
        Shell.ShowToast(TgRunning ? L["tools.tg.secret.restart"] : L["tools.tg.secret.new"]);
    }

    [RelayCommand]
    private void SaveTg()
    {
        if (SaveTgSettings())
        {
            _ = RefreshTgAsync();
            Shell.ShowToast(TgRunning ? L["tools.tg.saved.restart"] : L["toast.saved"]);
        }
    }

    private bool SaveTgSettings()
    {
        if (TgPort is < 1 or > 65535)
        {
            Shell.ShowToast(L["tools.tg.badport"], isError: true);
            return false;
        }

        var sources = TgProxyProtocolSources.Split('\n', ',', ' ').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        try
        {
            TgWsProxyOptions.ParseTrustedSources(sources);
        }
        catch (EngineException ex)
        {
            Shell.ShowToast(Loc.Error(ex.Message, ex.LocalizationKey), isError: true);
            return false;
        }

        var t = Services.Settings.TgWsProxy;
        t.Host = string.IsNullOrWhiteSpace(TgHost) ? "127.0.0.1" : TgHost.Trim();
        t.Port = TgPort;
        t.DcIps = [.. TgDcIps.Split('\n', ',').Select(x => x.Trim()).Where(x => x.Length > 0)];
        t.FakeTlsDomain = TgFakeTls.Trim();
        t.CfProxyFallback = TgCfFallback;
        t.Verbose = TgVerbose;
        t.CfProxyDomains = [.. TgCfDomains.Split('\n', ',', ' ').Select(x => x.Trim()).Where(x => x.Length > 0)];
        t.ProxyProtocol = TgProxyProtocol;
        t.ProxyProtocolTrustedSources = sources;
        _ = Services.SaveSettingsAsync();
        return true;
    }

    private async Task RefreshTgAsync()
    {
        var secret = await Services.TgSecrets.GetOrCreateAsync();
        var t = Services.Settings.TgWsProxy;
        TgLink = TgLinkBuilder.Build(t.Host, t.Port, secret, t.FakeTlsDomain);
        var availability = await Services.Registry.Create(EngineKind.TgWsProxy, new AppSettings { TgWsProxy = t }).CheckAvailabilityAsync(Services.Settings);
        TgRuntimeInfo = availability.IsReady ? L["tools.tg.runtime.builtin"] : Loc.Error(availability.Message, availability.LocalizationKey);
    }

    // ---------- Engines ----------

    [RelayCommand]
    private async Task InstallEngine(EngineRow row)
    {
        if (row.LockId is null)
        {
            return;
        }

        IsInstalling = true;
        row.Status = L["tools.engines.downloading"];
        try
        {
            var ok = await Shell.Guard(() => Services.Installer.InstallAsync(row.LockId, PlatformInfo.Current,
                new Progress<InstallProgress>(p => row.Status = p.Fraction is { } f ? $"{L["tools.engines.downloading"]} {f:P0}" : p.Stage)));
            if (ok)
            {
                Shell.ShowToast(Loc.T("tools.engines.installed", row.Name));
            }
        }
        finally
        {
            IsInstalling = false;
            await RefreshEnginesAsync();
        }
    }

    private async Task RefreshEnginesAsync()
    {
        var real = new AppSettings
        {
            EnginePaths = Services.Settings.EnginePaths,
            Zapret = Services.Settings.Zapret,
            TgWsProxy = Services.Settings.TgWsProxy,
            Mode = ConnectionMode.LocalProxy,
        };
        var rows = new List<EngineRow>();
        foreach (var d in Services.Registry.Descriptors)
        {
            var row = new EngineRow(d);
            var a = await Services.Registry.Create(d.Kind, real).CheckAvailabilityAsync(real);
            row.IsReady = a.Status is AvailabilityStatus.Ready or AvailabilityStatus.NeedsElevation;
            row.Status = a.Status switch
            {
                AvailabilityStatus.Ready => L["engine.ready"],
                AvailabilityStatus.NeedsElevation => L["engine.needsadmin"],
                AvailabilityStatus.NotInstalled => L["engine.notinstalled"],
                AvailabilityStatus.NotImplemented => L["engine.planned"],
                _ => L["engine.unsupported"],
            };
            row.CanInstall = row.LockId is not null && d.Kind != EngineKind.Zapret && Services.Installer.HasAsset(row.LockId, PlatformInfo.Current) && !row.IsReady;
            row.Version = row.LockId is null ? null : Services.Lock.Find(row.LockId)?.Version;
            rows.Add(row);
        }

        Engines.ReplaceWith(rows);
    }

    private string StatusOf(SessionSnapshot s) => s.Status switch
    {
        ConnectionStatus.Connected => L["status.running"],
        ConnectionStatus.Connecting => L["status.starting"],
        ConnectionStatus.Disconnecting => L["status.stopping"],
        ConnectionStatus.Error => L["status.error"],
        _ => L["status.stopped"],
    };

    private void OnLogs(IReadOnlyList<LogEntry> batch)
    {
        foreach (var e in batch)
        {
            var target = e.Source switch
            {
                ConnectionManager.ZapretKey => ZapretLog,
                ConnectionManager.TgWsProxyKey => TgLog,
                _ => null,
            };
            target?.Add($"{e.Time:HH:mm:ss}  {e.Message}");
        }

        Trim(ZapretLog);
        Trim(TgLog);
    }

    private static void Trim(ObservableCollection<string> log)
    {
        while (log.Count > 300)
        {
            log.RemoveAt(0);
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsMock))
        {
            RefreshListEditing();
        }

        if (e.PropertyName == nameof(MainViewModel.Zapret))
        {
            OnPropertyChanged(nameof(ZapretRunning));
            OnPropertyChanged(nameof(ZapretStatus));
            OnPropertyChanged(nameof(ZapretError));
            OnPropertyChanged(nameof(ZapretNeedsElevation));
            OnPropertyChanged(nameof(ShowRestartElevated));
            OnPropertyChanged(nameof(ShowZapretHelperSetup));
        }
        else if (e.PropertyName == nameof(MainViewModel.Tg))
        {
            OnPropertyChanged(nameof(TgRunning));
            OnPropertyChanged(nameof(CanOpenInTelegram));
            OnPropertyChanged(nameof(TgStatus));
            OnPropertyChanged(nameof(TgError));
        }
    }

    private void RefreshListEditing()
    {
        OnPropertyChanged(nameof(CanEditZapretLists));
        SaveListCommand.NotifyCanExecuteChanged();
    }
}
