using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;
using Bydlo.Core.Serialization;

namespace Bydlo.Tests;

public class AutomaticAppUpdaterTests
{
    /// <summary>Only a Velopack install may update itself (issue #413); the policy is otherwise unchanged.</summary>
    private static AutomaticAppUpdater Velopack(FakeBackend backend, TimeProvider? clock = null) =>
        new AutomaticAppUpdater(() => backend, clock) { InstallKind = AppInstallKind.Velopack };

    [Fact]
    public void Data_directory_must_be_outside_replaceable_payload_including_exact_path()
    {
        var application = Path.Combine(TestDirs.New("updater-paths"), "app");
        Assert.False(AutomaticAppUpdater.HasSeparateDataDirectory(application + Path.DirectorySeparatorChar, application));
        Assert.False(AutomaticAppUpdater.HasSeparateDataDirectory(application, application + Path.DirectorySeparatorChar));
        Assert.False(AutomaticAppUpdater.HasSeparateDataDirectory(application, Path.Combine(application, "data")));
        Assert.True(AutomaticAppUpdater.HasSeparateDataDirectory(application, application + "-data"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Disabled_or_mock_never_constructs_backend(bool enabled, bool blocked)
    {
        var policy = new AutomaticAppUpdater(() => throw new InvalidOperationException("No I/O allowed"));
        var settings = new AppSettings { AutoUpdateApp = enabled, ConsentAutomaticUpdates = enabled };
        Assert.False(await policy.DownloadIfDueAsync(settings, blocked));
        policy.ApplyOnExit(settings, blocked);
        Assert.Null(settings.LastAutomaticUpdateCheck);
    }

    [Fact]
    public async Task Applies_only_at_exit_with_current_opt_in_and_preserves_user_data()
    {
        var backend = new FakeBackend();
        var policy = Velopack(backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true, LastProfileId = Guid.NewGuid(), SubscriptionAutoUpdate = true };
        var original = settings.LastProfileId;
        Assert.True(await policy.DownloadIfDueAsync(settings, false));
        Assert.Equal(0, backend.Applied);
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        Assert.Equal(1, backend.Downloads);
        policy.ApplyOnExit(settings, true);
        Assert.Equal(0, backend.Applied);
        settings.AutoUpdateApp = false;
        policy.ApplyOnExit(settings, false);
        Assert.Equal(0, backend.Applied);
        settings.AutoUpdateApp = true;
        policy.ApplyOnExit(settings, false);
        policy.ApplyOnExit(settings, false);
        Assert.Equal(1, backend.Applied);
        var restored = BydloJson.DeepClone(settings);
        Assert.True(restored.AutoUpdateApp);
        Assert.Equal(settings.LastAutomaticUpdateCheck, restored.LastAutomaticUpdateCheck);
        Assert.Equal(original, restored.LastProfileId);
        Assert.True(restored.SubscriptionAutoUpdate);
    }

    [Fact]
    public async Task Turning_off_during_download_prevents_installation()
    {
        var backend = new FakeBackend();
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        backend.Download = () => { settings.AutoUpdateApp = false; return Task.FromResult(true); };
        var policy = Velopack(backend);
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        settings.AutoUpdateApp = true;
        policy.ApplyOnExit(settings, false);
        Assert.Equal(0, backend.Applied);
    }

    [Fact]
    public async Task Failed_check_is_rate_limited_hourly_and_never_installs()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var backend = new FakeBackend { Download = () => throw new IOException("offline") };
        var policy = Velopack(backend, clock);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        await Assert.ThrowsAsync<IOException>(() => policy.DownloadIfDueAsync(settings, false));
        Assert.Null(settings.LastAutomaticUpdateCheck);
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        Assert.Equal(1, backend.Downloads);
        clock.Now = clock.Now.AddHours(1);
        await Assert.ThrowsAsync<IOException>(() => policy.DownloadIfDueAsync(settings, false));
        Assert.Equal(2, backend.Downloads);
        policy.ApplyOnExit(settings, false);
        Assert.Equal(0, backend.Applied);
    }

    [Fact]
    public async Task Release_published_after_empty_check_is_picked_up_on_next_run()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var backend = new FakeBackend { Download = () => Task.FromResult(false) };
        var policy = Velopack(backend, clock);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        Assert.Null(settings.LastAutomaticUpdateCheck);
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        Assert.Equal(1, backend.Downloads);
        clock.Now = clock.Now.AddHours(1);
        backend.Download = () => Task.FromResult(true);
        Assert.True(await policy.DownloadIfDueAsync(settings, false));
        Assert.Equal(2, backend.Downloads);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Cancelled_download_does_not_consume_the_daily_window()
    {
        var backend = new FakeBackend { Download = () => throw new OperationCanceledException() };
        var policy = Velopack(backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.DownloadIfDueAsync(settings, false));
        Assert.Null(settings.LastAutomaticUpdateCheck);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.DownloadIfDueAsync(settings, false));
        Assert.Equal(2, backend.Downloads);
        policy.ApplyOnExit(settings, false);
        Assert.Equal(0, backend.Applied);
    }

    [Fact]
    public async Task Manual_download_ignores_the_daily_window()
    {
        var backend = new FakeBackend();
        var policy = Velopack(backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        Assert.True(await policy.DownloadIfDueAsync(settings, false));
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        Assert.Equal(1, backend.Downloads);
        Assert.True(await policy.DownloadNowAsync(settings, false));
        Assert.Equal(2, backend.Downloads);
    }

    [Fact]
    public async Task Manual_download_keeps_the_opt_in_and_blocked_guards()
    {
        var backend = new FakeBackend();
        var policy = Velopack(backend);
        Assert.False(await policy.DownloadNowAsync(new AppSettings { AutoUpdateApp = false }, false));
        Assert.False(await policy.DownloadNowAsync(new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true }, blocked: true));
        Assert.Equal(0, backend.Downloads);
    }

    [Fact]
    public async Task Progress_is_forwarded_to_the_backend()
    {
        var backend = new FakeBackend();
        var policy = Velopack(backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        var progress = new Progress<int>();
        Assert.True(await policy.DownloadIfDueAsync(settings, false, progress));
        Assert.Same(progress, backend.Progress);
    }

    [Fact]
    public void Availability_reflects_the_backend()
    {
        Assert.True(Velopack(new FakeBackend()).IsAvailable());
        Assert.False(Velopack(new FakeBackend { IsInstalled = false }).IsAvailable());
        Assert.False(new AutomaticAppUpdater(() => throw new InvalidOperationException("No I/O allowed")).IsAvailable());
    }

    [Fact]
    public async Task Unpackaged_build_never_downloads_or_applies()
    {
        var backend = new FakeBackend { IsInstalled = false };
        var policy = new AutomaticAppUpdater(() => backend);
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };
        Assert.False(await policy.DownloadIfDueAsync(settings, false));
        policy.ApplyOnExit(settings, false);
        Assert.Equal(0, backend.Downloads);
        Assert.Equal(0, backend.Applied);
        Assert.False(new AppSettings().AutoUpdateApp);
    }

    private sealed class FakeBackend : IAppUpdateBackend
    {
        public bool IsInstalled { get; set; } = true;
        public string? PendingVersion { get; set; } = "0.4.4";
        public int Downloads { get; private set; }
        public int Applied { get; private set; }
        public IProgress<int>? Progress { get; private set; }
        public Func<Task<bool>> Download { get; set; } = () => Task.FromResult(true);

        public Task<bool> DownloadLatestAsync(IProgress<int>? progress, CancellationToken ct)
        {
            Downloads++;
            Progress = progress;
            return Download();
        }

        public void ApplyOnExit() => Applied++;
    }
}
