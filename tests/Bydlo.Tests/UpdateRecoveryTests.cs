using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

/// <summary>
/// Issue #412: the local recovery protocol around the Velopack updater. Every test uses a temporary directory
/// and fakes; no installer, engine, driver or network request is involved.
/// </summary>
public sealed class UpdateRecoveryTests
{
    private const string OldVersion = "0.4.4";
    private const string NewVersion = "0.4.5";
    private const string Bundle = "bundle-a";

    private static UpdateRecovery Recovery(string dataDirectory, string currentVersion = OldVersion,
        AppInstallKind kind = AppInstallKind.Velopack, bool previousPackage = false) =>
        new(dataDirectory, Path.Combine(dataDirectory, "app"), kind, currentVersion, Bundle,
            new FakeRollbackProbe(previousPackage));

    private static UpdateReadiness Ready() => new(uiStarted: true, userDataLoaded: true,
        componentsConsistent: true, helperCompatible: true);

    private static UpdateReadiness NotReady() => new(uiStarted: false, userDataLoaded: true,
        componentsConsistent: true, helperCompatible: true);

    private static string JournalPath(string dataDirectory) =>
        Path.Combine(dataDirectory, UpdateRecovery.JournalFileName);

    [Fact]
    public void Journal_round_trip_is_atomic_and_leaves_no_temporary_file()
    {
        var dir = TestDirs.New("recovery-roundtrip");
        var store = new UpdateRecoveryStore(JournalPath(dir));
        var journal = new UpdateRecoveryJournal
        {
            Phase = UpdateRecoveryPhase.PendingConfirmation,
            FromVersion = OldVersion,
            ToVersion = NewVersion,
            EngineBundleVersion = Bundle,
            Attempts = 1,
            SnapshotDirectory = Path.Combine(dir, "snapshot"),
        };

        Assert.True(store.TrySave(journal));
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(UpdateRecoveryPhase.PendingConfirmation, loaded.Phase);
        Assert.Equal(NewVersion, loaded.ToVersion);
        Assert.Equal(1, loaded.Attempts);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public async Task Truncated_journal_is_ignored_and_kept_for_diagnostics()
    {
        var dir = TestDirs.New("recovery-truncated");
        // Fault injection: the process died in the middle of the journal write.
        await File.WriteAllTextAsync(JournalPath(dir), "{\"Version\":1,\"Phase\":\"Appli");

        var recovery = Recovery(dir);
        Assert.Equal(UpdateRecoveryStartup.Nothing, recovery.PrepareStartup());
        Assert.Null(recovery.Journal);
        Assert.Single(Directory.GetFiles(dir, UpdateRecovery.JournalFileName + ".corrupt-*"));

        // The next download replaces the broken file instead of failing forever.
        Assert.True(recovery.Stage(NewVersion));
        Assert.Equal(UpdateRecoveryPhase.Staged, new UpdateRecoveryStore(JournalPath(dir)).Load()!.Phase);
    }

    [Fact]
    public async Task Leftover_temporary_file_does_not_break_the_journal()
    {
        var dir = TestDirs.New("recovery-tmp");
        await File.WriteAllTextAsync(JournalPath(dir) + ".tmp", "{\"Version\":1,\"Phase\":\"Appl");

        var recovery = Recovery(dir);
        Assert.Equal(UpdateRecoveryStartup.Nothing, recovery.PrepareStartup());
        Assert.True(recovery.Stage(NewVersion));
        Assert.False(File.Exists(JournalPath(dir) + ".tmp"));
    }

    [Fact]
    public async Task Journal_from_a_newer_format_is_ignored_instead_of_guessed()
    {
        var dir = TestDirs.New("recovery-newer");
        await File.WriteAllTextAsync(JournalPath(dir),
            "{\"Version\":99,\"Phase\":\"Applying\",\"ToVersion\":\"0.4.5\"}");

        var recovery = Recovery(dir, NewVersion);
        Assert.Equal(UpdateRecoveryStartup.Nothing, recovery.PrepareStartup());
        Assert.Null(recovery.Journal);
        // The unknown file is left untouched for the version that wrote it.
        Assert.Contains("\"Version\":99", await File.ReadAllTextAsync(JournalPath(dir)), StringComparison.Ordinal);
    }

    [Fact]
    public void Package_is_applied_only_with_a_staged_journal_and_snapshot()
    {
        var dir = TestDirs.New("recovery-canapply");
        var recovery = Recovery(dir);

        Assert.False(recovery.CanApply(NewVersion));
        Assert.True(recovery.Stage(NewVersion));
        Assert.True(recovery.CanApply(NewVersion));
        Assert.True(recovery.CanApply("0.4.5.0"));
        Assert.False(recovery.CanApply("0.4.6"));
        Assert.True(recovery.MarkApplying());
        Assert.False(recovery.CanApply(NewVersion));

        // A deleted snapshot removes the recovery guarantee, so the package must not install.
        var second = Recovery(dir);
        Assert.True(second.Stage(NewVersion));
        Directory.Delete(Path.Combine(dir, UpdateRecovery.SnapshotDirectoryName), recursive: true);
        Assert.False(second.CanApply(NewVersion));
    }

    [Fact]
    public async Task New_version_confirms_readiness_and_drops_the_snapshot()
    {
        var dir = TestDirs.New("recovery-confirm");
        await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), "{}");

