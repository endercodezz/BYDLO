using System.Text.Json;
using Bydlo.Core.Config;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public sealed class ConfigImportTests
{
    [Fact]
    public async Task Custom_json_round_trips_through_secret_storage_without_leaking_to_profiles_file()
    {
        var file = Path.Combine(TestDirs.New("custom-json-secrets"), "profiles.json");
        var secrets = new Bydlo.Core.Security.InMemorySecretStore();
        var store = new Bydlo.Core.Storage.ProfileStore(file, secrets);
        var imported = Assert.Single(new ProfileImporter().ImportText("""
            {"outbounds":[{"type":"trojan","tag":"remote","server":"proxy.invalid","server_port":443,"password":"unique-private-password"}]}
            """).Profiles);
        await store.AddSubscriptionWithProfilesAsync("Custom", "https://subscription.invalid/list", [imported]);
        Assert.DoesNotContain("unique-private-password", await File.ReadAllTextAsync(file));
        await store.LoadAsync();
        var profile = Assert.Single(store.Profiles);
        var restored = await secrets.LoadAsync(profile.Id.ToString());
        var config = new SingBoxConfigBuilder().Build(profile, restored, new BuildContext(new AppSettings()));
        Assert.Equal("unique-private-password", (string?)config["outbounds"]![0]!["password"]);
    }

    [Theory]
    [InlineData("proxies: [ &proxy {type: trojan, server: proxy.invalid, port: 443, password: secret}, *proxy ]")]
    [InlineData("proxies: {type: trojan}")]
    public void Rejects_yaml_aliases_and_invalid_proxy_collections(string yaml)
    {
        var result = new ProfileImporter().ImportText(yaml);
        Assert.Empty(result.Profiles);
        Assert.Equal("import.clash.invalid", Assert.Single(result.Errors));
    }

    [Fact]
    public void Preserves_clash_h2_hosts_and_hysteria_bandwidth_in_generated_config()
    {
        var result = new ProfileImporter().ImportText("""
            proxies:
              - name: H2
                type: vless
                server: proxy.invalid
                port: 443
                uuid: id
                tls: True
                skip-cert-verify: True
                network: h2
                h2-opts:
                  path: /relay
                  host: [one.invalid, two.invalid]
              - {type: hy2, server: proxy.invalid, port: 443, password: secret, up: 50 Mbps, down: 100}
            """);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Profiles.Count);
        var first = result.Profiles[0];
        var config = new SingBoxConfigBuilder().Build(first.Profile, first.Secrets, new BuildContext(new AppSettings()));
        Assert.True((bool)config["outbounds"]![0]!["tls"]!["enabled"]!);
        Assert.True((bool)config["outbounds"]![0]!["tls"]!["insecure"]!);
        Assert.Equal("two.invalid", (string?)config["outbounds"]![0]!["transport"]!["host"]![1]);
        var second = result.Profiles[1];
        config = new SingBoxConfigBuilder().Build(second.Profile, second.Secrets, new BuildContext(new AppSettings()));
        Assert.Equal(50, (int?)config["outbounds"]![0]!["up_mbps"]);
        Assert.Equal(100, (int?)config["outbounds"]![0]!["down_mbps"]);
    }

    [Theory]
    [InlineData("{\"type\":\"trojan\",\"tag\":\"proxy\"}")]
    [InlineData("{\"type\":\"trojan\",\"tag\":\"proxy\",\"server\":\"host\",\"server_port\":443}")]
    [InlineData("{\"type\":\"trojan\",\"tag\":\"proxy\",\"server\":\"host\",\"server_port\":65536,\"password\":\"secret\"}")]
    [InlineData("{\"type\":\"trojan\",\"tag\":\"proxy\",\"server\":\"host\",\"server_port\":\"443\",\"password\":\"secret\"}")]
    [InlineData("{\"type\":\"trojan\",\"tag\":\"proxy\",\"server\":\"host\",\"server_port\":443,\"password\":\"secret\",\"tls\":true}")]
    [InlineData("{\"protocol\":\"trojan\",\"tag\":\"proxy\",\"settings\":{\"servers\":[{\"address\":\"host\",\"port\":0,\"password\":\"secret\"}]}}")]
    [InlineData("{\"protocol\":\"vless\",\"tag\":\"proxy\",\"settings\":{\"vnext\":[{\"address\":\"host\",\"port\":443,\"users\":[]}]}}")]
    public void Rejects_incomplete_or_malformed_outbounds_before_preview(string outbound)
    {
        var result = new ProfileImporter().ImportText("{\"outbounds\":[" + outbound + "]}");
        Assert.Empty(result.Profiles);
        Assert.Equal("import.json.unsafe", Assert.Single(result.Errors));
    }

    [Fact]
    public void Custom_sing_box_tun_uses_owned_listener_and_authenticated_loopback_api()
    {
        var imported = Assert.Single(new ProfileImporter().ImportText("""
            {"inbounds":[{"type":"mixed","listen":"0.0.0.0","listen_port":9999}],
             "outbounds":[{"type":"direct","tag":"direct"}]}
            """).Profiles);
        var settings = new AppSettings { Mode = ConnectionMode.Tun };
        var config = new SingBoxConfigBuilder().Build(imported.Profile, imported.Secrets, new BuildContext(settings, 3132, "local-secret"));
        Assert.Equal("tun", (string?)config["inbounds"]![0]!["type"]);
        Assert.DoesNotContain("0.0.0.0", config.ToJsonString());
        Assert.Equal("127.0.0.1:3132", (string?)config["experimental"]!["clash_api"]!["external_controller"]);
        Assert.Equal("local-secret", (string?)config["experimental"]!["clash_api"]!["secret"]);
        Assert.Contains(config["route"]!["rules"]!.AsArray(), rule => (string?)rule?["action"] == "hijack-dns");
    }

    [Fact]
    public void Parses_mixed_clash_proxies_and_reports_skipped_rows_without_credentials()
    {
        var result = new ProfileImporter().ImportText("""
            proxies:
              - { name: One, type: vless, server: one.invalid, port: 443, uuid: id, tls: true }
              - { name: Two, type: vmess, server: two.invalid, port: 443, uuid: id }
              - { name: Three, type: trojan, server: three.invalid, port: 443, password: pass }
              - { name: Four, type: ss, server: four.invalid, port: 443, cipher: aes-128-gcm, password: pass }
              - { name: Five, type: hysteria2, server: five.invalid, port: 443, password: pass }
              - { name: Six, type: tuic, server: six.invalid, port: 443, uuid: id, password: pass }
              - { name: Secret, type: invalid, password: skipped-secret }
            """);

        Assert.Equal(6, result.Profiles.Count);
        Assert.Equal("import.clash.skipped|7", Assert.Single(result.Errors));
        Assert.All(result.Profiles, item => Assert.DoesNotContain("pass", JsonSerializer.Serialize(item.Profile)));
    }

    [Theory]
    [InlineData("type", "sing-box", ProtocolKind.SingBoxConfig)]
    [InlineData("protocol", "xray", ProtocolKind.XrayConfig)]
    public void Imports_custom_outbounds_but_substitutes_the_local_listeners(string key, string engine, ProtocolKind protocol)
    {
        var raw = engine == "sing-box"
            ? """{"inbounds":[{"type":"mixed","listen":"0.0.0.0","listen_port":2222}],"outbounds":[{"type":"trojan","tag":"remote","server":"proxy.invalid","server_port":443,"password":"hidden"}]}"""
            : """{"inbounds":[{"protocol":"http","listen":"0.0.0.0","port":2222}],"outbounds":[{"protocol":"trojan","tag":"remote","settings":{"servers":[{"address":"proxy.invalid","port":443,"password":"hidden"}]}}]}""";
        var imported = Assert.Single(new ProfileImporter().ImportText(raw).Profiles);
        Assert.Equal(protocol, imported.Profile.Protocol);
        Assert.DoesNotContain("hidden", JsonSerializer.Serialize(imported.Profile));
        Assert.Contains("hidden", imported.Secrets[Keys.RawConfig]);
        var settings = new AppSettings { LocalProxyPort = 3131 };
        var context = new BuildContext(settings, 3132, "loopback-token");
        var config = protocol == ProtocolKind.SingBoxConfig
            ? new SingBoxConfigBuilder().Build(imported.Profile, imported.Secrets, context)
            : new XrayConfigBuilder().Build(imported.Profile, imported.Secrets, context);
        Assert.Equal("127.0.0.1", (string?)config["inbounds"]![0]!["listen"]);
        Assert.Equal(3131, (int?)config["inbounds"]![0]![key == "type" ? "listen_port" : "port"]);
        Assert.Equal("remote", (string?)config["outbounds"]![0]!["tag"]);
    }

    [Theory]
    [InlineData("""{"outbounds":[{"type":"direct","tag":"direct"}],"experimental":{"clash_api":{"external_controller":"0.0.0.0:9090"}}}""")]
    [InlineData("""{"outbounds":[{"type":"trojan","tag":"remote","tls":{"certificate_path":"/private/cert"}}]}""")]
    [InlineData("""{"outbounds":[{"protocol":"freedom","tag":"direct"}],"routing":{"rules":[{"domain":["ext:/private/geosite.dat:tag"]}]}}""")]
    public void Rejects_external_files_and_supplied_control_apis(string raw)
    {
        var result = new ProfileImporter().ImportText(raw);
        Assert.Empty(result.Profiles);
        Assert.Equal("import.json.unsafe", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("\"dns\":[]")]
    [InlineData("\"dns\":{\"servers\":{}}")]
    [InlineData("\"inbounds\":{}")]
    [InlineData("\"route\":{\"rules\":{}}")]
    [InlineData("\"route\":{\"rules\":[null]}")]
    [InlineData("\"route\":{\"final\":\"missing\"}")]
    [InlineData("\"route\":{\"rules\":[{\"outbound\":\"missing\"}]}")]
    public void Rejects_invalid_section_shapes_and_unknown_route_targets(string section)
    {
        var raw = "{\"outbounds\":[{\"type\":\"direct\",\"tag\":\"direct\"}]," + section + "}";
        var result = new ProfileImporter().ImportText(raw);
        Assert.Empty(result.Profiles);
        Assert.Equal("import.json.unsafe", Assert.Single(result.Errors));
    }

    [Theory]
    [InlineData("""{"type":"selector","tag":"proxy","outbounds":["missing"]}""")]
    [InlineData("""{"type":"urltest","tag":"proxy","outbounds":[]}""")]
    [InlineData("""{"type":"trojan","tag":"proxy","detour":"missing"}""")]
    public void Rejects_unknown_outbound_members_and_detours(string outbound)
    {
        var result = new ProfileImporter().ImportText("{\"outbounds\":[" + outbound + "]}");
        Assert.Empty(result.Profiles);
    }

    [Fact]
    public void Preserves_valid_selector_members_and_custom_route_targets()
    {
        var result = new ProfileImporter().ImportText("""
            {"outbounds":[
                {"type":"selector","tag":"proxy","outbounds":["remote"],"default":"remote"},
                {"type":"trojan","tag":"remote","server":"proxy.invalid","server_port":443,"password":"hidden"}],
             "route":{"final":"proxy","rules":[{"domain":["example.invalid"],"outbound":"remote"}]}}
            """);
        var imported = Assert.Single(result.Profiles);
        var config = new SingBoxConfigBuilder().Build(imported.Profile, imported.Secrets, new BuildContext(new AppSettings()));
        Assert.Equal("proxy", (string?)config["route"]!["final"]);
        Assert.Equal("remote", (string?)config["outbounds"]![0]!["outbounds"]![0]);
    }

    [Theory]
    [InlineData("\"routing\":{\"rules\":[{\"type\":\"field\",\"outboundTag\":\"missing\"}]}")]
    [InlineData("\"routing\":{\"rules\":{}}")]
    [InlineData("\"dns\":[]")]
    public void Rejects_invalid_xray_sections_and_route_targets(string section)
    {
        var raw = "{\"outbounds\":[{\"protocol\":\"freedom\",\"tag\":\"direct\"}]," + section + "}";
        var result = new ProfileImporter().ImportText(raw);
        Assert.Empty(result.Profiles);
        Assert.Equal("import.json.unsafe", Assert.Single(result.Errors));
    }
}
