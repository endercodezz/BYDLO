using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Bydlo.App.ViewModels;
using Bydlo.Core.Connection;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Platform;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.Services;

/// <summary>Owns desktop-only UI and keeps hidden sessions alive until explicit exit.</summary>
public sealed class DesktopIntegration : IDisposable
{
    private readonly MainViewModel _shell;
    private readonly Window _window;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly TrayIcon _tray;
    private readonly SessionNotificationTracker _notifications = new();
    private readonly DesktopNotifications _nativeNotifications;
    private bool _exiting;
    private bool _disposed;
    private bool _startupHandled;
    private bool _refreshPending;
    private readonly NativeMenu _menu = new();
    private readonly NativeMenu _servers = new();
    private readonly NativeMenuItem _open = new();
    private readonly NativeMenuItem _status = new() { IsEnabled = false };
    private readonly NativeMenuItem _connect = new();
    private readonly NativeMenuItem _serverList = new();
    private readonly NativeMenuItem _zapret = new();
    private readonly NativeMenuItem _tg = new();
    private readonly NativeMenuItem _exit = new();
    private string? _profilesState;

    public DesktopIntegration(MainViewModel shell, Window window, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _shell = shell;
        _window = window;
        _lifetime = lifetime;
        _nativeNotifications = new DesktopNotifications(shell.Services.Runner);
        _open.Command = new RelayCommand(ShowWindow);
        _connect.Command = shell.Home.ToggleConnectionCommand;
        _serverList.Menu = _servers;
        _zapret.Command = shell.Tools.ToggleZapretCommand;
        _tg.Command = shell.Tools.ToggleTgCommand;
        _exit.Command = new AsyncRelayCommand(ExitAsync);
        _menu.Items.Add(_open);
        _menu.Items.Add(_status);
        _menu.Items.Add(_connect);
        _menu.Items.Add(_serverList);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(_zapret);
        _menu.Items.Add(_tg);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(_exit);
        // Avalonia.Native retains the exported menu identity. Never replace it during refresh.
        _tray = new TrayIcon { Menu = _menu, IsVisible = true };
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
        _tray.Clicked += OnTrayClicked;
        window.Closing += OnClosing;
        window.Opened += OnOpened;
        lifetime.ShutdownRequested += OnShutdownRequested;
        lifetime.Exit += OnExit;
        shell.PropertyChanged += OnShellChanged;
        shell.Profiles.CollectionChanged += OnProfilesChanged;
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        shell.Services.Connections.SessionChanged += OnSessionChanged;
        Refresh();
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowWindow();

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting) { return; }
        // Only a user window close may hide the UI; shutdown always stops the sessions.
        if (e.CloseReason == WindowCloseReason.WindowClosing &&
            _shell.Services.Settings.MinimizeToTray && _tray.NativeMenuExporter is not null)
        {
            e.Cancel = true;
            _window.Hide();
            return;
        }

