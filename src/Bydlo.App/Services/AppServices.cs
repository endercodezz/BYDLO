using Bydlo.Platform.Processes;
using Avalonia.Controls;
using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Import;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;
using Bydlo.Engines;
using Bydlo.Engines.Installation;
using Bydlo.Engines.TgWsProxy;
using Bydlo.Engines.Zapret;
using Bydlo.Platform;

namespace Bydlo.App.Services;

/// <summary>Composition root: creates every service once. No DI container on purpose — the graph is small.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "ShutdownAsync owns the asynchronous application lifetime and disposes its resources.")]
public sealed class AppServices
{
    private AppServices(string[] args, IPrivilegeService? privileges)
    {
        Args = args;
        ForcedMock = AppSettings.IsMockForced(args, Environment.GetEnvironmentVariable("BYDLO_MOCK"));
        Paths = AppPathsFactory.Create(args);
        Log = new LogHub(LogRedactor.Shared, Paths.LogsDirectory);
        Secrets = SecretStoreFactory.Create(Paths, ForcedMock);
        SettingsStore = new SettingsStore(Paths.SettingsFile);
        Profiles = new ProfileStore(Paths.ProfilesFile, Secrets);
        Privileges = privileges ?? new PrivilegeService();
        Journal = new ProcessJournal(Paths.JournalFile);
        Runner = new SystemProcessRunner(Journal);
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(Bydlo.Core.AppVersion.UserAgent);
        Lock = EngineLock.LoadEmbedded();
        InstallKind = AppInstallLocator.CreateDefault().Detect(AppContext.BaseDirectory, Environment.ProcessPath);
        UpdateRecovery = new UpdateRecovery(Paths.DataDirectory, AppContext.BaseDirectory, InstallKind,
            Bydlo.Core.AppVersion.Current, EngineLock.BundleVersion);
        Installer = new EngineInstaller(Http, Lock, Paths.EnginesDirectory);
        Locator = new EngineLocator(Installer, PlatformInfo.Current);
        ZapretFirewall = new ZapretFirewall(Runner, Installer.UserDataDirectory);
        AmneziaNetwork = AmneziaNetworkFactory.Create(Runner, Paths);
        OpenVpnDriver = new OpenVpnDriver(Runner, Privileges, () => Locator.BundledExecutable("openvpn") is { } exe ? Path.GetDirectoryName(exe) : null);
        TgSecrets = new TgWsProxySecrets(Secrets);
        Registry = EngineRegistryFactory.Create(Runner, LogRedactor.Shared, Locator, Privileges, () => new MockEngine(), ZapretFirewall, AmneziaNetwork, OpenVpnDriver);
        Registry.Decorate = (engine, settings) => UsesPrivilegedHelper &&
            HelperRequestPolicy.Supports(engine.Descriptor.Kind) &&
            (engine.Descriptor.Kind != EngineKind.SingBox || settings.Mode == ConnectionMode.Tun)
                ? new HelperEngine(engine, () => OperatingSystem.IsWindows() ? new HelperConnection() : new UnixHelperConnection(),
                    () => HelperManagement.IsInstalled, ExpectedHelperBuild) : engine;
        SystemProxy = SystemProxyFactory.Create(Runner, Paths);
        Connections = new ConnectionManager(Registry, Secrets, Paths, new ConflictDetector(new TcpPortChecker()), Log, SystemProxy);
        ConnectionCheck = new ConnectionCheckService();
        Importer = new ProfileImporter();
        Subscriptions = new SubscriptionClient(Http, Importer);
        Zapret = new ZapretEngine(Runner, LogRedactor.Shared, Locator, Privileges, ZapretFirewall);
        ZapretUpdater = new ZapretUpdater(Installer, PlatformInfo.Current);
        AutomaticUpdates = new(() => new PackagedAppUpdater(Paths.DataDirectory, Http))
        {
            // Package markers decide who owns updates; a writable path alone never enables the built-in updater.
            InstallKind = InstallKind,
            Recovery = UpdateRecovery,
        };
        // Read-only probe of /etc/apt: the app never runs apt and never writes to system files (issue #488).
        AptRepositoryConfigured = OperatingSystem.IsLinux() && AptRepositorySource.IsConfigured();
    }

    public string[] Args { get; }
    internal static HelperBuild ExpectedHelperBuild { get; } = HelperManagement.GetBuild(EngineLock.BundleVersion);

    /// <summary>Mock Mode forced for this session by <c>--mock</c> / <c>BYDLO_MOCK=1</c>.</summary>
    public bool ForcedMock { get; }

    public IAppPaths Paths { get; }

    public LogHub Log { get; }

    public ISecretStore Secrets { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; private set; } = new();

    public ProfileStore Profiles { get; }

    public IPrivilegeService Privileges { get; }

    public ProcessJournal Journal { get; private set; }

    public IProcessRunner Runner { get; }

