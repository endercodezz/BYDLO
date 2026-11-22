using Bydlo.Core.Diagnostics;

namespace Bydlo.Tests;

/// <summary>
/// Issue #488: the app only reads the apt configuration to decide which update text to show.
/// Every case runs against a temporary directory; nothing under /etc is read or written and apt is
/// never executed.
/// </summary>
public sealed class AptRepositorySourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bydlo-apt-" + Guid.NewGuid().ToString("N"));

    private string SourcesDirectory => Path.Combine(_root, "sources.list.d");

    private string SourcesListFile => Path.Combine(_root, "sources.list");

    public AptRepositorySourceTests() => Directory.CreateDirectory(SourcesDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary directory must never fail the suite.
        }
    }

    [Fact]
    public void Deb822_source_with_the_repository_uri_is_detected()
    {
        Write("bydlo.sources", """
            Types: deb
            URIs: https://endercodezz.github.io/BYDLO/
            Suites: stable
            Components: main
            Architectures: amd64 arm64
            Signed-By: /etc/apt/keyrings/bydlo.gpg
            """);

        Assert.True(IsConfigured());
    }

    [Fact]
    public void One_line_list_source_with_the_repository_uri_is_detected()
    {
        Write("bydlo.list",
            "deb [signed-by=/etc/apt/keyrings/bydlo.gpg] https://endercodezz.github.io/BYDLO/ stable main\n");

        Assert.True(IsConfigured());
    }

    [Fact]
    public void Repository_uri_without_a_trailing_slash_or_with_a_sub_path_is_detected()
    {
        Write("bydlo.sources", "Types: deb\nURIs: https://endercodezz.github.io/BYDLO\nSuites: stable\n");
        Assert.True(IsConfigured());

        Write("bydlo.sources", "Types: deb\nURIs: https://endercodezz.github.io/BYDLO/apt\nSuites: stable\n");
        Assert.True(IsConfigured());
    }

    [Fact]
    public void Host_comparison_ignores_case()
    {
        Write("bydlo.list", "deb https://Endercodezz.GitHub.io/bydlo/ stable main\n");

        Assert.True(IsConfigured());
    }

    [Fact]
    public void Repository_in_the_main_sources_list_is_detected()
    {
        File.WriteAllText(SourcesListFile, "deb https://endercodezz.github.io/BYDLO/ stable main\n");

        Assert.True(IsConfigured());
    }

    [Fact]
    public void Commented_out_source_is_not_configured()
    {
        Write("bydlo.list", "# deb https://endercodezz.github.io/BYDLO/ stable main\n");
        Assert.False(IsConfigured());

        Write("bydlo.sources", "# URIs: https://endercodezz.github.io/BYDLO/\nTypes: deb\n");
        Assert.False(IsConfigured());
    }

    [Fact]
    public void Disabled_deb822_stanza_is_not_configured()
    {
        Write("bydlo.sources", """
            Types: deb
            URIs: https://endercodezz.github.io/BYDLO/
            Suites: stable
            Enabled: no
            """);

        Assert.False(IsConfigured());
    }

    [Fact]
    public void Enabled_stanza_next_to_a_disabled_one_is_detected()
    {
        Write("bydlo.sources", """
            Types: deb
            URIs: https://endercodezz.github.io/BYDLO/
            Suites: stable
            Enabled: no

            Types: deb
            URIs: https://endercodezz.github.io/BYDLO/
            Suites: stable
            Enabled: yes
            """);

        Assert.True(IsConfigured());
    }

    [Fact]
    public void Unrelated_repositories_are_not_detected()
    {
        Write("debian.sources", "Types: deb\nURIs: https://deb.debian.org/debian\nSuites: trixie\n");
        Write("vendor.list", "deb https://packages.example.com/apt stable main\n");
        // A lookalike host must not count as the BYDLO repository.
        Write("lookalike.list", "deb https://endercodezz.github.io/BYDLO-mirror/ stable main\n");

        Assert.False(IsConfigured());
    }

    [Fact]
    public void Files_that_apt_does_not_read_are_ignored()
    {
        Write("bydlo.sources.bak", "Types: deb\nURIs: https://endercodezz.github.io/BYDLO/\n");
        Write("README", "https://endercodezz.github.io/BYDLO/\n");

        Assert.False(IsConfigured());
    }

    [Fact]
    public void Missing_configuration_is_not_configured()
    {
        Assert.False(AptRepositorySource.IsConfigured(Path.Combine(_root, "absent"), null));
        Assert.False(AptRepositorySource.IsConfigured(SourcesDirectory, Path.Combine(_root, "absent.list")));
    }

    [Fact]
    public void Directory_entry_named_like_a_source_file_is_ignored()
    {
        // Only regular files are apt sources; a stray directory must not break detection.
        Directory.CreateDirectory(Path.Combine(SourcesDirectory, "bydlo.sources"));

        Assert.False(IsConfigured());
    }

    [Fact]
    public void Empty_text_is_not_configured()
    {
        Assert.False(AptRepositorySource.MentionsRepository(null));
        Assert.False(AptRepositorySource.MentionsRepository(""));
        Assert.False(AptRepositorySource.MentionsRepository("   \n\n  "));
    }

    [Fact]
    public void Published_repository_uri_matches_the_documented_one()
    {
        Assert.Equal("https://endercodezz.github.io/BYDLO/", AptRepositorySource.RepositoryUri);
        Assert.True(AptRepositorySource.MentionsRepository($"URIs: {AptRepositorySource.RepositoryUri}"));
    }

    private bool IsConfigured() => AptRepositorySource.IsConfigured(SourcesDirectory, SourcesListFile);

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(SourcesDirectory, name), content);
}
