using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Engines.TgWsProxy;

public enum TgLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Runtime options of the native proxy (the upstream CLI flags that BYDLO exposes).</summary>
public sealed record TgWsProxyOptions
{
    public static readonly IReadOnlyDictionary<int, string> DcDefaultIps = new Dictionary<int, string>
    {
        [1] = "149.154.175.50",
        [2] = "149.154.167.51",
        [3] = "149.154.175.100",
        [4] = "149.154.167.91",
        [5] = "149.154.171.5",
        [203] = "91.105.192.100",
    };

    public static readonly IReadOnlyDictionary<int, string> DcTestIps = new Dictionary<int, string>
    {
        [1] = "149.154.175.10",
        [2] = "149.154.167.40",
        [3] = "149.154.175.117",
    };

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 1443;

    public required byte[] Secret { get; init; }

    /// <summary>DCs reached through Telegram's WebSocket endpoint at the given IP; others use the fallbacks.</summary>
    public IReadOnlyDictionary<int, string> DcRedirects { get; init; } = new Dictionary<int, string>();

    public string FakeTlsDomain { get; init; } = "";

    public bool CfProxyFallback { get; init; } = true;

    /// <summary>Own Cloudflare-proxied domains; empty uses Flowseal's list.</summary>
    public IReadOnlyList<string> CfProxyDomains { get; init; } = [];

    public bool Verbose { get; init; }

    public int PoolSize { get; init; } = 4;

    public int BufferSize { get; init; } = 256 * 1024;

    public bool ForceTestDc { get; init; }

    /// <summary>Port 80 instead of TLS for the Cloudflare paths (upstream <c>--no-secure</c>).</summary>
    public bool DisableSecure { get; init; }

    /// <summary>Accept a PROXY protocol v1 header before the client handshake (upstream <c>--proxy-protocol</c>).</summary>
    public bool ProxyProtocol { get; init; }

    /// <summary>Peers allowed to send that header. Loopback only unless configured otherwise.</summary>
    public IReadOnlyList<IPAddress> ProxyProtocolTrustedSources { get; init; } = DefaultTrustedSources;

    /// <summary>Loopback, the only sender trusted by default.</summary>
    public static readonly IReadOnlyList<IPAddress> DefaultTrustedSources = [IPAddress.Loopback, IPAddress.IPv6Loopback];

    public static TgWsProxyOptions From(TgWsProxySettings settings, string secretHex) => new()
    {
        Host = string.IsNullOrWhiteSpace(settings.Host) ? "127.0.0.1" : settings.Host.Trim(),
        Port = settings.Port,
        Secret = Convert.FromHexString(secretHex),
        DcRedirects = ParseDcIps(settings.DcIps),
        FakeTlsDomain = settings.FakeTlsDomain.Trim(),
        CfProxyFallback = settings.CfProxyFallback,
        CfProxyDomains = TgWsProxy.CfProxyDomains.Normalize(settings.CfProxyDomains),
        Verbose = settings.Verbose,
        ProxyProtocol = settings.ProxyProtocol,
        ProxyProtocolTrustedSources = ParseTrustedSources(settings.ProxyProtocolTrustedSources),
    };

    /// <summary>Parses the trusted PROXY protocol senders; an empty list keeps the loopback default.</summary>
    public static IReadOnlyList<IPAddress> ParseTrustedSources(IEnumerable<string> entries)
    {
        var result = new List<IPAddress>();
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            if (!IPAddress.TryParse(entry, out var ip))
            {
                throw new EngineException($"Invalid trusted PROXY protocol source '{entry}'.", localizationKey: "error.engine.tg.proxyprotocol");
            }

            result.Add(ip);
        }

