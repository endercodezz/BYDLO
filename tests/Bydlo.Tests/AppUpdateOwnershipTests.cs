using Bydlo.App.Services;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

/// <summary>
/// Issue #413: the install kind decides who owns updates, and the release check and the packaged
/// updater share one state. Everything runs through fake probes and a fake backend; no file is written,
/// no engine starts and no request leaves the process.
/// </summary>
public sealed class AppUpdateOwnershipTests
{
    private const string DebianApplication = "/opt/bydlo";
    private const string DpkgDatabaseFile = "/var/lib/dpkg/info/bydlo.list";
    private const string PacmanDatabaseDirectory = "/var/lib/pacman/local";
    private const string NixApplication = "/nix/store/0123456789abcdef-bydlo-0.4.2/bin";

    [Fact]
    public void Velopack_install_is_detected_from_its_own_marker()
    {
        var application = Path.Combine("bydlo-install", "current");
        var probe = new FakeProbe();
        probe.Files.Add(Path.Combine(application, AppInstallLocator.VelopackMarkerFileName));
        // A Velopack payload also ships the release layout; the marker must win.
        probe.Directories.Add(Path.Combine(application, AppInstallLocator.BundleEnginesDirectoryName));
        probe.Files.Add(Path.Combine(application, AppInstallLocator.BundleChecksumsFileName));

        Assert.Equal(AppInstallKind.Velopack, new AppInstallLocator(probe).Detect(application));
    }

    [Fact]
    public void Windows_velopack_layout_is_detected_from_update_exe()
    {
        var root = Path.Combine("bydlo-install", "BydloDesktop");
        var application = Path.Combine(root, "current");
        var probe = new FakeProbe();
        probe.Files.Add(Path.Combine(root, "Update.exe"));

        Assert.Equal(AppInstallKind.Velopack, new AppInstallLocator(probe).Detect(application));
    }

    [Fact]
    public void Portable_marker_is_detected()
    {
        var application = Path.Combine("bydlo-install", "portable");
        var probe = new FakeProbe();
        probe.Files.Add(Path.Combine(application, AppInstallLocator.PortableMarkerFileName));

        Assert.Equal(AppInstallKind.Portable, new AppInstallLocator(probe).Detect(application));
    }

    [Fact]
    public void Old_zip_release_layout_is_detected_as_portable()
    {
        var application = Path.Combine("bydlo-install", "BYDLO-win-x64");
        var probe = new FakeProbe();
        probe.Directories.Add(Path.Combine(application, AppInstallLocator.BundleEnginesDirectoryName));
        probe.Files.Add(Path.Combine(application, AppInstallLocator.BundleChecksumsFileName));

        Assert.Equal(AppInstallKind.Portable, new AppInstallLocator(probe).Detect(application));
    }

    [Fact]
    public void Debian_package_is_detected_from_the_dpkg_database()
    {
        var probe = new FakeProbe();
        probe.Files.Add(DpkgDatabaseFile);
        probe.Directories.Add(PacmanDatabaseDirectory);

        Assert.Equal(AppInstallKind.Debian, new AppInstallLocator(probe).Detect(DebianApplication));
    }

    [Fact]
    public void Portable_bundle_stays_manual_on_a_machine_with_the_deb_installed()
    {
        var probe = new FakeProbe();
        probe.Files.Add(DpkgDatabaseFile);
        const string portable = "/home/user/BYDLO";
        probe.Directories.Add(Path.Combine(portable, AppInstallLocator.BundleEnginesDirectoryName));
        probe.Files.Add(Path.Combine(portable, AppInstallLocator.BundleChecksumsFileName));

        Assert.Equal(AppInstallKind.Portable, new AppInstallLocator(probe).Detect(portable));
    }

    [Fact]
    public void Arch_package_is_detected_from_the_pacman_database()
    {
        var probe = new FakeProbe();
        probe.Directories.Add(PacmanDatabaseDirectory);

        Assert.Equal(AppInstallKind.Arch, new AppInstallLocator(probe).Detect(DebianApplication));
    }

    [Fact]
    public void Nix_store_install_is_detected_from_the_store_path()
    {
        var probe = new FakeProbe();

        Assert.Equal(AppInstallKind.Nix, new AppInstallLocator(probe).Detect(NixApplication));
        Assert.Equal(AppInstallKind.Nix, new AppInstallLocator(probe).Detect(
            Path.Combine("bydlo-install", "app"), Path.Combine(NixApplication, "BYDLO")));
    }

