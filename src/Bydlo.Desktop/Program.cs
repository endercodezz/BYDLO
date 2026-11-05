using Avalonia;
using Avalonia.Media;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Security;

namespace Bydlo.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var earlyPaths = Bydlo.Platform.AppPathsFactory.Create(args);
        CrashReporter.Install(earlyPaths.LogsDirectory, LogRedactor.Shared);
        // Do this before claiming the single-instance mutex or forwarding activation.
        // Otherwise the elevated child forwards back to the parent and exits during UAC restart.
        if (OperatingSystem.IsWindows() && !ElevationHandoff.WaitForParent(ref args))
        {
            Environment.ExitCode = 1;
            return;
        }

        var dataDirectory = earlyPaths.DataDirectory;
        using var activation = OperatingSystem.IsWindows() ? new WindowsActivationBroker(
            dataDirectory, Bydlo.App.App.EnqueueActivation) : null;
        if (activation is { IsPrimary: false })
        {
            try
            {
                activation.Forward(args);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                Environment.ExitCode = 1;
            }

            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            UnixActivationBroker? unixActivation = null;
            try
            {
                unixActivation = new UnixActivationBroker(dataDirectory, Bydlo.App.App.EnqueueActivation);
                if (!unixActivation.IsPrimary)
                {
                    unixActivation.Forward(args);
                    unixActivation.Dispose();
                    return;
                }

                unixActivation.Listen();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
            {
                unixActivation?.Dispose();
                // No services, engines or recovery are initialized in this error-only application.
                Bydlo.App.App.StartupErrorKey = "startup.activationFailed";
                BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
                Environment.ExitCode = 1;
                return;
            }

            using (unixActivation)
            {
                Bydlo.App.App.Args = args;
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }

            return;
        }

        activation?.Listen();
        Bydlo.App.App.Args = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Bydlo.App.App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                CompositionMode = [Win32CompositionMode.RedirectionSurface],
            })
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();
}
