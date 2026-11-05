using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Security;

namespace Bydlo.App;

public sealed partial class App : Application
{
    public static System.Collections.Concurrent.ConcurrentQueue<string[]> PendingActivations { get; } = new();
    public static event Action? ActivationRequested;

    public static void EnqueueActivation(string[] inputs)
    {
        PendingActivations.Enqueue(inputs);
        ActivationRequested?.Invoke();
    }

    /// <summary>Command-line arguments, set by the platform head before start.</summary>
    public static string[] Args { get; set; } = [];

    /// <summary>Displays a startup failure without opening settings, engines or recovery.</summary>
    public static string? StartupErrorKey { get; set; }

    public AppServices? Services { get; private set; }

    public DesktopIntegration? Desktop { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Services?.Paths.LogsDirectory;
                if (dir is not null)
                {
                    CrashReporter.WriteReport(dir, LogRedactor.Shared, e.Exception);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (StartupErrorKey is { } errorKey && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime errorDesktop)
        {
            Loc.Instance.SetLanguage("auto");
            Console.Error.WriteLine(Loc.T(errorKey));
            errorDesktop.MainWindow = new Window
            {
                Title = "BYDLO",
                Width = 520,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                Content = new TextBlock
                {
                    Text = Loc.T(errorKey),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(24),
                },
            };
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // Loading settings and profiles is fast local file I/O; doing it synchronously keeps startup simple.
        Services = Task.Run(() => AppServices.CreateAsync(Args)).GetAwaiter().GetResult();
        if (Bydlo.Core.Platform.StartupElevation.ShouldElevateOnStartup(
                Services.Settings, Services.Privileges, Services.IsMock, Bydlo.Core.Security.NetworkSafetyGuard.IsLocked))
        {
            var launchArgs = Bydlo.Core.Platform.ElevatedRestart.BuildArguments(
                Args, Environment.ProcessId, Services.ForcedMock, Services.Paths.DataDirectory);
            if (Services.Privileges.TryRestartElevated(launchArgs))
            {
                Environment.Exit(0);
                return;
            }
            // Declined or failed: continue with limited features, the settings page explains what is unavailable.
        }
        Loc.Instance.SetLanguage(Services.Settings.Language);
        ApplyTheme(Services);
        Services.Theme.TrackSystemTheme();
        var shell = new MainViewModel(Services);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = shell };
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Desktop = new DesktopIntegration(shell, window, desktop);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new MainView { DataContext = shell };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Applies the saved theme; a broken or missing custom file falls back to Purple.</summary>
    private static void ApplyTheme(AppServices services) => services.Theme.ApplySaved(services.Settings, services.Paths.DataDirectory);
}
