using Bydlo.Core.Models;
using Bydlo.Core.Storage;

namespace Bydlo.Core.Backup;

/// <summary>
/// Typed deep copy of backup snapshots (issue #393). Replaces a JSON round-trip that
/// produced an extra plaintext buffer with every secret. Mutable containers and models
/// are copied; strings and immutable records are shared. <c>BackupCopyTests</c> fails
/// when a model gains a property that is not copied here.
/// </summary>
internal static class BackupCopy
{
    public static BackupData Copy(BackupData source) => new()
    {
        Version = source.Version,
        Settings = source.Settings is null ? null! : Copy(source.Settings),
        Profiles = source.Profiles?.Select(p => p is null ? null! : new ImportedProfile(Copy(p.Profile), Copy(p.Secrets))).ToList()!,
        Subscriptions = source.Subscriptions?.Select(s => s is null ? null! : new BackupSubscription(Copy(s.Subscription), s.Url)).ToList()!,
        ToolSecrets = Copy(source.ToolSecrets),
        Chains = source.Chains?.Select(c => Copy(c)).ToList()!,
    };

    public static ProfileSecrets Copy(ProfileSecrets source) => source is null ? null! : new()
    {
        Values = source.Values is null ? null! : new(source.Values, source.Values.Comparer),
    };

    public static Profile Copy(Profile source) => source is null ? null! : new()
    {
        Id = source.Id,
        Name = source.Name,
        Protocol = source.Protocol,
        Host = source.Host,
        Port = source.Port,
        Options = source.Options is null ? null! : new(source.Options, source.Options.Comparer),
        SubscriptionId = source.SubscriptionId,
        PreferredEngine = source.PreferredEngine,
        AddedAt = source.AddedAt,
        IsFavorite = source.IsFavorite,
    };

    public static Subscription Copy(Subscription source) => source is null ? null! : new()
    {
        Id = source.Id,
        Name = source.Name,
        LastUpdated = source.LastUpdated,
        Upload = source.Upload,
        Download = source.Download,
        Total = source.Total,
        Expires = source.Expires,
        Title = source.Title,
        UpdateIntervalHours = source.UpdateIntervalHours,
        SupportUrl = source.SupportUrl,
        Announce = source.Announce,
        UserAgent = source.UserAgent,
        Mode = source.Mode,
        SourceOptions = source.SourceOptions?.ToDictionary(
            entry => entry.Key,
            entry => entry.Value is null ? null! : new Dictionary<string, string>(entry.Value, entry.Value.Comparer)),
    };

    public static ProxyChain Copy(ProxyChain source) => source is null ? null! : new()
    {
        Id = source.Id,
        Name = source.Name,
        ProfileIds = source.ProfileIds is null ? null! : [.. source.ProfileIds],
        PreferredEngine = source.PreferredEngine,
        AddedAt = source.AddedAt,
        IsFavorite = source.IsFavorite,
    };

    public static ServerGroup Copy(ServerGroup source) => source is null ? null! : new()
    {
        Id = source.Id,
        Name = source.Name,
        ProfileIds = source.ProfileIds is null ? null! : [.. source.ProfileIds],
        SubscriptionId = source.SubscriptionId,
        SelectedProfileId = source.SelectedProfileId,
        Engine = source.Engine,
        Mode = source.Mode,
        ProbeUrl = source.ProbeUrl,
        IntervalSeconds = source.IntervalSeconds,
        ToleranceMs = source.ToleranceMs,
    };

