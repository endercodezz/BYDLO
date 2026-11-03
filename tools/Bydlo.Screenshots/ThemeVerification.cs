using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Models;
using Bydlo.Core.Storage;
using Bydlo.Core.Themes;

internal static class ThemeVerification
{
    // Real Avalonia app with the real ThemeService (headless). No engines, no network.
    public static void Run(MainViewModel shell, string output)
    {
        var services = shell.Services;
        Require(shell.IsOnboardingVisible && shell.OnboardingThemeSystem, "Fresh installation starts with onboarding and System selected");
        VerifyThemeChoices(shell, output);
        var themesDir = CustomThemeStore.ThemesDirectory(services.Paths.DataDirectory);
        services.Theme.Apply(ThemePreference.Purple);
        var purpleBackground = BackgroundColor();

        var same = CustomTheme.Parse("""
            {"id": "same", "name": "Same", "author": "", "version": 1, "base": "dark",
             "colors": {"Text": "#112233", "Bg": "#112233", "Surface": "#112233"}}
            """);
        Require(services.Theme.ApplyCustom(same), "Identical text and background trigger the contrast warning");
        Require(ThemeService.HasLowContrast(Avalonia.Application.Current!), "Warning is visible through merged dictionaries");
        services.Settings.Theme = ThemePreference.Custom;
        services.Settings.CustomThemeId = "missing";
        Require(!services.Theme.ApplySaved(services.Settings, services.Paths.DataDirectory), "Missing custom file reports no warning");
        shell.Settings.RefreshCustomThemes();
        Require(!shell.Settings.HasCustomThemeWarning, "Fallback to Purple clears the warning");
        Require(BackgroundColor() == purpleBackground, "Missing custom theme restores the Purple palette");

        // A theme that cannot be applied must never block startup:
        // ApplyCustom throws, ApplySaved falls back to the Purple base palette.
        services.Theme.Apply(ThemePreference.Light);
        Require(BackgroundColor() != purpleBackground, "Light has a different background from Purple");
        var bad = new CustomThemeDocument("bad", "Bad", "", 1, true,
            new Dictionary<string, string> { ["Text"] = "#112233\n" }, []);
        try
        {
            services.Theme.ApplyCustom(bad);
            throw new InvalidOperationException("Unappliable color was applied");
        }
        catch (FormatException) { }
        Require(!services.Theme.ApplySaved(new AppSettings { Theme = ThemePreference.Custom, CustomThemeId = "missing" }, services.Paths.DataDirectory),
            "Unappliable custom theme falls back without throwing");
        Require(BackgroundColor() == purpleBackground, "Fallback applies the Purple base palette");

        var ocean = CustomTheme.Parse("""
            {"id": "ocean", "name": "Ocean", "author": "BYDLO", "version": 1, "base": "dark",
             "colors": {"Bg": "#0F1A24", "Surface": "#15222E", "Primary": "#2E8FB8", "Accent": "#5EC8E5", "Text": "#E8F1F5"}}
            """);
        CustomThemeStore.Save(themesDir, """
            {"id": "ocean", "name": "Ocean", "author": "BYDLO", "version": 1, "base": "dark",
             "colors": {"Bg": "#0F1A24", "Surface": "#15222E", "Primary": "#2E8FB8", "Accent": "#5EC8E5", "Text": "#E8F1F5"}}
            """);
        Require(!services.Theme.ApplyCustom(ocean), "Readable theme applies without a warning");
        services.Settings.CustomThemeId = "ocean";
        shell.Settings.Theme = ThemePreference.Custom;
        Require(shell.Settings.InstalledThemes.Count == 1 && shell.Settings.InstalledThemes[0].IsActive, "Installed theme is listed and active");
        Require(!shell.Settings.HasCustomThemeWarning, "Readable theme shows no warning in Settings");

        var window = new Window { Width = 1180, Height = 900, Content = new SettingsView { DataContext = shell.Settings } };
        window.Show();
        Pump();
        using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "themes-custom.png")); }
        window.Close();
        services.Settings.Theme = ThemePreference.Purple;
        services.Settings.CustomThemeId = null;
        services.Theme.Apply(ThemePreference.Purple);
        Console.WriteLine("Theme verification passed: first-run System, legacy selections, user theme grouping, Black, contrast warning, fallback, install, Settings render.");
    }

    private static void VerifyThemeChoices(MainViewModel shell, string output)
    {
        var services = shell.Services;
        var view = new SettingsView { DataContext = shell.Settings };
        var window = new Window { Width = 1180, Height = 900, Content = view };
        window.Show();
        Pump();
        view.GetVisualDescendants().OfType<Expander>().Single(e => e.Header?.ToString() == Loc.T("theme.custom.title")).IsExpanded = true;
        Pump();
        var radios = view.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "theme").ToArray();
        var purple = radios.Single(r => r.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Loc.T("theme.purple")));
        Require(purple.FindAncestorOfType<Expander>() is not null, "Purple belongs to the user themes group");
        Require(radios.Where(r => r != purple).All(r => r.FindAncestorOfType<Expander>() is null), "Standard themes remain outside the user group");
        Require(radios.Any(r => r.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Loc.T("theme.oled"))), "Black is available");
        foreach (var preference in Enum.GetValues<ThemePreference>().Where(t => t != ThemePreference.Custom))
        {
            // Simulate settings written by older versions, without renaming serialized enum values.
            var legacyPath = Path.Combine(output, "legacy-settings.json");
            File.WriteAllText(legacyPath, "{\"Theme\":\"" + preference + "\"}");
            var saved = new SettingsStore(legacyPath).LoadAsync().GetAwaiter().GetResult();
            Require(saved.Theme == preference && saved.OnboardingCompleted, "Legacy selection survives without onboarding");
            services.Theme.ApplySaved(saved, services.Paths.DataDirectory);
            var background = BackgroundColor();
            shell.Settings.Theme = saved.Theme;
            Pump();
            Require(BackgroundColor() == background, "Selecting a legacy theme preserves its palette");
            Require(radios.Count(r => r.IsChecked == true) == 1, "Exactly one theme is selected across both groups");
        }
        purple.IsChecked = true;
        Pump();
        Require(shell.Settings.ThemePurple && shell.Settings.IsUserThemeSelected, "Purple can be selected without importing a file");
        using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "themes-purple.png")); }
        shell.Settings.ThemeOled = true;
        Pump();
        Require(!purple.IsChecked.GetValueOrDefault() && BackgroundColor() == Colors.Black, "Black deselects Purple and uses a black background");
        window.Close();

        shell.OnboardingThemePurple = true;
        var onboarding = new MainView { DataContext = shell };
        window = new Window { Width = 1180, Height = 900, Content = onboarding };
        window.Show();
        Pump();
        onboarding.GetVisualDescendants().OfType<Expander>().Single(e => e.Header?.ToString() == Loc.T("onboarding.appearance")).IsExpanded = true;
        Pump();
        var onboardingPurple = onboarding.GetVisualDescendants().OfType<RadioButton>()
            .Single(r => r.GroupName == "onboarding-theme" && r.IsChecked == true);
        Require(onboardingPurple.FindAncestorOfType<Expander>()?.Header?.ToString() == Loc.T("theme.custom.title"), "First-run Purple is selected in the user themes group");
        shell.OnboardingThemeOled = true;
        Pump();
        Require(onboardingPurple.IsChecked == false && services.Settings.Theme == ThemePreference.Oled, "First-run Black selection is saved");
        window.Close();
    }

    private static Color BackgroundColor() =>
        Avalonia.Application.Current!.Resources.TryGetResource("BgBrush", null, out var resource) &&
        resource is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException("The active theme must provide a solid background brush.");

    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
