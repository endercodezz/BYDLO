using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using Bydlo.Core.Models;

namespace Bydlo.Core.Import.Parsers;

internal static class ClashYamlParser
{
    public static bool CanParse(string text) => text.Split('\n').Any(line =>
        line.TrimStart().StartsWith("proxies:", StringComparison.Ordinal));

    public static ImportResult Parse(string text)
    {
        if (text.Length > 5 * 1024 * 1024)
        {
            return new([], ["import.clash.invalid"]);
        }

        try
        {
            using var input = new StringReader(text);
            var parser = new Parser(input);
            var depth = 0;
            var count = 0;
            while (parser.MoveNext())
            {
                if (++count > 100000 || parser.Current is AnchorAlias)
                {
                    return new([], ["import.clash.invalid"]);
                }
                if (parser.Current is MappingStart or SequenceStart && ++depth > 32)
                {
                    return new([], ["import.clash.invalid"]);
                }
                if (parser.Current is MappingEnd or SequenceEnd) { depth--; }
            }

            using var source = new StringReader(text);
            var yaml = new YamlStream();
            yaml.Load(source);
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root
                || Node(root, "proxies") is not YamlSequenceNode proxies || proxies.Children.Count > 5000)
            {
                return new([], ["import.clash.invalid"]);
            }
            var imported = new List<ImportedProfile>();
            var errors = new List<string>();
            for (var i = 0; i < proxies.Children.Count; i++)
            {
                try
                {
                    if (proxies.Children[i] is not YamlMappingNode proxy)
                    {
                        throw new FormatException();
                    }
                    imported.Add(ParseProxy(proxy));
                }
                catch (FormatException)
                {
                    errors.Add("import.clash.skipped|" + (i + 1).ToString(CultureInfo.InvariantCulture));
                }
            }
            return new(imported, errors);
        }
        catch (YamlException)
        {
            return new([], ["import.clash.invalid"]);
        }
    }

    private static ImportedProfile ParseProxy(YamlMappingNode proxy)
    {
        var protocol = Scalar(proxy, "type") switch
        {
            "vless" => ProtocolKind.Vless,
            "vmess" => ProtocolKind.Vmess,
            "trojan" => ProtocolKind.Trojan,
            "ss" => ProtocolKind.Shadowsocks,
            "hysteria2" or "hy2" => ProtocolKind.Hysteria2,
            "tuic" => ProtocolKind.Tuic,
            _ => throw new FormatException(),
        };
        var host = Scalar(proxy, "server");
        var network = Scalar(proxy, "network") ?? "tcp";
        if (host is null || Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            !int.TryParse(Scalar(proxy, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535 || Node(proxy, "dialer-proxy") is not null
            || network is not ("tcp" or "ws" or "grpc" or "http" or "h2" or "httpupgrade" or "xhttp"))
        {
            throw new FormatException();
        }
        var profile = new Profile { Protocol = protocol, Name = Scalar(proxy, "name") ?? host, Host = host, Port = port };
        var secrets = new ProfileSecrets();
        if (protocol is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Tuic)
        {
            secrets.Values[Keys.Uuid] = Required("uuid");
        }
        if (protocol is ProtocolKind.Trojan or ProtocolKind.Shadowsocks or ProtocolKind.Hysteria2 or ProtocolKind.Tuic)
        {
            secrets.Values[Keys.Password] = Required("password");
        }

        if (protocol == ProtocolKind.Shadowsocks)
        {
            profile.Options[Keys.Method] = Required("cipher");
            if (Node(proxy, "plugin") is not null) { throw new FormatException(); }
        }
        if (protocol == ProtocolKind.Vmess)
        {
            Copy(proxy, "cipher", Keys.Method);
            Copy(proxy, "alterId", "alterId");
        }

        profile.Options[Keys.Transport] = network;
        var security = bool.TryParse(Scalar(proxy, "tls"), out var tls) && tls || protocol is ProtocolKind.Trojan or ProtocolKind.Hysteria2 or ProtocolKind.Tuic
            ? "tls" : "none";
        if (Node(proxy, "reality-opts") is YamlMappingNode reality)
        {
            security = "reality";
            Copy(reality, "public-key", Keys.RealityPublicKey);
            Copy(reality, "short-id", Keys.RealityShortId);
        }
        profile.Options[Keys.Security] = security;
        Copy(proxy, "servername", Keys.Sni);
        Copy(proxy, "sni", Keys.Sni);
        Copy(proxy, "client-fingerprint", Keys.Fingerprint);
        if (Scalar(proxy, "skip-cert-verify") is { } insecure)
        {
            if (!bool.TryParse(insecure, out var enabled)) { throw new FormatException(); }
            profile.Options[Keys.AllowInsecure] = enabled ? "true" : "false";
        }
        Copy(proxy, "flow", Keys.Flow);
        Copy(proxy, "obfs", Keys.Obfs);
        Copy(proxy, "congestion-controller", Keys.CongestionControl);
        Copy(proxy, "udp-relay-mode", Keys.UdpRelayMode);
        if (protocol == ProtocolKind.Hysteria2)
        {
            CopyBandwidth("up", Keys.UpMbps);
            CopyBandwidth("down", Keys.DownMbps);
        }
        if (Scalar(proxy, "obfs-password") is { } obfs)
        {
            secrets.Values[Keys.ObfsPassword] = obfs;
        }
        if (Node(proxy, "alpn") is YamlSequenceNode alpn)
        {
            profile.Options[Keys.Alpn] = string.Join(',', alpn.Children.OfType<YamlScalarNode>().Select(item => item.Value));
        }
        if (Node(proxy, network + "-opts") is YamlMappingNode transport)
        {
            Copy(transport, "path", Keys.Path);
            Copy(transport, "host", Keys.HostHeader);
            if (network is "h2" or "http" && Node(transport, "host") is YamlSequenceNode hosts)
            {
                if (hosts.Children.Any(item => item is not YamlScalarNode { Value.Length: > 0 }))
                { throw new FormatException(); }
                profile.Options[Keys.HostHeader] = string.Join(',', hosts.Children.Cast<YamlScalarNode>().Select(item => item.Value));
            }
            Copy(transport, "grpc-service-name", Keys.ServiceName);
            if (Node(transport, "headers") is YamlMappingNode headers)
            {
                Copy(headers, "Host", Keys.HostHeader);
            }
        }
        return new(profile, secrets);

        string Required(string key) => Scalar(proxy, key) is { Length: > 0 } value ? value : throw new FormatException();
        void CopyBandwidth(string key, string target)
        {
            if (Scalar(proxy, key) is not { } value) { return; }
            value = value.Trim();
            if (value.EndsWith("Mbps", StringComparison.OrdinalIgnoreCase)) { value = value[..^4].TrimEnd(); }
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var mbps) || mbps <= 0)
            { throw new FormatException(); }
            profile.Options[target] = mbps.ToString(CultureInfo.InvariantCulture);
        }
        void Copy(YamlMappingNode source, string key, string target)
        {
            if (Scalar(source, key) is { Length: > 0 } value)
            {
                profile.Options[target] = value;
            }
        }
    }

    private static YamlNode? Node(YamlMappingNode source, string key) =>
        source.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? Scalar(YamlMappingNode source, string key) => (Node(source, key) as YamlScalarNode)?.Value;
}
