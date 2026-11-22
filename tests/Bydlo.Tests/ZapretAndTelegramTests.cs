using Bydlo.Core.Models;
using Bydlo.Engines.TgWsProxy;
using Bydlo.Engines.Zapret;

namespace Bydlo.Tests;

public class ZapretAndTelegramTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Theory]
    [InlineData("general.bat.txt")]
    [InlineData("general-alt.bat.txt")]
    public void Flowseal_strategy_is_parsed_into_winws_arguments(string fixture)
    {
        var root = TestDirs.New("zapret");
        var parsed = ZapretStrategyParser.Parse(Fixture(fixture), root, GameFilterValues.From("disabled"));

        Assert.Empty(parsed.Warnings);
        var args = parsed.Arguments;
        Assert.StartsWith("--wf-tcp=80,443", args[0], StringComparison.Ordinal);
        Assert.EndsWith(",12", args[0], StringComparison.Ordinal);          // game filter placeholder
        Assert.DoesNotContain(args, a => a.Contains('%') || a.Contains('"') || a == "^");
        Assert.Contains("--new", args);
        var hostlist = args.First(a => a.StartsWith("--hostlist=", StringComparison.Ordinal));
        Assert.True(Path.IsPathRooted(hostlist["--hostlist=".Length..]));
        Assert.Contains(Path.Combine(root, "lists", "list-general.txt"), hostlist, StringComparison.Ordinal);

        // Flowseal 1.10.4 (issue #421): every fake payload file resolves inside the installation's bin directory.
        var fakes = args.Where(a => a.StartsWith("--dpi-desync-fake-", StringComparison.Ordinal) && a.Contains(".bin", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(fakes);
        Assert.All(fakes, a => Assert.StartsWith(Path.Combine(root, "bin") + Path.DirectorySeparatorChar, a[(a.IndexOf('=') + 1)..], StringComparison.Ordinal));
    }

    [Fact]
    public void Game_filter_modes_match_service_bat()
    {
        Assert.Equal(new GameFilterValues("12", "12", "12"), GameFilterValues.From("disabled"));
        Assert.Equal(new GameFilterValues("1024-65535", "1024-65535", "1024-65535"), GameFilterValues.From("all"));
        Assert.Equal(new GameFilterValues("1024-65535", "1024-65535", "12"), GameFilterValues.From("tcp"));
        Assert.Equal(new GameFilterValues("1024-65535", "12", "1024-65535"), GameFilterValues.From("udp"));
    }

    [Fact]
    public void Strategies_are_discovered_in_natural_order_without_service_bat()
    {
        var root = TestDirs.New("zapret-discover");
        foreach (var n in new[] { "general.bat", "general (ALT10).bat", "general (ALT2).bat", "general (ALT).bat", "service.bat" })
        {
            File.WriteAllText(Path.Combine(root, n), "rem");
        }

        var names = ZapretStrategyParser.Discover(root).Select(s => s.FileName).ToList();
        Assert.DoesNotContain("service.bat", names);
        Assert.True(names.IndexOf("general (ALT2).bat") < names.IndexOf("general (ALT10).bat"));
    }

    [Fact]
    public void User_lists_get_flowseal_defaults_and_never_end_up_empty()
    {
        var install = new ZapretInstallation(TestDirs.New("zapret-lists"));
        install.EnsureUserLists();
        Assert.Contains("203.0.113.113/32", install.ReadUserList(ZapretUserList.IpsetExclude), StringComparison.Ordinal);

        install.WriteUserList(ZapretUserList.General, "example.org\nexample.org\n\n");
        Assert.Equal("example.org\n", install.ReadUserList(ZapretUserList.General));

        install.WriteUserList(ZapretUserList.Exclude, "   ");
        Assert.Contains("domain.example.abc", install.ReadUserList(ZapretUserList.Exclude), StringComparison.Ordinal);
    }

    [Fact]
    public void Telegram_links_match_upstream_format()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        Assert.Equal($"tg://proxy?server=127.0.0.1&port=1443&secret=dd{secret}", TgLinkBuilder.Build("0.0.0.0", 1443, secret));
        Assert.Equal($"tg://proxy?server=127.0.0.1&port=1443&secret=ee{secret}6578616d706c652e636f6d", TgLinkBuilder.Build("127.0.0.1", 1443, secret, "example.com"));
        Assert.True(TgLinkBuilder.IsValidSecret(TgLinkBuilder.NewSecret()));
        Assert.False(TgLinkBuilder.IsValidSecret("xyz"));
    }

    [Fact]
    public void TgWsProxy_options_follow_settings()
    {
        var s = new TgWsProxySettings { Port = 1555, DcIps = ["4:149.154.167.220", " "], FakeTlsDomain = " a.com ", CfProxyFallback = false, Verbose = true, CfProxyDomains = ["Example.COM", "example.com", "bad_domain"] };
        var o = TgWsProxyOptions.From(s, "0123456789abcdef0123456789abcdef");
        Assert.Equal("127.0.0.1", o.Host);
        Assert.Equal(1555, o.Port);
        Assert.Equal(16, o.Secret.Length);
        Assert.Equal("149.154.167.220", Assert.Single(o.DcRedirects).Value);
        Assert.Equal("a.com", o.FakeTlsDomain);
        Assert.False(o.CfProxyFallback);
        Assert.True(o.Verbose);
        Assert.Equal(new[] { "example.com" }, o.CfProxyDomains);

        Assert.Empty(TgWsProxyOptions.From(new TgWsProxySettings { DcIps = [] }, "0123456789abcdef0123456789abcdef").DcRedirects);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("x:149.154.167.220")]
    [InlineData("4:149.154.167")]
    [InlineData("4:::1")]
    [InlineData("4:example.com")]
    public void TgWsProxy_rejects_bad_dc_ip(string entry)
    {
        var ex = Assert.Throws<Bydlo.Core.Engines.EngineException>(() => TgWsProxyOptions.ParseDcIps([entry]));
        Assert.Equal("error.engine.tg.dcip", ex.LocalizationKey);
    }
}
