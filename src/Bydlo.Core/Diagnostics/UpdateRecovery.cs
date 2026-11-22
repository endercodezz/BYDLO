using System.Text.Json;
using Bydlo.Core.Serialization;
using Bydlo.Core.Storage;

namespace Bydlo.Core.Diagnostics;

/// <summary>Phase of the local update recovery journal (issue #412).</summary>
public enum UpdateRecoveryPhase
{
    /// <summary>The package is downloaded and verified, the snapshot exists, no file was replaced yet.</summary>
    Staged,

    /// <summary>The package was handed to the installer and this process is exiting.</summary>
    Applying,

    /// <summary>The new version started but has not confirmed readiness yet.</summary>
    PendingConfirmation,

    /// <summary>The new version started, the UI came up, user data loaded and the components match.</summary>
    Confirmed,

    /// <summary>
    /// The protocol returned to the last known-good state: the previous version is running again or the
    /// snapshot was restored. BYDLO never replaces application files itself, so this is not a binary rollback.
    /// </summary>
    RolledBack,
}

/// <summary>Why the protocol stopped trying (issue #412).</summary>
public enum UpdateRecoveryReason
{
    None,

    /// <summary>The installer never replaced the files: the previous version started again.</summary>
    Interrupted,

    /// <summary>The new version started but failed the readiness check too many times.</summary>
    NotReady,
}

/// <summary>What the startup recovery decided; drives the localized message and the auto-apply guard.</summary>
public enum UpdateRecoveryStartup
{
    /// <summary>This copy is not a Velopack install: recovery never touches it.</summary>
    Ignored,

    /// <summary>No journal, or the last update was already confirmed.</summary>
    Nothing,

    /// <summary>The new version is running and waits for the UI to confirm readiness.</summary>
    AwaitingConfirmation,

    /// <summary>The installer did not replace the files; the previous version is running.</summary>
    Interrupted,

    /// <summary>The attempt budget is exhausted: automatic updates stay off until the user asks explicitly.</summary>
    Blocked,
}

/// <summary>Outcome of <see cref="UpdateRecovery.Confirm"/>.</summary>
public enum UpdateRecoveryConfirmation
{
    /// <summary>No confirmation was pending.</summary>
    Nothing,

    /// <summary>The new version is proven and the snapshot was dropped.</summary>
    Confirmed,

    /// <summary>The check failed but the attempt budget is not exhausted; the next start retries.</summary>
    Retry,

    /// <summary>The budget is exhausted: automatic updates are paused and the next start restores the snapshot.</summary>
    RolledBack,
}

/// <summary>
/// Durable record of one update transition (issue #412). Written atomically before the installer runs and
/// updated at every phase, so an interrupted write or a crash at any step leaves a readable state.
/// </summary>
public sealed class UpdateRecoveryJournal
{
    public int Version { get; set; } = 1;

    public UpdateRecoveryPhase Phase { get; set; }

    public UpdateRecoveryReason Reason { get; set; }

    /// <summary>Version that was running when the package was staged.</summary>
    public string? FromVersion { get; set; }

    /// <summary>Version the staged package installs.</summary>
    public string? ToVersion { get; set; }

    /// <summary>Hash of the pinned engine lock the staged package was built with.</summary>
    public string? EngineBundleVersion { get; set; }

    /// <summary>Starts of the new version that did not confirm readiness, or interrupted applies.</summary>
    public int Attempts { get; set; }

    /// <summary>Automatic downloads and installs stay off until the user asks explicitly.</summary>
    public bool AutoApplyBlocked { get; set; }

