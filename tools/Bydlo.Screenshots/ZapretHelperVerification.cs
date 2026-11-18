using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Connection;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

internal static class ZapretHelperVerification
{
    public static void Run(string output)
    {
        Require(NetworkSafetyGuard.IsLocked, "Safety lock is required");
        var services = Wait(AppServices.CreateAsync(["--mock"], new UnelevatedPrivileges()));
        var shell = new MainViewModel(services);
        shell.FinishOnboardingCommand.Execute(null);
        shell.Zapret = new SessionSnapshot(ConnectionManager.ZapretKey, ConnectionStatus.Error,
            null, null, true, null, "Zapret needs administrator / root rights.", null)
        { ErrorLocalizationKey = "error.engine.zapret.admin" };
        Require(shell.Tools.ShowZapretHelperSetup, "Missing privilege exposes helper setup");
        Require(!shell.Tools.ShowRestartElevated, "Linux-style privilege service cannot restart the UI as root");
        var window = new Window();
        window.Show();
        foreach (var language in new[] { "ru", "en" })
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Oled })
        foreach (var width in new[] { 1000, 400 })
        {
            shell.Settings.Language = language;
            shell.Settings.Theme = theme;
            window.Content = new ToolsView { DataContext = shell.Tools };
            window.Width = width;
            window.Height = 900;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            Dispatcher.UIThread.MainLoop(timeout.Token);
            var button = window.GetVisualDescendants().OfType<Button>().Single(b =>
                ReferenceEquals(b.Command, shell.Tools.InstallZapretHelperCommand));
            Require(button.IsEffectivelyVisible && !button.IsEffectivelyEnabled,
                "Recovery is visible and installation is disabled under the safety lock");
            Require(button.Content is TextBlock label && label.Text == Loc.Instance["tools.zapret.helper.install"],
                $"Recovery action is localized: requested={language}, current={Loc.Instance.Language}, actual={(button.Content as TextBlock)?.Text}, expected={Loc.Instance["tools.zapret.helper.install"]}");
            Wait(shell.Tools.InstallZapretHelperCommand.ExecuteAsync(null));
            Require(services.Connections.Zapret.Status == ConnectionStatus.Disconnected,
                "Guarded install never starts an engine");
            Require(!shell.Settings.HelperBusy, "Guarded install never manages a service");
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame");
            frame.Save(Path.Combine(output, $"zapret-helper-{language}-{theme}-{width}.png"));
        }
        shell.Zapret = shell.Zapret with { ErrorLocalizationKey = "error.engine.zapret.missing" };
        Require(!shell.Tools.ShowZapretHelperSetup, "Unrelated errors do not offer privilege installation");
        window.Close();
        Wait(services.ShutdownAsync());
        Console.WriteLine("Zapret helper recovery: localized action, desktop/compact Light/Oled, unrelated error and safety guards passed.");
    }

    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Require(task.IsCompleted, "Operation completes");
        task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
    private sealed class UnelevatedPrivileges : IPrivilegeService
    {
        public bool IsElevated => false;
        public bool CanRestartElevated => false;
        public bool TryRestartElevated(IReadOnlyList<string> args) => throw new InvalidOperationException("Unexpected elevation");
    }
}
