using System.Text.Json;
using System.Text.RegularExpressions;
using Bydlo.Core.Models;
using static Bydlo.Core.Import.ImportHelpers;

namespace Bydlo.Core.Import.Parsers;

/// <summary>Shared mapping of V2Ray-style query parameters (security, transport, REALITY...).</summary>
internal static class StreamOptions
{
    public static void Apply(Dictionary<string, string> q, Profile p, string defaultSecurity = "none")
    {
        p.Options[Keys.Security] = q.TryGetValue("security", out var sec) && !string.IsNullOrWhiteSpace(sec) ? sec.ToLowerInvariant() : defaultSecurity;
        p.Options[Keys.Transport] = q.TryGetValue("type", out var t) && !string.IsNullOrWhiteSpace(t) ? t.ToLowerInvariant() : "tcp";
        Copy(q, p,
            ("sni", Keys.Sni), ("peer", Keys.Sni), ("fp", Keys.Fingerprint), ("alpn", Keys.Alpn),
            ("pcs", Keys.PinnedPeerCertSha256), ("vcn", Keys.VerifyPeerCertByName),
            ("pbk", Keys.RealityPublicKey), ("sid", Keys.RealityShortId), ("spx", Keys.RealitySpiderX),
            ("path", Keys.Path), ("host", Keys.HostHeader), ("serviceName", Keys.ServiceName),
            ("flow", Keys.Flow), ("mode", Keys.XhttpMode), ("headerType", Keys.HeaderType),
            ("seed", Keys.Seed));
        if (q.TryGetValue("extra", out var extra) && !string.IsNullOrWhiteSpace(extra))
        {
            p.Options[Keys.XhttpExtra] = XhttpExtraValidator.Normalize(extra);
        }
        if (q.TryGetValue("allowInsecure", out var ai) && ai is "1" or "true" || q.TryGetValue("insecure", out var ins) && ins is "1" or "true")
        {
            p.Options[Keys.AllowInsecure] = "true";
        }
    }
}

public sealed class VlessParser : IProfileParser
{
    public bool CanParse(string input) => input.StartsWith("vless://", StringComparison.OrdinalIgnoreCase);

    public ImportedProfile Parse(string input)
    {
        var uri = ParseUri(input, "vless");
        var uuid = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrWhiteSpace(uuid))
        {
            throw new ImportException("VLESS link has no user id.");
        }

        var q = Query(uri);
        var p = new Profile { Protocol = ProtocolKind.Vless, Host = Host(uri), Port = Port(uri, 443, input) };
        p.Name = Fragment(uri, $"VLESS {p.Host}");
        StreamOptions.Apply(q, p);
        if (q.TryGetValue("encryption", out var enc) && !string.IsNullOrWhiteSpace(enc))
        {
            p.Options[Keys.Encryption] = enc.Trim();
        }
        else
        {
            p.Options[Keys.Encryption] = "none";
        }
        if (p.Option(Keys.Security) == "reality" && p.Option(Keys.RealityPublicKey) is null)
        {
            throw new ImportException("VLESS REALITY link has no public key (pbk).");
        }

        return new ImportedProfile(p, new ProfileSecrets { Values = { [Keys.Uuid] = uuid } });
    }
}

public sealed class VmessParser : IProfileParser
{
    public bool CanParse(string input) => input.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase);

    public ImportedProfile Parse(string input)
    {
        var parts = input.Trim()[8..].Split('#', 2)[0].Split('?', 2);
        if (!TryDecodeBase64(parts[0], out var json))
        {
            throw new ImportException("VMess link is not valid base64 JSON.");
        }

        string? fingerprint = null;
        if (parts.Length == 2)
        {
            var query = System.Web.HttpUtility.ParseQueryString(parts[1]);
            fingerprint = query["fp"];
            if (query.Count != 1 || string.IsNullOrWhiteSpace(fingerprint))
            {
                throw new ImportException("VMess link has unsupported query options.");
            }
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ImportException("VMess link contains invalid JSON.");
        }

        using var ownedDocument = document;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ImportException("VMess link JSON must be an object.");
        }

        string S(string name) => root.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

        var host = S("add");
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(S("port"), out var port) || port is <= 0 or > 65535)
        {
            throw new ImportException("VMess link has no valid server or port.");
        }

        var p = new Profile { Protocol = ProtocolKind.Vmess, Host = host, Port = port };
        p.Name = string.IsNullOrWhiteSpace(S("ps")) ? $"VMess {host}" : S("ps");
        var tls = S("tls");
        p.Options[Keys.Security] = string.IsNullOrEmpty(tls) || tls == "none" ? "none" : tls.ToLowerInvariant();
        p.Options[Keys.Transport] = string.IsNullOrEmpty(S("net")) ? "tcp" : S("net").ToLowerInvariant();
        p.Options[Keys.Method] = string.IsNullOrEmpty(S("scy")) ? "auto" : S("scy");
        void Opt(string key, string value) { if (!string.IsNullOrWhiteSpace(value)) { p.Options[key] = value; } }
        Opt(Keys.Sni, S("sni"));
        Opt(Keys.Alpn, S("alpn"));
        Opt(Keys.Fingerprint, fingerprint ?? S("fp"));
        Opt(Keys.PinnedPeerCertSha256, S("pcs"));
        Opt(Keys.VerifyPeerCertByName, S("vcn"));
        Opt(Keys.HostHeader, S("host"));
        Opt(p.Options[Keys.Transport] == "grpc" ? Keys.ServiceName : Keys.Path, S("path"));
        Opt(Keys.HeaderType, S("type"));
        Opt("alterId", S("aid"));

        var id = S("id");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ImportException("VMess link has no user id.");
        }

        return new ImportedProfile(p, new ProfileSecrets { Values = { [Keys.Uuid] = id } });
    }
}

