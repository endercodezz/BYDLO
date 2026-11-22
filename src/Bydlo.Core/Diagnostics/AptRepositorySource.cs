namespace Bydlo.Core.Diagnostics;

/// <summary>
/// Read-only detection of a configured BYDLO apt source (issue #488). The application never runs apt
/// and never writes to <c>/etc</c>: it only reads the source files to decide which update text to show.
/// </summary>
public static class AptRepositorySource
{
    /// <summary>Public apt repository published by the release workflow to GitHub Pages.</summary>
    public const string RepositoryUri = "https://endercodezz.github.io/BYDLO/";

    /// <summary>Directory with the deb822 <c>*.sources</c> and one-line <c>*.list</c> files.</summary>
    public const string DefaultSourcesDirectory = "/etc/apt/sources.list.d";

    /// <summary>Main source list; a repository added here counts as configured too.</summary>
    public const string DefaultSourcesListFile = "/etc/apt/sources.list";

    private static readonly Uri Repository = new(RepositoryUri, UriKind.Absolute);

    private static readonly string RepositoryPath = Repository.AbsolutePath.TrimEnd('/');

    /// <summary>
    /// True when any enabled apt source points at the BYDLO repository. Unreadable or missing files mean
    /// "not configured", so the UI never promises apt updates it cannot verify.
    /// </summary>
    public static bool IsConfigured(
        string sourcesDirectory = DefaultSourcesDirectory,
        string? sourcesListFile = DefaultSourcesListFile)
    {
        try
        {
            if (sourcesListFile is not null && MentionsRepository(ReadOrNull(sourcesListFile)))
            {
                return true;
            }
            if (!Directory.Exists(sourcesDirectory))
            {
                return false;
            }
            foreach (var file in Directory.EnumerateFiles(sourcesDirectory))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(".sources", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".list", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (MentionsRepository(ReadOrNull(file)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// True when the text of a source file names the BYDLO repository. Commented-out lines and deb822
    /// stanzas with <c>Enabled: no</c> are ignored, because apt ignores them as well.
    /// </summary>
    public static bool MentionsRepository(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        foreach (var stanza in text.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = stanza.Split('\n').Select(StripComment).ToArray();
            if (lines.Any(IsDisabledLine))
            {
                continue;
            }
            foreach (var token in lines.SelectMany(line =>
                         line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            {
                if (IsRepositoryUri(token))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string StripComment(string line)
    {
        var comment = line.IndexOf('#', StringComparison.Ordinal);
        return comment >= 0 ? line[..comment] : line;
    }

    private static bool IsDisabledLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("Enabled:", StringComparison.OrdinalIgnoreCase) &&
            trimmed[(trimmed.IndexOf(':') + 1)..].Trim().Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRepositoryUri(string token)
    {
        var value = token.Trim('"', '\'', ',', ';');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }
        var path = uri.AbsolutePath.TrimEnd('/');
        return uri.Host.Equals(Repository.Host, StringComparison.OrdinalIgnoreCase) &&
            (path.Equals(RepositoryPath, StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith(RepositoryPath + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
