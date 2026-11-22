using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;

internal static class AutomaticUpdateVerification
{
    public static void Run(MainViewModel shell, string output)
    {
        shell.FinishOnboardingCommand.Execute(null);
        shell.Settings.ReduceMotion = true;
        // The headless run is a development build, so the install kind is forced to exercise both paths.
        shell.Services.AutomaticUpdates.InstallKind = AppInstallKind.Velopack;
        shell.Settings.RefreshUpdateState();
        foreach (var language in new[] { "ru", "en" })
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Oled })
        foreach (var width in new[] { 1180, 380 })
        {
            shell.Settings.Language = language;
            Loc.Instance.SetLanguage(language);
            shell.Settings.Theme = theme;
            shell.Services.Theme.Apply(theme);
            Pump();
            var view = new SettingsView { DataContext = shell.Settings };
            var window = new Window { Width = width, Height = 900, Content = view };
            window.Show();
            Pump();
            var picker = view.FindControl<ComboBox>("CompactCategory")!;
            picker.SelectedIndex = 0;
            Pump();
            picker.SelectedIndex = picker.Items.Count - 1;
            Pump();
            var toggle = view.FindControl<ToggleSwitch>("AutoUpdateToggle")!;
            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggle))) { throw new InvalidOperationException("Missing accessible update label"); }
            if (!toggle.IsEffectivelyVisible || toggle.Bounds.Width <= 0) { throw new InvalidOperationException("Update toggle is hidden"); }
            // Mock Mode keeps the switch but explains why the built-in updater cannot run here.
            if (view.FindControl<TextBlock>("UpdateGuidance")!.Text != Loc.Instance["settings.update.auto.unavailable"])
            { throw new InvalidOperationException("Mock Mode must explain why automatic updates are unavailable"); }
            // Issue #438: without the GitHub update consent the opt-in cannot be switched on.
            shell.Settings.ConsentAutomaticUpdates = false;
            Pump();
            if (toggle.IsEffectivelyEnabled) { throw new InvalidOperationException("Update opt-in must wait for the GitHub consent"); }
            shell.Settings.ConsentAutomaticUpdates = true;
            Pump();
            shell.Settings.AutoUpdateApp = false;
            toggle.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Pump();
            if (!shell.Services.Settings.AutoUpdateApp) { throw new InvalidOperationException("Keyboard opt-in failed"); }
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Pump();
            if (shell.Services.Settings.AutoUpdateApp) { throw new InvalidOperationException("Keyboard opt-out failed"); }
            shell.Settings.AutoUpdateApp = true;
            shell.Settings.CheckForUpdatesIfDueAsync().GetAwaiter().GetResult();
            if (shell.Services.Settings.LastAutomaticUpdateCheck is not null) { throw new InvalidOperationException("Mock must block automatic downloads"); }
            shell.Settings.AutoUpdateApp = false;
            Pump();
            if (Loc.Instance.Language != language) { throw new InvalidOperationException("Verification language changed unexpectedly"); }
            if (!view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Loc.Instance["settings.update.auto"]))
            { throw new InvalidOperationException("Update label did not render in the requested language"); }
            using var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(output, $"auto-update-{language}-{theme}-{width}.png"));
            window.Close();
        }
        VerifyManualGuidance(shell, output);
        VerifyReleaseNotificationKeepsTheState(shell);
        Console.WriteLine("Automatic update UI passed: keyboard toggle/persistence, Mock guard, RU/EN, Light/Black, wide/narrow, manual guidance per install kind.");
    }

    /// <summary>
    /// A package-manager or portable copy must not offer the opt-in switch and must explain the manual path
    /// instead (issue #413).
    /// </summary>
    private static void VerifyManualGuidance(MainViewModel shell, string output)
    {
        // The screenshots show the plain .deb text; a configured apt source is a separate case (issue #488).
        shell.Services.AptRepositoryConfigured = false;
        foreach (var kind in new[]
        {
            AppInstallKind.Debian, AppInstallKind.Arch, AppInstallKind.Nix, AppInstallKind.Portable, AppInstallKind.Unknown,
        })
        {
            shell.Services.AutomaticUpdates.InstallKind = kind;
            shell.Settings.RefreshUpdateState();
            Pump();
            var view = new SettingsView { DataContext = shell.Settings };
            var window = new Window { Width = 1180, Height = 900, Content = view };
            window.Show();
            Pump();
            var picker = view.FindControl<ComboBox>("CompactCategory")!;
            picker.SelectedIndex = picker.Items.Count - 1;
            Pump();
            var toggle = view.FindControl<ToggleSwitch>("AutoUpdateToggle")!;
            if (toggle.IsEffectivelyVisible) { throw new InvalidOperationException("The opt-in switch must be hidden for " + kind); }
            var guidance = view.FindControl<TextBlock>("UpdateGuidance")!;
            var expected = Loc.Instance[UpdateGuidance.KeyFor(kind)];
            if (!guidance.IsEffectivelyVisible || guidance.Text != expected)
            { throw new InvalidOperationException("Missing manual update guidance for " + kind); }
            using var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(output, $"auto-update-manual-{kind.ToString().ToLowerInvariant()}.png"));
            window.Close();
        }
        shell.Services.AutomaticUpdates.InstallKind = AppInstallKind.Velopack;
        shell.Settings.RefreshUpdateState();
        Pump();
    }

    /// <summary>The release notification only records a version; it never replaces the packaged update state.</summary>
    private static void VerifyReleaseNotificationKeepsTheState(MainViewModel shell)
    {
        shell.Services.AutomaticUpdates.MarkAvailable("v9.9.9");
        shell.Settings.RefreshUpdateState();
        Pump();
        if (shell.Settings.UpdateStatus != Loc.T("settings.update.available", "v9.9.9"))
        { throw new InvalidOperationException("An available release must be visible in the unified status line."); }
        shell.Services.AutomaticUpdates.MarkAvailable(null);
        shell.Settings.RefreshUpdateState();
        Pump();
        if (shell.Settings.UpdateStatus == Loc.T("settings.update.available", "v9.9.9"))
        { throw new InvalidOperationException("A cleared release must not stay in the status line."); }
    }

    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }
}
