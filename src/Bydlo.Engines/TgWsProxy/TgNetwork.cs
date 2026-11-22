using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Bydlo.Core.Security;

namespace Bydlo.Engines.TgWsProxy;

/// <summary>Outbound connections of the native TgWsProxy. Tests replace it with in-memory streams.</summary>
public interface ITgNetwork
{
    Task<Stream> ConnectAsync(string host, int port, CancellationToken ct);

    /// <summary>TLS client connection. <paramref name="validateCertificate"/> is false only for domain fronting.</summary>
    Task<Stream> ConnectTlsAsync(string host, int port, string serverName, bool validateCertificate, CancellationToken ct);

    /// <summary>Downloads a small text file (the Cloudflare domain list); null on any failure.</summary>
    Task<string?> GetStringAsync(Uri uri, IReadOnlyDictionary<string, string> pinnedHosts, CancellationToken ct);

    /// <summary>Binds the local listener that Telegram clients connect to.</summary>
    ITgListener Listen(string host, int port);
}

/// <summary>The local listening socket. <see cref="AcceptAsync"/> throws once the socket is dead.</summary>
public interface ITgListener : IDisposable
{
    /// <summary>
    /// Waits for the next client. <c>RemoteAddress</c> is the peer address, or null when the transport
    /// cannot report it; the PROXY protocol trust check refuses a header from an unknown peer.
    /// </summary>
    Task<(Stream Stream, string Label, IPAddress? RemoteAddress)> AcceptAsync(CancellationToken ct);
}

/// <summary>Real sockets. Every call checks <see cref="NetworkSafetyGuard"/>, so nothing connects in development.</summary>
public sealed class SystemTgNetwork(int bufferSize = 256 * 1024) : ITgNetwork
{
    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        NetworkSafetyGuard.EnsureAllowed("open TgWsProxy connections");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveBufferSize = bufferSize, SendBufferSize = bufferSize };
        try
        {
            await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<Stream> ConnectTlsAsync(string host, int port, string serverName, bool validateCertificate, CancellationToken ct)
    {
        var raw = await ConnectAsync(host, port, ct).ConfigureAwait(false);
        var tls = new SslStream(raw, leaveInnerStreamOpen: false);
        try
        {
            var options = new SslClientAuthenticationOptions { TargetHost = serverName };
            if (!validateCertificate)
            {
                // Domain fronting: the certificate belongs to the fronting name, not to Telegram. The MTProto payload
                // is end-to-end encrypted by Telegram, so TLS here only carries the obfuscated stream (as upstream).
#pragma warning disable CA5359
                options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
            }

            await tls.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
            return tls;
        }
        catch
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string?> GetStringAsync(Uri uri, IReadOnlyDictionary<string, string> pinnedHosts, CancellationToken ct)
    {
        NetworkSafetyGuard.EnsureAllowed("download the TgWsProxy domain list");
        using var handler = new SocketsHttpHandler
        {
            // Pinned addresses keep the download working when DNS for the host is poisoned.
            ConnectCallback = async (context, token) =>
            {
                var host = pinnedHosts.TryGetValue(context.DnsEndPoint.Host, out var ip) ? ip : context.DnsEndPoint.Host;
                return await ConnectAsync(host, context.DnsEndPoint.Port, token).ConfigureAwait(false);
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("tg-ws-proxy");
        try
        {
            return await http.GetStringAsync(uri, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public ITgListener Listen(string host, int port) => new SystemTgListener(Bind(host, port), bufferSize);

    private static TcpListener Bind(string host, int port)
    {
        NetworkSafetyGuard.EnsureAllowed("start the TgWsProxy listener");
        var address = host is "" or "0.0.0.0" ? IPAddress.Any
            : host == "::" ? IPAddress.IPv6Any
            : IPAddress.TryParse(host, out var parsed) ? parsed
            : host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback
            : throw new ArgumentException($"Listen address '{host}' is not an IP address.", nameof(host));
        var listener = new TcpListener(address, port);
        listener.Server.NoDelay = true;
        listener.Start();
        return listener;
    }
}

internal sealed class SystemTgListener(TcpListener listener, int bufferSize) : ITgListener
{
    public async Task<(Stream Stream, string Label, IPAddress? RemoteAddress)> AcceptAsync(CancellationToken ct)
    {
        var socket = await listener.AcceptSocketAsync(ct).ConfigureAwait(false);
        socket.NoDelay = true;
        socket.ReceiveBufferSize = bufferSize;
        socket.SendBufferSize = bufferSize;
        var remote = socket.RemoteEndPoint as IPEndPoint;
        return (new NetworkStream(socket, ownsSocket: true), remote?.ToString() ?? "?", remote?.Address);
    }

    public void Dispose()
    {
        listener.Stop();
        listener.Dispose();
    }
}