    [Fact]
    public void Explicit_package_marker_wins_over_path_heuristics()
    {
        var probe = new FakeProbe();
        probe.Files.Add(DpkgDatabaseFile);
        probe.Directories.Add(PacmanDatabaseDirectory);
        probe.Texts[Path.Combine(DebianApplication, AppInstallLocator.MarkerFileName)] = "arch\n";

        Assert.Equal(AppInstallKind.Arch, new AppInstallLocator(probe).Detect(DebianApplication));
    }

    [Fact]
    public void Unpackaged_build_is_unknown()
    {
        var probe = new FakeProbe();
        probe.Directories.Add(Path.Combine("bydlo-install", "app", AppInstallLocator.BundleEnginesDirectoryName));

        Assert.Equal(AppInstallKind.Unknown, new AppInstallLocator(probe).Detect(Path.Combine("bydlo-install", "app")));
    }

    [Theory]
    [InlineData(AppInstallKind.Velopack, AppUpdateOwner.Velopack)]
    [InlineData(AppInstallKind.Debian, AppUpdateOwner.PackageManager)]
    [InlineData(AppInstallKind.Arch, AppUpdateOwner.PackageManager)]
    [InlineData(AppInstallKind.Nix, AppUpdateOwner.PackageManager)]
    [InlineData(AppInstallKind.Portable, AppUpdateOwner.Manual)]
    [InlineData(AppInstallKind.Unknown, AppUpdateOwner.None)]
    public void Ownership_follows_the_install_kind(AppInstallKind kind, AppUpdateOwner expected) =>
        Assert.Equal(expected, AppUpdateOwnership.OwnerOf(kind));

    [Fact]
    public async Task Package_manager_install_never_uses_the_built_in_updater()
    {
        var policy = new AutomaticAppUpdater(() => throw new InvalidOperationException("No I/O allowed"))
        {
            InstallKind = AppInstallKind.Debian,
        };
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };

