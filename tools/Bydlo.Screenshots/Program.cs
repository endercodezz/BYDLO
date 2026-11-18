using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Models;

// Usage: dotnet run --project tools/Bydlo.Screenshots -- <output-dir> [en|ru]
// Forces Mock Mode and the development safety lock; nothing is started and no network is used.
Environment.SetEnvironmentVariable("BYDLO_MOCK", "1");
Environment.SetEnvironmentVariable("BYDLO_SAFE_DEV", "1");
var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(".tmp", "screenshots"));
var lang = args.Length > 1 ? args[1] : "en";
Directory.CreateDirectory(output);
if (args.Contains("--verify-licenses", StringComparer.Ordinal))
{
    Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", Path.Combine(output, "appdata"));
}
if (args.Contains("--verify-overhaul", StringComparer.Ordinal) || args.Contains("--verify-settings-navigation", StringComparer.Ordinal) || args.Contains("--verify-zapret-helper", StringComparer.Ordinal))
{
    Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", Path.Combine(output, "appdata"));
}
if (args.Contains("--verify-server-groups", StringComparer.Ordinal) || args.Contains("--verify-simple-ui", StringComparer.Ordinal) || args.Contains("--verify-wizard", StringComparer.Ordinal) || args.Contains("--verify-backup", StringComparer.Ordinal) || args.Contains("--verify-import", StringComparer.Ordinal) || args.Contains("--verify-share", StringComparer.Ordinal) ||
    args.Contains("--verify-updates", StringComparer.Ordinal) || args.Contains("--verify-speed", StringComparer.Ordinal) || args.Contains("--verify-themes", StringComparer.Ordinal) || args.Contains("--verify-theme-gallery", StringComparer.Ordinal) || args.Contains("--verify-active-connections", StringComparer.Ordinal) || args.Contains("--verify-routing-rules", StringComparer.Ordinal) || args.Contains("--verify-routing-picker", StringComparer.Ordinal) || args.Contains("--verify-connect-switch", StringComparer.Ordinal) ||
    args.Contains("--verify-zapret-selection", StringComparer.Ordinal) || args.Contains("--verify-desktop", StringComparer.Ordinal) || args.Contains("--verify-bundled-engines", StringComparer.Ordinal) || args.Contains("--verify-contributors", StringComparer.Ordinal) || args.Contains("--verify-elevation", StringComparer.Ordinal) ||
    Environment.GetEnvironmentVariable("BYDLO_DATA_DIR") is null)
{
    Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", Path.Combine(output, "appdata"));
}

if (args.Contains("--verify-disclosure", StringComparer.Ordinal))
{
    Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", Path.Combine(output, "appdata"));
}

if (args.Contains("--verify-startup-error", StringComparer.Ordinal))
{
    StartupErrorVerification.Run(output, lang);
    return;
}

Bydlo.App.App.Args = ["--mock"];
AppBuilder.Configure<Bydlo.App.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
    .SetupWithoutStarting();

var app = (Bydlo.App.App)Application.Current!;
if (app.Services is null)
{
    app.OnFrameworkInitializationCompleted();
}
var services = app.Services!;
Bydlo.App.Services.Loc.Instance.SetLanguage(lang);

if (args.Contains("--verify-zapret-helper", StringComparer.Ordinal))
{
    ZapretHelperVerification.Run(output);
    return;
}

if (args.Contains("--verify-elevation", StringComparer.Ordinal))
{
    ElevationVerification.Run(output);
    return;
}

if (args.Contains("--export-brand-icon", StringComparer.Ordinal))
{
    BrandIconExport.Run(output);
    return;
}

if (args.Contains("--verify-disclosure", StringComparer.Ordinal))
{
    DisclosureVerification.Run(new MainViewModel(services), output);
    return;
}