    public HttpClient Http { get; }

    public EngineLock Lock { get; }

    public EngineInstaller Installer { get; }

    public EngineLocator Locator { get; }

    public ZapretFirewall ZapretFirewall { get; }

    public IAmneziaNetwork AmneziaNetwork { get; }

    public OpenVpnDriver OpenVpnDriver { get; }

    public TgWsProxySecrets TgSecrets { get; }

    public EngineRegistry Registry { get; }

    public ISystemProxyService SystemProxy { get; }

    public ConnectionManager Connections { get; }

    /// <summary>Explicit external-IP check used by Home. Never runs automatically.</summary>
    public ConnectionCheckService ConnectionCheck { get; }

    /// <summary>Explicit per-server latency probe used by Connections. Never runs automatically.</summary>
    public ServerLatencyProbe ServerLatency { get; } = new();

    public ProfileImporter Importer { get; }

    public SubscriptionClient Subscriptions { get; }

    /// <summary>Used for Zapret strategy / install queries (not for running).</summary>
    public ZapretEngine Zapret { get; }

    public ZapretUpdater ZapretUpdater { get; }

    public ThemeService Theme { get; } = new();

    /// <summary>Engines left running by a previous crashed session.</summary>
    public IReadOnlyList<JournalEntry> Orphans { get; private set; } = [];

    /// <summary>Set by the root view so services can reach clipboard / storage / launcher.</summary>
    public Func<TopLevel?> TopLevel { get; set; } = () => null;

    public bool IsMock => ForcedMock || Settings.MockMode;

    public AutomaticAppUpdater AutomaticUpdates { get; }

    /// <summary>How this copy was installed (issue #413).</summary>
    public AppInstallKind InstallKind { get; }

    /// <summary>Local update recovery journal and readiness confirmation (issue #412).</summary>
    public UpdateRecovery UpdateRecovery { get; }

    /// <summary>
    /// Whether an apt source for the BYDLO repository is configured (issue #488). Detected once at
    /// startup; tests and the headless UI verification override it.
    /// </summary>
    public bool AptRepositoryConfigured { get; set; }

    /// <summary>Whether privileged engines are delegated to the installed helper.</summary>
    public bool UsesPrivilegedHelper => HelperManagement.IsSupported && !Privileges.IsElevated &&
        !IsMock && !NetworkSafetyGuard.IsLocked && HelperManagement.IsInstalled;

    /// <summary>Settings passed to engines (Mock Mode forced by the command line wins).</summary>
    public AppSettings EffectiveSettings => Settings.WithMockMode(IsMock);

    public static async Task<AppServices> CreateAsync(string[] args, IPrivilegeService? privileges = null)
    {
        var s = new AppServices(args, privileges);
        // Recovery runs before any user data is read: a snapshot restored here is what the app then loads (issue #412).
        s.UpdateRecovery.PrepareStartup();
        var firstRun = !File.Exists(s.Paths.SettingsFile);
        s.Settings = await s.SettingsStore.LoadAsync().ConfigureAwait(false);
        if (firstRun)
        {
            s.Settings.OnboardingCompleted = false;
            s.Settings.Language = "auto";
            s.Settings.Theme = ThemePreference.System;
        }
        s.Settings.MigrateConsents(firstRun);

        await s.Profiles.LoadAsync().ConfigureAwait(false);
        if (!s.IsMock && !NetworkSafetyGuard.IsLocked && s.Privileges.IsElevated && s.AmneziaNetwork.HasPendingRecovery)
        {
            try { s.Journal.KillOrphans("amneziawg"); await s.AmneziaNetwork.RemoveAsync().ConfigureAwait(false); }
            catch (Exception ex) { s.Log.Error("amneziawg", "Network recovery failed: " + ex.Message); }
        }
        if (!s.IsMock && !NetworkSafetyGuard.IsLocked && s.Privileges.IsElevated && s.ZapretFirewall.HasPendingRecovery)
        {
            try { await s.ZapretFirewall.RemoveAsync().ConfigureAwait(false); }
            catch (Exception ex) { s.Log.Error("zapret", "Firewall recovery failed: " + ex.Message); }
        }

        if (!s.IsMock && !NetworkSafetyGuard.IsLocked)
        {
            try { await s.SystemProxy.ClearAsync().ConfigureAwait(false); }
            catch (Exception ex) { s.Log.Error("system-proxy", "Proxy recovery failed: " + ex.Message); }
            // A retained TUN may outlive a crashed UI. Release only BYDLO's journaled guard.
            try { s.Journal.KillOrphans(RetainedTunEngine.GuardProcessName); }
            catch (Exception ex) { s.Log.Error("kill-switch", "Crash recovery failed: " + ex.Message); }
        }
        s.Orphans = s.Journal.FindOrphans();
        s.Log.Info("app", $"BYDLO started. Data: {s.Paths.DataDirectory}. Mock mode: {s.IsMock}. Safety lock: {NetworkSafetyGuard.IsLocked}.");
        return s;
    }