    /// <summary>Directory of the user-data snapshot taken before the first run of the new version.</summary>
    public string? SnapshotDirectory { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Blocking readiness checks that failed, as short identifiers.</summary>
    public List<string> FailedChecks { get; set; } = [];

    /// <summary>Informational findings, e.g. an outdated privileged helper.</summary>
    public List<string> Notes { get; set; } = [];
}

/// <summary>
/// Local readiness of a freshly installed version (issue #412). Only local state is inspected: no tunnel,
/// engine, driver, service or network request is involved.
/// </summary>
public sealed class UpdateReadiness
{
    public UpdateReadiness(bool uiStarted, bool userDataLoaded, bool componentsConsistent, bool helperCompatible)
    {
        UiStarted = uiStarted;
        UserDataLoaded = userDataLoaded;
        ComponentsConsistent = componentsConsistent;
        HelperCompatible = helperCompatible;
        var failures = new List<string>();
        if (!uiStarted) { failures.Add("ui"); }
        if (!userDataLoaded) { failures.Add("user-data"); }
        if (!componentsConsistent) { failures.Add("components"); }
        Failures = failures;
        // An outdated helper degrades privileged features but never blocks the app: it is reported, not fatal.
        Notes = helperCompatible ? [] : ["helper"];
    }

    public bool UiStarted { get; }

    /// <summary>Settings and profiles parsed without falling back to an empty document.</summary>
    public bool UserDataLoaded { get; }

    /// <summary>Running application and pinned engine bundle match the versions recorded at staging.</summary>
    public bool ComponentsConsistent { get; }

    /// <summary>Installed privileged helper matches this build, or no helper is installed.</summary>
    public bool HelperCompatible { get; }

    public bool IsReady => UiStarted && UserDataLoaded && ComponentsConsistent;

    public IReadOnlyList<string> Failures { get; }