if (args.Contains("--verify-wizard", StringComparer.Ordinal))
{
    WizardVerification.Run(services, output);
    return;
}
var shell = new MainViewModel(services);
if (args.Contains("--verify-licenses", StringComparer.Ordinal))
{
    LicenseVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-auto-updates", StringComparer.Ordinal))
{
    AutomaticUpdateVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-settings-navigation", StringComparer.Ordinal))
{
    SettingsNavigationVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-overhaul", StringComparer.Ordinal))
{
    OverhaulVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-server-groups", StringComparer.Ordinal))
{
    ServerGroupVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-theme-gallery", StringComparer.Ordinal))
{
    ThemeVerification.RunGallery(shell, output);
    return;
}
if (args.Contains("--verify-themes", StringComparer.Ordinal))
{
    ThemeVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-backup", StringComparer.Ordinal))
{
    BackupVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-bundled-engines", StringComparer.Ordinal))
{
    BundledEnginesVerification.Run(shell, services, output);
    return;
}
if (args.Contains("--verify-desktop", StringComparer.Ordinal))
{
    DesktopVerification.Run(shell, services, output);
    return;
}
if (args.Contains("--verify-zapret-selection", StringComparer.Ordinal))
{
    ZapretSelectionVerification.Run(shell, services, output);
    return;
}
if (args.Contains("--verify-share", StringComparer.Ordinal))
{
    ShareVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-connect-switch", StringComparer.Ordinal))
{
    ConnectSwitchVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-import", StringComparer.Ordinal))
{
    ImportVerification.Run(shell, output);
    return;
}

// The UI thread is this thread: never block on a task, pump the dispatcher until it completes.
void Wait(Task task)
{
    while (!task.IsCompleted)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(15);
    }

    task.GetAwaiter().GetResult();
}

void Pump(int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(15);
    }
}

void Shoot(string name, double width, double height, Action<Window>? arrange = null)
{
    var window = new Window { Width = width, Height = height, Content = new MainView { DataContext = shell } };
    window.Show();
    Pump(400);
    if (arrange is not null)
    {
        arrange(window);
        Pump(150);
    }
    using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame: " + name);
    frame.Save(Path.Combine(output, name + ".png"));
    window.Close();
    Console.WriteLine($"saved {name}.png");
}

if (args.Contains("--verify-updates", StringComparer.Ordinal))
{
    if (shell.HasReleaseNotification || services.Settings.LastUpdateCheck is not null)
        throw new InvalidOperationException("Mock startup must skip release HTTP checks.");
    var release = new Bydlo.Core.Diagnostics.GitHubRelease("v9.9.9", "");
    shell.ShowReleaseNotification(release);
    services.Settings.PendingUpdate = release;
    shell.IsOnboardingVisible = true;
    foreach (var width in new[] { 1180, 380 })
    {
        Shoot("update-onboarding-" + width, width, 780, window =>
        {
            var open = window.GetVisualDescendants().OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, shell.OpenReleaseNotificationCommand));
            if (!open.IsEffectivelyVisible || open.Bounds.Width <= 0)
                throw new InvalidOperationException("Release action must be visible during onboarding.");
        });
    }
    shell.FinishOnboardingCommand.Execute(null);
    Shoot("update-home-compact", 380, 700, _ =>
    {
        shell.ShowToast("Temporary notification");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4.5));
        Dispatcher.UIThread.MainLoop(timeout.Token);
        if (!shell.HasReleaseNotification || shell.Toast is not null)
            throw new InvalidOperationException("Release notice must outlive unrelated toasts.");
    });
    Bydlo.App.Services.Loc.Instance.SetLanguage("en");
    if (!shell.ReleaseNotificationText.Contains("v9.9.9", StringComparison.Ordinal))
        throw new InvalidOperationException("Release version must survive a language change.");
    Bydlo.App.Services.Loc.Instance.SetLanguage(lang);
    Wait(shell.DismissReleaseNotificationCommand.ExecuteAsync(null));
    if (shell.HasReleaseNotification || services.Settings.PendingUpdate is not null)
        throw new InvalidOperationException("Dismiss must clear the notification and persisted release.");
    Shoot("update-dismissed", 1180, 780);
    Console.WriteLine("Release notification verification passed (Mock Mode, no network).");
    return;
}

if (args.Contains("--verify-contributors", StringComparer.Ordinal))
{
    Pump(300);
    if (services.Settings.LastContributorsCheck is not null || shell.Contributors.HasContributors)
        throw new InvalidOperationException("Mock startup must skip the contributors HTTP request.");
    shell.FinishOnboardingCommand.Execute(null);
    Shoot("contributors-sidebar", 1180, 780, window =>
    {
        var entry = window.GetVisualDescendants().OfType<RadioButton>().Single(b => b.Name == "ContributorsNavButton");
        var sidebar = entry.GetVisualAncestors().OfType<Border>().Single(b => b.Name == "DesktopSidebar");
        var bottom = entry.TranslatePoint(new Point(0, entry.Bounds.Height), sidebar) ?? default;
        if (!entry.IsEffectivelyVisible || bottom.Y > sidebar.Bounds.Height || bottom.Y < sidebar.Bounds.Height / 2)
            throw new InvalidOperationException("Contributors entry must sit in the lower sidebar corner.");
        entry.Command!.Execute(entry.CommandParameter);
    });
    if (shell.CurrentNav != shell.ContributorsNav || !shell.ContributorsNav.IsSelected)
        throw new InvalidOperationException("Sidebar entry must open the contributors page.");
    Shoot("contributors-empty", 1180, 780, window => AssertRows(window, 0));
    // Sample cache, as if loaded from GitHub; these names exist only in this verification.
    shell.Contributors.Show(Enumerable.Range(1, 7).Select(i => new Bydlo.Core.Diagnostics.Contributor("contributor-" + i, 100 - i * 9)));
    Shoot("contributors-page", 1180, 780, window => AssertRows(window, 7));
    shell.NavigateCommand.Execute("settings");
    Shoot("contributors-compact", 380, 700, window =>
    {
        window.GetVisualDescendants().OfType<Expander>()
            .Single(e => Equals(e.Header, Bydlo.App.Services.Loc.Instance["settings.about"])).IsExpanded = true;
        Pump(150);
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ContributorsButton");
        button.Command!.Execute(button.CommandParameter);
    });
    if (shell.CurrentNav != shell.ContributorsNav)
        throw new InvalidOperationException("Settings must open the contributors page in the compact layout.");
    Shoot("contributors-page-compact", 380, 700, window => AssertRows(window, 7));
    Console.WriteLine("Contributors page verification passed (Mock Mode, no network).");
    return;

    static void AssertRows(Window window, int expected)
    {
        var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ContributorList");
        var rows = list.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Bounds.Width > 0);
        if (rows != expected)
            throw new InvalidOperationException($"Expected {expected} contributor rows, got {rows}.");
    }
}

if (args.Contains("--preview-brand", StringComparer.Ordinal))
{
    shell.FinishOnboardingCommand.Execute(null);
    Bydlo.App.Services.Loc.Instance.SetLanguage(lang);
    services.Theme.Apply(ThemePreference.Purple);
    Shoot("brand-desktop", 1180, 780);
    Shoot("brand-compact", 380, 700);
    return;
}

if (args.Contains("--verify-speed", StringComparer.Ordinal))
{
    shell.FinishOnboardingCommand.Execute(null);
    Wait(shell.Connections.AddDemoProfilesCommand.ExecuteAsync(null));
    shell.Navigate("connections");
    var ids = shell.Profiles.Select(profile => profile.Id).ToArray();
    shell.SelectedProfile = shell.Profiles[1];
    shell.SetServerSpeeds(new Dictionary<Guid, long> { [ids[0]] = 1024, [ids[1]] = 8192, [ids[2]] = 0 });
    shell.SortBySpeed = true;
    if (shell.Profiles[0].Id != ids[1] || shell.SelectedProfile?.Id != ids[1] ||
        shell.Profiles.Single(profile => profile.Id == ids[2]).MeasuredSpeedText != "—")
        throw new InvalidOperationException("Speed sorting, selection or unavailable result failed.");
    if (shell.Connections.CanMeasureSpeed || string.IsNullOrEmpty(shell.Connections.SpeedTestDisabledReason))
        throw new InvalidOperationException("Mock/safe session must disable speed tests with an explanation.");
    // Even direct command execution must respect the safety guard.
    Wait(shell.Connections.MeasureSpeedCommand.ExecuteAsync(null));
    foreach (var width in new[] { 1180, 380 })
    {
        var window = new Window { Width = width, Height = 900, Content = new ConnectionsView { DataContext = shell.Connections } };
        window.Show();
        Pump(100);
        var expander = window.GetVisualDescendants().OfType<Expander>()
            .Single(control => Equals(control.Header, Bydlo.App.Services.Loc.Instance["connections.speed.title"]));
        expander.IsExpanded = true;
        Pump(100);
        expander.BringIntoView();
        Pump(100);
        var start = expander.GetVisualDescendants().OfType<Button>()
            .Single(button => ReferenceEquals(button.Command, shell.Connections.MeasureSpeedCommand));
        if (start.IsEnabled || !expander.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.IsVisible && text.Text == shell.Connections.SpeedTestDisabledReason))
            throw new InvalidOperationException("Speed test bindings or disabled explanation failed.");
        using (var frame = window.CaptureRenderedFrame()) { frame?.Save(Path.Combine(output, $"speed-{width}.png")); }
        window.Close();
    }
    Console.WriteLine("Speed UI verification passed: results, sorting, selection, safety guard, bindings and compact layout.");
    return;
}

if (args.Contains("--verify-simple-ui", StringComparer.Ordinal))
{
    shell.FinishOnboardingCommand.Execute(null);
    if (shell.Profiles.Count != 0) throw new InvalidOperationException("Use a fresh output directory for empty-state verification.");
    Bydlo.App.Services.Loc.Instance.SetLanguage(lang);
    foreach (var theme in new[] { ThemePreference.Purple, ThemePreference.Light, ThemePreference.Oled, ThemePreference.System })
    {
        services.Theme.Apply(theme);
        foreach (var (width, height) in new[] { (1180, 780), (900, 780), (760, 780), (380, 700) })
        {
            shell.Navigate("home");
            Shoot($"simple-home-{theme.ToString().ToLowerInvariant()}-{width}", width, height);
        }
    }
    services.Theme.Apply(ThemePreference.Purple);
    foreach (var source in new[] { "link", "subscription", "clipboard", "file", "qr" })
    {
        // No native pickers or clipboard in headless mode: their unavailable-service errors
        // must leave the wizard open on the correct source page, never start an engine.
        services.TopLevel = () => null;
        Wait(shell.OpenImportCommand.ExecuteAsync(source));
        var expected = source == "file" ? AddServerStep.File : source == "qr" ? AddServerStep.Qr : AddServerStep.Link;
        if (!shell.IsAddServerOpen || shell.AddServerWizard.Step != expected)
            throw new InvalidOperationException("Wrong direct import destination: " + source);
        shell.AddServerWizard.CloseCommand.Execute(null);
    }
    Wait(shell.OpenImportCommand.ExecuteAsync("link"));
    shell.AddServerWizard.Input = "draft";
    Wait(shell.OpenImportCommand.ExecuteAsync("subscription"));
    if (shell.AddServerWizard.Input != "draft") throw new InvalidOperationException("Direct import reset an open wizard.");
    shell.AddServerWizard.CloseCommand.Execute(null);
    var settingsWindow = new Window { Width = 380, Height = 700, Content = new SettingsView { DataContext = shell.Settings } };
    settingsWindow.Show();
    Pump(100);
    foreach (var page in new[] { "routing", "statistics" })
    {
        // The standalone SettingsView has no MainView to update the shell breakpoint.
        shell.IsCompact = true;
        var section = settingsWindow.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.CommandParameter, page));
        if (!section.IsEffectivelyVisible || !section.Focusable)
            throw new InvalidOperationException("Settings section must be visible and focusable: " + page);
        section.Command!.Execute(section.CommandParameter);
        if (!shell.IsSettingsDetail || !shell.PrimaryNavItems.Single(item => item.Key == "settings").IsSelected)
            throw new InvalidOperationException("Settings subsection lost its parent navigation: " + page);
        Shoot($"simple-{page}-380", 380, 700);
    }
    if (shell.PrimaryNavItems.Count != 4)
        throw new InvalidOperationException("The compact shell must expose four primary destinations.");
    shell.Navigate("settings");
    var gallery = settingsWindow.GetVisualDescendants().OfType<Button>()
        .Single(button => ReferenceEquals(button.Command, shell.Settings.FetchGalleryCommand));
    if (!gallery.IsEffectivelyVisible || !gallery.Focusable)
        throw new InvalidOperationException("The GitHub theme gallery must be visible without expanding custom themes.");
    var advanced = settingsWindow.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "AdvancedSettings");
    if (advanced.IsExpanded) throw new InvalidOperationException("Advanced settings must start collapsed.");
    var categoryPicker = settingsWindow.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "CompactCategory");
    categoryPicker.SelectedItem = categoryPicker.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "AdvancedSection"));
    Pump(50);
    if (!advanced.IsExpanded || !shell.Settings.ShowAdvanced || !services.Settings.ShowAdvanced)
        throw new InvalidOperationException("The settings index must open the advanced disclosure and update the saved preference.");
    advanced.IsExpanded = false;
    Pump(50);
    if (shell.Settings.ShowAdvanced) throw new InvalidOperationException("Advanced disclosure did not close.");
    settingsWindow.Close();
    foreach (var page in new[] { "tools", "settings" })
    {
        shell.Navigate(page);
        Shoot($"simple-{page}", 1180, 900);
        Shoot($"simple-{page}-380", 380, 700);
    }
    shell.Navigate("home");
    var homeWindow = new Window { Width = 1180, Height = 780, Content = new MainView { DataContext = shell } };
    homeWindow.Show();
    Pump(100);
    var telegramToggle = homeWindow.GetVisualDescendants().OfType<ToggleSwitch>()
        .Single(control => ReferenceEquals(control.Command, shell.Home.ToggleTgCommand));
    var telegramButton = homeWindow.GetVisualDescendants().OfType<Button>()
        .Single(button => ReferenceEquals(button.Command, shell.Tools.OpenInTelegramCommand));
    if (!telegramToggle.IsEffectivelyVisible || telegramButton.IsEffectivelyVisible)
        throw new InvalidOperationException("The Telegram switch must be visible and its open button hidden until the proxy runs.");
    var publicServers = homeWindow.GetVisualDescendants().OfType<Button>()
        .Single(button => Equals(button.CommandParameter, "catalog"));
    if (!publicServers.IsEffectivelyVisible || !publicServers.Focusable)
        throw new InvalidOperationException("Public servers must be directly accessible from the empty home.");
    Wait(shell.OpenImportCommand.ExecuteAsync(publicServers.CommandParameter));
    if (!shell.IsAddServerOpen || !shell.AddServerWizard.IsCatalog)
        throw new InvalidOperationException("Public servers must open the catalog directly.");
    Shoot("simple-public-servers-380", 380, 760);
    shell.AddServerWizard.CloseCommand.Execute(null);
    homeWindow.Width = 380;
    Pump(100);
    var shortcut = homeWindow.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ToolShortcut");
    if (!shortcut.IsEffectivelyVisible || !ReferenceEquals(shortcut.Command, shell.NavigateCommand))
        throw new InvalidOperationException("Compact home must expose the tool shortcut.");
    homeWindow.Close();
    Wait(shell.Home.ToggleZapretCommand.ExecuteAsync(null));
    Pump(50); // Session notifications are posted to the UI dispatcher.
    if (!shell.Home.ZapretOn) throw new InvalidOperationException("Mock Zapret tool did not start.");
    Wait(shell.Home.ToggleZapretCommand.ExecuteAsync(null));
    Pump(50); // Session notifications are posted to the UI dispatcher.
    if (shell.Home.ZapretOn) throw new InvalidOperationException("Mock Zapret tool did not stop.");
    // No OS launcher in headless mode. Opening Telegram must never start a proxy.
    services.TopLevel = () => null;
    Wait(shell.Tools.OpenInTelegramCommand.ExecuteAsync(null));
    Pump(50);
    if (shell.Home.TgOn) throw new InvalidOperationException("Opening Telegram started the stopped proxy.");
    Wait(shell.Home.ToggleTgCommand.ExecuteAsync(null));
    Pump(50);
    if (!shell.Home.TgOn || !shell.Tools.CanOpenInTelegram)
        throw new InvalidOperationException("The switch did not start the mock proxy and enable the Telegram button.");
    var telegramStarted = services.Connections.TgWsProxy.ConnectedSince;
    Wait(shell.Tools.OpenInTelegramCommand.ExecuteAsync(null));
    if (services.Connections.TgWsProxy.ConnectedSince != telegramStarted)
        throw new InvalidOperationException("Opening Telegram twice restarted the proxy.");
    Shoot("simple-home-telegram-enabled", 1180, 780);
    Wait(services.Connections.StopAllAsync());
    Wait(shell.Connections.AddDemoProfilesCommand.ExecuteAsync(null));
    Shoot("simple-home-with-server", 1180, 780);
    Shoot("simple-home-with-server-380", 380, 700);
    Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
    Shoot("simple-home-connected", 1180, 780);
    Wait(services.Connections.StopAllAsync());
    Console.WriteLine("Simple UI verification passed: themes, compact layout, direct import destinations, independent mock tools and VPN.");
    return;
}

