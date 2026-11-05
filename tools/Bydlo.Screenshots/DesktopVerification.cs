using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Connection;

internal static class DesktopVerification
{
    public static void Run(MainViewModel shell, AppServices services, string output)
    {
        if (!services.IsMock || !Bydlo.Core.Security.NetworkSafetyGuard.IsLocked)
        { throw new InvalidOperationException("Desktop verification requires explicit mock and safety lock."); }

        Wait(shell.Connections.ImportTextAsync("socks5://127.0.0.1:1080#Desktop-test"));
        shell.FinishOnboardingCommand.Execute(null);
        shell.Settings.ConnectOnStartup = true;
        var window = new Window { Width = 1000, Height = 900, Content = new MainView { DataContext = shell } };
        using var lifetime = new ClassicDesktopStyleApplicationLifetime { MainWindow = window, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var integration = new DesktopIntegration(shell, window, lifetime);
        // SetupWithoutStarting has no lifetime-owned windows; close after integration disposal on Exit.
        lifetime.Exit += (_, _) => window.Close();
        window.Show();
        Until(() => services.Connections.Primary.Status == ConnectionStatus.Connected);
        Pump(50);
        var clock = (DispatcherTimer)typeof(HomeViewModel).GetField("_clock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(shell.Home)!;
        if (!clock.IsEnabled) { throw new InvalidOperationException("Connected duration clock did not start."); }
        if (!services.Connections.Primary.IsMock) { throw new InvalidOperationException("Startup did not use MockEngine."); }
        var tray = TrayIcon.GetIcons(Application.Current!)!.Single();
        if (tray.Icon is null || !tray.ToolTipText!.Contains(Loc.Instance["status.connected"], StringComparison.Ordinal))
        { throw new InvalidOperationException("Tray did not reflect startup connection."); }
        var menu = tray.Menu!;
        var servers = ((NativeMenuItem)menu.Items[3]).Menu!;
        if (servers.Items.Count != shell.Profiles.Count || !((NativeMenuItem)servers.Items[0]).IsChecked)
        { throw new InvalidOperationException("Tray server selection is missing."); }
        var disconnect = (NativeMenuItem)menu.Items[2];
        disconnect.Command!.Execute(null);
        Until(() => services.Connections.Primary.Status == ConnectionStatus.Disconnected);
        Pump(50);
        if (clock.IsEnabled) { throw new InvalidOperationException("Disconnected duration clock did not stop."); }

        // Registration must remain blocked in developer sessions without touching the OS.
        shell.Settings.StartWithSystem = true;
        if (shell.Settings.StartWithSystem || services.Settings.StartWithSystem)
        { throw new InvalidOperationException("Safety lock allowed autostart registration."); }
        shell.Settings.ConnectOnStartup = false;
        shell.Settings.MinimizeToTray = true;
        shell.Settings.ShowNotifications = false;
        if (services.Settings.ConnectOnStartup || !services.Settings.MinimizeToTray || services.Settings.ShowNotifications)
        { throw new InvalidOperationException("Desktop preferences did not update."); }

        shell.Navigate("settings");
        shell.Toast = null;
        foreach (var (name, width) in new[] { ("desktop-settings", 1000), ("desktop-settings-narrow", 400) })
        {
            window.Width = width;
            Pump(100);
            var title = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == Loc.Instance["settings.desktop"]);
            var scroll = title.GetVisualAncestors().OfType<ScrollViewer>().First();
            var position = title.TranslatePoint(new Point(), (Visual)scroll.Content!);
            scroll.Offset = new Vector(0, position!.Value.Y - 24);
            Pump(100);
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing desktop settings frame.");
            frame.Save(Path.Combine(output, name + ".png"));
        }

        // Exercise explicit exit after the close setting; headless cannot display an OS tray.
        shell.Settings.MinimizeToTray = false;
        window.Close();
        Until(() => !window.IsVisible);
        Console.WriteLine("Desktop integration: mock startup connection, tray state/menu, disconnect, settings, registration guard and explicit exit passed.");
    }

    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) { Pump(10); }
        if (!condition()) { throw new TimeoutException("Desktop verification did not reach the expected state."); }
    }

    private static void Wait(Task task)
    {
        Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); } while (DateTime.UtcNow < until);
    }
}