    public IReadOnlyList<string> Notes { get; }
}

/// <summary>
/// Answers whether the installer kept a full package of another version next to the application (issue #412).
/// BYDLO mentions that path only when the file is really there and never runs the installer itself.
/// </summary>
public interface IUpdateRollbackProbe
{
    bool HasPreviousPackage(string applicationDirectory, string currentVersion);
}

/// <summary>Read-only directory probe; a missing or unreadable folder means "no previous package".</summary>
public sealed class FileSystemRollbackProbe : IUpdateRollbackProbe
{
    public bool HasPreviousPackage(string applicationDirectory, string currentVersion)
    {
        // Without the application directory the layout is unknown, so no rollback path is promised.
        if (!Directory.Exists(applicationDirectory)) { return false; }
        // Windows keeps <root>/current next to <root>/packages; other layouts simply have no such folder.
        foreach (var directory in new[]
        {
            Path.Combine(applicationDirectory, "packages"),
            Path.Combine(applicationDirectory, "..", "packages"),
        })
        {
            try
            {
                if (!Directory.Exists(directory)) { continue; }
                foreach (var file in Directory.EnumerateFiles(directory, "*-full.nupkg"))
                {
                    if (!Path.GetFileName(file).Contains("-" + currentVersion + "-", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return false;
    }
}

/// <summary>
/// Copies the user data files the updater must never lose (issue #412). Secrets live in the secret store,
/// which the updater does not touch, so they are not copied here.
/// </summary>
public sealed class UpdateSnapshot(string directoryPath)
{
    public const string SettingsFileName = "settings.json";

    public const string ProfilesFileName = "profiles.json";

    private static readonly string[] Files = [SettingsFileName, ProfilesFileName];

    public string DirectoryPath { get; } = directoryPath;

    /// <summary>Copies the user data files that exist. False means the snapshot could not be written.</summary>
    public bool TryCreate(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            foreach (var name in Files)
            {
                var source = Path.Combine(dataDirectory, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(DirectoryPath, name), overwrite: true);
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Restores only files that are missing or no longer parse. A readable file is never overwritten, so
    /// changes made in the new version are not silently discarded.
    /// </summary>
    public IReadOnlyList<string> RestoreMissingOrBroken(string dataDirectory)
    {
        var restored = new List<string>();
        foreach (var name in Files)
        {
            var source = Path.Combine(DirectoryPath, name);
            if (!File.Exists(source)) { continue; }
            var target = Path.Combine(dataDirectory, name);
            if (File.Exists(target) && Parses(target)) { continue; }
            try
            {
                File.Copy(source, target, overwrite: true);
                restored.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return restored;
    }

    public void Delete()
    {
        try
        {
            if (Directory.Exists(DirectoryPath)) { Directory.Delete(DirectoryPath, recursive: true); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool Parses(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Atomic, corruption-tolerant journal file (issue #412). A missing, truncated or unreadable file means
/// "no journal": recovery then does nothing instead of guessing, and the broken file is kept for diagnostics.
/// </summary>
public sealed class UpdateRecoveryStore(string path)
{
    /// <summary>Journal format this build understands. A newer file is ignored instead of guessed.</summary>
    public const int CurrentVersion = 1;

    public string Path { get; } = path;

    public UpdateRecoveryJournal? Load()
    {
        try
        {
            if (!File.Exists(Path)) { return null; }
            using var stream = File.OpenRead(Path);
            var journal = JsonSerializer.Deserialize(stream, JsonDefaults.Options.Info<UpdateRecoveryJournal>());
            return journal is { Version: > 0 and <= CurrentVersion, ToVersion.Length: > 0 } ? journal : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            JsonDefaults.SaveCorruptCopy(Path);
            return null;
        }
    }

    /// <summary>Writes through a temporary file and a rename: a crash leaves the old or the new file, never half of it.</summary>
    public bool TrySave(UpdateRecoveryJournal journal)
    {
        var tmp = Path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, journal, JsonDefaults.Options.Info<UpdateRecoveryJournal>());
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}

/// <summary>
/// Minimal recovery protocol around the existing Velopack updater (issue #412). It never installs anything
/// itself: it records the transition, snapshots user data, confirms the new version locally and stops
/// automatic updates when the new version does not come up. Package-manager installs are never touched.
/// </summary>
public sealed class UpdateRecovery
{
    /// <summary>Starts of an unconfirmed version (or interrupted applies) before automatic updates stop.</summary>
    public const int MaxAttempts = 2;

    public const string JournalFileName = "update-recovery.json";

    public const string SnapshotDirectoryName = "update-recovery";

    public const string SnapshotFolderName = "snapshot";

    private readonly UpdateRecoveryStore _store;
    private readonly UpdateSnapshot _snapshot;
    private readonly IUpdateRollbackProbe _rollback;
    private readonly TimeProvider _time;
    private readonly AppInstallKind _installKind;
    private readonly string _applicationDirectory;
    private readonly string _currentVersion;
    private readonly string _engineBundleVersion;
    private bool? _previousPackageAvailable;

    public UpdateRecovery(string dataDirectory, string applicationDirectory, AppInstallKind installKind,
        string currentVersion, string engineBundleVersion, IUpdateRollbackProbe? rollback = null,
        TimeProvider? time = null)
    {
        DataDirectory = dataDirectory;
        _applicationDirectory = applicationDirectory;
        _installKind = installKind;
        _currentVersion = currentVersion;
        _engineBundleVersion = engineBundleVersion;
        _rollback = rollback ?? new FileSystemRollbackProbe();
        _time = time ?? TimeProvider.System;
        _store = new UpdateRecoveryStore(Path.Combine(dataDirectory, JournalFileName));
        _snapshot = new UpdateSnapshot(Path.Combine(dataDirectory, SnapshotDirectoryName, SnapshotFolderName));
    }

    public string DataDirectory { get; }

    public UpdateRecoveryJournal? Journal { get; private set; }

    /// <summary>What <see cref="PrepareStartup"/> decided for this start.</summary>
    public UpdateRecoveryStartup Startup { get; private set; } = UpdateRecoveryStartup.Nothing;

    /// <summary>Only a Velopack install may run the protocol; deb, Arch and Nix copies are owned by their manager.</summary>
    public bool IsOwned => _installKind == AppInstallKind.Velopack;

    public bool AutoApplyBlocked => Journal?.AutoApplyBlocked == true;

    /// <summary>True while the running version still has to confirm readiness.</summary>
    public bool IsPending => Journal?.Phase == UpdateRecoveryPhase.PendingConfirmation;

    /// <summary>True only when the installer really kept a package of another version next to the app.</summary>
    public bool PreviousPackageAvailable => _previousPackageAvailable ??=
        IsOwned && _rollback.HasPreviousPackage(_applicationDirectory, _currentVersion);

    /// <summary>
    /// Runs before any user data is read. Decides the phase for this start, counts the attempt and, when the
    /// budget is exhausted, restores the snapshot so the app starts from the last known-good data.
    /// </summary>
    public UpdateRecoveryStartup PrepareStartup()
    {
        if (!IsOwned)
        {
            Startup = UpdateRecoveryStartup.Ignored;
            return Startup;
        }

        Journal = _store.Load();
        if (Journal is not { } journal)
        {
            Startup = UpdateRecoveryStartup.Nothing;
            return Startup;
        }

        if (journal.Phase == UpdateRecoveryPhase.Confirmed)
        {
            Startup = UpdateRecoveryStartup.Nothing;
            return Startup;
        }

        if (journal.Phase == UpdateRecoveryPhase.RolledBack)
        {
            // The restore always happens here, before user data is read, so the app starts from the snapshot.
            if (journal.AutoApplyBlocked && _snapshot.RestoreMissingOrBroken(DataDirectory).Count > 0)
            {
                journal.Notes.Add("snapshot-restored");
                Save(journal);
            }
            Startup = journal.AutoApplyBlocked ? UpdateRecoveryStartup.Blocked : UpdateRecoveryStartup.Nothing;
            return Startup;
        }

        var runningTarget = SameVersion(journal.ToVersion, _currentVersion);
        if (journal.Phase == UpdateRecoveryPhase.Staged && !runningTarget)
        {
            // The package is downloaded but the installer never ran: nothing was replaced and nothing is lost.
            Startup = UpdateRecoveryStartup.Nothing;
            return Startup;
        }

        if ((journal.Phase is UpdateRecoveryPhase.Staged or UpdateRecoveryPhase.Applying) && runningTarget)
        {
            // The new version is running; the UI confirms readiness later in this start.
            journal.Phase = UpdateRecoveryPhase.PendingConfirmation;
            journal.Attempts = 1;
            Save(journal);
            Startup = UpdateRecoveryStartup.AwaitingConfirmation;
            return Startup;
        }

        if (journal.Phase == UpdateRecoveryPhase.Applying)
        {
            // The installer ran but the previous version started again.
            return RollBack(journal, UpdateRecoveryReason.Interrupted);
        }

        if (!runningTarget)
        {
            // PendingConfirmation for another version: the new one never started.
            return RollBack(journal, UpdateRecoveryReason.Interrupted);
        }

        // Another start of the version that still has not confirmed readiness.
        journal.Attempts++;
        if (journal.Attempts > MaxAttempts)
        {
            return RollBack(journal, UpdateRecoveryReason.NotReady);
        }
        Save(journal);
        Startup = UpdateRecoveryStartup.AwaitingConfirmation;
        return Startup;
    }

    /// <summary>
    /// Records a verified package and snapshots user data before anything is replaced. False means the journal
    /// or the snapshot could not be written: the package must then never be applied.
    /// </summary>
    public bool Stage(string? toVersion)
    {
        if (!IsOwned || string.IsNullOrWhiteSpace(toVersion)) { return false; }
        if (!_snapshot.TryCreate(DataDirectory)) { return false; }
        // An interrupted apply keeps its count, so a package that never replaces the files cannot loop forever.
        var previous = Journal ?? _store.Load();
        var attempts = previous is { Phase: UpdateRecoveryPhase.RolledBack, Reason: UpdateRecoveryReason.Interrupted }
            ? previous.Attempts
            : 0;
        var journal = new UpdateRecoveryJournal
        {
            Phase = UpdateRecoveryPhase.Staged,
            FromVersion = _currentVersion,
            ToVersion = toVersion,
            EngineBundleVersion = _engineBundleVersion,
            Attempts = attempts,
            SnapshotDirectory = _snapshot.DirectoryPath,
            UpdatedAt = _time.GetUtcNow(),
        };
        if (!_store.TrySave(journal)) { return false; }
        Journal = journal;
        return true;
    }

    /// <summary>
    /// Marks the hand-over to the installer. False means the journal cannot be written, so the package must not
    /// be applied: without a durable journal there is no recovery.
    /// </summary>
    public bool MarkApplying()
    {
        if (!IsOwned || Journal is not { } journal || journal.Phase != UpdateRecoveryPhase.Staged) { return false; }
        journal.Phase = UpdateRecoveryPhase.Applying;
        journal.UpdatedAt = _time.GetUtcNow();
        return _store.TrySave(journal);
    }

    /// <summary>True only when a staged journal and its snapshot exist for the package that is about to install.</summary>
    public bool CanApply(string? pendingVersion)
    {
        if (!IsOwned || Journal is not { } journal || journal.Phase != UpdateRecoveryPhase.Staged) { return false; }
        if (pendingVersion is { Length: > 0 } && !SameVersion(journal.ToVersion, pendingVersion))
        {
            return false;
        }
        return journal.SnapshotDirectory is { Length: > 0 } && Directory.Exists(journal.SnapshotDirectory);
    }

    /// <summary>Compares the running build with the versions recorded when the package was staged.</summary>
    public bool ComponentsConsistent(string appVersion, string engineBundleVersion) =>
        Journal is not { } journal ||
        (SameVersion(journal.ToVersion, appVersion) &&
         string.Equals(journal.EngineBundleVersion, engineBundleVersion, StringComparison.Ordinal));

    /// <summary>Records the local readiness result of the running version.</summary>
    public UpdateRecoveryConfirmation Confirm(UpdateReadiness readiness)
    {
        if (!IsOwned || Journal is not { } journal || journal.Phase != UpdateRecoveryPhase.PendingConfirmation)
        {
            return UpdateRecoveryConfirmation.Nothing;
        }

        journal.FailedChecks = [.. readiness.Failures];
        journal.Notes = [.. readiness.Notes];
        if (readiness.IsReady)
        {
            journal.Phase = UpdateRecoveryPhase.Confirmed;
            journal.Reason = UpdateRecoveryReason.None;
            journal.Attempts = 0;
            journal.AutoApplyBlocked = false;
            Save(journal);
            // The new version is proven; the snapshot of the previous one is no longer needed.
            _snapshot.Delete();
            Startup = UpdateRecoveryStartup.Nothing;
            return UpdateRecoveryConfirmation.Confirmed;
        }

        if (journal.Attempts >= MaxAttempts)
        {
            journal.Phase = UpdateRecoveryPhase.RolledBack;
            journal.Reason = UpdateRecoveryReason.NotReady;
            journal.AutoApplyBlocked = true;
            // The snapshot is restored by the next start, before any user data is read.
            Save(journal);
            Startup = UpdateRecoveryStartup.Blocked;
            return UpdateRecoveryConfirmation.RolledBack;
        }

        Save(journal);
        return UpdateRecoveryConfirmation.Retry;
    }

    /// <summary>Explicit user request: allows automatic updates again after a failed confirmation.</summary>
    public void ClearBlock()
    {
        if (Journal is not { AutoApplyBlocked: true } journal) { return; }
        journal.AutoApplyBlocked = false;
        Save(journal);
        Startup = UpdateRecoveryStartup.Nothing;
    }

    private UpdateRecoveryStartup RollBack(UpdateRecoveryJournal journal, UpdateRecoveryReason reason)
    {
        journal.Phase = UpdateRecoveryPhase.RolledBack;
        journal.Reason = reason;
        journal.Attempts++;
        journal.AutoApplyBlocked = journal.Attempts >= MaxAttempts;
        if (journal.AutoApplyBlocked && _snapshot.RestoreMissingOrBroken(DataDirectory).Count > 0)
        {
            journal.Notes.Add("snapshot-restored");
        }
        Save(journal);
        Startup = journal.AutoApplyBlocked ? UpdateRecoveryStartup.Blocked : UpdateRecoveryStartup.Interrupted;
        return Startup;
    }

    private void Save(UpdateRecoveryJournal journal)
    {
        journal.UpdatedAt = _time.GetUtcNow();
        // A failed write keeps the previous phase, which is safe: the next start re-evaluates the same state.
        _store.TrySave(journal);
    }

    /// <summary>
    /// Velopack reports <c>0.4.5</c> while the assembly may report <c>0.4.5.0</c>; both name the same release.
    /// </summary>
    private static bool SameVersion(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) { return true; }
        return Version.TryParse(left, out var a) && Version.TryParse(right, out var b) &&
            a.Major == b.Major && a.Minor == b.Minor &&
            Math.Max(a.Build, 0) == Math.Max(b.Build, 0) &&
            Math.Max(a.Revision, 0) == Math.Max(b.Revision, 0);
    }
}
