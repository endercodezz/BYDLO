using System.Text.Json;
using System.Text.Json.Nodes;
using Bydlo.Core.Models;

namespace Bydlo.Core.Import.Parsers;

/// <summary>Accepts a bounded, inline configuration. BYDLO owns all listeners and diagnostics.</summary>
internal static class CustomJsonConfig
{
    private static readonly HashSet<string> SingBoxProtocols = new(
        "vless vmess trojan shadowsocks hysteria2 tuic socks http direct anytls ssh selector urltest".Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> XrayProtocols = new(
        "vless vmess trojan shadowsocks socks http freedom blackhole".Split(' '), StringComparer.Ordinal);

    private static readonly HashSet<string> Fields = new(
        ("log inbounds outbounds route routing dns tag type protocol listen listen_port port settings " +
         "server server_port address username user password uuid method flow alter_id alterId cipher security encryption " +
         "network transport tls enabled server_name serverName insecure allowInsecure alpn utls fingerprint reality " +
         "public_key publicKey short_id shortId spiderX tlsSettings realitySettings streamSettings " +
         "wsSettings grpcSettings httpupgradeSettings xhttpSettings path host headers Host service_name serviceName mode " +
         "vnext servers users id email level userLevel domainStrategy targetStrategy " +
         "rules final action outbound outboundTag inbound inboundTag ip ip_cidr domain domain_suffix domain_keyword " +
         "domain_regex port_range ip_is_private network_strategy auto_detect_interface default_domain_resolver " +
         "detour domain_resolver strategy interval tolerance url default interrupt_exist_connections " +
         "skipFallback disableFallbackIfMatch queryStrategy query_type cache_capacity reverse_mapping " +
         "download upload level timestamp loglevel multiplex padding max_connections min_streams max_streams " +
         "mux concurrency xudpConcurrency xudpProxyUDP443 listen_port tcp_fast_open version congestion_control " +
         "udp_relay_mode up_mbps down_mbps obfs auth udp accounts pass sniffing destOverride socks5").Split(' '), StringComparer.Ordinal);

    public static ImportResult Parse(string text, string? name)
    {
        try
        {
            var root = Read(text, out var protocol);
            var label = protocol == ProtocolKind.SingBoxConfig ? "sing-box JSON" : "Xray JSON";
            var profile = new Profile
            {
                Name = string.IsNullOrWhiteSpace(name) ? label : name,
                Protocol = protocol,
                PreferredEngine = protocol == ProtocolKind.SingBoxConfig ? EngineKind.SingBox : EngineKind.Xray,
            };
            var secrets = new ProfileSecrets { Values = { [Keys.RawConfig] = root.ToJsonString() } };
            var index = 0;
            foreach (var credential in Credentials(root).Distinct(StringComparer.Ordinal))
            {
                secrets.Values["json-credential-" + index++] = credential;
            }
            return new([new(profile, secrets)], []);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return new([], ["import.json.unsafe"]);
        }
    }

    public static JsonObject Read(string text, ProtocolKind expected)
    {
        var root = Read(text, out var protocol);
        return protocol == expected ? root : throw new FormatException("Unexpected custom config engine.");
    }

    private static JsonObject Read(string text, out ProtocolKind protocol)
    {
        if (text.Length > 2 * 1024 * 1024)
        {
            throw new FormatException();
        }
        var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject
            ?? throw new FormatException();
        if (root["outbounds"] is not JsonArray { Count: > 0 and <= 128 } outbounds || outbounds[0] is not JsonObject first)
        {
            throw new FormatException();
        }
        var singBox = first.ContainsKey("type");
        protocol = singBox ? ProtocolKind.SingBoxConfig : ProtocolKind.XrayConfig;
        var allowed = singBox ? SingBoxProtocols : XrayProtocols;
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in outbounds)
        {
            if (node is not JsonObject outbound ||
                (string?)outbound[singBox ? "type" : "protocol"] is not { } kind || !allowed.Contains(kind)
                || (string?)outbound["tag"] is not { Length: > 0 and <= 128 } tag || !tags.Add(tag))
            {
                throw new FormatException();
            }
        }
        if (root.Any(pair => pair.Key is not ("log" or "inbounds" or "outbounds" or "dns" or "route" or "routing")) ||
            (singBox ? root.ContainsKey("routing") : root.ContainsKey("route")))
        {
            throw new FormatException();
        }
        foreach (var section in new[] { "log", "dns", "route", "routing" })
        {
            if (root.ContainsKey(section) && root[section] is not JsonObject)
            {
                throw new FormatException();
            }
        }
        if (root.ContainsKey("inbounds") && root["inbounds"] is not JsonArray)
        {
            throw new FormatException();
        }
        var route = root[singBox ? "route" : "routing"] as JsonObject;
        if (route is not null)
        {
            if (route.ContainsKey("rules") && route["rules"] is not JsonArray)
            {
                throw new FormatException();
            }
            if (route["rules"] is JsonArray rules)
            {
                foreach (var node in rules)
                {
                    if (node is not JsonObject rule)
                    {
                        throw new FormatException();
                    }
                    CheckReference(rule, singBox ? "outbound" : "outboundTag", tags);
                }
            }
            if (singBox) { CheckReference(route, "final", tags); }
        }
        foreach (var outbound in outbounds.OfType<JsonObject>())
        {
            ValidateOutbound(outbound, singBox);
            CheckReference(outbound, "detour", tags);
            if (singBox && (string?)outbound["type"] is "selector" or "urltest")
            {
                if (outbound["outbounds"] is not JsonArray { Count: > 0 } members ||
                    members.Any(member => member is not JsonValue scalar || !scalar.TryGetValue<string>(out var tag) || !tags.Contains(tag)))
                {
                    throw new FormatException();
                }
                CheckReference(outbound, "default", tags);
            }
        }
        if (root["dns"] is JsonObject dns && dns.ContainsKey("servers") && dns["servers"] is not JsonArray)
        {
            throw new FormatException();
        }
        if (root["dns"]?["servers"] is JsonArray servers && singBox &&
            servers.Any(server => (string?)server?["type"] is not ("local" or "udp" or "tcp" or "https" or "tls" or "quic" or "fakeip")))
        {
            throw new FormatException();
        }
        Validate(root, null);
        return root;
    }

