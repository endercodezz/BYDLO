namespace Bydlo.Core.Models;

public enum ThemePreference
{
    System,

    /// <summary>Legacy built-in theme; now a gallery JSON theme. Migrated to <see cref="System"/> on load (issue #134).</summary>
    Purple,
    Light,
    Oled,
    Custom,
}

/// <summary>External IP lookup service used by the "Check connection" action on Home.</summary>
public enum IpCheckProvider
{
    IpWhoIs,
    IpApiCo,
    IpInfo,
}

public enum MockScenario
{
    /// <summary>Connects after a short delay and produces synthetic traffic.</summary>
    Success,

    /// <summary>Fails while connecting (e.g. "handshake timeout").</summary>
    FailOnConnect,

    /// <summary>Connects, then the simulated process crashes after ~20 seconds.</summary>
    DropAfterConnect,

    /// <summary>Takes ~6 seconds to connect.</summary>
    SlowConnect,
}

public sealed class AppSettings
{
    public List<ServerGroup> ServerGroups { get; set; } = [];

    public bool StartWithSystem { get; set; }

    public bool ConnectOnStartup { get; set; }

    public bool MinimizeToTray { get; set; }

    public bool ShowNotifications { get; set; } = true;

    public bool SuggestClipboardLinks { get; set; } = true;

    /// <summary>Fresh installs follow the OS (Light or Black).</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public bool ReduceMotion { get; set; }

    /// <summary>
    /// Interface scale in percent (issue #477). The shell applies it once through a layout transform,
    /// so text, buttons, fields, paddings and icons grow together and stay vector-sharp.
    /// </summary>
    public int InterfaceScale { get; set; } = DefaultInterfaceScale;

    public const int DefaultInterfaceScale = 100;

    public const int MinInterfaceScale = 80;

    public const int MaxInterfaceScale = 150;

    public const int InterfaceScaleStep = 10;

    /// <summary>Every selectable scale, ascending. The default is in the middle of the list.</summary>
    public static IReadOnlyList<int> InterfaceScaleSteps { get; } =
        [.. Enumerable.Range(0, (MaxInterfaceScale - MinInterfaceScale) / InterfaceScaleStep + 1)
            .Select(index => MinInterfaceScale + index * InterfaceScaleStep)];

    /// <summary>
    /// Clamps a stored, imported or hand-edited scale to the supported range and snaps it to the
    /// nearest step, so the UI never shows a value it cannot offer.
    /// </summary>
    public static int ClampInterfaceScale(int value)
    {
        var clamped = Math.Clamp(value, MinInterfaceScale, MaxInterfaceScale);
        var steps = (clamped - MinInterfaceScale + InterfaceScaleStep / 2) / InterfaceScaleStep;
        return MinInterfaceScale + steps * InterfaceScaleStep;
    }

    /// <summary>Layout multiplier for <see cref="InterfaceScale"/>; 1.0 is 100%.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double InterfaceScaleFactor => ClampInterfaceScale(InterfaceScale) / 100.0;

    /// <summary>Repairs a scale written by an older build or edited by hand. Returns true when changed.</summary>
    public bool NormalizeInterfaceScale()
    {
        var clamped = ClampInterfaceScale(InterfaceScale);
        if (clamped == InterfaceScale)
        {
            return false;
        }
        InterfaceScale = clamped;
        return true;
    }

    /// <summary>Installed custom theme id (file <c>themes/&lt;id&gt;.json</c>). Used only when <see cref="Theme"/> is Custom.</summary>
    public string? CustomThemeId { get; set; }

    /// <summary>"auto", "en" or "ru".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// False only for a fresh installation. Existing settings without this field deserialize as true,
    /// so upgrading users are not shown the first-run screen again.
    /// </summary>
    public bool OnboardingCompleted { get; set; } = true;

    /// <summary>
    /// Explicit consent to contact GitHub for BYDLO updates (issue #438). Off until the user ticks it
    /// on the first-run screen or in Settings; nothing is enabled implicitly.
    /// </summary>
    public bool ConsentAutomaticUpdates { get; set; }

    /// <summary>
    /// Explicit consent to load notification content from GitHub and show desktop notifications
    /// (issue #438). Independent of <see cref="ConsentAutomaticUpdates"/>.
    /// </summary>
    public bool ConsentNotifications { get; set; }

    /// <summary>
    /// Explicit consent to download and parse the theme gallery from GitHub (issue #438).
    /// Independent of the other two consents.
    /// </summary>
    public bool ConsentThemes { get; set; }

