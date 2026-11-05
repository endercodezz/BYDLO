using System.Text;
using System.Web;
using Bydlo.Core.Models;

namespace Bydlo.Core.Import;

public interface IProfileParser
{
    bool CanParse(string input);

    /// <summary>Parses one import unit (a link or a config file). Throws <see cref="ImportException"/> on invalid input.</summary>
    ImportedProfile Parse(string input);
}

public sealed class ImportException(string message) : FormatException(message);

internal static class ImportHelpers
{
    /// <summary>Decodes standard or URL-safe base64, with or without padding.</summary>
    public static bool TryDecodeBase64(string text, out string decoded)
    {
        decoded = "";
        var s = text.Trim().Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "");
        if (s.Length == 0 || s.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')))
        {
            return false;
        }

        s = s.TrimEnd('=');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => "" };
        if (s.Length % 4 != 0)
        {
            return false;
        }

        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Parses the query of a URI into a case-insensitive dictionary (first value wins).</summary>
    public static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in q.AllKeys)
        {
            if (key is not null && q[key] is { } v && !result.ContainsKey(key))
            {
                result[key] = v;
            }
        }

        return result;
    }

    public static string Fragment(Uri uri, string fallback)
    {
        var f = uri.Fragment.TrimStart('#');
        return string.IsNullOrWhiteSpace(f) ? fallback : Uri.UnescapeDataString(f).Trim();
    }

    public static Uri ParseUri(string input, string scheme)
    {
        // Uri cannot parse IPv6 hosts without brackets nor some exotic userinfo; normalize the most common issues.
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || !uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportException($"Not a valid {scheme}:// link.");
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            throw new ImportException($"The {scheme}:// link has no server address.");
        }

        return uri;
    }

    public static string Host(Uri uri) => uri.Host.Trim('[', ']');

    /// <summary>
    /// Detects whether the original link text contains an explicit port in the authority
    /// (handles hostnames, IPv4, and bracketed IPv6). Needed because <see cref="Uri.IsDefaultPort"/>
    /// is true both when the port is omitted and when it is explicitly set to the scheme default
    /// (e.g. http://host:80), so callers cannot rely on it to apply proxy-specific defaults.
    /// </summary>
    public static bool HasExplicitPort(Uri uri, string? originalInput = null)
    {
        var s = (originalInput ?? uri.OriginalString)?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return false;
        }

        var rest = s[(schemeEnd + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        if (string.IsNullOrEmpty(authority))
        {
            return false;
        }

        var at = authority.LastIndexOf('@');
        var hostPort = at >= 0 ? authority[(at + 1)..] : authority;
        if (string.IsNullOrEmpty(hostPort))
        {
            return false;
        }

        if (hostPort[0] == '[')
        {
            var close = hostPort.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            var after = hostPort[(close + 1)..];
            if (after.Length == 0 || after[0] != ':')
            {
                return false;
            }

            var portPart = after[1..];
            return portPart.Length > 0 && portPart.All(char.IsAsciiDigit);
        }

        var colon = hostPort.LastIndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        // Unbracketed IPv6 contains multiple colons; its trailing segment is not a port.
        if (hostPort[..colon].Contains(':'))
        {
            return false;
        }

        var port = hostPort[(colon + 1)..];
        return port.Length > 0 && port.All(char.IsAsciiDigit);
    }

    public static int Port(Uri uri, int defaultPort, string? originalInput = null)
    {
        int p;
        if (!uri.IsDefaultPort && uri.Port > 0)
        {
            p = uri.Port;
        }
        else if (uri.Port > 0 && HasExplicitPort(uri, originalInput))
        {
            // Explicitly specified scheme-default port (e.g. http://host:80): keep it
            // instead of replacing it with the proxy-specific default.
            p = uri.Port;
        }
        else
        {
            p = defaultPort;
        }

        return p is > 0 and <= 65535 ? p : throw new ImportException("Invalid port.");
    }

    public static void Copy(Dictionary<string, string> query, Profile profile, params (string From, string To)[] map)
    {
        foreach (var (from, to) in map)
        {
            if (query.TryGetValue(from, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                profile.Options[to] = v;
            }
        }
    }
}
