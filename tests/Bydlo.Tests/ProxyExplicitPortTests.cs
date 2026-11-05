using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

/// <summary>Issue #352: an explicit :80 in an http proxy link must not become 8080.</summary>
public sealed class ProxyExplicitPortTests
{
    private readonly ProfileImporter _importer = new();

    private ImportedProfile One(string text)
    {
        var r = _importer.ImportText(text);
        Assert.Empty(r.Errors);
        return Assert.Single(r.Profiles);
    }

    [Theory]
    [InlineData("http://user:pass@proxy.example:80", 80)]
    [InlineData("http://proxy.example", 8080)]
    [InlineData("http://proxy.example:8080", 8080)]
    [InlineData("http://proxy.example:80", 80)]
    [InlineData("https://proxy.example", 443)]
    [InlineData("https://proxy.example:443", 443)]
    [InlineData("https://user:pass@proxy.example:443", 443)]
    [InlineData("socks5://proxy.example", 1080)]
    [InlineData("socks5://proxy.example:1080", 1080)]
    [InlineData("socks5://user:pass@proxy.example:1080", 1080)]
    public void Proxy_explicit_and_default_ports_are_preserved(string link, int expectedPort)
    {
        var p = One(link);
        Assert.Equal(expectedPort, p.Profile.Port);
    }

    [Theory]
    [InlineData("http://user:pass@[::1]:80", "::1", 80)]
    [InlineData("http://[::1]", "::1", 8080)]
    [InlineData("http://[::1]:8080", "::1", 8080)]
    [InlineData("https://[::1]:443", "::1", 443)]
    [InlineData("socks5://[::1]", "::1", 1080)]
    [InlineData("socks5://[::1]:1080", "::1", 1080)]
    public void Proxy_ipv6_explicit_and_default_ports(string link, string expectedHost, int expectedPort)
    {
        var p = One(link);
        Assert.Equal(expectedHost, p.Profile.Host);
        Assert.Equal(expectedPort, p.Profile.Port);
    }

    [Fact]
    public void Http_explicit_port_80_keeps_credentials()
    {
        var p = One("http://user:pass@proxy.example:80");
        Assert.Equal(ProtocolKind.Http, p.Profile.Protocol);
        Assert.Equal("proxy.example", p.Profile.Host);
        Assert.Equal(80, p.Profile.Port);
        Assert.Equal("user", p.Profile.Option(Keys.Username));
        Assert.Equal("pass", p.Secrets[Keys.Password]);
    }
}