if (args.Contains("--verify-routing-rules", StringComparer.Ordinal))
{
    shell.FinishOnboardingCommand.Execute(null);
    services.Settings.Routing = new RoutingSettings();
    shell.Navigate("routing");
    var routing = shell.Routing;
    routing.ShowRuleEditor = true;
    routing.SourceTag = "custom-domains";
    routing.SourceUrl = "https://example.test/pinned/domains.srs";
    routing.SourceHash = new string('a', 64);
    routing.SourceMaxBytes = "1024";
    Wait(routing.SaveSourceCommand.ExecuteAsync(null));
    if (routing.RuleSetSources.Count != 1) { throw new InvalidOperationException("Source was not added."); }
    routing.SelectedSource = routing.RuleSetSources[0];
    routing.SourceMaxBytes = "2048";
    Wait(routing.SaveSourceCommand.ExecuteAsync(null));
    if (routing.RuleSetSources.Count != 1 || routing.RuleSetSources[0].MaxBytes != 2048)
    { throw new InvalidOperationException("Source edit did not preserve its position."); }
    routing.SourceTag = "custom-domains";
    routing.SourceUrl = "https://example.test/duplicate.srs";
    routing.SourceHash = new string('b', 64);
    Wait(routing.SaveSourceCommand.ExecuteAsync(null));
    if (routing.RuleSetSources.Count != 1) { throw new InvalidOperationException("Duplicate source was accepted."); }
    routing.RuleConditionIndex = (int)Bydlo.Core.Routing.RoutingCondition.GeoSite;
    routing.RuleValue = "custom-domains";
    routing.RuleActionIndex = (int)Bydlo.Core.Routing.RoutingAction.Direct;
    routing.AddRuleCommand.Execute(null);
    var first = routing.OrderedRules.Last();
    routing.RuleValue = "custom-domains";
    routing.AddRuleCommand.Execute(null);
    var duplicate = routing.OrderedRules.Last();
    routing.RuleConditionIndex = (int)Bydlo.Core.Routing.RoutingCondition.Port;
    routing.RuleValue = "443";
    routing.RuleActionIndex = (int)Bydlo.Core.Routing.RoutingAction.Block;
    routing.AddRuleCommand.Execute(null);
    var port = routing.OrderedRules.Last();
    routing.MoveRuleTo(duplicate, port);
    if (!ReferenceEquals(routing.OrderedRules[0], first) || !ReferenceEquals(routing.OrderedRules[2], duplicate))
    { throw new InvalidOperationException("Dragging a duplicate rule moved the wrong row."); }
    routing.SelectedRule = duplicate;
    routing.RuleActionIndex = (int)Bydlo.Core.Routing.RoutingAction.Proxy;
    routing.UpdateRuleCommand.Execute(null);
    Wait(routing.SaveCommand.ExecuteAsync(null));
    routing.OnShown();
    if (services.Settings.Routing.RuleSetSources.Count != 1 || routing.OrderedRules[1].Condition != Bydlo.Core.Routing.RoutingCondition.Port ||
        routing.OrderedRules[2].Action != Bydlo.Core.Routing.RoutingAction.Proxy || routing.CanDownloadPreset)
    { throw new InvalidOperationException("Routing persistence, order or mock download lock failed."); }
    routing.ShowRuleEditor = true;
    foreach (var (width, height) in new[] { (1180, 900), (380, 700) })
    {
        Shoot($"routing-sources-{width}", width, height, window =>
        {
            var editor = window.GetVisualDescendants().OfType<Expander>().Single(control => control.Name == "SourceEditor");
            editor.IsExpanded = true;
            Pump(200);
            window.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "SourceTagInput").BringIntoView();
        });
        Shoot($"routing-rules-{width}", width, height, window =>
            window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "RuleList").BringIntoView());
    }
    routing.SelectedSource = routing.RuleSetSources[0];
    routing.RemoveSourceCommand.Execute(null);
    if (routing.RuleSetSources.Count != 0) { throw new InvalidOperationException("Source was not removed."); }
    Console.WriteLine("Routing rules verification passed: source add/edit/remove, duplicate rejection, rule edit/reorder, persistence, compact layout and mock download lock.");
    return;
}

