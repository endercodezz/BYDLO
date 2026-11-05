using System.Text;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public class ImportTests
{
    [Theory]
    [InlineData("anytls://secret%3Avalue@example.com:443?sni=tls.example&alpn=h2&insecure=1#Any", ProtocolKind.AnyTls)]
    [InlineData("shadowtls://shadowsecret@example.com:443?version=3&method=aes-256-gcm&ss_password=sssecret&sni=tls.example#Shadow", ProtocolKind.ShadowTls)]
    [InlineData("naive+quic://user:secret@example.com:443?sni=tls.example#Naive", ProtocolKind.Naive)]
    [InlineData("ssh://user:secret@example.com:22?private_key=private%20key&private_key_passphrase=keypass&host_key=ssh-ed25519%20AAAA#SSH", ProtocolKind.Ssh)]
    public void New_protocols_round_trip_without_exposing_credentials(string link, ProtocolKind protocol)
    {
        var first = One(link);
        Assert.Equal(protocol, first.Profile.Protocol);
        var second = One(ProfileExporter.ExportLink(first.Profile, first.Secrets));
        Assert.Equal(first.Profile.Name, second.Profile.Name);
        Assert.Equal(first.Profile.Host, second.Profile.Host);
        Assert.Equal(first.Profile.Port, second.Profile.Port);
        Assert.Equal(first.Profile.Options.OrderBy(x => x.Key), second.Profile.Options.OrderBy(x => x.Key));
        Assert.Equal(first.Secrets.Values.OrderBy(x => x.Key), second.Secrets.Values.OrderBy(x => x.Key));
        foreach (var secret in first.Secrets.Values.Values)
            Assert.DoesNotContain(first.Profile.Options.Values, value => value.Contains(secret, StringComparison.Ordinal));
        var config = new Bydlo.Core.Config.SingBoxConfigBuilder().Build(first.Profile, first.Secrets, new(new AppSettings()));
        var outbound = config["outbounds"]!.AsArray()[0]!;
        Assert.Equal(protocol == ProtocolKind.ShadowTls ? "shadowsocks" : protocol.ToString().ToLowerInvariant(), outbound["type"]!.GetValue<string>());
        if (protocol == ProtocolKind.ShadowTls)
            Assert.Equal("shadowtls", outbound["detour"]!.GetValue<string>());
    }

    [Fact]
    public void New_link_secrets_are_redacted_from_logs_and_import_errors()
    {
        var link = "shadowtls://shadowsecret@example.com:443?ss_password=sssecret&method=aes-256-gcm";
        var redacted = new Bydlo.Core.Security.LogRedactor().Redact(link);
        Assert.DoesNotContain("shadowsecret", redacted);
        Assert.DoesNotContain("sssecret", redacted);
        var invalid = _importer.ImportText("shadowtls://shadowsecret@example.com:443?ss_password=sssecret");
        Assert.DoesNotContain("shadowsecret", string.Join(' ', invalid.Errors));
        Assert.DoesNotContain("sssecret", string.Join(' ', invalid.Errors));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"text\"")]
    public void Vmess_non_object_does_not_abort_mixed_import(string json)
    {
        var link = "vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        var result = _importer.ImportText("vless://11111111-2222-3333-4444-555555555555@example.test:443#Good\n" + link);
        Assert.Equal("Good", Assert.Single(result.Profiles).Profile.Name);
        Assert.Single(result.Errors);
    }

    private readonly ProfileImporter _importer = new();

    private ImportedProfile One(string text)
    {
        var r = _importer.ImportText(text);
        Assert.Empty(r.Errors);
        return Assert.Single(r.Profiles);
    }

    [Fact]
    public void Vless_reality_link_is_parsed_and_uuid_is_secret()
    {
        var p = One("vless://11111111-2222-3333-4444-555555555555@example.com:443?encryption=none&security=reality&sni=www.microsoft.com&fp=chrome&pbk=PUBKEY123&sid=ab12&type=tcp&flow=xtls-rprx-vision#My%20Server");
        Assert.Equal(ProtocolKind.Vless, p.Profile.Protocol);
        Assert.Equal("My Server", p.Profile.Name);
        Assert.Equal("example.com", p.Profile.Host);
        Assert.Equal(443, p.Profile.Port);
        Assert.Equal("reality", p.Profile.Option(Keys.Security));
        Assert.Equal("PUBKEY123", p.Profile.Option(Keys.RealityPublicKey));
        Assert.Equal("xtls-rprx-vision", p.Profile.Option(Keys.Flow));
        Assert.Equal("11111111-2222-3333-4444-555555555555", p.Secrets[Keys.Uuid]);
        Assert.DoesNotContain(p.Profile.Options.Values, v => v.Contains("11111111", StringComparison.Ordinal));
    }

    [Fact]
    public void Vless_reality_without_public_key_is_rejected()
    {
        var r = _importer.ImportText("vless://id@example.com:443?security=reality&sni=a.com");
        Assert.Empty(r.Profiles);
        Assert.Single(r.Errors);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("?fp=chrome#fixture", "chrome")]
    public void Vmess_base64_json_is_parsed(string suffix, string? fingerprint)
    {
        var json = """{"v":"2","ps":"VM","add":"vm.example.com","port":"8443","id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","aid":"0","scy":"auto","net":"ws","type":"none","host":"cdn.example.com","path":"/ws","tls":"tls","sni":"vm.example.com"}""";
        var p = One("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + suffix);
        Assert.Equal(ProtocolKind.Vmess, p.Profile.Protocol);
        Assert.Equal(8443, p.Profile.Port);
        Assert.Equal(fingerprint, p.Profile.Option(Keys.Fingerprint));
        Assert.Equal("ws", p.Profile.Option(Keys.Transport));
        Assert.Equal("/ws", p.Profile.Option(Keys.Path));
        Assert.Equal("tls", p.Profile.Option(Keys.Security));
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", p.Secrets[Keys.Uuid]);
    }

    [Fact]
    public void Trojan_defaults_to_tls()
    {
        var p = One("trojan://s3cret@tr.example.com:443?sni=tr.example.com#TR");
        Assert.Equal(ProtocolKind.Trojan, p.Profile.Protocol);
        Assert.Equal("tls", p.Profile.Option(Keys.Security));
        Assert.Equal("s3cret", p.Secrets[Keys.Password]);
    }

    [Fact]
    public void Shadowsocks_sip002_and_legacy_forms_are_parsed()
    {
        var userinfo = Convert.ToBase64String(Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:pass word")).TrimEnd('=');
        var sip = One($"ss://{userinfo}@ss.example.com:8388#SS");
        Assert.Equal("chacha20-ietf-poly1305", sip.Profile.Option(Keys.Method));
        Assert.Equal("pass word", sip.Secrets[Keys.Password]);

        var legacy = One("ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pw@1.2.3.4:8000")) + "?plugin=obfs-local%3Bobfs%3Dhttp#Old");
        Assert.Equal("1.2.3.4", legacy.Profile.Host);
        Assert.Equal(8000, legacy.Profile.Port);
        Assert.Equal("pw", legacy.Secrets[Keys.Password]);
        Assert.Equal("Old", legacy.Profile.Name);
        Assert.Equal("obfs-local;obfs=http", legacy.Profile.Option(Keys.Plugin));

        var standardBase64 = One("ss://YWVzLTI1Ni1nY206cHcxMDA/Pg==@203.0.113.5:8388#Standard");
        Assert.Equal("203.0.113.5", standardBase64.Profile.Host);
        Assert.Equal("pw100?>", standardBase64.Secrets[Keys.Password]);
    }

    [Fact]
    public void Hysteria2_with_port_hopping_and_obfs()
    {
        var p = One("hy2://authpass@hy.example.com:443,20000-30000/?sni=hy.example.com&obfs=salamander&obfs-password=obfspw&insecure=1#HY");
        Assert.Equal(ProtocolKind.Hysteria2, p.Profile.Protocol);
        Assert.Equal(443, p.Profile.Port);
        Assert.Equal("20000-30000", p.Profile.Option("ports"));
        Assert.Equal("salamander", p.Profile.Option(Keys.Obfs));
        Assert.Equal("true", p.Profile.Option(Keys.AllowInsecure));
        Assert.Equal("authpass", p.Secrets[Keys.Password]);
        Assert.Equal("obfspw", p.Secrets[Keys.ObfsPassword]);
    }

    [Fact]
    public void Tuic_link_is_parsed()
    {
        var p = One("tuic://uuid-1:pw@tuic.example.com:443?congestion_control=bbr&alpn=h3&sni=tuic.example.com#T");
        Assert.Equal(ProtocolKind.Tuic, p.Profile.Protocol);
        Assert.Equal("uuid-1", p.Secrets[Keys.Uuid]);
        Assert.Equal("pw", p.Secrets[Keys.Password]);
        Assert.Equal("bbr", p.Profile.Option(Keys.CongestionControl));
    }

    [Fact]
    public void Socks_with_credentials()
    {
        var p = One("socks5://user:pa%40ss@10.0.0.1:1080#S");
        Assert.Equal(ProtocolKind.Socks5, p.Profile.Protocol);
        Assert.Equal("user", p.Profile.Option(Keys.Username));
        Assert.Equal("pa@ss", p.Secrets[Keys.Password]);
    }

    private const string WireGuardConf = """
        [Interface]
        PrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=
        Address = 10.8.0.2/32, fd00::2/128
        DNS = 1.1.1.1
        MTU = 1420

        [Peer]
        PublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=
        PresharedKey = /UwcSPg38hW/D9Y3tcS1FOV0K1wuURMbS0sesJEP5ak=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = wg.example.com:51820
        PersistentKeepalive = 25
        """;

    [Fact]
    public void WireGuard_config_is_detected()
    {
        var p = One(WireGuardConf);
        Assert.Equal(ProtocolKind.WireGuard, p.Profile.Protocol);
        Assert.Equal("wg.example.com", p.Profile.Host);
        Assert.Equal(51820, p.Profile.Port);
        Assert.NotNull(p.Secrets[Keys.RawConfig]);
        Assert.DoesNotContain(p.Profile.Options.Values, v => v.Contains("yAnz5TF", StringComparison.Ordinal));
    }

    [Fact]
    public void AmneziaWG_config_is_detected_by_obfuscation_keys()
    {
        var conf = WireGuardConf.Replace("MTU = 1420", "MTU = 1420\nJc = 4\nJmin = 40\nJmax = 70\nS1 = 0\nS2 = 0\nH1 = 1\nH2 = 2\nH3 = 3\nH4 = 4", StringComparison.Ordinal);
        var p = One(conf);
        Assert.Equal(ProtocolKind.AmneziaWG, p.Profile.Protocol);
        Assert.Equal("4", p.Profile.Option("awg.Jc"));
    }

    [Fact]
    public void OpenVpn_config_is_detected()
    {
        var p = One("client\ndev tun\nproto udp\nremote vpn.example.com 1195 udp\nauth-user-pass\n<ca>\n-----BEGIN CERTIFICATE-----\n</ca>\n");
        Assert.Equal(ProtocolKind.OpenVpn, p.Profile.Protocol);
        Assert.Equal("vpn.example.com", p.Profile.Host);
        Assert.Equal(1195, p.Profile.Port);
        Assert.Equal("true", p.Profile.Option("authUserPass"));
    }

    [Fact]
    public void Base64_subscription_with_mixed_lines()
    {
        var body = "trojan://a@t.example.com:443#1\nvless://id@v.example.com:443?security=tls#2\nunknown://x\n";
        var r = _importer.ImportText(Convert.ToBase64String(Encoding.UTF8.GetBytes(body)));
        Assert.Equal(2, r.Profiles.Count);
        Assert.Single(r.Errors);
    }

    [Fact]
    public void Errors_never_echo_credentials()
    {
        var r = _importer.ImportText("vless://supersecretuuid@example.com:443?security=reality");
        Assert.All(r.Errors, e => Assert.DoesNotContain("supersecretuuid", e, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://sub.example.com/api/v1/client/subscribe?token=abc", true)]
    [InlineData("https://proxy.example.com:8080", false)]
    [InlineData("vless://a@b:1", false)]
    public void Subscription_url_detection(string text, bool expected) =>
        Assert.Equal(expected, ProfileImporter.LooksLikeSubscriptionUrl(text));

    [Fact]
    public async Task Subscription_client_parses_body_without_real_network()
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("trojan://a@t.example.com:443#1"));
        using var http = new HttpClient(new StubHandler(body));
        var client = new SubscriptionClient(http, _importer);
        var r = await client.FetchAsync("https://sub.example.invalid/s");
        Assert.Single(r.Profiles);
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

