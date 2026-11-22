using Bydlo.Core.Backup;
using Bydlo.Core.Models;
using Bydlo.Core.Routing;

namespace Bydlo.Tests;

public sealed class BackupSettingsValidationTests
{
    [Theory]
    [InlineData("FakeIpExceptions")]
    [InlineData("RemoteDnsDomains")]
    [InlineData("DirectDnsDomains")]
    [InlineData("RemoteDnsRuleSets")]
    [InlineData("DirectDnsRuleSets")]
    public void Rejects_null_dns_collections(string property)
    {
        var data = new BackupData();
        typeof(RoutingSettings).GetProperty(property)!.SetValue(data.Settings.Routing, null);
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));
    }

    [Theory]
    [InlineData("RemoteDnsTransport")]
    [InlineData("DirectDnsTransport")]
    public void Rejects_unknown_dns_transports(string property)
    {
        var data = new BackupData();
        typeof(RoutingSettings).GetProperty(property)!.SetValue(data.Settings.Routing, (DnsTransport)99);
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));
    }

    [Fact]
    public void Rejects_null_or_invalid_dpi_and_ordered_rules()
    {
        var data = new BackupData();
        data.Settings.DpiDefaults = null!;
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));
        data.Settings.DpiDefaults = new DpiSettings { Size = "invalid" };
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));
        data.Settings.DpiDefaults = new DpiSettings { Interval = null! };
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));
        data.Settings.DpiDefaults = new();
        data.Settings.Routing.OrderedRules = [null!];
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));
        data.Settings.Routing.OrderedRules = [new(RoutingCondition.Domain, "valid.invalid", (RoutingAction)99)];
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));
    }

    [Fact]
    public void Accepts_settings_from_before_the_new_dns_and_dpi_fields()
    {
        var data = System.Text.Json.JsonSerializer.Deserialize<BackupData>("""{"Settings":{"Routing":{"RemoteDns":"1.1.1.1"}}}""")!;
        BackupArchive.Validate(data);
        Assert.Equal(DnsTransport.Https, data.Settings.Routing.RemoteDnsTransport);
        Assert.Equal("off", data.Settings.DpiDefaults.Fragment);
    }

    [Fact]
    public void Rejects_null_or_invalid_proxy_protocol_trusted_sources()
    {
        var data = new BackupData();
        data.Settings.TgWsProxy.ProxyProtocolTrustedSources = null!;
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));

        data.Settings.TgWsProxy.ProxyProtocolTrustedSources = ["127.0.0.1", "not-an-ip"];
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));

        data.Settings.TgWsProxy.ProxyProtocolTrustedSources = ["127.0.0.1", "::1"];
        data.Settings.TgWsProxy.ProxyProtocol = true;
        BackupArchive.Validate(data);
    }

    [Fact]
    public void Accepts_settings_from_before_the_proxy_protocol_fields()
    {
        var data = System.Text.Json.JsonSerializer.Deserialize<BackupData>("""{"Settings":{"TgWsProxy":{"Port":1443}}}""")!;
        BackupArchive.Validate(data);
        Assert.False(data.Settings.TgWsProxy.ProxyProtocol);
        Assert.Equal(new[] { "127.0.0.1", "::1" }, data.Settings.TgWsProxy.ProxyProtocolTrustedSources.ToArray());
    }
}