        Assert.Equal(AppUpdateOwner.PackageManager, policy.Owner);
        Assert.False(policy.IsAvailable());
        Assert.False(await policy.DownloadNowAsync(settings, false));
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        Assert.Equal(AppUpdateApplyResult.Nothing, policy.ApplyOnExit(settings, false));
        Assert.Null(settings.LastAutomaticUpdateCheck);
    }

    [Fact]
    public async Task Downloading_state_is_visible_while_the_backend_runs()
    {
        var backend = new FakeBackend();
        var policy = Velopack(backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        AppUpdateState? observed = null;
        backend.Download = () => { observed = policy.State; return Task.FromResult(true); };

        Assert.True(await policy.DownloadNowAsync(settings, false));
        Assert.Equal<AppUpdateState?>(AppUpdateState.Downloading, observed);
        Assert.Equal(AppUpdateState.Ready, policy.State);
    }

    [Fact]
    public async Task Release_notification_never_overwrites_a_ready_package()
    {
        var backend = new FakeBackend();
        var policy = Velopack(backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        Assert.True(await policy.DownloadNowAsync(settings, false));

        policy.MarkAvailable("v9.9.9");

        Assert.Equal(AppUpdateState.Ready, policy.State);
        Assert.Equal("v9.9.9", policy.AvailableVersion);
        Assert.Equal(AppUpdateApplyResult.Applied, policy.ApplyOnExit(settings, false));
        Assert.Equal(1, backend.Applied);
    }

    [Fact]
    public void Release_notification_moves_an_idle_copy_to_available()
    {
        var policy = Velopack(new FakeBackend());

        policy.MarkAvailable("v9.9.9");
        Assert.Equal(AppUpdateState.Available, policy.State);

        policy.MarkAvailable(null);
        Assert.Equal(AppUpdateState.Idle, policy.State);
        Assert.Null(policy.AvailableVersion);
    }

    [Fact]
    public async Task Opt_out_after_download_defers_and_never_applies()
    {
        var backend = new FakeBackend();
        var policy = Velopack(backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        Assert.True(await policy.DownloadNowAsync(settings, false));

        settings.AutoUpdateApp = false;
        policy.SetOptIn(false);

        Assert.Equal(AppUpdateState.Deferred, policy.State);
        Assert.Equal(AppUpdateApplyResult.Nothing, policy.ApplyOnExit(settings, false));
        Assert.Equal(0, backend.Applied);

        settings.AutoUpdateApp = true;
        policy.SetOptIn(true);

        Assert.Equal(AppUpdateState.Ready, policy.State);
        Assert.Equal(AppUpdateApplyResult.Applied, policy.ApplyOnExit(settings, false));
        Assert.Equal(1, backend.Applied);
    }

    [Fact]
    public async Task Cleanup_failure_defers_the_downloaded_package()
    {
        var backend = new FakeBackend();
        var policy = Velopack(backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        Assert.True(await policy.DownloadNowAsync(settings, false));

        policy.Defer();

        Assert.Equal(AppUpdateState.Deferred, policy.State);
        Assert.Equal(AppUpdateApplyResult.Deferred, policy.ApplyOnExit(settings, false));
        Assert.Equal(0, backend.Applied);
    }

    [Fact]
    public async Task Cancelled_download_returns_to_available_and_never_applies()
    {
        var backend = new FakeBackend { Download = () => throw new OperationCanceledException() };
        var policy = Velopack(backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        policy.MarkAvailable("v9.9.9");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.DownloadNowAsync(settings, false));

        Assert.Equal(AppUpdateState.Available, policy.State);
        Assert.Equal(AppUpdateApplyResult.Nothing, policy.ApplyOnExit(settings, false));
        Assert.Equal(0, backend.Applied);
    }

    [Fact]
    public async Task Failed_download_is_reported_and_never_applies()
    {
        var backend = new FakeBackend { Download = () => throw new IOException("offline") };
        var policy = Velopack(backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };

        await Assert.ThrowsAsync<IOException>(() => policy.DownloadNowAsync(settings, false));

        Assert.Equal(AppUpdateState.Failed, policy.State);
        Assert.Equal(AppUpdateApplyResult.Nothing, policy.ApplyOnExit(settings, false));
        Assert.Equal(0, backend.Applied);
    }

    [Fact]
    public async Task Apply_failure_is_reported_as_failed()
    {
        var backend = new FakeBackend { ApplyError = new IOException("locked") };
        var policy = Velopack(backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        Assert.True(await policy.DownloadNowAsync(settings, false));

        Assert.Equal(AppUpdateApplyResult.Failed, policy.ApplyOnExit(settings, false));
        Assert.Equal(AppUpdateState.Failed, policy.State);
    }

    [Theory]
    [InlineData(AppInstallKind.Debian, "settings.update.manual.deb")]
    [InlineData(AppInstallKind.Arch, "settings.update.manual.arch")]
    [InlineData(AppInstallKind.Nix, "settings.update.manual.nix")]
    [InlineData(AppInstallKind.Portable, "settings.update.manual.portable")]
    [InlineData(AppInstallKind.Unknown, "settings.update.manual.unknown")]
    public void Guidance_points_at_the_owning_package_manager(AppInstallKind kind, string expected) =>
        Assert.Equal(expected, UpdateGuidance.KeyFor(kind));

    [Fact]
    public void Guidance_strings_exist_in_both_languages()
    {
        var root = RepositoryRoot();
        var en = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "en.json"));
        var ru = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "ru.json"));
        var keys = Enum.GetValues<AppInstallKind>().Select(UpdateGuidance.KeyFor)
            .Concat(new[] { "settings.update.deferred", "settings.update.ready", "settings.update.downloading", "settings.update.error" })
            .Distinct(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            Assert.Contains($"\"{key}\"", en, StringComparison.Ordinal);
            Assert.Contains($"\"{key}\"", ru, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Debian_guidance_does_not_promise_a_configured_repository()
    {
        var root = RepositoryRoot();
        var en = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "en.json"));
        var ru = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "ru.json"));

        Assert.Contains("No apt repository is configured", en, StringComparison.Ordinal);
        Assert.Contains("Репозиторий apt для BYDLO не настраивается", ru, StringComparison.Ordinal);
    }

    [Fact]
    public void Portable_guidance_does_not_promise_automatic_migration()
    {
        var root = RepositoryRoot();
        var en = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "en.json"));
        var ru = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "ru.json"));

        Assert.Contains("Automatic migration to an installed build is not provided", en, StringComparison.Ordinal);
        Assert.Contains("Автоматического перехода на установленную сборку нет", ru, StringComparison.Ordinal);
    }

    private static AutomaticAppUpdater Velopack(FakeBackend backend) =>
        new AutomaticAppUpdater(() => backend) { InstallKind = AppInstallKind.Velopack };

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

    private sealed class FakeProbe : IAppInstallProbe
    {
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

        public bool FileExists(string path) => Files.Contains(path);

        public bool DirectoryExists(string path) => Directories.Contains(path);

        public string? ReadText(string path) => Texts.TryGetValue(path, out var text) ? text : null;
    }

    private sealed class FakeBackend : IAppUpdateBackend
    {
        public bool IsInstalled { get; set; } = true;

        public string? PendingVersion { get; set; } = "0.4.4";

        public int Applied { get; private set; }

        public Exception? ApplyError { get; set; }

        public Func<Task<bool>> Download { get; set; } = () => Task.FromResult(true);

        public Task<bool> DownloadLatestAsync(IProgress<int>? progress, CancellationToken ct) => Download();

        public void ApplyOnExit()
        {
            if (ApplyError is { } error) { throw error; }
            Applied++;
        }
    }
}