    private static void ValidateOutbound(JsonObject outbound, bool singBox)
    {
        var kind = (string)outbound[singBox ? "type" : "protocol"]!;
        if (kind is "direct" or "selector" or "urltest" or "freedom" or "blackhole") { return; }

        if (singBox)
        {
            Endpoint(outbound, "server", "server_port");
            if (kind is "vless" or "vmess" or "tuic") { RequiredString(outbound, "uuid"); }
            if (kind is "trojan" or "shadowsocks" or "hysteria2" or "tuic" or "anytls")
            { RequiredString(outbound, "password"); }
            if (kind == "shadowsocks") { RequiredString(outbound, "method"); }
            foreach (var key in new[] { "tls", "transport", "multiplex", "obfs" })
            {
                if (outbound.ContainsKey(key) && outbound[key] is not JsonObject) { throw new FormatException(); }
            }
            return;
        }

        if (outbound["settings"] is not JsonObject settings) { throw new FormatException(); }
        var vnext = kind is "vless" or "vmess";
        if (settings[vnext ? "vnext" : "servers"] is not JsonArray { Count: > 0 } servers)
        { throw new FormatException(); }
        foreach (var item in servers)
        {
            if (item is not JsonObject server) { throw new FormatException(); }
            Endpoint(server, "address", "port");
            if (kind is "trojan" or "shadowsocks") { RequiredString(server, "password"); }
            if (kind == "shadowsocks") { RequiredString(server, "method"); }
            if (vnext)
            {
                if (server["users"] is not JsonArray { Count: > 0 } users) { throw new FormatException(); }
                foreach (var user in users)
                {
                    if (user is not JsonObject account) { throw new FormatException(); }
                    RequiredString(account, "id");
                }
            }
        }
        if (outbound.ContainsKey("streamSettings") && outbound["streamSettings"] is not JsonObject)
        { throw new FormatException(); }
    }

    private static void Endpoint(JsonObject source, string hostKey, string portKey)
    {
        RequiredString(source, hostKey);
        if (source[portKey] is not JsonValue value || !value.TryGetValue<int>(out var port) || port is < 1 or > 65535)
        { throw new FormatException(); }
    }

    private static void RequiredString(JsonObject source, string key)
    {
        if (source[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
        { throw new FormatException(); }
    }

    private static void CheckReference(JsonObject source, string key, HashSet<string> tags)
    {
        if (source.ContainsKey(key) && (source[key] is not JsonValue scalar ||
            !scalar.TryGetValue<string>(out var tag) || !tags.Contains(tag)))
        {
            throw new FormatException();
        }
    }

    private static IEnumerable<string> Credentials(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                if (key is "password" or "pass" or "uuid" or "id" or "user" or "username" &&
                    value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length > 0)
                {
                    yield return text;
                }
                foreach (var child in Credentials(value)) { yield return child; }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                foreach (var child in Credentials(value)) { yield return child; }
            }
        }
    }

    private static void Validate(JsonNode? node, string? parent)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (!Fields.Contains(key) ||
                        (key == "path" && parent is not ("transport" or "wsSettings" or "xhttpSettings" or "httpupgradeSettings")))
                    {
                        throw new FormatException();
                    }
                    Validate(value, key);
                }
                break;
            case JsonArray array:
                foreach (var child in array) { Validate(child, parent); }
                break;
            case JsonValue scalar when scalar.TryGetValue<string>(out var value) &&
                (value.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("ext-ip:", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)):
                throw new FormatException();
        }
    }
}