public sealed class TrojanParser : IProfileParser
{
    public bool CanParse(string input) => input.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase);

    public ImportedProfile Parse(string input)
    {
        var uri = ParseUri(input, "trojan");
        var password = Uri.UnescapeDataString(uri.UserInfo);
        if (string.IsNullOrEmpty(password))
        {
            throw new ImportException("Trojan link has no password.");
        }

        var p = new Profile { Protocol = ProtocolKind.Trojan, Host = Host(uri), Port = Port(uri, 443, input) };
        p.Name = Fragment(uri, $"Trojan {p.Host}");
        StreamOptions.Apply(Query(uri), p, defaultSecurity: "tls");
        return new ImportedProfile(p, new ProfileSecrets { Values = { [Keys.Password] = password } });
    }
}

public sealed partial class ShadowsocksParser : IProfileParser
{
    public bool CanParse(string input) => input.StartsWith("ss://", StringComparison.OrdinalIgnoreCase);

    public ImportedProfile Parse(string input)
    {
        var raw = input.Trim()[5..];
        var name = "";
        var hash = raw.IndexOf('#');
        if (hash >= 0)
        {
            name = Uri.UnescapeDataString(raw[(hash + 1)..]);
            raw = raw[..hash];
        }

        var queryStart = raw.IndexOf('?');
        var authority = queryStart < 0 ? raw : raw[..queryStart];
        var query = queryStart < 0 ? "" : raw[queryStart..];
        // Legacy form encodes the entire authority. SIP002 can encode just the credentials.
        var isLegacy = false;
        if (!authority.Contains('@') && TryDecodeBase64(authority, out var legacy))
        {
            authority = legacy;
            isLegacy = true;
        }

        var at = authority.LastIndexOf('@');
        var userInfo = at < 0 ? "" : authority[..at];
        var uri = ParseUri("ss://" + authority[(at + 1)..] + query, "ss");
        if (!isLegacy)
        {
            userInfo = Uri.UnescapeDataString(userInfo);
            if (!userInfo.Contains(':') && TryDecodeBase64(userInfo, out var decoded))
            {
                userInfo = decoded;
            }
        }

        var sep = userInfo.IndexOf(':');
        if (sep <= 0)
        {
            throw new ImportException("Shadowsocks link must contain method:password.");
        }

        var method = userInfo[..sep];
        var password = userInfo[(sep + 1)..];
        var p = new Profile { Protocol = ProtocolKind.Shadowsocks, Host = Host(uri), Port = Port(uri, 8388, input) };
        p.Name = string.IsNullOrWhiteSpace(name) ? $"Shadowsocks {p.Host}" : name.Trim();
        p.Options[Keys.Method] = method.ToLowerInvariant();
        var q = Query(uri);
        if (q.TryGetValue("plugin", out var plugin))
        {
            p.Options[Keys.Plugin] = plugin;
        }

        return new ImportedProfile(p, new ProfileSecrets { Values = { [Keys.Password] = password } });
    }
}