if (args.Contains("--verify-routing-picker", StringComparer.Ordinal))
{
    shell.FinishOnboardingCommand.Execute(null);
    if (OperatingSystem.IsWindows())
    {
        shell.Home.IsTun = true;
        shell.Navigate("routing");
        var routing = shell.Routing;
        routing.ShowRuleEditor = true;
        Wait(routing.FindApplicationsCommand.ExecuteAsync(null));
        var application = routing.MatchingApplications.First();
        routing.ApplicationSearch = application.Name;
        routing.SelectedApplication = routing.MatchingApplications.First();
        routing.AddApplicationCommand.Execute(null);
        routing.ProxyOnlyListedProcesses = true;
        Wait(routing.SaveCommand.ExecuteAsync(null));
        if (!services.Settings.Routing.DirectProcesses.Contains(application.Name) || !services.Settings.Routing.ProxyOnlyListedProcesses)
        {
            throw new InvalidOperationException("The selected Windows application or routing mode was not saved.");
        }
        foreach (var theme in new[] { ThemePreference.Purple, ThemePreference.Light, ThemePreference.Oled })
        {
            services.Theme.Apply(theme);
            foreach (var (width, height) in new[] { (1180, 900), (380, 700) })
            {
                Shoot($"routing-application-picker-{theme.ToString().ToLowerInvariant()}-{width}", width, height,
                    window => window.GetVisualDescendants().OfType<ComboBox>()
                        .Single(control => control.Name == "ApplicationPicker").BringIntoView());
            }
        }
        Console.WriteLine("Routing picker verification passed: Windows catalog, filtering, selection and saving.");
    }
    return;
}

