using Bydlo.Core.Models;
using static Bydlo.Core.Import.ImportHelpers;

namespace Bydlo.Core.Import.Parsers;

/// <summary>Parsed INI-style WireGuard / AmneziaWG configuration.</summary>
public sealed record WireGuardConfig(
    IReadOnlyDictionary<string, string> Interface,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Peers)
{
    /// <summary>AmneziaWG obfuscation keys. Their presence marks a config as AmneziaWG.</summary>
    public static readonly string[] AmneziaKeys = ["Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4", "I1", "I2", "I3", "I4", "I5"];

    public bool IsAmnezia => AmneziaKeys.Any(Interface.ContainsKey);

    public static WireGuardConfig Parse(string text)
    {
        var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peers = new List<IReadOnlyDictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase))
            {
                current = iface;
                continue;
            }

            if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                peers.Add(current);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || current is null)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            current[key] = current.TryGetValue(key, out var existing) ? existing + ", " + value : value;
        }

        return new WireGuardConfig(iface, peers);
    }
}

/// <summary>WireGuard and AmneziaWG <c>.conf</c> files.</summary>
public sealed class WireGuardConfParser : IProfileParser
{
    public bool CanParse(string input) =>
        input.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) && input.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);

    public ImportedProfile Parse(string input)
    {
        var cfg = WireGuardConfig.Parse(input);
        if (!cfg.Interface.TryGetValue("PrivateKey", out var privateKey) || string.IsNullOrWhiteSpace(privateKey))
        {
            throw new ImportException("WireGuard config has no [Interface] PrivateKey.");
        }

        if (cfg.Peers.Count == 0 || !cfg.Peers[0].TryGetValue("Endpoint", out var endpoint))
        {
            throw new ImportException("WireGuard config has no [Peer] Endpoint.");
        }

        var (host, port) = SplitEndpoint(endpoint);
        var peer = cfg.Peers[0];
        var p = new Profile
        {
            Protocol = cfg.IsAmnezia ? ProtocolKind.AmneziaWG : ProtocolKind.WireGuard,
            Host = host,
            Port = port,
            Name = $"{(cfg.IsAmnezia ? "AmneziaWG" : "WireGuard")} {host}",
        };
        void Opt(string key, IReadOnlyDictionary<string, string> src, string name)
        {
            if (src.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                p.Options[key] = v;
            }
        }

        Opt("address", cfg.Interface, "Address");
        Opt("dns", cfg.Interface, "DNS");
        Opt("mtu", cfg.Interface, "MTU");
        Opt("peerPublicKey", peer, "PublicKey");
        Opt("allowedIps", peer, "AllowedIPs");
        Opt("keepalive", peer, "PersistentKeepalive");
        foreach (var k in WireGuardConfig.AmneziaKeys)
        {
            Opt("awg." + k, cfg.Interface, k);
        }

        var secrets = new ProfileSecrets { Values = { ["privateKey"] = privateKey, [Keys.RawConfig] = input.Replace("\r\n", "\n") } };
        if (peer.TryGetValue("PresharedKey", out var psk))
        {
            secrets.Values["presharedKey"] = psk;
        }

        return new ImportedProfile(p, secrets);
    }

    internal static (string Host, int Port) SplitEndpoint(string endpoint)
    {
        endpoint = endpoint.Trim();
        string host;
        string portText;
        if (endpoint.StartsWith('['))
        {
            var close = endpoint.IndexOf(']');
            if (close <= 1 || close + 1 >= endpoint.Length || endpoint[close + 1] != ':')
            {
                throw new ImportException("Endpoint must be [host]:port.");
            }

            host = endpoint[1..close];
            portText = endpoint[(close + 2)..];
        }
        else
        {
            var colon = endpoint.LastIndexOf(':');
            if (colon <= 0)
            {
                throw new ImportException("Endpoint must be host:port.");
            }

            host = endpoint[..colon];
            portText = endpoint[(colon + 1)..];
        }

        return int.TryParse(portText, out var port) && port is > 0 and <= 65535
            ? (host, port)
            : throw new ImportException("Endpoint has an invalid port.");
    }
}