        return result.Count > 0 ? result : DefaultTrustedSources;
    }

    /// <summary>Parses <c>DC:IP</c> lines (IPv4 only, as upstream).</summary>
    public static Dictionary<int, string> ParseDcIps(IEnumerable<string> entries)
    {
        var result = new Dictionary<int, string>();
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0
                || !int.TryParse(entry.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var dc)
                || !IPAddress.TryParse(entry[(colon + 1)..], out var ip)
                || ip.AddressFamily != AddressFamily.InterNetwork
                || entry[(colon + 1)..].Count(c => c == '.') != 3)
            {
                throw new EngineException($"Invalid DC:IP entry '{entry}'.", localizationKey: "error.engine.tg.dcip");
            }

            result[dc] = ip.ToString();
        }

        return result;
    }
}

/// <summary>Counters shared by all connections (thread-safe).</summary>
public sealed class TgWsProxyStats
{
    private long _total;
    private long _active;
    private long _ws;
    private long _tcpFallback;
    private long _cf;
    private long _fronting;
    private long _bad;
    private long _masked;
    private long _wsErrors;
    private long _up;
    private long _down;
    private long _poolHits;
    private long _poolMisses;

    public long BytesUp => Interlocked.Read(ref _up);

    public long BytesDown => Interlocked.Read(ref _down);

    public long ActiveConnections => Interlocked.Read(ref _active);

    internal void Opened() { Interlocked.Increment(ref _total); Interlocked.Increment(ref _active); }

    internal void Closed() => Interlocked.Decrement(ref _active);

    internal void Ws() => Interlocked.Increment(ref _ws);

    internal void TcpFallback() => Interlocked.Increment(ref _tcpFallback);

    internal void CfProxy() => Interlocked.Increment(ref _cf);

    internal void Fronting() => Interlocked.Increment(ref _fronting);

    internal void Bad() => Interlocked.Increment(ref _bad);

    internal void Masked() => Interlocked.Increment(ref _masked);

    internal void WsError() => Interlocked.Increment(ref _wsErrors);

    internal void PoolHit() => Interlocked.Increment(ref _poolHits);

    internal void PoolMiss() => Interlocked.Increment(ref _poolMisses);

    internal void Up(int n) => Interlocked.Add(ref _up, n);

    internal void Down(int n) => Interlocked.Add(ref _down, n);

    public string Summary()
    {
        var hits = Interlocked.Read(ref _poolHits);
        var poolTotal = hits + Interlocked.Read(ref _poolMisses);
        return $"total={Interlocked.Read(ref _total)} active={ActiveConnections} ws={Interlocked.Read(ref _ws)} "
            + $"tcp_fb={Interlocked.Read(ref _tcpFallback)} cf={Interlocked.Read(ref _cf)} front={Interlocked.Read(ref _fronting)} "
            + $"bad={Interlocked.Read(ref _bad)} masked={Interlocked.Read(ref _masked)} err={Interlocked.Read(ref _wsErrors)} "
            + $"pool={(poolTotal > 0 ? $"{hits}/{poolTotal}" : "n/a")} up={HumanBytes(BytesUp)} down={HumanBytes(BytesDown)}";
    }

    public static string HumanBytes(long n)
    {
        double value = n;
        foreach (var unit in new[] { "B", "KB", "MB", "GB" })
        {
            if (Math.Abs(value) < 1024)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{value:0.0}{unit}");
            }

            value /= 1024;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0}TB");
    }
}

/// <summary>State shared by the server, the pool and the bridges of one proxy run.</summary>
internal sealed class TgWsProxyContext(TgWsProxyOptions options, ITgNetwork network, TimeProvider time, Action<TgLogLevel, string> log, CancellationToken stopping)
{
    public TgWsProxyOptions Options { get; } = options;

    public ITgNetwork Network { get; } = network;

    public TimeProvider Time { get; } = time;

    public TgWsProxyStats Stats { get; } = new();

    public CfProxyDomains CfDomains { get; } = new();

    public CancellationToken Stopping { get; } = stopping;

    public void Log(TgLogLevel level, string message)
    {
        if (level == TgLogLevel.Debug && !Options.Verbose)
        {
            return;
        }

        log(level, CfProxyDomains.Censor(message));
    }
}