if (args.Contains("--verify-active-connections", StringComparer.Ordinal))
{
    shell.FinishOnboardingCommand.Execute(null);
    Wait(shell.Connections.AddDemoProfilesCommand.ExecuteAsync(null));
    Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
    shell.Navigate("settings");
    shell.Settings.ShowAdvanced = true;
    var stats = shell.ActiveConnections;
    stats.ActiveConnectionsExpanded = true;
    Pump(200);
    var primarySnapshot = shell.Primary;
    foreach (var engine in new[] { EngineKind.Xray, EngineKind.WireGuard, EngineKind.OpenVpn })
    {
        shell.Primary = primarySnapshot with { Engine = engine, IsMock = false };
        if (stats.SupportsActiveConnections || stats.FilteredConnections.Any())
        {
            throw new InvalidOperationException("Unsupported engine offered active connections.");
        }
    }
    shell.Primary = primarySnapshot;
    Pump(200);
    var connection = stats.FilteredConnections.Single();
    foreach (var filter in new[] { " EXAMPLE.INVALID ", "BROWSER", "mock", "proxy" })
    {
        stats.ConnectionFilter = filter;
        if (stats.FilteredConnections.Single() != connection) { throw new InvalidOperationException("Connection field filter failed."); }
    }
    stats.ConnectionFilter = "missing";
    if (stats.FilteredConnections.Any()) { throw new InvalidOperationException("Filter did not hide the mock connection."); }
    stats.ConnectionFilter = "browser";
    if (stats.FilteredConnections.Single() != connection) { throw new InvalidOperationException("Filter lost the matching connection."); }
    foreach (var theme in new[] { ThemePreference.Purple, ThemePreference.Light, ThemePreference.Oled })
    {
        services.Theme.Apply(theme);
        Shoot($"active-connections-{theme.ToString().ToLowerInvariant()}", 1180, 900,
            window => window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "ActiveConnectionsList").BringIntoView());
        Shoot($"active-connections-{theme.ToString().ToLowerInvariant()}-minimum", 380, 700,
            window => window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "ActiveConnectionsList").BringIntoView());
    }
    shell.Settings.ShowAdvanced = false;
    Pump(2200);
    if (stats.FilteredConnections.Any()) { throw new InvalidOperationException("Collapsed advanced section kept polling."); }
    shell.Settings.ShowAdvanced = true;
    Pump(200);
    if (!stats.FilteredConnections.Any()) { throw new InvalidOperationException("Reopening advanced did not refresh."); }
    shell.Navigate("home");
    Pump(2200);
    if (stats.FilteredConnections.Any()) { throw new InvalidOperationException("Hidden page kept polling."); }
    shell.Navigate("settings");
    Pump(200);
    stats.ActiveConnectionsExpanded = false;
    Pump(2200);
    if (stats.FilteredConnections.Any()) { throw new InvalidOperationException("Collapsed list kept polling."); }
    stats.ActiveConnectionsExpanded = true;
    Pump(200);
    if (!stats.FilteredConnections.Any()) { throw new InvalidOperationException("Reopening list did not refresh."); }
    Wait(stats.CloseConnectionCommand.ExecuteAsync(connection));
    if (stats.FilteredConnections.Any()) { throw new InvalidOperationException("Mock connection remained after closing it."); }
    if (stats.AddConnectionRuleCommand.CanExecute(connection with { Host = "192.0.2.1" }))
    {
        throw new InvalidOperationException("A domain rule must not be offered for an IP address.");
    }
    stats.AddConnectionRuleCommand.Execute(connection);
    if (shell.Routing.RuleValue != "example.invalid" || !shell.Routing.ShowRuleEditor)
    {
        throw new InvalidOperationException("Connection rule was not prefilled.");
    }
    Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
    Console.WriteLine("Active connections verification passed: mock list, filter, close, prefilled routing rule.");
    return;
}