    public Task SaveSettingsAsync() => SettingsStore.SaveAsync(Settings);

    /// <summary>
    /// Local readiness confirmation for a freshly installed version (issue #412). Called once the UI is up;
    /// it only inspects local state and never starts a tunnel, an engine, a driver or a network request.
    /// </summary>
    public void ConfirmUpdateReadiness()
    {
        if (!UpdateRecovery.IsPending) { return; }
        var readiness = new UpdateReadiness(
            uiStarted: true,
            userDataLoaded: IsUserDataLoaded(SettingsStore.LoadStatus) && IsUserDataLoaded(Profiles.LoadStatus),
            componentsConsistent: UpdateRecovery.ComponentsConsistent(Bydlo.Core.AppVersion.Current, EngineLock.BundleVersion),
            helperCompatible: !HelperManagement.IsOutdated(ExpectedHelperBuild));
        var result = UpdateRecovery.Confirm(readiness);
        Log.Info("update", $"Update readiness: {result}. Phase: {UpdateRecovery.Journal?.Phase}. " +
            $"Failed checks: {string.Join(',', readiness.Failures)}. Notes: {string.Join(',', readiness.Notes)}.");
        if (result == UpdateRecoveryConfirmation.RolledBack)
        {
            Log.Error("update", "The new version did not confirm readiness; automatic updates are paused.");
        }
    }

    /// <summary>A missing file is fine; a reset or unreadable one means the new version did not load user data.</summary>
    private static bool IsUserDataLoaded(StoreLoadStatus status) =>
        status is StoreLoadStatus.Ok or StoreLoadStatus.Missing or StoreLoadStatus.RecoveredFromBackup;

    public async Task RestoreBackupAsync(Bydlo.Core.Backup.BackupData data, Bydlo.Core.Backup.BackupImportMode mode)
    {
        var restored = Bydlo.Core.Backup.BackupArchive.ImportSettings(data, Settings, mode);
        if (restored.LastProfileId is { } id && !data.Profiles.Any(p => p.Profile.Id == id) &&
            (mode == Bydlo.Core.Backup.BackupImportMode.Replace || !Profiles.Profiles.Any(p => p.Id == id)))
        { restored.LastProfileId = null; }
        await SettingsStore.SaveAsync(restored);
        try { await Profiles.RestoreBackupAsync(data, mode); }
        catch { await SettingsStore.SaveAsync(Settings); throw; }
        Settings = restored;
    }

    private readonly CancellationTokenSource _shutdownCancellation = new();
    private Task? _subscriptionRefreshTask;

    public void StartSubscriptionAutoRefresh(Func<CancellationToken, Task> run) =>
        _subscriptionRefreshTask ??= run(ShutdownToken);

    public CancellationToken ShutdownToken => _shutdownCancellation.Token;

    public void CleanupKillSwitch()
    {
        if (IsMock || NetworkSafetyGuard.IsLocked) { return; }
        Journal.KillOrphans(RetainedTunEngine.GuardProcessName);
        if (Journal.FindOrphans().Any(e => e.DisplayName == RetainedTunEngine.GuardProcessName))
        {
            throw new InvalidOperationException(Loc.Instance["settings.killswitch.recovery"]);
        }
    }

    public int CleanupOrphans()
    {
        var killed = Journal.KillOrphans();
        Orphans = Journal.FindOrphans();
        if (Orphans.Count > 0)
        {
            Log.Error("app", $"Stopped {killed} engine process(es); {Orphans.Count} still running and kept for retry.");
            throw new InvalidOperationException(Loc.Instance["error.orphans.stop"]);
        }
        Log.Warn("app", $"Stopped {killed} engine process(es) left by a previous session.");
        return killed;
    }

    /// <summary>Replaces the process journal (isolated unit tests only).</summary>
    internal void ReplaceJournalForTests(ProcessJournal journal)
    {
        Journal = journal;
        Orphans = Journal.FindOrphans();
    }

    private readonly object _shutdownGate = new();
    private Task? _shutdownTask;

    public Task ShutdownAsync()
    {
        lock (_shutdownGate)
        {
            if (_shutdownTask is null || _shutdownTask.IsFaulted || _shutdownTask.IsCanceled)
            { _shutdownTask = ShutdownCoreAsync(); }
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        _shutdownCancellation.Cancel();
        if (_subscriptionRefreshTask is not null) { await _subscriptionRefreshTask.ConfigureAwait(false); }
        await Connections.StopAllAsync().ConfigureAwait(false);
        await SaveSettingsAsync().ConfigureAwait(false);
        // The file writer is asynchronous: drain it so the shutdown lines reach bydlo.log.
        await Log.FlushAsync().ConfigureAwait(false);
        Http.Dispose();
        _shutdownCancellation.Dispose();
    }
}