    /// <summary>
    /// True once the first-run screen or the upgrade migration has decided the three consents
    /// (issue #438). Without it a revoked consent would be granted again on the next start.
    /// </summary>
    public bool ConsentsInitialized { get; set; }

    public bool MockMode { get; set; }

    /// <summary>Session override without changing the saved user preference.</summary>
    public AppSettings WithMockMode(bool enabled)
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.MockMode = enabled;
        return copy;
    }

    public static bool IsMockForced(IEnumerable<string> args, string? environmentValue) =>
        args.Contains("--mock", StringComparer.Ordinal) || environmentValue == "1";

    public MockScenario MockScenario { get; set; } = MockScenario.Success;

    public Guid? LastProfileId { get; set; }

    public ConnectionMode Mode { get; set; } = ConnectionMode.LocalProxy;

    /// <summary>Port of the local mixed (SOCKS5 + HTTP) inbound.</summary>
    public int LocalProxyPort { get; set; } = 2080;

    /// <summary>
    /// Listen on all interfaces so other devices on the LAN can use the local proxy.
    /// Off by default. Requires <see cref="LocalProxyUsername"/> and a password in the
    /// secret store ("tool:localproxy"); the firewall is never changed automatically.
    /// </summary>
    public bool AllowLan { get; set; }

    /// <summary>Login for LAN clients. The password lives in the secret store (never in settings JSON).</summary>
    public string LocalProxyUsername { get; set; } = "";

    /// <summary>Service used by the explicit "Check connection" action. The request always goes through the tunnel.</summary>
    public IpCheckProvider IpCheckProvider { get; set; } = IpCheckProvider.IpWhoIs;

    /// <summary>Sets the OS proxy to the local inbound while connected. Off by default.</summary>
    public bool SetSystemProxy { get; set; }

    /// <summary>
    /// When true, Windows asks for elevation (UAC) right after startup so TUN, tunnels,
    /// Zapret and the system proxy are available immediately (issue #246). Off by default;
    /// when elevation is declined the app keeps working with a limited feature set.
    /// Linux/macOS ignore this setting (use sudo or the privileged helper instead).
    /// Never triggers while mock or <c>BYDLO_SAFE_DEV=1</c> safety lock is active.
    /// </summary>
    public bool RunElevatedOnStartup { get; set; }

    /// <summary>Retain a separate strict TUN while the sing-box backend reconnects. Off by default.</summary>
    public bool KillSwitch { get; set; }

    public bool AutoReconnect { get; set; } = true;

    public bool ShowAdvanced { get; set; }

    /// <summary>Automatically refresh subscriptions whose <see cref="Storage.Subscription.LastUpdated"/> is older than the interval.</summary>
    public bool SubscriptionAutoUpdate { get; set; } = true;

    /// <summary>Auto-update interval in hours (1-168). Default 12.</summary>
    public int SubscriptionUpdateIntervalHours { get; set; } = 12;

    /// <summary>Automatically check GitHub Releases for a new BYDLO version. On by default.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Opt in to packaged application updates. Legacy notification consent does not enable installation.</summary>
    public bool AutoUpdateApp { get; set; }

    public DateTimeOffset? LastAutomaticUpdateCheck { get; set; }

    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>
    /// Purple is no longer built in (issue #134): it is installed from the theme gallery like any other JSON theme.
    /// Settings and backups from earlier versions that stored it switch to System. Returns true when changed.
    /// </summary>
    public bool MigrateLegacyTheme()
    {
        if (Theme != ThemePreference.Purple)
        {
            return false;
        }
        Theme = ThemePreference.System;
        return true;
    }

    /// <summary>
    /// Decides the three GitHub consents once (issue #438). A fresh profile keeps all of them off;
    /// settings written before the consents existed keep the GitHub access the user already had, so
    /// upgrading does not silently stop update checks. A profile that never confirmed the first-run
    /// screen grants nothing. Returns true when the consents were decided by this call.
    /// </summary>
    public bool MigrateConsents(bool firstRun)
    {
        if (ConsentsInitialized)
        {
            return false;
        }
        ConsentsInitialized = true;
        if (firstRun || !OnboardingCompleted)
        {
            return true;
        }
        ConsentAutomaticUpdates = CheckForUpdates;
        ConsentNotifications = ShowNotifications;
        return true;
    }

    /// <summary>Unacknowledged stable release, retained across startup check intervals.</summary>
    public Diagnostics.GitHubRelease? PendingUpdate { get; set; }

    /// <summary>Contributor list cached from GitHub for the sidebar; refreshed with automatic update checks.</summary>
    public List<Diagnostics.Contributor> Contributors { get; set; } = [];

    public DateTimeOffset? LastContributorsCheck { get; set; }

    /// <summary>Id of the project notice (<c>notice.json</c>) the user closed; a new id shows again.</summary>
    public string? DismissedNoticeId { get; set; }

    public RoutingSettings Routing { get; set; } = new();
    public DpiSettings DpiDefaults { get; set; } = new();

    public ZapretSettings Zapret { get; set; } = new();

    public TgWsProxySettings TgWsProxy { get; set; } = new();

    /// <summary>Explicit paths to engine executables, keyed by <see cref="EngineKind"/> name.</summary>
    public Dictionary<string, string> EnginePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum RoutingPreset
{
    All,
    BlockedOnly,
    ExceptLocal,
}