/// <summary><c>wireguard://privatekey@host:port?publickey=..&amp;address=..</c> share links.</summary>
public sealed class WireGuardUriParser : IProfileParser
{
    public bool CanParse(string input) =>
        input.StartsWith("wireguard://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("wg://", StringComparison.OrdinalIgnoreCase);

    public ImportedProfile Parse(string input)
    {
        var text = "wireguard://" + input.Trim()[(input.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var uri = ParseUri(text, "wireguard");
        var q = Query(uri);
        var privateKey = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrEmpty(privateKey) || !q.TryGetValue("publickey", out var pub))
        {
            throw new ImportException("WireGuard link needs a private key and publickey parameter.");
        }

        var address = q.TryGetValue("address", out var a) ? a : "10.0.0.2/32";
        var conf = $"[Interface]\nPrivateKey = {privateKey}\nAddress = {address}\n" +
                   (q.TryGetValue("mtu", out var mtu) ? $"MTU = {mtu}\n" : "") +
                   $"\n[Peer]\nPublicKey = {pub}\n" +
                   (q.TryGetValue("presharedkey", out var psk) ? $"PresharedKey = {psk}\n" : "") +
                   $"AllowedIPs = 0.0.0.0/0, ::/0\nEndpoint = {(uri.HostNameType == UriHostNameType.IPv6 ? $"[{Host(uri)}]" : Host(uri))}:{Port(uri, 51820, text)}\n";
        var imported = new WireGuardConfParser().Parse(conf);
        imported.Profile.Name = Fragment(uri, imported.Profile.Name);
        return imported;
    }
}

/// <summary>OpenVPN <c>.ovpn</c> client configurations, validated before storing their original text.</summary>
public sealed class OpenVpnParser : IProfileParser
{
    public bool CanParse(string input)
    {
        var lines = input.Split('\n').Select(l => l.Trim());
        return lines.Any(l => l.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
            && lines.Any(l => l.Equals("client", StringComparison.OrdinalIgnoreCase) || l.StartsWith("dev ", StringComparison.OrdinalIgnoreCase));
    }

    public ImportedProfile Parse(string input)
    {
        try { Security.OpenVpnConfigPolicy.Validate(input); }
        catch (InvalidDataException) { throw new ImportException("import.openvpn.unsafe"); }
        string? host = null;
        var port = 1194;
        var proto = "udp";
        var needsAuth = false;
        foreach (var raw in input.Split('\n'))
        {
            var parts = raw.Trim().Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].StartsWith('#') || parts[0].StartsWith(';'))
            {
                continue;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "remote" when host is null && parts.Length >= 2:
                    host = parts[1];
                    if (parts.Length >= 3 && !parts[2].StartsWith('#') && !parts[2].StartsWith(';'))
                    {
                        if (!int.TryParse(parts[2], out var rp) || rp is < 1 or > 65535)
                        { throw new ImportException("Invalid OpenVPN remote port."); }
                        port = rp;
                        if (parts.Length >= 4 && !parts[3].StartsWith('#') && !parts[3].StartsWith(';'))
                        { proto = parts[3]; }
                    }

                    break;
                case "port":
                    if (parts.Length < 2 || !int.TryParse(parts[1], out var pp) || pp is < 1 or > 65535)
                    { throw new ImportException("Invalid OpenVPN port."); }
                    port = pp;
                    break;
                case "proto" when parts.Length >= 2:
                    proto = parts[1];
                    break;
                case "auth-user-pass" when parts.Length == 1 || parts[1].StartsWith('#') || parts[1].StartsWith(';'):
                    needsAuth = true;
                    break;
            }
        }

        if (host is null)
        {
            throw new ImportException("OpenVPN config has no 'remote' line.");
        }

        var p = new Profile { Protocol = ProtocolKind.OpenVpn, Host = host, Port = port, Name = $"OpenVPN {host}" };
        p.Options["proto"] = proto;
        if (needsAuth)
        {
            p.Options["authUserPass"] = "true";
        }

        return new ImportedProfile(p, new ProfileSecrets { Values = { [Keys.RawConfig] = input.Replace("\r\n", "\n") } });
    }
}