if (shell.IsOnboardingVisible)
{
    Shoot("onboarding", 1180, 780);
    Shoot("mobile-onboarding", 400, 860);
    Shoot("mobile-onboarding-minimum", 380, 560);

    // Verify that Settings picks up choices made during onboarding.
    shell.OnboardingLanguage = "en";
    shell.OnboardingTheme = ThemePreference.Oled;
    shell.FinishOnboardingCommand.Execute(null);
    shell.Navigate("settings");
    Shoot("settings-after-onboarding", 1180, 900);
    shell.OnboardingLanguage = lang;
    shell.OnboardingTheme = ThemePreference.System;
    shell.Navigate("home");
}

Shoot("home-empty", 1180, 780);
Shoot("mobile-home-empty", 400, 860);
if (shell.Profiles.Count == 0)
{
    Wait(shell.Connections.AddDemoProfilesCommand.ExecuteAsync(null));
}
// Exercise the actual radio group: selecting either mode must update the shared setting.
// This only changes app settings in the isolated screenshot data directory; no engine runs.
var originalMode = services.Settings.Mode;
var modeWindow = new Window { Width = 400, Height = 860, Content = new HomeView { DataContext = shell.Home } };
modeWindow.Show();
Pump(100);
var modes = modeWindow.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "connectionMode").ToArray();
if (modes.Length != 2) throw new InvalidOperationException("Connection mode controls are missing.");
modes[1].IsChecked = true;
Pump(50);
if (services.Settings.Mode != ConnectionMode.Tun || shell.Home.IsProxy) throw new InvalidOperationException("Whole-device selection failed.");
modes[0].IsChecked = true;
Pump(50);
if (services.Settings.Mode != ConnectionMode.LocalProxy || shell.Home.IsTun) throw new InvalidOperationException("Proxy selection failed.");
shell.Home.IsTun = originalMode == ConnectionMode.Tun;
modeWindow.Content = new SettingsView { DataContext = shell.Settings };
Pump(100);
var disclosure = modeWindow.GetVisualDescendants().OfType<Expander>().First();
var disclosureButton = disclosure.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Classes.Contains("disclosure"));
disclosureButton.IsChecked = true;
Pump(50);
if (!disclosure.IsExpanded) throw new InvalidOperationException("Disclosure did not open.");
disclosureButton.IsChecked = false;
Pump(50);
if (disclosure.IsExpanded) throw new InvalidOperationException("Disclosure did not close.");
modeWindow.Close();
Pump(4000); // Let the demo-import toast disappear before documenting the resting UI.

foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Oled })
{
    services.Theme.Apply(theme);
    shell.Navigate("home");
    Shoot($"home-{theme.ToString().ToLowerInvariant()}", 1180, 780);
}

services.Settings.Theme = ThemePreference.Oled;
services.Theme.Apply(ThemePreference.Oled);

// Simulated connection (Mock engine) for the "connected" state.
services.Settings.MockScenario = MockScenario.Success;
Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
Pump(3500);
Shoot("home-connected", 1180, 780);

foreach (var page in new[] { "connections", "tools", "routing", "statistics", "settings" })
{
    shell.Navigate(page);
    if (page == "settings")
    {
        shell.Settings.ShowAdvanced = true;
    }

    Shoot(page, 1180, 900);
    Shoot($"mobile-{page}", 400, 860);
}

shell.Navigate("home");
Shoot("mobile-home", 400, 860);
Shoot("home-minimum", 380, 700);
Shoot("home-narrow-desktop", 900, 780);
Shoot("home-navigation-breakpoint", MainView.CompactWidth, 780);
Wait(services.Connections.StopAllAsync());
Pump(100);
services.Settings.MockScenario = MockScenario.FailOnConnect;
Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
Pump(100);
if (!shell.Home.IsError) throw new InvalidOperationException("Mock connection failure was not shown.");
if (shell.HasVisibleToast && shell.Toast == shell.Home.Error) throw new InvalidOperationException("Duplicate connection error toast.");
Shoot("home-error", 400, 860);
Wait(services.Connections.StopAllAsync());
services.Settings.MockScenario = MockScenario.Success;
Console.WriteLine("done: " + output);