    public static AppSettings Copy(AppSettings source) => new()
    {
        ServerGroups = source.ServerGroups?.Select(g => Copy(g)).ToList()!,
        StartWithSystem = source.StartWithSystem,
        ConnectOnStartup = source.ConnectOnStartup,
        MinimizeToTray = source.MinimizeToTray,
        ShowNotifications = source.ShowNotifications,
        SuggestClipboardLinks = source.SuggestClipboardLinks,
        Theme = source.Theme,
        ReduceMotion = source.ReduceMotion,
        InterfaceScale = source.InterfaceScale,
        CustomThemeId = source.CustomThemeId,
        Language = source.Language,
        OnboardingCompleted = source.OnboardingCompleted,
        ConsentAutomaticUpdates = source.ConsentAutomaticUpdates,
        ConsentNotifications = source.ConsentNotifications,
        ConsentThemes = source.ConsentThemes,
        ConsentsInitialized = source.ConsentsInitialized,
        MockMode = source.MockMode,
        MockScenario = source.MockScenario,
        LastProfileId = source.LastProfileId,
        Mode = source.Mode,
        LocalProxyPort = source.LocalProxyPort,
        AllowLan = source.AllowLan,
        LocalProxyUsername = source.LocalProxyUsername,
        IpCheckProvider = source.IpCheckProvider,
        SetSystemProxy = source.SetSystemProxy,
        RunElevatedOnStartup = source.RunElevatedOnStartup,
        KillSwitch = source.KillSwitch,
        AutoReconnect = source.AutoReconnect,
        ShowAdvanced = source.ShowAdvanced,
        SubscriptionAutoUpdate = source.SubscriptionAutoUpdate,
        SubscriptionUpdateIntervalHours = source.SubscriptionUpdateIntervalHours,
        CheckForUpdates = source.CheckForUpdates,
        AutoUpdateApp = source.AutoUpdateApp,
        LastAutomaticUpdateCheck = source.LastAutomaticUpdateCheck,
        LastUpdateCheck = source.LastUpdateCheck,
        PendingUpdate = source.PendingUpdate,
        Contributors = source.Contributors is null ? null! : [.. source.Contributors],
        LastContributorsCheck = source.LastContributorsCheck,
        DismissedNoticeId = source.DismissedNoticeId,
        Routing = source.Routing is null ? null! : Copy(source.Routing),
        DpiDefaults = source.DpiDefaults,
        Zapret = source.Zapret is null ? null! : Copy(source.Zapret),
        TgWsProxy = source.TgWsProxy is null ? null! : Copy(source.TgWsProxy),
        EnginePaths = source.EnginePaths is null ? null! : new(source.EnginePaths, source.EnginePaths.Comparer),
    };

    private static List<T> List<T>(List<T> source) => source is null ? null! : [.. source];

    public static RoutingSettings Copy(RoutingSettings source) => new()
    {
        PreserveMeshVpnRanges = source.PreserveMeshVpnRanges,
        BypassPrivateNetworks = source.BypassPrivateNetworks,
        OrderedRules = List(source.OrderedRules),
        RuleSetSources = List(source.RuleSetSources),
        Preset = source.Preset,
        PresetCountry = source.PresetCountry,
        RemoteDns = source.RemoteDns,
        DirectDns = source.DirectDns,
        RemoteDnsTransport = source.RemoteDnsTransport,
        DirectDnsTransport = source.DirectDnsTransport,
        RemoteDnsThroughProxy = source.RemoteDnsThroughProxy,
        DirectDnsThroughProxy = source.DirectDnsThroughProxy,
        FakeIp = source.FakeIp,
        FakeIpExceptions = List(source.FakeIpExceptions),
        RemoteDnsDomains = List(source.RemoteDnsDomains),
        DirectDnsDomains = List(source.DirectDnsDomains),
        RemoteDnsRuleSets = List(source.RemoteDnsRuleSets),
        DirectDnsRuleSets = List(source.DirectDnsRuleSets),
        DirectDomains = List(source.DirectDomains),
        ProxyDomains = List(source.ProxyDomains),
        BlockDomains = List(source.BlockDomains),
        DirectCidrs = List(source.DirectCidrs),
        ProxyOnlyListed = source.ProxyOnlyListed,
        DirectProcesses = List(source.DirectProcesses),
        ProxyOnlyListedProcesses = source.ProxyOnlyListedProcesses,
    };

    public static ZapretSettings Copy(ZapretSettings source) => new()
    {
        TestTargets = List(source.TestTargets),
        InstallPath = source.InstallPath,
        Strategy = source.Strategy,
        GameFilter = source.GameFilter,
    };

    public static TgWsProxySettings Copy(TgWsProxySettings source) => new()
    {
        Host = source.Host,
        Port = source.Port,
        DcIps = List(source.DcIps),
        FakeTlsDomain = source.FakeTlsDomain,
        Verbose = source.Verbose,
        CfProxyFallback = source.CfProxyFallback,
        CfProxyDomains = List(source.CfProxyDomains),
        ProxyProtocol = source.ProxyProtocol,
        ProxyProtocolTrustedSources = List(source.ProxyProtocolTrustedSources),
    };
}