public sealed partial class Hysteria2Parser : IProfileParser
{
    public bool CanParse(string input) =>
        input.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase);

    public ImportedProfile Parse(string input)
    {
        var text = input.Trim();
        text = "hysteria2://" + text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];

        // Normalize the authority before System.Uri sees its non-standard port list.
        string? hop = null;
        var m = PortList().Match(text);
        if (m.Success)
        {
            var ports = m.Groups["ports"];
            var ranges = Hysteria2Ports.Parse(ports.Value);
            hop = Hysteria2Ports.Additional(ranges);
            text = text.Remove(ports.Index, ports.Length).Insert(ports.Index,
                ranges[0].Start.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var uri = ParseUri(text, "hysteria2");
        var auth = Uri.UnescapeDataString(uri.UserInfo);
        var p = new Profile { Protocol = ProtocolKind.Hysteria2, Host = Host(uri), Port = Port(uri, 443, input) };
        p.Name = Fragment(uri, $"Hysteria2 {p.Host}");
        var q = Query(uri);
        Copy(q, p, ("sni", Keys.Sni), ("obfs", Keys.Obfs), ("alpn", Keys.Alpn), ("upmbps", Keys.UpMbps), ("downmbps", Keys.DownMbps), ("pinSHA256", "pinSHA256"));
        if (q.TryGetValue("insecure", out var ins) && ins is "1" or "true")
        {
            p.Options[Keys.AllowInsecure] = "true";
        }

        if (!string.IsNullOrEmpty(hop))
        {
            p.Options["ports"] = hop;
        }

        var secrets = new ProfileSecrets();
        if (!string.IsNullOrEmpty(auth))
        {
            secrets.Values[Keys.Password] = auth;
        }

        if (q.TryGetValue("obfs-password", out var op))
        {
            secrets.Values[Keys.ObfsPassword] = op;
        }

        return new ImportedProfile(p, secrets);
    }

    [GeneratedRegex(@"^hysteria2://(?:[^/?#@]*@)?(?:\[[^\]]+\]|[^:/?#@]+):(?<ports>[^/?#]+)")]
    private static partial Regex PortList();
}

public sealed class TuicParser : IProfileParser
{
    public bool CanParse(string input) => input.StartsWith("tuic://", StringComparison.OrdinalIgnoreCase);

    public ImportedProfile Parse(string input)
    {
        var uri = ParseUri(input, "tuic");
        var info = Uri.UnescapeDataString(uri.UserInfo);
        var sep = info.IndexOf(':');
        if (sep <= 0)
        {
            throw new ImportException("TUIC link must contain uuid:password.");
        }

        var p = new Profile { Protocol = ProtocolKind.Tuic, Host = Host(uri), Port = Port(uri, 443, input) };
        p.Name = Fragment(uri, $"TUIC {p.Host}");
        var q = Query(uri);
        Copy(q, p, ("sni", Keys.Sni), ("alpn", Keys.Alpn), ("congestion_control", Keys.CongestionControl), ("udp_relay_mode", Keys.UdpRelayMode));
        if (q.TryGetValue("allow_insecure", out var ai) && ai is "1" or "true" || q.TryGetValue("insecure", out var ins) && ins is "1" or "true")
        {
            p.Options[Keys.AllowInsecure] = "true";
        }

        return new ImportedProfile(p, new ProfileSecrets { Values = { [Keys.Uuid] = info[..sep], [Keys.Password] = info[(sep + 1)..] } });
    }
}

/// <summary>socks5://, socks://, http:// and https:// proxies (https = HTTP proxy over TLS).</summary>
public sealed class ProxyParser : IProfileParser
{
    private static readonly string[] Schemes = ["socks5://", "socks://", "socks5h://", "http://", "https://"];

    public bool CanParse(string input) => Schemes.Any(s => input.StartsWith(s, StringComparison.OrdinalIgnoreCase));

    public ImportedProfile Parse(string input)
    {
        var scheme = input[..input.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant();
        var uri = ParseUri(input, scheme);
        var isHttp = scheme.StartsWith("http", StringComparison.Ordinal);
        var p = new Profile
        {
            Protocol = isHttp ? ProtocolKind.Http : ProtocolKind.Socks5,
            Host = Host(uri),
            Port = Port(uri, isHttp ? (scheme == "https" ? 443 : 8080) : 1080, input),
        };
        p.Name = Fragment(uri, $"{(isHttp ? "HTTP" : "SOCKS5")} {p.Host}");
        if (scheme == "https")
        {
            p.Options[Keys.Tls] = "true";
        }

        var secrets = new ProfileSecrets();
        var info = Uri.UnescapeDataString(uri.UserInfo);
        if (!string.IsNullOrEmpty(info))
        {
            var sep = info.IndexOf(':');
            p.Options[Keys.Username] = sep >= 0 ? info[..sep] : info;
            if (sep >= 0)
            {
                secrets.Values[Keys.Password] = info[(sep + 1)..];
            }
        }

        return new ImportedProfile(p, secrets);
    }
}

