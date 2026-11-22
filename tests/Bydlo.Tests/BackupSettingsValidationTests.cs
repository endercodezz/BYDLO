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

    [Fact]
    public void Rejects_null_server_groups_and_contributors()
    {
        // A null list used to pass validation and throw later in ImportSettings (Merge) or after saving.
        var data = new BackupData();
        data.Settings.ServerGroups = null!;
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));
        Assert.Throws<InvalidDataException>(() => BackupArchive.Encrypt(data, "a long backup password"));

        data = new BackupData();
        data.Settings.ServerGroups = [null!];
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));

        data = new BackupData();
        data.Settings.ServerGroups = [new ServerGroup { Name = "g", ProfileIds = null! }];
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));

        data = new BackupData();
        data.Settings.Contributors = null!;
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));
    }

    [Fact]
    public void Valid_server_groups_still_merge()
    {
        var data = new BackupData();
        var group = new ServerGroup { Name = "g" };
        data.Settings.ServerGroups = [group];
        BackupArchive.Validate(data);
        var merged = BackupArchive.ImportSettings(data, new AppSettings { ServerGroups = [new ServerGroup { Name = "local" }] }, BackupImportMode.Merge);
        Assert.Equal(2, merged.ServerGroups.Count);
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
}
