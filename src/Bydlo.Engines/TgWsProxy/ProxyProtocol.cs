using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bydlo.Engines.TgWsProxy;

/// <summary>Source address reported by a PROXY protocol v1 header.</summary>
/// <param name="Family">The address family token of the header: <c>TCP4</c>, <c>TCP6</c> or <c>UNKNOWN</c>.</param>
/// <param name="Source">The client address, or null for <c>UNKNOWN</c>.</param>
/// <param name="SourcePort">The client port, or 0 for <c>UNKNOWN</c>.</param>
internal sealed record ProxyProtocolHeader(string Family, IPAddress? Source, int SourcePort)
{
    /// <summary>Log label of the real client, or <paramref name="fallback"/> when the header carries no address.</summary>
    public string Describe(string fallback) => Source is null
        ? fallback
        : Source.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Source}]:{SourcePort}" : $"{Source}:{SourcePort}";
}

/// <summary>
/// PROXY protocol v1 (text) header, the only version Flowseal tg-ws-proxy 1.11.4 accepts: with
/// <c>--proxy-protocol</c> it reads one line before the client handshake and uses the address for the log label.
/// Upstream has no v2 support, so the binary v2 signature is rejected instead of parsed.
/// </summary>
internal static class ProxyProtocol
{
    /// <summary>Longest v1 header the specification allows, CRLF included.</summary>
    public const int MaxHeaderLength = 107;

    /// <summary>How long a trusted peer may take to send the header; upstream waits 10 s.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private const string Prefix = "PROXY ";

    /// <summary>Binary v2 signature. Upstream never parses v2, so a connection that starts with it is refused.</summary>
    private static ReadOnlySpan<byte> V2Signature => "\r\n\r\n\0\r\nQUIT\n"u8;

    /// <summary>True when <paramref name="peer"/> may send a header; IPv4-mapped IPv6 peers match their IPv4 form.</summary>
    public static bool IsTrustedSource(IPAddress? peer, IReadOnlyList<IPAddress> trusted)
    {
        if (peer is null)
        {
            return false;
        }

        var normalized = Normalize(peer);
        foreach (var candidate in trusted)
        {
            if (Normalize(candidate).GetAddressBytes().AsSpan().SequenceEqual(normalized.GetAddressBytes()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads one v1 header. Returns null when it is malformed, oversized, incomplete or not sent within
    /// <paramref name="timeout"/>; the caller then closes the connection without touching the handshake.
    /// </summary>
    public static async Task<ProxyProtocolHeader?> ReadAsync(Stream stream, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var buffer = new byte[MaxHeaderLength];
        var length = 0;
        try
        {
            // One byte at a time: the header ends at LF and the next bytes already belong to the client handshake,
            // so nothing may be read past it.
            while (length < buffer.Length)
            {
                if (await stream.ReadAsync(buffer.AsMemory(length, 1), deadline.Token).ConfigureAwait(false) == 0)
                {
                    return null;
                }

                length++;
                if (length <= V2Signature.Length && V2Signature[..length].SequenceEqual(buffer.AsSpan(0, length)))
                {
                    if (length == V2Signature.Length)
                    {
                        // A v2 header: upstream never parses it, so the connection is refused instead.
                        return null;
                    }

                    continue;
                }

                if (buffer[length - 1] == (byte)'\n')
                {
                    return Parse(buffer.AsSpan(0, length));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        // No LF within the v1 limit.
        return null;
    }

    private static ProxyProtocolHeader? Parse(ReadOnlySpan<byte> line)
    {
        // "PROXY ...\r\n": the line must end with CRLF and carry printable ASCII only.
        if (line.Length < Prefix.Length + 3 || line[^2] != (byte)'\r' || line[..^2].ContainsAnyExceptInRange((byte)0x20, (byte)0x7E))
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(line[..^2]);
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        // Fields are separated by single spaces; a double space yields an empty field and is rejected.
        var parts = text.Split(' ');
        if (parts.Length < 2)
        {
            return null;
        }

        var family = parts[1];
        if (family == "UNKNOWN")
        {
            // The specification requires receivers to ignore everything after UNKNOWN.
            return new ProxyProtocolHeader(family, null, 0);
        }

        if (family is not ("TCP4" or "TCP6") || parts.Length != 6)
        {
            return null;
        }

        var expected = family == "TCP4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        if (!IPAddress.TryParse(parts[2], out var source) || source.AddressFamily != expected
            || !IPAddress.TryParse(parts[3], out var destination) || destination.AddressFamily != expected
            || !TryParsePort(parts[4], out var sourcePort) || !TryParsePort(parts[5], out _))
        {
            return null;
        }

        return new ProxyProtocolHeader(family, source, sourcePort);
    }

    private static bool TryParsePort(string value, out int port) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 0 and <= 65535;

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
