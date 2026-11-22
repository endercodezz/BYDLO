using System.Net;
using System.Text.Json;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;
using Bydlo.Core.Themes;

namespace Bydlo.Tests;

/// <summary>
/// Issue #438: three independent first-run consents for the GitHub-backed features. Every check here
/// is isolated: fake HTTP handlers and a fake update backend, no engine, no real network.
/// </summary>
public sealed class FirstRunConsentTests
{
    [Fact]
    public void Fresh_profile_has_all_three_consents_off()
    {
        var settings = new AppSettings();
        Assert.False(settings.ConsentAutomaticUpdates);
        Assert.False(settings.ConsentNotifications);
        Assert.False(settings.ConsentThemes);
        Assert.Equal(FeatureConsent.None, FeatureConsent.From(settings));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Every_consent_combination_allows_only_its_own_feature(bool updates, bool notifications, bool themes)
    {
        var consent = new FeatureConsent(updates, notifications, themes);
        Assert.Equal(updates, consent.Allows(OptionalFeature.AutomaticUpdates));
        Assert.Equal(notifications, consent.Allows(OptionalFeature.Notifications));
        Assert.Equal(themes, consent.Allows(OptionalFeature.Themes));

        var settings = new AppSettings();
        consent.ApplyTo(settings);
        Assert.Equal(consent, FeatureConsent.From(settings));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Forbidden_features_never_reach_github(bool updates, bool notifications, bool themes)
    {
        var updateRequests = 0;
        var noticeRequests = 0;
        var contributorRequests = 0;
        var backend = new FakeBackend();
        var settings = new AppSettings
        {
            ConsentAutomaticUpdates = updates,
            ConsentNotifications = notifications,
            ConsentThemes = themes,
            AutoUpdateApp = true,
        };

        await new StartupUpdateChecker(new GitHubUpdateClient(() => new CountingHandler(() => updateRequests++)))
            .CheckAsync(settings, "0.1.0", networkBlocked: false);
        await new ProjectNoticeChecker(new ProjectNoticeClient(() => new CountingHandler(() => noticeRequests++)))
            .CheckAsync(settings, "0.1.0", networkBlocked: false);
        await new ContributorsRefresher(new GitHubContributorsClient(() => new CountingHandler(() => contributorRequests++)))
            .RefreshAsync(settings, networkBlocked: false);
        await new AutomaticAppUpdater(() => backend) { InstallKind = AppInstallKind.Velopack }.DownloadIfDueAsync(settings, blocked: false);

        Assert.Equal(updates ? 1 : 0, updateRequests);
        Assert.Equal(notifications ? 1 : 0, noticeRequests);
        Assert.Equal(updates ? 1 : 0, contributorRequests);
        Assert.Equal(updates ? 1 : 0, backend.Downloads);
        Assert.Equal(themes, FeatureConsent.From(settings).Allows(OptionalFeature.Themes));
    }

    [Fact]
    public void Consents_round_trip_through_settings_json()
    {
        var settings = new AppSettings
        {
            ConsentAutomaticUpdates = true,
            ConsentNotifications = false,
            ConsentThemes = true,
            ConsentsInitialized = true,
        };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(new FeatureConsent(true, false, true), FeatureConsent.From(restored));
        Assert.True(restored.ConsentsInitialized);
    }

    [Fact]
    public void Upgrade_keeps_the_github_access_the_user_already_had()
    {
        var settings = new AppSettings { CheckForUpdates = true, ShowNotifications = true };
        Assert.True(settings.MigrateConsents(firstRun: false));
        Assert.True(settings.ConsentAutomaticUpdates);
        Assert.True(settings.ConsentNotifications);
        // Themes were never downloaded automatically, so the upgrade grants nothing there.
        Assert.False(settings.ConsentThemes);
        Assert.True(settings.ConsentsInitialized);
    }

    [Fact]
    public void Upgrade_does_not_grant_what_the_user_had_turned_off()
    {
        var settings = new AppSettings { CheckForUpdates = false, ShowNotifications = false };
        settings.MigrateConsents(firstRun: false);
        Assert.Equal(FeatureConsent.None, FeatureConsent.From(settings));
    }

    [Fact]
    public void Fresh_profile_migration_keeps_every_consent_off()
    {
        var settings = new AppSettings { OnboardingCompleted = false };
        settings.MigrateConsents(firstRun: true);
        Assert.Equal(FeatureConsent.None, FeatureConsent.From(settings));
    }

    [Fact]
    public void Closing_the_first_run_screen_before_confirming_grants_nothing()
    {
        // The screen saved the language but was never confirmed: the file exists, onboarding is pending.
        var settings = new AppSettings { OnboardingCompleted = false, CheckForUpdates = true, ShowNotifications = true };
        settings.MigrateConsents(firstRun: false);
        Assert.Equal(FeatureConsent.None, FeatureConsent.From(settings));
    }

    [Fact]
    public void Revoked_consent_is_not_granted_again_on_the_next_start()
    {
        var settings = new AppSettings { CheckForUpdates = true, ShowNotifications = true };
        settings.MigrateConsents(firstRun: false);
        settings.ConsentAutomaticUpdates = false;
        settings.ConsentNotifications = false;
        Assert.False(settings.MigrateConsents(firstRun: false));
        Assert.Equal(FeatureConsent.None, FeatureConsent.From(settings));
    }

    [Fact]
    public async Task Revoking_the_update_consent_blocks_a_staged_package()
    {
        var backend = new FakeBackend();
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        var updater = new AutomaticAppUpdater(() => backend) { InstallKind = AppInstallKind.Velopack };
        Assert.True(await updater.DownloadIfDueAsync(settings, blocked: false));

        settings.ConsentAutomaticUpdates = false;
        updater.ApplyOnExit(settings, blocked: false);
        Assert.Equal(0, backend.Applied);
        Assert.False(await updater.DownloadNowAsync(settings, blocked: false));
        Assert.Equal(1, backend.Downloads);
    }

    [Fact]
    public void Consent_screen_links_point_at_the_sources_the_app_reads()
    {
        // Update checks read the GitHub Releases API; the screen links the release page of the same repository.
        Assert.Equal("https://api.github.com/repos/endercodezz/BYDLO/releases/latest", GitHubUpdateClient.BydloReleasesUrl);
        Assert.Equal("https://github.com/endercodezz/BYDLO/releases", GitHubSources.Releases);
        // Notification content and the theme gallery are repository files on the default branch.
        Assert.Equal("https://raw.githubusercontent.com/endercodezz/BYDLO/main/notice.json", ProjectNoticeClient.NoticeUrl);
        Assert.Equal("https://github.com/endercodezz/BYDLO/blob/main/notice.json", GitHubSources.Notice);
        Assert.Equal("https://raw.githubusercontent.com/endercodezz/BYDLO/main/themes/index.json", CustomTheme.DefaultGalleryIndexUrl);
        Assert.Equal("https://github.com/endercodezz/BYDLO/blob/main/themes/index.json", GitHubSources.ThemeGallery);
    }

    [Fact]
    public void First_run_screen_shows_three_unchecked_checkboxes_and_the_github_sources()
    {
        var view = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bydlo.App", "Views", "MainView.axaml"));
        foreach (var property in new[] { "ConsentAutomaticUpdates", "ConsentNotifications", "ConsentThemes" })
        {
            Assert.Contains($"<CheckBox IsChecked=\"{{Binding {property}}}\"", view, StringComparison.Ordinal);
        }
        // No consent may be pre-selected.
        Assert.DoesNotContain("IsChecked=\"True\"", view, StringComparison.Ordinal);
        Assert.Contains("onboarding.consent.updates", view, StringComparison.Ordinal);
        Assert.Contains("onboarding.consent.notifications", view, StringComparison.Ordinal);
        Assert.Contains("onboarding.consent.themes", view, StringComparison.Ordinal);
        Assert.Contains("gh:GitHubSources.Releases", view, StringComparison.Ordinal);
        Assert.Contains("gh:GitHubSources.Notice", view, StringComparison.Ordinal);
        Assert.Contains("gh:GitHubSources.ThemeGallery", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Continuing_without_any_consent_stays_available()
    {
        var view = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bydlo.App", "Views", "MainView.axaml"));
        Assert.Contains("Command=\"{Binding StartOnboardingImportCommand}\"", view, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding FinishOnboardingCommand}\"", view, StringComparison.Ordinal);
        // Neither way forward may be disabled by a consent.
        Assert.DoesNotContain("IsEnabled=\"{Binding Consent", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_expose_all_three_consents()
    {
        var view = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bydlo.App", "Views", "SettingsView.axaml"));
        foreach (var property in new[] { "ConsentAutomaticUpdates", "ConsentNotifications", "ConsentThemes" })
        {
            Assert.Contains($"IsChecked=\"{{Binding {property}}}\"", view, StringComparison.Ordinal);
        }
        Assert.Contains("Tag=\"GitHubSection\"", view, StringComparison.Ordinal);
        Assert.Contains("gh:GitHubSources.Releases", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Theme_gallery_checks_the_theme_consent_before_downloading()
    {
        var code = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bydlo.App", "ViewModels", "SettingsViewModel.Themes.cs"));
        var first = code.IndexOf("Allows(OptionalFeature.Themes)", StringComparison.Ordinal);
        var last = code.LastIndexOf("Allows(OptionalFeature.Themes)", StringComparison.Ordinal);
        Assert.True(first >= 0);
        Assert.True(first < code.IndexOf("await LoadGalleryAsync(Services.Http)", StringComparison.Ordinal));
        Assert.True(last < code.IndexOf("await DownloadCappedTextAsync(Services.Http, entry.Url)", StringComparison.Ordinal));
        Assert.Contains("theme.custom.consent", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_update_check_requires_the_update_consent()
    {
        var code = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bydlo.App", "ViewModels", "SettingsViewModel.Updates.cs"));
        var check = code.IndexOf("private async Task CheckUpdates()", StringComparison.Ordinal);
        var download = code.IndexOf("private async Task DownloadUpdate()", StringComparison.Ordinal);
        Assert.True(check >= 0 && download > check);
        // A manual check reads GitHub too, so it must not bypass the consent.
        var body = code[check..download];
        Assert.Contains("if (!ConsentAutomaticUpdates)", body, StringComparison.Ordinal);
        Assert.Contains("settings.update.detail.consent", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Consent_strings_exist_in_both_languages()
    {
        var root = RepositoryRoot();
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "en.json")))!;
        var ru = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "ru.json")))!;
        string[] keys =
        [
            "onboarding.consent.title", "onboarding.consent.text",
            "onboarding.consent.updates", "onboarding.consent.updates.hint",
            "onboarding.consent.notifications", "onboarding.consent.notifications.hint",
            "onboarding.consent.themes", "onboarding.consent.themes.hint",
            "onboarding.consent.sources", "onboarding.consent.source.releases",
            "onboarding.consent.source.notice", "onboarding.consent.source.themes",
            "settings.section.github", "settings.consent.title", "settings.consent.hint",
            "settings.consent.updates", "settings.consent.updates.hint",
            "settings.consent.notifications", "settings.consent.notifications.hint",
            "settings.consent.themes", "settings.consent.themes.hint",
            "settings.consent.sources", "settings.update.detail.consent", "theme.custom.consent",
        ];
        foreach (var key in keys)
        {
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english), "Missing en: " + key);
            Assert.True(ru.TryGetValue(key, out var russian) && !string.IsNullOrWhiteSpace(russian), "Missing ru: " + key);
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    /// <summary>Counts requests and answers with a body every parser treats as "nothing to show".</summary>
    private sealed class CountingHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class FakeBackend : IAppUpdateBackend
    {
        public bool IsInstalled => true;

        public string? PendingVersion => "0.4.4";

        public int Downloads { get; private set; }

        public int Applied { get; private set; }

        public Task<bool> DownloadLatestAsync(IProgress<int>? progress, CancellationToken ct)
        {
            Downloads++;
            return Task.FromResult(true);
        }

        public void ApplyOnExit() => Applied++;
    }
}
