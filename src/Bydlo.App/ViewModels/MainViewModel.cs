using System.Collections.ObjectModel;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    public NavItem(string key, string titleKey, string iconKey, PageViewModel page)
    {
        Key = key;
        TitleKey = titleKey;
        IconKey = iconKey;
        Page = page;
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ShortTitle));
        };
    }

    public string Key { get; }

    public string TitleKey { get; }

    public string IconKey { get; }

    public PageViewModel Page { get; }

    public string Title => Loc.Instance[TitleKey];

    /// <summary>Compact label for the phone bottom bar.</summary>
    public string ShortTitle => Loc.Instance[TitleKey + ".short"];

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Shell: navigation, shared profile list, live session state, toasts.</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private const int HistoryLength = 60;
    private int _toastVersion;
    private string? _toastLocalizationKey;
    private string[]? _toastLocalizationArguments;

    public MainViewModel(AppServices services, Bydlo.Core.Import.SubscriptionClient? subscriptionClient = null, Bydlo.Core.Diagnostics.ServerLatencyProbe? latencyProbe = null)
    {
        Services = services;
        L.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ReleaseNotificationText));
            if (Toast is not null && _toastLocalizationKey is not null)
            {
                Toast = Loc.Error(Toast, _toastLocalizationKey, _toastLocalizationArguments);
            }

            foreach (var c in Chains)
            {
                c.RefreshDetails();
            }
        };
        _isOnboardingVisible = !services.Settings.OnboardingCompleted;
        _onboardingLanguage = services.Settings.Language;
        _onboardingTheme = services.Settings.Theme;
        _consentAutomaticUpdates = services.Settings.ConsentAutomaticUpdates;
        _consentNotifications = services.Settings.ConsentNotifications;
        _consentThemes = services.Settings.ConsentThemes;
        ReloadProfiles();
        ReloadChains();

        Home = new HomeViewModel(this);
        Connections = new ConnectionsViewModel(this);
        AddServerWizard = new AddServerWizardViewModel(this, subscriptionClient, latencyProbe);
        ChainEditor = new ChainEditorViewModel(this);
        Tools = new ToolsViewModel(this);
        Routing = new RoutingViewModel(this);
        Statistics = new StatisticsViewModel(this);
        Settings = new SettingsViewModel(this);
        ActiveConnections = new ActiveConnectionsViewModel(this);
        Contributors = new ContributorsViewModel(this);
        NavItems =
        [
            new("home", "nav.home", "LineHome", Home),
            new("connections", "nav.connections", "LineServers", Connections),
            new("tools", "nav.tools", "LineTools", Tools),
            new("routing", "nav.routing", "LineRouting", Routing),
            new("statistics", "nav.statistics", "LineStats", Statistics),
            new("settings", "nav.settings", "LineSettings", Settings),
            new("contributors", "nav.contributors", "LineUsers", Contributors),
        ];
        PrimaryNavItems = NavItems.Where(item => item.Key is not ("routing" or "statistics" or "contributors")).ToArray();
        ConnectionNavItems = PrimaryNavItems.Where(item => item.Key != "settings").ToArray();
        NetworkNavItems = NavItems.Where(item => item.Key is "routing" or "statistics").ToArray();
        SettingsNav = PrimaryNavItems.Single(item => item.Key == "settings");
        _currentNav = NavItems[0];
        _currentNav.IsSelected = true;

        services.Connections.SessionChanged += (_, s) => Dispatcher.UIThread.Post(() => OnSessionChanged(s));
        Chains.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasChains));
            OnPropertyChanged(nameof(HasNoChains));
        };
        _hasOrphans = services.Orphans.Count > 0;
        ContributorsNav = NavItems.Single(item => item.Key == "contributors");
        Contributors.Show(services.Settings.Contributors);
        Loc.Instance.PropertyChanged += (_, _) => OnNoticeLanguageChanged();
        RefreshCrashReports();
        ShowStoreRecoveryToasts();
        ShowUpdateRecoveryToast();
        _ = RunStartupChecksAsync();
        services.StartSubscriptionAutoRefresh(Connections.RunAutoRefreshAsync);
    }

    private void ShowStoreRecoveryToasts()
    {
        var profiles = Services.Profiles.LoadStatus;
        var settings = Services.SettingsStore.LoadStatus;
        if (profiles == Bydlo.Core.Storage.StoreLoadStatus.NewerVersion ||
            settings == Bydlo.Core.Storage.StoreLoadStatus.NewerVersion)
        {
            ShowToast(L["store.newer"], isError: true, localizationKey: "store.newer");
            return;
        }
        if (profiles == Bydlo.Core.Storage.StoreLoadStatus.RecoveredFromBackup ||
            settings == Bydlo.Core.Storage.StoreLoadStatus.RecoveredFromBackup)
        {
            ShowToast(L["store.recovered"], isError: true, localizationKey: "store.recovered");
            return;
        }
        if (profiles == Bydlo.Core.Storage.StoreLoadStatus.ResetEmpty ||
            settings == Bydlo.Core.Storage.StoreLoadStatus.ResetEmpty)
        {
            ShowToast(L["store.reset"], isError: true, localizationKey: "store.reset");
        }
    }

    private UpdateRecoveryStartup? _shownUpdateRecovery;

    /// <summary>
    /// Explains an interrupted or unconfirmed update. Called once at startup and again after the readiness
    /// confirmation, which may turn the state into <see cref="UpdateRecoveryStartup.Blocked"/> (issue #412).
    /// </summary>
    public void ShowUpdateRecoveryToast()
    {
        var startup = Services.UpdateRecovery.Startup;
        if (startup == _shownUpdateRecovery) { return; }
        _shownUpdateRecovery = startup;
        switch (startup)
        {
            case UpdateRecoveryStartup.Blocked:
                ShowToast(Loc.T("update.recovery.blocked", Bydlo.Core.AppVersion.Current), isError: true,
                    localizationKey: "update.recovery.blocked", localizationArguments: [Bydlo.Core.AppVersion.Current]);
                break;
            case UpdateRecoveryStartup.Interrupted:
                ShowToast(Loc.T("update.recovery.interrupted", Bydlo.Core.AppVersion.Current), isError: true,
                    localizationKey: "update.recovery.interrupted", localizationArguments: [Bydlo.Core.AppVersion.Current]);
                break;
        }
    }

    public AppServices Services { get; }

    public HomeViewModel Home { get; }

    public ConnectionsViewModel Connections { get; }

    public AddServerWizardViewModel AddServerWizard { get; }

    public bool CanUseWorkspace => !IsAddServerOpen && !IsChainEditorOpen && !IsOnboardingVisible && !HasImportPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseWorkspace))]
    private bool _isAddServerOpen;

    [RelayCommand]
    private void OpenAddServer()
    {
        if (AddServerWizard.Busy || HasImportPreview || IsImportBusy) { return; }
        DismissClipboardOffer();
        AddServerWizard.Reset();
        IsAddServerOpen = true;
    }

    [RelayCommand]
    private async Task OpenImport(string source)
    {
        if (IsAddServerOpen || AddServerWizard.Busy || HasImportPreview || IsImportBusy) { return; }
        OpenAddServer();
        if (!IsAddServerOpen || AddServerWizard.Busy) { return; }
        switch (source)
        {
            case "catalog":
                AddServerWizard.ChooseCatalogCommand.Execute(null);
                break;
            case "file":
                AddServerWizard.ChooseFileCommand.Execute(null);
                await AddServerWizard.SelectFileCommand.ExecuteAsync(null);
                break;
            case "qr":
                AddServerWizard.ChooseQrCommand.Execute(null);
                await AddServerWizard.SelectQrCommand.ExecuteAsync(null);
                break;
            default:
                AddServerWizard.ChooseLinkCommand.Execute(null);
                if (source == "clipboard") { await AddServerWizard.PasteCommand.ExecuteAsync(null); }
                break;
        }
    }

    public ChainEditorViewModel ChainEditor { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseWorkspace))]
    private bool _isChainEditorOpen;

    [RelayCommand]
    private void NewChain()
    {
        if (IsChainEditorOpen) { return; }
        ChainEditor.Create();
        IsChainEditorOpen = true;
    }

    [RelayCommand]
    private void EditChain(ChainItem item)
    {
        if (IsChainEditorOpen) { return; }
        ChainEditor.Edit(item);
        IsChainEditorOpen = true;
    }

    public ToolsViewModel Tools { get; }

    public RoutingViewModel Routing { get; }

    public StatisticsViewModel Statistics { get; }

    public ContributorsViewModel Contributors { get; }

    /// <summary>Contributors page entry in the sidebar corner, outside the primary navigation.</summary>
    public NavItem ContributorsNav { get; }

    public SettingsViewModel Settings { get; }

    public ActiveConnectionsViewModel ActiveConnections { get; }

    public IReadOnlyList<NavItem> NavItems { get; }
    public IReadOnlyList<NavItem> PrimaryNavItems { get; }
    public IReadOnlyList<NavItem> ConnectionNavItems { get; }
    public IReadOnlyList<NavItem> NetworkNavItems { get; }
    public NavItem SettingsNav { get; }
    public bool IsSettingsDetail => IsCompact && CurrentNav.Key is "routing" or "statistics" or "contributors";

    public static IReadOnlyList<string> OnboardingLanguages { get; } = ["auto", "en", "ru"];

    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    /// <summary>Relay chains (issue #75), entry → exit.</summary>
    public ObservableCollection<ChainItem> Chains { get; } = [];

    public bool HasChains => Chains.Count > 0;

    public bool HasNoChains => Chains.Count == 0;

    [ObservableProperty]
    private ChainItem? _selectedChain;

    /// <summary>Last latency probe per profile id (issue #10). Empty until the user checks.</summary>
    public Dictionary<Guid, ServerLatencyResult> Latencies { get; } = new();

    public bool HasLatencyResults => Latencies.Count > 0;

    [ObservableProperty]
    private bool _sortByLatency;

    partial void OnSortByLatencyChanged(bool value) => ReloadProfiles();

    /// <summary>Last 60 seconds of download / upload speed (bytes/s) for charts.</summary>
    public ObservableCollection<double> DownHistory { get; } = [];

    public ObservableCollection<double> UpHistory { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleToast))]
    private NavItem _currentNav;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsDetail))]
    private bool _isCompact;

    [ObservableProperty]
    private ProfileItem? _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleToast))]
    private SessionSnapshot _primary = new(ConnectionManager.PrimaryKey, ConnectionStatus.Disconnected, null, null, false, null, null, null);

    [ObservableProperty]
    private SessionSnapshot _zapret = new(ConnectionManager.ZapretKey, ConnectionStatus.Disconnected, null, null, false, null, null, null);

    [ObservableProperty]
    private SessionSnapshot _tg = new(ConnectionManager.TgWsProxyKey, ConnectionStatus.Disconnected, null, null, false, null, null, null);

    [ObservableProperty]
    private bool _isMock;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseWorkspace))]
    private bool _isOnboardingVisible;

    [ObservableProperty]
    private string _onboardingLanguage = "auto";

    [ObservableProperty]
    private ThemePreference _onboardingTheme = ThemePreference.System;

    public bool OnboardingThemeLight { get => OnboardingTheme == ThemePreference.Light; set { if (value) { OnboardingTheme = ThemePreference.Light; } } }

    public bool OnboardingThemeOled { get => OnboardingTheme == ThemePreference.Oled; set { if (value) { OnboardingTheme = ThemePreference.Oled; } } }

    public bool OnboardingThemeSystem { get => OnboardingTheme == ThemePreference.System; set { if (value) { OnboardingTheme = ThemePreference.System; } } }

    /// <summary>
    /// First-run consents (issue #438). All three start unchecked and are written to the settings only
    /// when the user confirms the screen, so closing it early grants nothing.
    /// </summary>
    [ObservableProperty]
    private bool _consentAutomaticUpdates;

    [ObservableProperty]
    private bool _consentNotifications;

    [ObservableProperty]
    private bool _consentThemes;

    [ObservableProperty]
    private bool _hasOrphans;

    [ObservableProperty]
    private bool _hasCrashReports;

    [ObservableProperty]
    private int _crashReportCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleToast))]
    private string? _toast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleToast))]
    private bool _toastIsError;

    public PageViewModel CurrentPage => CurrentNav.Page;

    // A connection error is already visible inline on Home; do not cover the tools with a duplicate toast.
    public bool HasVisibleToast => Toast is not null &&
        !(ToastIsError && CurrentNav.Key == "home" && Home.Error == Toast);

    public bool IsElevated => Services.Privileges.IsElevated;

    partial void OnCurrentNavChanging(NavItem value) => CurrentNav?.Page.OnHidden();

    partial void OnCurrentNavChanged(NavItem value)
    {
        UpdateNavigationSelection();
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(IsSettingsDetail));
        value.Page.OnShown();
    }

    partial void OnIsCompactChanged(bool value) => UpdateNavigationSelection();

    private void UpdateNavigationSelection()
    {
        var selected = IsSettingsDetail ? SettingsNav : CurrentNav;
        foreach (var item in NavItems)
        {
            item.IsSelected = item == selected;
        }
    }

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        if (value is not null && Services.Settings.LastProfileId != value.Id)
        {
            Services.Settings.LastProfileId = value.Id;
            _ = Services.SaveSettingsAsync();
        }
    }

    partial void OnOnboardingLanguageChanged(string value)
    {
        Services.Settings.Language = value;
        Loc.Instance.SetLanguage(value);
        _ = Services.SaveSettingsAsync();
    }

    partial void OnOnboardingThemeChanged(ThemePreference value)
    {
        Services.Settings.Theme = value;
        Services.Theme.Apply(value);
        _ = Services.SaveSettingsAsync();
        OnPropertyChanged(nameof(OnboardingThemeLight));
        OnPropertyChanged(nameof(OnboardingThemeOled));
        OnPropertyChanged(nameof(OnboardingThemeSystem));
    }

    [RelayCommand]
    private void StartOnboardingImport()
    {
        FinishOnboarding();
        OpenAddServer();
    }

    [RelayCommand]
    private void FinishOnboarding()
    {
        // Consents are committed only here: closing the screen before confirming grants nothing (issue #438).
        Services.Settings.ConsentAutomaticUpdates = ConsentAutomaticUpdates;
        Services.Settings.ConsentNotifications = ConsentNotifications;
        Services.Settings.ConsentThemes = ConsentThemes;
        Services.Settings.ConsentsInitialized = true;
        Services.Settings.OnboardingCompleted = true;
        IsOnboardingVisible = false;
        Settings.RefreshConsents();
        _ = Services.SaveSettingsAsync();
    }

    /// <summary>Opens a documented GitHub source from the first-run screen; the URLs are constants.</summary>
    [RelayCommand]
    private async Task OpenUrl(string url)
    {
        if (Services.TopLevel()?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(new Uri(url));
        }
    }

    [RelayCommand]
    public void Navigate(string key)
    {
        CurrentNav = NavItems.FirstOrDefault(n => n.Key == key) ?? CurrentNav;
    }

    [RelayCommand]
    private void CleanupOrphans()
    {
        try
        {
            var n = Services.CleanupOrphans();
            HasOrphans = false;
            ShowToast(Loc.T("toast.orphans", n));
        }
        catch (InvalidOperationException ex)
        {
            HasOrphans = Services.Orphans.Count > 0;
            var key = ex.Message == Loc.Instance["error.orphans.stop"] ? "error.orphans.stop" : null;
            ShowToast(ex.Message, isError: true, localizationKey: key);
        }
    }

    private void RefreshCrashReports()
    {
        var reports = Bydlo.Core.Diagnostics.CrashReporter.ListReports(Services.Paths.LogsDirectory);
        CrashReportCount = reports.Count;
        HasCrashReports = reports.Count > 0;
    }

    [RelayCommand]
    private async Task CopyCrashReport()
    {
        var reports = Bydlo.Core.Diagnostics.CrashReporter.ListReports(Services.Paths.LogsDirectory);
        if (reports.Count == 0)
        {
            HasCrashReports = false;
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(reports[0]);
            if (Services.TopLevel()?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                ShowToast(L["toast.copied"]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private async Task OpenCrashFolder()
    {
        try
        {
            if (Services.TopLevel()?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(new Uri(Services.Paths.LogsDirectory));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void DismissCrashReports() => HasCrashReports = false;

    public void RefreshMock() => IsMock = Services.IsMock;

    [ObservableProperty] private bool _sortBySpeed;
    private IReadOnlyDictionary<Guid, long> _serverSpeeds = new Dictionary<Guid, long>();

    partial void OnSortBySpeedChanged(bool value) => ReloadProfiles();

    public void SetServerSpeeds(IReadOnlyDictionary<Guid, long> results)
    {
        _serverSpeeds = results;
        ReloadProfiles();
    }

    public void ReloadProfiles()
    {
        var activeId = Primary.Status == ConnectionStatus.Connected ? Primary.Profile?.Id : null;
        var ordered = ServerListOrdering.Order(Services.Profiles.Profiles, Latencies, SortByLatency);
        var profiles = SortBySpeed ? ordered.OrderByDescending(p => _serverSpeeds.GetValueOrDefault(p.Id)).ToList() : ordered;
        var items = profiles
            .Select(p => new ProfileItem(p)
            {
                IsActive = p.Id == activeId,
                MeasuredSpeed = _serverSpeeds.TryGetValue(p.Id, out var speed) ? speed : null,
            })
            .ToList();
        foreach (var item in items)
        {
            ApplyLatency(item);
        }

        var selectedId = SelectedProfile?.Id ?? Services.Settings.LastProfileId;
        Profiles.ReplaceWith(items);
        Connections?.RefreshProfileSearch();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
        IsMock = Services.IsMock;
        ReloadChains();
    }

    /// <summary>Reloads chain rows and resolves hop names against current profiles.</summary>
    public void ReloadChains()
    {
        var names = Services.Profiles.Profiles.ToDictionary(p => p.Id, p => p.Name);
        var activeId = Primary.Status == ConnectionStatus.Connected ? Primary.Chain?.Id : null;
        var items = Services.Profiles.Chains
            .OrderByDescending(c => c.IsFavorite)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ChainItem(c)
            {
                IsActive = c.Id == activeId,
                HopSummary = string.Join(" → ", c.ProfileIds.Select(id => names.TryGetValue(id, out var n) ? n : "?")),
            })
            .ToList();
        foreach (var item in items)
        {
            item.RefreshDetails();
        }

        var selectedId = SelectedChain?.Id;
        Chains.ReplaceWith(items);
        SelectedChain = items.FirstOrDefault(c => c.Id == selectedId);
        OnPropertyChanged(nameof(HasChains));
        OnPropertyChanged(nameof(HasNoChains));
    }

    /// <summary>Stores fresh probe results and refreshes the visible rows (issue #10).</summary>
    public void SetLatencies(IReadOnlyDictionary<Guid, ServerLatencyResult> results)
    {
        Latencies.Clear();
        foreach (var (id, result) in results)
        {
            Latencies[id] = result;
        }

        foreach (var item in Profiles)
        {
            ApplyLatency(item);
        }

        if (SortByLatency)
        {
            ReloadProfiles();
        }

        OnPropertyChanged(nameof(HasLatencyResults));
    }

    private void ApplyLatency(ProfileItem item)
    {
        if (Latencies.TryGetValue(item.Id, out var result))
        {
            item.Latency = result.Reachable ? result.Latency : null;
            item.LatencyFailed = !result.Reachable;
        }
        else
        {
            item.Latency = null;
            item.LatencyFailed = false;
        }
    }

    public void ShowToast(string message, bool isError = false, string? localizationKey = null, string[]? localizationArguments = null)
    {
        _toastLocalizationKey = localizationKey;
        _toastLocalizationArguments = localizationArguments;
        message = Loc.Error(message, localizationKey, localizationArguments);
        var version = ++_toastVersion;
        Toast = message;
        ToastIsError = isError;
        DispatcherTimer.RunOnce(() =>
        {
            if (version == _toastVersion)
            {
                Toast = null;
            }
        }, TimeSpan.FromSeconds(isError ? 6 : 3.5));
    }

    /// <summary>Runs an action, turning exceptions into an error toast.</summary>
    public async Task<bool> Guard(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is EngineException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or IOException or HttpRequestException or NotSupportedException or UnauthorizedAccessException or FormatException or TimeoutException or PlatformNotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            ShowToast(ex.Message, isError: true, localizationKey: (ex as EngineException)?.LocalizationKey ??
                (ex is InvalidDataException && ex.Message is "routing.rules.invalid" or "routing.sources.invalid" ? ex.Message : null), localizationArguments: (ex as EngineException)?.LocalizationArguments);
            return false;
        }
    }

    private void OnSessionChanged(SessionSnapshot s)
    {
        switch (s.Key)
        {
            case ConnectionManager.PrimaryKey:
                var wasConnected = Primary.Status == ConnectionStatus.Connected;
                Primary = s;
                foreach (var p in Profiles)
                {
                    p.IsActive = s.Status == ConnectionStatus.Connected && p.Id == s.Profile?.Id;
                }

                foreach (var c in Chains)
                {
                    c.IsActive = s.Status == ConnectionStatus.Connected && c.Id == s.Chain?.Id;
                }

                if (s.LastTraffic is { } t && s.Status == ConnectionStatus.Connected)
                {
                    Push(DownHistory, t.DownloadBytesPerSecond);
                    Push(UpHistory, t.UploadBytesPerSecond);
                }
                else if (s.Status != ConnectionStatus.Connected && wasConnected)
                {
                    DownHistory.Clear();
                    UpHistory.Clear();
                }

                break;
            case ConnectionManager.ZapretKey:
                Zapret = s;
                break;
            case ConnectionManager.TgWsProxyKey:
                Tg = s;
                break;
        }
    }

    private static void Push(ObservableCollection<double> list, long value)
    {
        list.Add(value);
        while (list.Count > HistoryLength)
        {
            list.RemoveAt(0);
        }
    }
}