public enum DnsTransport
{
    System,
    Udp,
    Tcp,
    Https,
    Tls,
    Quic,
}

public sealed class RoutingSettings
{
    /// <summary>
    /// Keep CGNAT / mesh-VPN address ranges (100.64.0.0/10, fd7a:115c:a1e3::/48) outside the tunnel,
    /// so other VPN or mesh-network clients keep working while BYDLO is connected.
    /// </summary>
    public bool PreserveMeshVpnRanges { get; set; } = true;

    public bool BypassPrivateNetworks { get; set; } = true;
    public List<Routing.OrderedRoutingRule> OrderedRules { get; set; } = [];
    public List<Routing.RuleSetSource> RuleSetSources { get; set; } = [];
    public RoutingPreset Preset { get; set; }
    public string PresetCountry { get; set; } = "ru";

    /// <summary>DNS-over-HTTPS server used through the tunnel.</summary>
    public string RemoteDns { get; set; } = "1.1.1.1";

    public string DirectDns { get; set; } = "local";
    public DnsTransport RemoteDnsTransport { get; set; } = DnsTransport.Https;
    public DnsTransport DirectDnsTransport { get; set; } = DnsTransport.Udp;
    public bool RemoteDnsThroughProxy { get; set; } = true;
    public bool DirectDnsThroughProxy { get; set; }
    public bool FakeIp { get; set; }
    public List<string> FakeIpExceptions { get; set; } = [];
    public List<string> RemoteDnsDomains { get; set; } = [];
    public List<string> DirectDnsDomains { get; set; } = [];
    public List<string> RemoteDnsRuleSets { get; set; } = [];
    public List<string> DirectDnsRuleSets { get; set; } = [];

    public List<string> DirectDomains { get; set; } = [];

    public List<string> ProxyDomains { get; set; } = [];

    public List<string> BlockDomains { get; set; } = [];

    public List<string> DirectCidrs { get; set; } = [];

    /// <summary>When true only <see cref="ProxyDomains"/> go through the tunnel (split tunneling "include" mode).</summary>
    public bool ProxyOnlyListed { get; set; }

    /// <summary>Process names that bypass the tunnel (sing-box TUN; Xray on Windows/Linux).</summary>
    public List<string> DirectProcesses { get; set; } = [];

    /// <summary>In sing-box TUN mode, only the listed processes can use the proxy.</summary>
    public bool ProxyOnlyListedProcesses { get; set; }
}

public sealed class ZapretSettings
{
    public List<string> TestTargets { get; set; } = ["https://www.youtube.com/", "https://discord.com/"];

    /// <summary>Fallback folder with an extracted Flowseal release or a native nfqws/tpws executable.</summary>
    public string? InstallPath { get; set; }

    /// <summary>Flowseal batch file name or native preset id. Empty selects the platform default.</summary>
    public string? Strategy { get; set; }

    /// <summary>Game filter: "disabled", "all", "tcp" or "udp".</summary>
    public string GameFilter { get; set; } = "disabled";
}

public sealed class TgWsProxySettings
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 1443;

    public List<string> DcIps { get; set; } = ["2:149.154.167.220", "4:149.154.167.220"];

    public string FakeTlsDomain { get; set; } = "";

    public bool Verbose { get; set; }

    public bool CfProxyFallback { get; set; } = true;

    /// <summary>Own Cloudflare-proxied domains for the fallback. Empty uses the list maintained by Flowseal.</summary>
    public List<string> CfProxyDomains { get; set; } = [];

    /// <summary>
    /// Accept a PROXY protocol v1 header before the client handshake (upstream <c>--proxy-protocol</c>).
    /// Only for a listener placed behind a trusted reverse proxy; off by default.
    /// </summary>
    public bool ProxyProtocol { get; set; }

    /// <summary>Peers allowed to send the header. Empty means loopback only, the default.</summary>
    public List<string> ProxyProtocolTrustedSources { get; set; } = ["127.0.0.1", "::1"];
}
