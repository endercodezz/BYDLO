using Bydlo.Platform.Processes;
using System.Globalization;
using Bydlo.Core.Models;
using System.Text.RegularExpressions;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Engines.Zapret;

/// <summary>
/// Managed port of the standard HTTP/TLS suite in Flowseal Test Zapret 1.10.4 (MIT).
/// The interactive upstream process/ipset manager is deliberately not executed.
/// Each curl is owned by IProcessRunner, with real response codes and measured successful latency.
/// </summary>
public sealed partial class TestZapretProbe(IProcessRunner runner, PlatformInfo? platform = null) : IZapretStrategyProbe
{
    public async Task PrepareAsync(CancellationToken ct)
    {
        try
        {
            var result = await runner.RunToCompletionAsync(new ProcessSpec
            {
                FileName = (platform ?? PlatformInfo.Current).Os == OsFamily.Windows ? "curl.exe" : "curl",
                Arguments = ["--disable", "--version"],
                DisplayName = "curl version", AffectsNetwork = false,
            }, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || !result.Output.StartsWith("curl ", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unknown curl version output.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidDataException or TimeoutException)
        {
            throw new Bydlo.Core.Engines.EngineException(ex.Message, localizationKey: "tools.select.curl");
        }
    }

    public static IReadOnlyList<string> DefaultTargets { get; } = ["https://www.youtube.com/", "https://discord.com/"];
    public static IReadOnlyList<string> Protocols { get; } = ["HTTP", "TLS1.2", "TLS1.3"];

    public static IReadOnlyList<string> ParseTargets(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (lines.Length is < 1 or > 20 || lines.Any(t => !Uri.TryCreate(t, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0))
        {
            throw new FormatException("Enter 1–20 HTTP(S) URLs without credentials, queries or fragments.");
        }
        return lines;
    }

    public async Task<IReadOnlyList<ZapretCheck>> CheckAsync(IReadOnlyList<string> targets, CancellationToken ct)
    {
        // SystemProcessRunner also enforces this, including for every child curl.
        NetworkSafetyGuard.EnsureAllowed("run Test Zapret standard checks");
        return await CheckWithRunnerAsync(targets, ct).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<ZapretCheck>> CheckWithRunnerAsync(IReadOnlyList<string> targets, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(4);
        var tasks = targets.SelectMany(target => Protocols.Select(async protocol =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IReadOnlyList<string> version = protocol switch
                {
                    "TLS1.2" => ["--tlsv1.2", "--tls-max", "1.2"],
                    "TLS1.3" => ["--tlsv1.3", "--tls-max", "1.3"],
                    _ => ["--http1.1"],
                };
                var spec = new ProcessSpec
                {
                    FileName = (platform ?? PlatformInfo.Current).Os == OsFamily.Windows ? "curl.exe" : "curl",
                    DisplayName = "Test Zapret HTTP/TLS",
                    Arguments = ["--disable", "--head", "--silent", "--show-error", "--noproxy", "*",
                        "--max-time", "4", "--connect-timeout", "2", "--output", (platform ?? PlatformInfo.Current).Os == OsFamily.Windows ? "NUL" : "/dev/null",
                        "--write-out", "\nBYDLO_RESULT:%{http_code}:%{time_total}\n", .. version, "--url", target],
                };
                try
                {
                    var result = await runner.RunToCompletionAsync(spec, TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return Parse(target, protocol, result.ExitCode, result.Output);
                }
                catch (TimeoutException)
                {
                    ct.ThrowIfCancellationRequested();
                    return new ZapretCheck(target, protocol, ZapretCheckOutcome.Timeout, null);
                }
            }
            finally { slots.Release(); }
        }));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static ZapretCheck Parse(string target, string protocol, int exitCode, string output)
    {
        var match = ResultLine().Match(output);
        var outcome = exitCode == 28 ? ZapretCheckOutcome.Timeout
            : exitCode is 2 or 4 or 35 ? ZapretCheckOutcome.Unknown
            : exitCode != 0 ? ZapretCheckOutcome.Error
            : !match.Success ? ZapretCheckOutcome.Unknown
            : int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) is >= 200 and < 400 ? ZapretCheckOutcome.Success
            : ZapretCheckOutcome.Error;
        double? milliseconds = outcome == ZapretCheckOutcome.Success &&
            double.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds)
            ? seconds * 1000 : null;
        return new ZapretCheck(target, protocol, outcome, milliseconds, LogRedactor.Shared.Redact(output.Trim()));
    }

    [GeneratedRegex(@"(?m)^BYDLO_RESULT:(\d{3}):(\d+(?:\.\d+)?)\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex ResultLine();
}