        var old = Recovery(dir);
        Assert.True(old.Stage(NewVersion));
        Assert.True(old.MarkApplying());

        var next = Recovery(dir, NewVersion);
        Assert.Equal(UpdateRecoveryStartup.AwaitingConfirmation, next.PrepareStartup());
        Assert.True(next.ComponentsConsistent(NewVersion, Bundle));
        Assert.Equal(UpdateRecoveryConfirmation.Confirmed, next.Confirm(Ready()));
        Assert.Equal(UpdateRecoveryPhase.Confirmed, next.Journal!.Phase);
        Assert.Equal(0, next.Journal.Attempts);
        Assert.False(next.AutoApplyBlocked);
        Assert.False(Directory.Exists(Path.Combine(dir, UpdateRecovery.SnapshotDirectoryName, UpdateRecovery.SnapshotFolderName)));

        // A later start of the confirmed version does nothing.
        Assert.Equal(UpdateRecoveryStartup.Nothing, Recovery(dir, NewVersion).PrepareStartup());
    }

    [Fact]
    public void Failed_confirmation_is_bounded_and_stops_automatic_updates()
    {
        var dir = TestDirs.New("recovery-fail");
        var old = Recovery(dir);
        Assert.True(old.Stage(NewVersion));
        Assert.True(old.MarkApplying());

        // First start of the new version: one attempt, the check fails, the next start retries.
        var first = Recovery(dir, NewVersion);
        Assert.Equal(UpdateRecoveryStartup.AwaitingConfirmation, first.PrepareStartup());
        Assert.Equal(UpdateRecoveryConfirmation.Retry, first.Confirm(NotReady()));
        Assert.Equal(1, first.Journal!.Attempts);
        Assert.False(first.AutoApplyBlocked);

        // Second start: the budget is exhausted, the protocol stops and blocks automatic updates.
        var second = Recovery(dir, NewVersion);
        Assert.Equal(UpdateRecoveryStartup.AwaitingConfirmation, second.PrepareStartup());
        Assert.Equal(UpdateRecoveryConfirmation.RolledBack, second.Confirm(NotReady()));
        Assert.Equal(UpdateRecoveryPhase.RolledBack, second.Journal!.Phase);
        Assert.Equal(UpdateRecoveryReason.NotReady, second.Journal.Reason);
        Assert.True(second.AutoApplyBlocked);

        // Third start: no further attempt, no loop, and a late success cannot silently unblock.
        var third = Recovery(dir, NewVersion);
        Assert.Equal(UpdateRecoveryStartup.Blocked, third.PrepareStartup());
        Assert.Equal(UpdateRecoveryConfirmation.Nothing, third.Confirm(Ready()));
        Assert.Equal(2, third.Journal!.Attempts);
        Assert.True(third.AutoApplyBlocked);
    }

    [Fact]
    public void Interrupted_apply_returns_to_the_previous_version_and_is_bounded()
    {
        var dir = TestDirs.New("recovery-interrupted");
        var old = Recovery(dir);
        Assert.True(old.Stage(NewVersion));
        Assert.True(old.MarkApplying());

        // The installer never replaced the files: the previous version starts again.
        var again = Recovery(dir);
        Assert.Equal(UpdateRecoveryStartup.Interrupted, again.PrepareStartup());
        Assert.Equal(UpdateRecoveryPhase.RolledBack, again.Journal!.Phase);
        Assert.Equal(UpdateRecoveryReason.Interrupted, again.Journal.Reason);
        Assert.False(again.AutoApplyBlocked);

        // A second interrupted apply exhausts the budget instead of retrying forever.
        Assert.True(again.Stage(NewVersion));
        Assert.Equal(1, again.Journal!.Attempts);
        Assert.True(again.MarkApplying());
        var twice = Recovery(dir);
        Assert.Equal(UpdateRecoveryStartup.Blocked, twice.PrepareStartup());
        Assert.True(twice.AutoApplyBlocked);
    }

    [Fact]
    public async Task Snapshot_restores_missing_or_broken_user_data_only()
    {
        var dir = TestDirs.New("recovery-snapshot");
        var settings = Path.Combine(dir, "settings.json");
        var profiles = Path.Combine(dir, "profiles.json");
        await File.WriteAllTextAsync(settings, "{\"Language\":\"ru\"}");
        await File.WriteAllTextAsync(profiles, "{\"Version\":1,\"Profiles\":[]}");

        var old = Recovery(dir);
        Assert.True(old.Stage(NewVersion));
        Assert.True(old.MarkApplying());

        // The new version breaks the settings file and loses the profile list.
        await File.WriteAllTextAsync(settings, "{ broken");
        File.Delete(profiles);

        var first = Recovery(dir, NewVersion);
        Assert.Equal(UpdateRecoveryStartup.AwaitingConfirmation, first.PrepareStartup());
        Assert.Equal(UpdateRecoveryConfirmation.Retry, first.Confirm(NotReady()));
        // Nothing is restored while the budget is not exhausted.
        Assert.Equal("{ broken", await File.ReadAllTextAsync(settings));

        var second = Recovery(dir, NewVersion);
        Assert.Equal(UpdateRecoveryStartup.AwaitingConfirmation, second.PrepareStartup());
        Assert.Equal(UpdateRecoveryConfirmation.RolledBack, second.Confirm(NotReady()));
        // The restore happens on the next start, before any user data is read.
        Assert.Equal("{ broken", await File.ReadAllTextAsync(settings));

        var third = Recovery(dir, NewVersion);
        Assert.Equal(UpdateRecoveryStartup.Blocked, third.PrepareStartup());
        Assert.Contains("snapshot-restored", third.Journal!.Notes);
        Assert.Contains("ru", await File.ReadAllTextAsync(settings));
        Assert.Contains("Profiles", await File.ReadAllTextAsync(profiles));
    }

    [Fact]
    public async Task Readable_user_data_is_never_overwritten_by_the_snapshot()
    {
        var dir = TestDirs.New("recovery-snapshot-keep");
        var settings = Path.Combine(dir, "settings.json");
        await File.WriteAllTextAsync(settings, "{\"Language\":\"ru\"}");

        var old = Recovery(dir);
        Assert.True(old.Stage(NewVersion));
        Assert.True(old.MarkApplying());

        // The user changed a setting in the new version; the file is readable, so it must survive the rollback.
        await File.WriteAllTextAsync(settings, "{\"Language\":\"en\"}");
        var first = Recovery(dir, NewVersion);
        first.PrepareStartup();
        first.Confirm(NotReady());
        var second = Recovery(dir, NewVersion);
        second.PrepareStartup();
        Assert.Equal(UpdateRecoveryConfirmation.RolledBack, second.Confirm(NotReady()));
        var third = Recovery(dir, NewVersion);
        Assert.Equal(UpdateRecoveryStartup.Blocked, third.PrepareStartup());

        Assert.Equal("{\"Language\":\"en\"}", await File.ReadAllTextAsync(settings));
    }

    [Fact]
    public void Package_manager_install_never_runs_recovery()
    {
        var dir = TestDirs.New("recovery-deb");
        var deb = Recovery(dir, kind: AppInstallKind.Debian);
        Assert.False(deb.Stage(NewVersion));
        Assert.Equal(UpdateRecoveryStartup.Ignored, deb.PrepareStartup());
        Assert.False(Directory.Exists(Path.Combine(dir, UpdateRecovery.SnapshotDirectoryName)));

        // A journal left by a Velopack install is not touched by a package-manager copy.
        var velopack = Recovery(dir);
        Assert.True(velopack.Stage(NewVersion));
        Assert.True(velopack.MarkApplying());
        var debAgain = Recovery(dir, NewVersion, AppInstallKind.Debian);
        Assert.Equal(UpdateRecoveryStartup.Ignored, debAgain.PrepareStartup());
        Assert.Null(debAgain.Journal);
        Assert.Contains("Applying", File.ReadAllText(JournalPath(dir)), StringComparison.Ordinal);
    }

    [Fact]
    public void Readiness_requires_ui_user_data_and_matching_components()
    {
        Assert.True(Ready().IsReady);
        Assert.False(new UpdateReadiness(false, true, true, true).IsReady);
        Assert.False(new UpdateReadiness(true, false, true, true).IsReady);
        Assert.False(new UpdateReadiness(true, true, false, true).IsReady);
        Assert.Equal(["ui", "user-data", "components"], new UpdateReadiness(false, false, false, true).Failures);

        // An outdated privileged helper is reported but never blocks the app and is never reinstalled.
        var helper = new UpdateReadiness(true, true, true, helperCompatible: false);
        Assert.True(helper.IsReady);
        Assert.Equal(["helper"], helper.Notes);
    }

    [Fact]
    public void Components_must_match_the_staged_package()
    {
        var dir = TestDirs.New("recovery-components");
        var recovery = Recovery(dir);
        Assert.True(recovery.Stage(NewVersion));

        Assert.True(recovery.ComponentsConsistent(NewVersion, Bundle));
        Assert.False(recovery.ComponentsConsistent(OldVersion, Bundle));
        Assert.False(recovery.ComponentsConsistent(NewVersion, "bundle-b"));
    }

    [Fact]
    public async Task Updater_stages_the_journal_and_requires_it_before_applying()
    {
        var dir = TestDirs.New("recovery-updater");
        var recovery = Recovery(dir);
        var backend = new FakeBackend();
        var updater = new AutomaticAppUpdater(() => backend)
        {
            InstallKind = AppInstallKind.Velopack,
            Recovery = recovery,
        };
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };

        Assert.True(await updater.DownloadIfDueAsync(settings, false));
        Assert.Equal(UpdateRecoveryPhase.Staged, recovery.Journal!.Phase);
        Assert.Equal(OldVersion, recovery.Journal.FromVersion);
        Assert.Equal(NewVersion, recovery.Journal.ToVersion);
        Assert.Equal(AppUpdateApplyResult.Applied, updater.ApplyOnExit(settings, false));
        Assert.Equal(UpdateRecoveryPhase.Applying, recovery.Journal.Phase);
        Assert.Equal(1, backend.Applied);
    }

    [Fact]
    public async Task Updater_does_not_apply_when_the_snapshot_is_missing()
    {
        var dir = TestDirs.New("recovery-updater-nosnapshot");
        var recovery = Recovery(dir);
        var backend = new FakeBackend();
        var updater = new AutomaticAppUpdater(() => backend)
        {
            InstallKind = AppInstallKind.Velopack,
            Recovery = recovery,
        };
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };

        Assert.True(await updater.DownloadIfDueAsync(settings, false));
        Directory.Delete(Path.Combine(dir, UpdateRecovery.SnapshotDirectoryName), recursive: true);
        Assert.Equal(AppUpdateApplyResult.Deferred, updater.ApplyOnExit(settings, false));
        Assert.Equal(0, backend.Applied);
    }

    [Fact]
    public async Task Package_is_not_applied_when_the_snapshot_cannot_be_written()
    {
        var dir = TestDirs.New("recovery-nosnapshot");
        var data = Path.Combine(dir, "data");
        // Fault injection: the data directory is a file, so neither the snapshot nor the journal can be written.
        await File.WriteAllTextAsync(data, "not a directory");
        var recovery = new UpdateRecovery(data, Path.Combine(dir, "app"), AppInstallKind.Velopack,
            OldVersion, Bundle, new FakeRollbackProbe(false));
        var backend = new FakeBackend();
        var updater = new AutomaticAppUpdater(() => backend)
        {
            InstallKind = AppInstallKind.Velopack,
            Recovery = recovery,
        };
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };

        Assert.False(await updater.DownloadIfDueAsync(settings, false));
        Assert.Equal(AppUpdateState.Deferred, updater.State);
        Assert.Equal(AppUpdateApplyResult.Deferred, updater.ApplyOnExit(settings, false));
        Assert.Equal(0, backend.Applied);
    }

    [Fact]
    public async Task Blocked_recovery_stops_automatic_updates_until_an_explicit_request()
    {
        var dir = TestDirs.New("recovery-blocked");
        var old = Recovery(dir);
        Assert.True(old.Stage(NewVersion));
        Assert.True(old.MarkApplying());
        var first = Recovery(dir, NewVersion);
        first.PrepareStartup();
        first.Confirm(NotReady());
        var second = Recovery(dir, NewVersion);
        second.PrepareStartup();
        Assert.Equal(UpdateRecoveryConfirmation.RolledBack, second.Confirm(NotReady()));
        Assert.True(second.AutoApplyBlocked);

        var backend = new FakeBackend();
        var updater = new AutomaticAppUpdater(() => backend)
        {
            InstallKind = AppInstallKind.Velopack,
            Recovery = second,
        };
        var settings = new AppSettings { AutoUpdateApp = true, ConsentAutomaticUpdates = true };

        Assert.False(await updater.DownloadIfDueAsync(settings, false));
        Assert.Equal(0, backend.Downloads);
        Assert.Equal(AppUpdateApplyResult.Nothing, updater.ApplyOnExit(settings, false));
        Assert.Equal(0, backend.Applied);

        // Only an explicit user request clears the block.
        Assert.True(await updater.DownloadNowAsync(settings, false));
        Assert.False(second.AutoApplyBlocked);
        Assert.Equal(1, backend.Downloads);
    }

    [Fact]
    public void Previous_package_is_reported_only_when_the_probe_finds_one()
    {
        var dir = TestDirs.New("recovery-probe");
        Assert.True(Recovery(dir, NewVersion, previousPackage: true).PreviousPackageAvailable);
        Assert.False(Recovery(dir, NewVersion, previousPackage: false).PreviousPackageAvailable);
        // A package-manager copy never offers a rollback path.
        Assert.False(Recovery(dir, NewVersion, AppInstallKind.Debian, previousPackage: true).PreviousPackageAvailable);
    }

    [Fact]
    public async Task File_system_probe_finds_a_kept_package_next_to_the_application()
    {
        var root = TestDirs.New("recovery-packages");
        var application = Path.Combine(root, "current");
        Directory.CreateDirectory(application);
        var packages = Path.Combine(root, "packages");
        Directory.CreateDirectory(packages);
        await File.WriteAllTextAsync(Path.Combine(packages, "BydloDesktop-0.4.4-full.nupkg"), "package");

        var probe = new FileSystemRollbackProbe();
        Assert.True(probe.HasPreviousPackage(application, NewVersion));
        Assert.False(probe.HasPreviousPackage(application, OldVersion));
        Assert.False(probe.HasPreviousPackage(Path.Combine(root, "missing"), NewVersion));
    }

    [Fact]
    public void Recovery_strings_exist_in_both_languages()
    {
        var root = RepositoryRoot();
        var en = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "en.json"));
        var ru = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "ru.json"));
        foreach (var key in new[]
        {
            "settings.update.recovery.blocked",
            "settings.update.recovery.previous",
            "settings.update.recovery.manual",
            "update.recovery.blocked",
            "update.recovery.interrupted",
        })
        {
            Assert.Contains($"\"{key}\"", en, StringComparison.Ordinal);
            Assert.Contains($"\"{key}\"", ru, StringComparison.Ordinal);
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

    private sealed class FakeRollbackProbe(bool hasPreviousPackage) : IUpdateRollbackProbe
    {
        public bool HasPreviousPackage(string applicationDirectory, string currentVersion) => hasPreviousPackage;
    }

    private sealed class FakeBackend : IAppUpdateBackend
    {
        public bool IsInstalled { get; set; } = true;

        public string? PendingVersion { get; set; } = NewVersion;

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