        e.Cancel = true;
        Dispatcher.UIThread.Post(async () => await ExitAsync());
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_exiting) { return; }
        e.Cancel = true;
        Dispatcher.UIThread.Post(async () => await ExitAsync());
    }

    public async Task ExitAsync()
    {
        if (_exiting) { return; }
        _exiting = true;
        // Cleanup completes before the UI dispatcher and native window are destroyed.
        try { await _shell.Services.ShutdownAsync(); }
        catch (Exception ex)
        {
            _shell.Services.Log.Error("app", "Shutdown failed: " + ex.Message);
            // A half-stopped session must never be replaced by a new package (issue #413).
            _shell.Services.AutomaticUpdates.Defer();
            _shell.Settings.RefreshUpdateState();
            _exiting = false;
            ShowWindow();
            _shell.ShowToast(Loc.Instance["tray.exit.error"], isError: true);
            return;
        }
        try
        {
            var result = _shell.Services.AutomaticUpdates.ApplyOnExit(_shell.Services.Settings,
                _shell.Services.IsMock || NetworkSafetyGuard.IsLocked);
            if (result == Bydlo.Core.Diagnostics.AppUpdateApplyResult.Deferred)
            {
                _shell.Services.Log.Info("update", "Update installation deferred to the next exit.");
            }
        }
        catch (Exception ex)
        {
            _shell.Services.Log.Warn("update", "Update deferred: " + ex.GetType().Name);
        }
        _lifetime.Shutdown();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_startupHandled) { return; }
        _startupHandled = true;
        var services = _shell.Services;
        // The window is up: this is the local readiness confirmation of a freshly installed version (issue #412).
        services.ConfirmUpdateReadiness();
        _shell.ShowUpdateRecoveryToast();
        if (services.Args.Contains("--autostart", StringComparer.Ordinal) &&
            services.Settings.OnboardingCompleted && services.Settings.MinimizeToTray &&
            _tray.NativeMenuExporter is not null) { _window.Hide(); }
        if (services.Settings.ConnectOnStartup && services.Settings.OnboardingCompleted &&
            _shell.SelectedProfile is not null && (!NetworkSafetyGuard.IsLocked || services.IsMock))
        {
            await _shell.Home.ToggleConnectionCommand.ExecuteAsync(null);
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Primary) or nameof(MainViewModel.Zapret) or nameof(MainViewModel.Tg) or nameof(MainViewModel.SelectedProfile))
        { ScheduleRefresh(); }
    }

    private void OnProfilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ScheduleRefresh();
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (_refreshPending || _disposed) { return; }
        _refreshPending = true;
        Dispatcher.UIThread.Post(() => { _refreshPending = false; if (!_disposed) { Refresh(); } });
    }

    private string? _menuState;
    private void Refresh()
    {
        var l = Loc.Instance;
        var state = string.Join('|', l["tray.open"], _shell.Home.StatusText, _shell.Home.ButtonText,
            _shell.Home.ZapretState, _shell.Home.TgState, _shell.SelectedProfile?.Id.ToString(),
            string.Join(';', _shell.Profiles.Select(p => p.Id + ":" + p.Name)));
        if (state == _menuState) { return; }
        _tray.ToolTipText = "BYDLO · " + _shell.Home.StatusText + "\nZapret · " + _shell.Home.ZapretState + "\nTgWsProxy · " + _shell.Home.TgState;
        _open.Header = l["tray.open"];
        _status.Header = _shell.Home.StatusText;
        _connect.Header = _shell.Home.ButtonText;
        _serverList.Header = l["tray.servers"];
        _serverList.IsEnabled = _shell.Profiles.Count > 0;
        _zapret.Header = "Zapret · " + _shell.Home.ZapretState;
        _tg.Header = "TgWsProxy · " + _shell.Home.TgState;
        _exit.Header = l["tray.exit"];
        var profilesState = string.Join(';', _shell.Profiles.Select(p => p.Id + ":" + p.Name));
        if (profilesState != _profilesState)
        {
            _servers.Items.Clear();
            foreach (var profile in _shell.Profiles)
            {
                _servers.Items.Add(new NativeMenuItem
                {
                    Header = profile.Name, ToggleType = NativeMenuItemToggleType.Radio,
                    Command = new RelayCommand(() => _shell.SelectedProfile = profile),
                });
            }
            _profilesState = profilesState;
        }
        for (var i = 0; i < _shell.Profiles.Count; i++)
        {
            ((NativeMenuItem)_servers.Items[i]).IsChecked = _shell.SelectedProfile?.Id == _shell.Profiles[i].Id;
        }
        UpdateIcon();
        _menuState = state;
    }

    private ConnectionStatus? _iconStatus;
    private void UpdateIcon()
    {
        var statuses = new[] { _shell.Primary.Status, _shell.Zapret.Status, _shell.Tg.Status };
        var status = statuses.Contains(ConnectionStatus.Error) ? ConnectionStatus.Error
            : statuses.Any(s => s is ConnectionStatus.Connecting or ConnectionStatus.Reconnecting) ? ConnectionStatus.Connecting
            : statuses.Contains(ConnectionStatus.Connected) ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
        if (_iconStatus == status) { return; }
        _iconStatus = status;
        using var logo = new Bitmap(AssetLoader.Open(new Uri("avares://Bydlo.App/Assets/bydlo-256.png")));
        using var rendered = new RenderTargetBitmap(new PixelSize(64, 64));
        using (var context = rendered.CreateDrawingContext())
        {
            context.DrawImage(logo, new Rect(0, 0, 64, 64));
            var key = status switch
            {
                ConnectionStatus.Connected => "SuccessBrush",
                ConnectionStatus.Error => "DangerBrush",
                ConnectionStatus.Connecting or ConnectionStatus.Reconnecting => "WarningBrush",
                _ => "TextFaintBrush",
            };
            if (Application.Current!.TryFindResource(key, out var resource) && resource is IBrush brush)
            { context.DrawEllipse(brush, null, new Point(51, 51), 12, 12); }
        }
        using var stream = new MemoryStream();
        rendered.Save(stream);
        stream.Position = 0;
        _tray.Icon = new WindowIcon(stream);
    }

    private void OnSessionChanged(object? sender, SessionSnapshot session) => Dispatcher.UIThread.Post(async () =>
    {
        if (_disposed || _exiting) { return; }
        var key = _notifications.Update(session);
        // Desktop notifications need their own consent (issue #438); the OS may still ask separately.
        if (key is null || !_shell.Services.Settings.ShowNotifications ||
            !_shell.Services.Settings.ConsentNotifications || NetworkSafetyGuard.IsLocked) { return; }
        var name = session.Key switch
        {
            ConnectionManager.ZapretKey => "Zapret", ConnectionManager.TgWsProxyKey => "TgWsProxy", _ => "BYDLO",
        };
        try
        {
            // Generic localized text keeps server credentials and raw engine diagnostics out of system history.
            await _nativeNotifications.ShowAsync(_window.TryGetPlatformHandle()?.Handle ?? 0, name, Loc.Instance[key]);
        }
        catch (Exception ex) { _shell.Services.Log.Warn("notifications", "Notification unavailable: " + ex.GetType().Name); }
    });

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _tray.Clicked -= OnTrayClicked;
        _tray.Dispose();
        _nativeNotifications.Dispose();
        _window.Closing -= OnClosing;
        _window.Opened -= OnOpened;
        _lifetime.ShutdownRequested -= OnShutdownRequested;
        _lifetime.Exit -= OnExit;
        _shell.PropertyChanged -= OnShellChanged;
        _shell.Profiles.CollectionChanged -= OnProfilesChanged;
        Loc.Instance.PropertyChanged -= OnLanguageChanged;
        _shell.Services.Connections.SessionChanged -= OnSessionChanged;
    }
}
