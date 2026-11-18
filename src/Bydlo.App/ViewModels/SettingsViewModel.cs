using Bydlo.App.Services;
using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed record Credit(string Name, string Author, string License, string Url, string Role, string? RoleKey = null)
{
    public string LocalizedRole => RoleKey is null ? Role : Loc.Instance[RoleKey];
}

public sealed partial class SettingsViewModel : PageViewModel
{
    public bool HelperSupported => Bydlo.Platform.HelperManagement.IsSupported;
    public bool HelperInstalled => Bydlo.Platform.HelperManagement.IsInstalled;
    public bool HelperOutdated => HelperSupported && Bydlo.Platform.HelperManagement.IsOutdated(AppServices.ExpectedHelperBuild);
    public bool CanManageHelper => HelperSupported && !Services.IsMock && !NetworkSafetyGuard.IsLocked && !HelperBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanManageHelper))]
    private bool _helperBusy;
    [ObservableProperty] private string _helperStatus = "";

    [RelayCommand]
    private async Task InstallHelperAsync() { await ChangeHelperAsync(true); }
    [RelayCommand]
    private async Task RemoveHelperAsync() { await ChangeHelperAsync(false); }

    internal Task<bool> InstallHelperForZapretAsync() => ChangeHelperAsync(true);

    private async Task<bool> ChangeHelperAsync(bool install)
    {
        if (!CanManageHelper) { return false; }
        HelperBusy = true;
        try
        {
            await Services.Connections.StopAllAsync();
            var success = await Bydlo.Platform.HelperManagement.ChangeAsync(install, userConsent: true);
            HelperStatus = Loc.Instance[success ? "helper.changed" : "helper.error"];
            return success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { HelperStatus = Loc.Instance["helper.error"]; return false; }
        finally
        {
            HelperBusy = false;
            OnPropertyChanged(nameof(HelperInstalled));
            OnPropertyChanged(nameof(HelperOutdated));
        }
    }

    public SettingsViewModel(MainViewModel shell)
        : base(shell)
    {
        var s = Services.Settings;
        _theme = s.Theme;
        _reduceMotion = s.ReduceMotion;
        _language = s.Language;
        _mockMode = Services.IsMock;
        _mockScenario = s.MockScenario;
        _localPort = s.LocalProxyPort;
        _ipCheckProvider = s.IpCheckProvider;
        _setSystemProxy = s.SetSystemProxy;
        _autoReconnect = s.AutoReconnect;
        _killSwitch = s.KillSwitch;
        _allowLan = s.AllowLan;
        _localProxyUsername = s.LocalProxyUsername;
        _showAdvanced = s.ShowAdvanced;
        _suggestClipboardLinks = s.SuggestClipboardLinks;
        _startWithSystem = s.StartWithSystem;
        _connectOnStartup = s.ConnectOnStartup;
        _minimizeToTray = s.MinimizeToTray;
        _showNotifications = s.ShowNotifications;
        _subscriptionAutoUpdate = s.SubscriptionAutoUpdate;
        _subscriptionUpdateIntervalHours = s.SubscriptionUpdateIntervalHours;
        _checkForUpdates = s.CheckForUpdates;
        _autoUpdateApp = s.AutoUpdateApp;
        _consentAutomaticUpdates = s.ConsentAutomaticUpdates;
        _consentNotifications = s.ConsentNotifications;
        _consentThemes = s.ConsentThemes;
        _runElevatedOnStartup = s.RunElevatedOnStartup;
    }

    /// <summary>Re-read settings changed outside this page, e.g. by the first-run onboarding.</summary>
    public override void OnShown()
    {
        OnPropertyChanged(nameof(SecretProtection));
        OnPropertyChanged(nameof(HelperInstalled));
        OnPropertyChanged(nameof(HelperOutdated));
        var s = Services.Settings;
        Theme = s.Theme;
        ReduceMotion = s.ReduceMotion;
        Language = s.Language;
        MockMode = Services.IsMock;
        MockScenario = s.MockScenario;
        LocalPort = s.LocalProxyPort;
        IpCheckProvider = s.IpCheckProvider;
        SetSystemProxy = s.SetSystemProxy;
        AutoReconnect = s.AutoReconnect;
        ShowAdvanced = s.ShowAdvanced;
        KillSwitch = s.KillSwitch;
        AllowLan = s.AllowLan;
        LocalProxyUsername = s.LocalProxyUsername;
        LocalProxyPassword = "";
        _ = Shell.Guard(RefreshLanPasswordStatusAsync);
        SuggestClipboardLinks = s.SuggestClipboardLinks;
        _updatingAutostart = true;
        StartWithSystem = s.StartWithSystem;
        _updatingAutostart = false;
        ConnectOnStartup = s.ConnectOnStartup;
        MinimizeToTray = s.MinimizeToTray;
        ShowNotifications = s.ShowNotifications;
        SubscriptionAutoUpdate = s.SubscriptionAutoUpdate;
        SubscriptionUpdateIntervalHours = s.SubscriptionUpdateIntervalHours;
        CheckForUpdates = s.CheckForUpdates;
        AutoUpdateApp = s.AutoUpdateApp;
        RefreshUpdateState();
        RefreshConsents();
        OnPropertyChanged(nameof(LanSysProxyConflict));
        RefreshCustomThemes();
    }

    public static IReadOnlyList<string> Languages { get; } = ["auto", "en", "ru"];

    [ObservableProperty] private bool _reduceMotion;

    partial void OnReduceMotionChanged(bool value)
    {
        Services.Settings.ReduceMotion = value;
        Persist();
    }

    public bool AutostartSupported => new Bydlo.Platform.AutostartService().IsSupported;

    [ObservableProperty] private bool _startWithSystem;
    [ObservableProperty] private bool _connectOnStartup;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _showNotifications;
    private bool _updatingAutostart;

    partial void OnStartWithSystemChanged(bool value)
    {
        if (_updatingAutostart) { return; }
        try
        {
            if (NetworkSafetyGuard.IsLocked) { throw new InvalidOperationException(L["settings.desktop.locked"]); }
            new Bydlo.Platform.AutostartService().SetEnabled(value);
            Services.Settings.StartWithSystem = value;
            Persist();
        }
        catch (Exception ex)
        {
            _updatingAutostart = true;
            StartWithSystem = Services.Settings.StartWithSystem;
            _updatingAutostart = false;
            Services.Log.Warn("autostart", ex.GetType().Name);
            Shell.ShowToast(L["settings.desktop.autostart.error"], isError: true);
        }
    }

    partial void OnConnectOnStartupChanged(bool value) { Services.Settings.ConnectOnStartup = value; Persist(); }
    partial void OnMinimizeToTrayChanged(bool value) { Services.Settings.MinimizeToTray = value; Persist(); }
    partial void OnShowNotificationsChanged(bool value) { Services.Settings.ShowNotifications = value; Persist(); }

    public static IReadOnlyList<MockScenario> Scenarios { get; } = Enum.GetValues<MockScenario>();

    public static IReadOnlyList<IpCheckProvider> CheckProviders { get; } = ConnectionCheckService.Providers;

    /// <summary>Upstream projects BYDLO builds on. Also listed in THIRD_PARTY_NOTICES.md.</summary>
    public static IReadOnlyList<Credit> Credits { get; } =
    [
        new("zapret-discord-youtube", "Flowseal", "MIT", "https://github.com/Flowseal/zapret-discord-youtube", "Zapret strategies, lists, winws bundle"),
        new("zapret", "bol-van", "MIT", "https://github.com/bol-van/zapret", "", "about.zapret.native"),
        new("WinDivert", "basil00", "LGPL-3.0 / GPL-2.0", "https://github.com/basil00/WinDivert", "Packet interception driver used by winws"),
        new("Cygwin 3.4.10", "Cygwin contributors / Red Hat", "LGPL-3.0+", "https://cygwin.com/licensing.html", "", "about.cygwin"),
        new("Newlib", "Newlib contributors", "BSD / MIT", "https://sourceware.org/newlib/", "", "about.newlib"),
        new("tg-ws-proxy", "Flowseal", "MIT", "https://github.com/Flowseal/tg-ws-proxy", "Telegram WebSocket bridge proxy"),
        new("vpn-configs-for-russia", "igareck", "GPL-3.0", "https://github.com/igareck/vpn-configs-for-russia", "Public subscription lists (downloaded on demand)"),
        new("Free-v2ray-Configs", "0xRadikal", "MIT", "https://github.com/0xRadikal/Free-v2ray-Configs", "Verified public subscription set (downloaded on demand)"),
        new("v2ray-configs", "MatinGhanbari", "MIT", "https://github.com/MatinGhanbari/v2ray-configs", "Public VLESS list (downloaded on demand)"),
        new("sing-box", "SagerNet (nekohasekai)", "GPL-3.0-or-later", "https://github.com/SagerNet/sing-box", "", "about.singbox"),
        new("Xray-core", "Project X (XTLS)", "MPL-2.0", "https://github.com/XTLS/Xray-core", "VLESS / REALITY / XHTTP"),
        new("Cronet / cronet-go", "The Chromium Authors / SagerNet", "BSD-3-Clause / GPL-3.0+", "https://github.com/SagerNet/cronet-go", "", "about.cronet"),
        new("Xray GeoIP / GeoSite", "Project X / V2Ray & contributors", "GPL-3.0 / MIT", "https://github.com/XTLS/Xray-rules-dat", "", "about.geodata"),
        new("WireGuard", "Jason A. Donenfeld / WireGuard LLC", "MIT / GPL-2.0", "https://www.wireguard.com", "WireGuard tunnels"),
        new("AmneziaWG", "WireGuard LLC / Amnezia VPN", "MIT (core), GPL-3.0+ (host)", "https://github.com/amnezia-vpn/amneziawg-go", "Obfuscated WireGuard"),
        new("Wintun 0.14.1", "WireGuard LLC", "Prebuilt Binaries License", "https://www.wintun.net/", "", "about.wintun"),
        new("OpenVPN DCO 2.8.14", "OpenVPN Inc. & contributors", "GPL-2.0", "https://github.com/OpenVPN/ovpn-dco-win", "", "about.dco"),
        new("OpenSSL 3.6.5", "OpenSSL Project Authors", "Apache-2.0", "https://github.com/openssl/openssl", "TLS / cryptography"),
        new("libcap-ng 0.9.6", "Steve Grubb & contributors", "LGPL-2.1-or-later", "https://github.com/stevegrubb/libcap-ng", "", "about.libcap"),
        new("pkcs11-helper", "Alon Bar-Lev & contributors", "BSD", "https://github.com/OpenSC/pkcs11-helper", "", "about.pkcs11"),
        new("Microsoft Visual C++ runtime", "Microsoft", "Microsoft runtime license", "https://www.microsoft.com", "", "about.vcruntime"),
        new("OpenVPN", "OpenVPN Inc. & community", "GPL-2.0", "https://github.com/OpenVPN/openvpn", "OpenVPN tunnels"),
        new("Avalonia UI", "AvaloniaUI OÜ", "MIT", "https://github.com/AvaloniaUI/Avalonia", "Cross-platform UI framework"),
        new("SkiaSharp / HarfBuzzSharp", "Microsoft / Mono & contributors", "MIT", "https://github.com/mono/SkiaSharp", "", "about.skia"),
        new("ANGLE", "The ANGLE Project Authors", "BSD-3-Clause", "https://github.com/google/angle", "", "about.angle"),
        new("MicroCom.Runtime", "MicroCom contributors", "MIT", "https://github.com/kekekeks/MicroCom", "", "about.microcom"),
        new("Tmds.DBus.Protocol", "Tom Deseyn", "MIT", "https://github.com/tmds/Tmds.DBus", "", "about.dbus"),
        new("Velopack", "Velopack contributors", "MIT", "https://github.com/velopack/velopack", "", "about.velopack"),
        new("CommunityToolkit.Mvvm", ".NET Foundation", "MIT", "https://github.com/CommunityToolkit/dotnet", "MVVM helpers"),
        new("YamlDotNet", "Antoine Aubry & contributors", "MIT", "https://github.com/aaubry/YamlDotNet", "", "about.yaml"),
        new(".NET", ".NET Foundation / Microsoft & contributors", "MIT", "https://github.com/dotnet/runtime", "", "about.dotnet"),
        new("ZXing.Net", "Michael Jahn", "Apache-2.0", "https://github.com/micjahn/ZXing.Net", "QR code decoding"),
        new("gRPC for .NET", "The gRPC Authors", "Apache-2.0", "https://github.com/grpc/grpc-dotnet", "Xray group control API"),
        new("Protocol Buffers", "Google LLC", "BSD-3-Clause", "https://github.com/protocolbuffers/protobuf", "Xray group API messages"),
        new("Inter", "Rasmus Andersson", "OFL-1.1", "https://github.com/rsms/inter", "Typeface"),
        new("Material Design Icons", "Pictogrammers", "Apache-2.0", "https://github.com/Templarian/MaterialDesign", "Icons"),
    ];

    public string Version => Bydlo.Core.AppVersion.Current;

    public string DataDirectory => Services.Paths.DataDirectory;

    public string SecretProtection => Bydlo.Platform.SecretStoreFactory.IsUsingSystemFallback(Services.Secrets)
        ? L["settings.secrets.fallback"] : Services.Secrets.ProtectionDescription;

    public bool IsElevated => Services.Privileges.IsElevated;

    public bool CanRestartElevated => !IsElevated && Services.Privileges.CanRestartElevated;

    public bool MockForcedByFlag => Services.ForcedMock;

    public bool SafetyLock => NetworkSafetyGuard.IsLocked;

    public bool SystemProxySupported => Services.SystemProxy.IsSupported;

    [ObservableProperty]
    private ThemePreference _theme;

    [ObservableProperty]
    private string _language;

    [ObservableProperty]
    private bool _mockMode;

    [ObservableProperty]
    private MockScenario _mockScenario;

    [ObservableProperty]
    private int _localPort;

    [ObservableProperty]
    private bool _allowLan;

    [ObservableProperty]
    private string _localProxyUsername = "";

    /// <summary>Transient entry field; the stored LAN password is never read back into the UI.</summary>
    [ObservableProperty]
    private string _localProxyPassword = "";

    [ObservableProperty]
    private bool _hasLocalProxyPassword;

    /// <summary>In-app restart with elevated rights exists only on Windows; other systems need a manual restart (issue #34).</summary>
    public bool ShowElevationManualHint => !IsElevated && !CanRestartElevated;

    /// <summary>The system proxy carries no credentials, so it cannot share the protected LAN port (issue #80).</summary>
    public bool LanSysProxyConflict => AllowLan && Services.Settings.SetSystemProxy;

    [ObservableProperty]
    private IpCheckProvider _ipCheckProvider;

    [ObservableProperty]
    private bool _setSystemProxy;

    [ObservableProperty]
    private bool _runElevatedOnStartup;

    [ObservableProperty]
    private bool _autoReconnect;

    [ObservableProperty]
    private bool _showAdvanced;

    [ObservableProperty]
    private bool _killSwitch;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnableKillSwitchCommand))]
    private bool _killSwitchAcknowledged;

    [RelayCommand(CanExecute = nameof(KillSwitchAcknowledged))]
    private void EnableKillSwitch()
    {
        KillSwitch = Services.Settings.KillSwitch = true;
        Persist();
        Shell.ShowToast(Loc.Instance["settings.killswitch.next"]);
    }

    [RelayCommand]
    private async Task DisableKillSwitch()
    {
        try
        {
            await Services.Connections.DisconnectAsync();
            Services.CleanupKillSwitch();
            KillSwitch = Services.Settings.KillSwitch = false;
            KillSwitchAcknowledged = false;
            await Services.SaveSettingsAsync();
        }
        catch (Exception ex) { Shell.ShowToast(ex.Message, isError: true); }
    }

    [ObservableProperty]
    private bool _suggestClipboardLinks;

    [ObservableProperty]
    private bool _subscriptionAutoUpdate;

    [ObservableProperty]
    private int _subscriptionUpdateIntervalHours;

    public bool CanRegisterAssociations => !NetworkSafetyGuard.IsLocked &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
        Bydlo.Platform.LauncherPath.Current is { } path && !Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    partial void OnSuggestClipboardLinksChanged(bool value)
    {
        Services.Settings.SuggestClipboardLinks = value;
        if (!value)
        {
            Shell.DismissClipboardOffer();
        }

        Persist();
    }

    partial void OnSubscriptionAutoUpdateChanged(bool value)
    {
        Services.Settings.SubscriptionAutoUpdate = value;
        Persist();
    }

    partial void OnSubscriptionUpdateIntervalHoursChanged(int value)
    {
        Services.Settings.SubscriptionUpdateIntervalHours = Math.Clamp(value, 1, 168);
        Persist();
    }

    [RelayCommand]
    private async Task RegisterAssociations()
    {
        await Shell.Guard(async () =>
        {
            if (CanRegisterAssociations)
            {
                try
                {
                    if (OperatingSystem.IsWindows()) { Bydlo.Platform.WindowsImportAssociations.Register(Environment.ProcessPath!); }
                    else { await new Bydlo.Platform.UnixImportAssociations(Bydlo.Platform.LauncherPath.Current!).RegisterAsync(); }
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidOperationException(L["settings.associations.conflict"], ex);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    throw new IOException(L["settings.associations.error"], ex);
                }

                Shell.ShowToast(L["settings.associations.registered"]);
            }

        });
    }

    [RelayCommand]
    private async Task RemoveAssociations()
    {
        await Shell.Guard(async () =>
        {
            if (CanRegisterAssociations)
            {
                try
                {
                    if (OperatingSystem.IsWindows()) { Bydlo.Platform.WindowsImportAssociations.Unregister(Environment.ProcessPath!); }
                    else { await new Bydlo.Platform.UnixImportAssociations(Bydlo.Platform.LauncherPath.Current!).UnregisterAsync(); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    throw new IOException(L["settings.associations.error"], ex);
                }
                Shell.ShowToast(L["settings.associations.removed"]);
            }

        });
    }

    public bool ThemeLight { get => Theme == ThemePreference.Light; set { if (value) { Theme = ThemePreference.Light; } } }

    public bool ThemeOled { get => Theme == ThemePreference.Oled; set { if (value) { Theme = ThemePreference.Oled; } } }

    public bool ThemeSystem { get => Theme == ThemePreference.System; set { if (value) { Theme = ThemePreference.System; } } }

    partial void OnThemeChanged(ThemePreference value)
    {
        Services.Settings.Theme = value;
        if (value == ThemePreference.Custom && !ApplyStoredCustom())
        {
            // The custom file is gone: fall back to System instead of a half-applied UI.
            Theme = ThemePreference.System;
            return;
        }
        if (value != ThemePreference.Custom)
        {
            Services.Theme.Apply(value);
            UpdateWarning(false);
        }
        Persist();
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeOled));
        OnPropertyChanged(nameof(ThemeSystem));
        OnPropertyChanged(nameof(ThemeCustom));
        RefreshCustomThemes();
    }

    partial void OnLanguageChanged(string value)
    {
        Services.Settings.Language = value;
        Loc.Instance.SetLanguage(value);
        // The manual update guidance is a resolved string: re-resolve it in the new language.
        RefreshUpdateState();
        Persist();
    }

    partial void OnMockModeChanged(bool value)
    {
        if (Services.ForcedMock) { return; }
        Services.Settings.MockMode = value;
        Shell.RefreshMock();
        Persist();
    }

    partial void OnMockScenarioChanged(MockScenario value)
    {
        Services.Settings.MockScenario = value;
        Persist();
    }

    partial void OnLocalPortChanged(int value)
    {
        if (value is > 0 and <= Bydlo.Core.Connection.ProxyPorts.MaxLocalProxyPort)
        {
            Services.Settings.LocalProxyPort = value;
            Persist();
        }
    }

    partial void OnAllowLanChanged(bool value)
    {
        OnPropertyChanged(nameof(LanSysProxyConflict));
        _ = Shell.Guard(() => ApplyAllowLanAsync(value));
    }

    partial void OnLocalProxyUsernameChanged(string value)
    {
        Services.Settings.LocalProxyUsername = value;
        Persist();
        if (Services.Settings.AllowLan && string.IsNullOrWhiteSpace(value))
        {
            AllowLan = false;
            Shell.ShowToast(L[LocalProxyAuthValidator.LoginRequiredKey], isError: true,
                localizationKey: LocalProxyAuthValidator.LoginRequiredKey);
        }
    }

    partial void OnLocalProxyPasswordChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        _ = Shell.Guard(() => SaveLanPasswordAsync(value));
    }

    private async Task ApplyAllowLanAsync(bool value)
    {
        if (value)
        {
            if (!string.IsNullOrEmpty(LocalProxyPassword))
            {
                await SaveLanPasswordAsync(LocalProxyPassword);
                LocalProxyPassword = "";
            }

            var key = LocalProxyAuthValidator.Validate(
                new AppSettings { AllowLan = true, LocalProxyUsername = LocalProxyUsername },
                HasLocalProxyPassword ? "present" : null);
            if (key is not null)
            {
                AllowLan = false;
                Shell.ShowToast(L[key], isError: true, localizationKey: key);
                return;
            }
        }

        Services.Settings.AllowLan = value;
        Persist();
    }

    private async Task SaveLanPasswordAsync(string password)
    {
        await Services.Secrets.SaveAsync(ConnectionManager.LocalProxySecretId,
            new ProfileSecrets { Values = { ["password"] = password } });
        HasLocalProxyPassword = true;
    }

    private async Task RefreshLanPasswordStatusAsync()
    {
        try
        {
            var stored = await Services.Secrets.LoadAsync(ConnectionManager.LocalProxySecretId);
            HasLocalProxyPassword = stored["password"] is not null;
        }
        finally
        {
            OnPropertyChanged(nameof(SecretProtection));
        }
    }

    partial void OnIpCheckProviderChanged(IpCheckProvider value)
    {
        Services.Settings.IpCheckProvider = value;
        Shell.Home.ResetConnectionCheck();
        Persist();
    }

    partial void OnSetSystemProxyChanged(bool value)
    {
        Services.Settings.SetSystemProxy = value;
        OnPropertyChanged(nameof(LanSysProxyConflict));
        Persist();
    }

    partial void OnRunElevatedOnStartupChanged(bool value)
    {
        Services.Settings.RunElevatedOnStartup = value;
        Persist();
    }

    partial void OnAutoReconnectChanged(bool value)
    {
        Services.Settings.AutoReconnect = value;
        Persist();
    }

    partial void OnShowAdvancedChanged(bool value)
    {
        Services.Settings.ShowAdvanced = value;
        Persist();
    }

    [RelayCommand]
    private async Task RestartElevated()
    {
        try
        {
            if (await Bydlo.Core.Platform.ElevatedRestart.TryAsync(Services.Privileges, Services.Args,
                Environment.ProcessId, Services.Connections.StopAllAsync, Services.ForcedMock, Services.Paths.DataDirectory))
            {
                Environment.Exit(0);
            }

            Shell.ShowToast(L["settings.admin.cancelled"]);
        }
        catch (Exception)
        {
            Shell.ShowToast(L["settings.admin.failed"], isError: true);
        }
    }

    [RelayCommand]
    private async Task OpenUrl(string url)
    {
        if (Services.TopLevel()?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(new Uri(url));
        }
    }

    public string BugTemplateUrl => Bydlo.Core.Diagnostics.DiagnosticReport.BugTemplateUrl;

    [RelayCommand]
    private async Task CopyDiagnosticReport()
    {
        try
        {
            var settings = Services.Settings;
            var byProtocol = Services.Profiles.Profiles
                .GroupBy(p => p.Protocol)
                .ToDictionary(g => g.Key, g => g.Count());
            var settingsLines = Bydlo.Core.Diagnostics.DiagnosticReport.SummarizeSettings(
                settings, byProtocol, Services.Profiles.Subscriptions.Count);
            var engines = Services.Lock.Engines
                .Select(e => new Bydlo.Core.Diagnostics.DiagnosticReport.DiagnosticEngineInfo(
                    e.Id, e.Version, e.Assets.ToDictionary(a => a.Key, a => a.Value.Sha256, StringComparer.Ordinal)))
                .ToList();
            var logs = Services.Log.Snapshot().TakeLast(Bydlo.Core.Diagnostics.DiagnosticReport.MaxLogLines)
                .Select(e => e.ToString()).ToList();
            string? crash = null;
            var reports = Bydlo.Core.Diagnostics.CrashReporter.ListReports(Services.Paths.LogsDirectory);
            if (reports.Count > 0)
            {
                try { crash = await File.ReadAllTextAsync(reports[0]); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { crash = null; }
            }
            var text = Bydlo.Core.Diagnostics.DiagnosticReport.Build(
                Bydlo.Core.AppVersion.Current,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                engines, settingsLines, logs, crash, Bydlo.Core.Security.LogRedactor.Shared);
            if (Services.TopLevel()?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                Shell.ShowToast(L["toast.copied"]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Shell.ShowToast(ex.Message, isError: true);
        }
    }

    private void Persist() => _ = Services.SaveSettingsAsync();
}
