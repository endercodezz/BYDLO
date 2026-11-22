using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Engines.TgWsProxy;

namespace Bydlo.Tests;

/// <summary>
/// Issue #425: the optional PROXY protocol v1 header of Flowseal tg-ws-proxy 1.11.4. Every case runs over
/// in-memory streams; no socket is opened and no engine is started.
/// </summary>
public partial class TgWsProxyTests
{
    private const string ValidHeader = "PROXY TCP4 203.0.113.7 198.51.100.1 51234 443\r\n";

    [Fact]
    public async Task Valid_v1_header_is_consumed_before_the_client_handshake()
    {
        var (proxy, logs) = ProxyProtocolServer();
        await using var _ = proxy;
        using var telegram = new TelegramClient(Secret, MtProto.Intermediate, 2);
        using var client = new RecordingStream([.. Encoding.ASCII.GetBytes(ValidHeader), .. telegram.Init]);

        await proxy.HandleClientAsync(client, "proxy-test", IPAddress.Loopback).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, l => l.Contains("PROXY protocol v1 header accepted (TCP4)", StringComparison.Ordinal));
        // The header address becomes the log label of the real client.
        Assert.Contains(logs, l => l.Contains("[203.0.113.7:51234]", StringComparison.Ordinal));
        // The handshake right after the header still parses: the header did not disturb MTProto.
        Assert.Contains(logs, l => l.Contains("handshake ok: DC2", StringComparison.Ordinal));
        Assert.Equal(ValidHeader.Length + MtProto.HandshakeLength, client.BytesRead);
        Assert.Contains("bad=0", proxy.Stats.Summary());
    }

    [Fact]
    public async Task Valid_v1_header_with_unknown_family_keeps_the_peer_label()
    {
        var (proxy, logs) = ProxyProtocolServer();
        await using var _ = proxy;
        using var telegram = new TelegramClient(Secret, MtProto.Intermediate, 2);
        using var client = new RecordingStream([.. "PROXY UNKNOWN\r\n"u8, .. telegram.Init]);

        await proxy.HandleClientAsync(client, "peer-label", IPAddress.Loopback).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, l => l.Contains("PROXY protocol v1 header accepted (UNKNOWN)", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("[peer-label]", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("handshake ok: DC2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Valid_v1_header_from_ipv6_loopback_is_accepted()
    {
        var (proxy, logs) = ProxyProtocolServer();
        await using var _ = proxy;
        using var telegram = new TelegramClient(Secret, MtProto.Intermediate, 2);
        using var client = new RecordingStream([.. "PROXY TCP6 2001:db8::7 2001:db8::1 51234 443\r\n"u8, .. telegram.Init]);

        await proxy.HandleClientAsync(client, "proxy-test", IPAddress.IPv6Loopback).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, l => l.Contains("[2001:db8::7]:51234", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("handshake ok: DC2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fragmented_header_is_reassembled()
    {
        var (proxy, logs) = ProxyProtocolServer();
        await using var _ = proxy;
        using var telegram = new TelegramClient(Secret, MtProto.Intermediate, 2);
        // One byte per read: the header arrives in as many pieces as it has bytes.
        using var client = new RecordingStream([.. Encoding.ASCII.GetBytes(ValidHeader), .. telegram.Init], chunk: 1);

        await proxy.HandleClientAsync(client, "proxy-test", IPAddress.Loopback).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, l => l.Contains("PROXY protocol v1 header accepted (TCP4)", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("handshake ok: DC2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Binary_v2_header_is_rejected_because_upstream_has_no_v2_support()
    {
        var (proxy, logs) = ProxyProtocolServer();
        await using var _ = proxy;
        // v2 signature followed by a minimal LOCAL command header.
        byte[] v2 = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A, 0x20, 0x00, 0x00, 0x00];
        using var client = new RecordingStream(v2);

        await proxy.HandleClientAsync(client, "proxy-test", IPAddress.Loopback).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, l => l.Contains("invalid PROXY protocol header", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains("handshake", StringComparison.Ordinal));
        // Rejected as soon as the 12-byte signature is complete, without scanning for a line end.
        Assert.Equal(12, client.BytesRead);
        Assert.Contains("bad=1", proxy.Stats.Summary());
        Assert.Equal(0, proxy.Stats.ActiveConnections);
    }

    [Theory]
    [InlineData("PROXY TCP4 203.0.113.7 198.51.100.1 51234\r\n")] // missing destination port
    [InlineData("PROXY TCP4 203.0.113.7 198.51.100.1 51234 443 7\r\n")] // extra field
    [InlineData("PROXY TCP4 203.0.113.7 198.51.100.1 51234 99999\r\n")] // port out of range
    [InlineData("PROXY TCP4 203.0.113.7 198.51.100.1 51234 -1\r\n")] // negative port
    [InlineData("PROXY TCP4 2001:db8::7 2001:db8::1 51234 443\r\n")] // family does not match the address
    [InlineData("PROXY TCP6 203.0.113.7 198.51.100.1 51234 443\r\n")]
    [InlineData("PROXY TCP9 203.0.113.7 198.51.100.1 51234 443\r\n")] // unknown family
    [InlineData("PROXY TCP4 203.0.113.7 198.51.100.1 51234 443\n")] // LF without CR
    [InlineData("PROXY  TCP4 203.0.113.7 198.51.100.1 51234 443\r\n")] // double space
    [InlineData("PROXY\r\n")]
    [InlineData("GET / HTTP/1.1\r\n")] // not a PROXY header at all
    [InlineData("PROXY TCP4 203.0.113.7 198.51.100.1 51234 443\rX")] // no LF, garbage tail
    public async Task Malformed_header_closes_the_connection(string header)
    {
        var (proxy, logs) = ProxyProtocolServer();
        await using var _ = proxy;
        using var client = new RecordingStream(Encoding.ASCII.GetBytes(header));

        await proxy.HandleClientAsync(client, "proxy-test", IPAddress.Loopback).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, l => l.Contains("invalid PROXY protocol header", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains("handshake", StringComparison.Ordinal));
        Assert.True(client.Disposed, "The connection must be closed after a bad header.");
        Assert.Equal(0, proxy.Stats.ActiveConnections);
    }

    [Fact]
    public async Task Non_ascii_header_is_rejected()
    {
        var (proxy, logs) = ProxyProtocolServer();
        await using var _ = proxy;
        byte[] header = [.. "PROXY TCP4 203.0.113.7 198.51.100.1 51234 443"u8, 0xFF, (byte)'\r', (byte)'\n'];
        using var client = new RecordingStream(header);

        await proxy.HandleClientAsync(client, "proxy-test", IPAddress.Loopback).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, l => l.Contains("invalid PROXY protocol header", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Oversized_header_is_rejected_without_reading_past_the_limit()
    {
        var (proxy, logs) = ProxyProtocolServer();
        await using var _ = proxy;
        using var client = new RecordingStream(Encoding.ASCII.GetBytes(new string('A', 4096)));

        await proxy.HandleClientAsync(client, "proxy-test", IPAddress.Loopback).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, l => l.Contains("invalid PROXY protocol header", StringComparison.Ordinal));
        Assert.Equal(ProxyProtocol.MaxHeaderLength, client.BytesRead);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task Incomplete_header_is_rejected_when_the_peer_disconnects()
    {
        var (proxy, logs) = ProxyProtocolServer();
        await using var _ = proxy;
        using var client = new RecordingStream("PROXY TCP4 203.0.113.7"u8.ToArray());

        await proxy.HandleClientAsync(client, "proxy-test", IPAddress.Loopback).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, l => l.Contains("invalid PROXY protocol header", StringComparison.Ordinal));
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task Header_read_times_out()
    {
        using var silent = new BlockingStream();
        var started = DateTime.UtcNow;

        var header = await ProxyProtocol.ReadAsync(silent, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Null(header);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "The read must end at the timeout, not hang.");
    }

    [Fact]
    public async Task Shutdown_cancels_a_pending_header_read_and_releases_the_client()
    {
        var (proxy, _) = ProxyProtocolServer();
        using var client = new BlockingStream();
        var handling = proxy.HandleClientAsync(client, "proxy-test", IPAddress.Loopback);

        await proxy.DisposeAsync();
        await handling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(client.Disposed, "The client stream must be released when shutdown cancels the header read.");
        Assert.Equal(0, proxy.Stats.ActiveConnections);
    }

    [Fact]
    public async Task Disabled_option_treats_header_bytes_as_client_data()
    {
        var logs = new ConcurrentQueue<string>();
        var options = new TgWsProxyOptions { Secret = Secret, PoolSize = 0, CfProxyFallback = false, Verbose = true };
        await using var proxy = new TgWsProxyServer(options, new FakeTgNetwork(), (_, m) => logs.Enqueue(m));
        using var telegram = new TelegramClient(Secret, MtProto.Intermediate, 2);
        using var client = new RecordingStream([.. Encoding.ASCII.GetBytes(ValidHeader), .. telegram.Init]);

        await proxy.HandleClientAsync(client, "proxy-test", IPAddress.Loopback).WaitAsync(TimeSpan.FromSeconds(5));

        // Nothing is parsed as a header: the first 64 bytes of the stream are the handshake.
        Assert.DoesNotContain(logs, l => l.Contains("PROXY protocol", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("bad handshake", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Header_from_an_untrusted_peer_is_treated_as_client_data()
    {
        var (proxy, logs) = ProxyProtocolServer();
        await using var _ = proxy;
        using var telegram = new TelegramClient(Secret, MtProto.Intermediate, 2);
        using var client = new RecordingStream([.. Encoding.ASCII.GetBytes(ValidHeader), .. telegram.Init]);

        await proxy.HandleClientAsync(client, "proxy-test", IPAddress.Parse("203.0.113.5")).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, l => l.Contains("PROXY protocol header not expected from this peer", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains("header accepted", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("bad handshake", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Header_from_a_peer_without_a_known_address_is_treated_as_client_data()
    {
        var (proxy, logs) = ProxyProtocolServer();
        await using var _ = proxy;
        using var telegram = new TelegramClient(Secret, MtProto.Intermediate, 2);
        using var client = new RecordingStream([.. Encoding.ASCII.GetBytes(ValidHeader), .. telegram.Init]);

        await proxy.HandleClientAsync(client, "proxy-test").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(logs, l => l.Contains("PROXY protocol header not expected from this peer", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("bad handshake", StringComparison.Ordinal));
    }

    [Fact]
    public void Trusted_sources_match_ipv4_mapped_ipv6_peers()
    {
        var trusted = new[] { IPAddress.Loopback, IPAddress.IPv6Loopback };

        Assert.True(ProxyProtocol.IsTrustedSource(IPAddress.Loopback, trusted));
        Assert.True(ProxyProtocol.IsTrustedSource(IPAddress.IPv6Loopback, trusted));
        Assert.True(ProxyProtocol.IsTrustedSource(IPAddress.Parse("::ffff:127.0.0.1"), trusted));
        Assert.False(ProxyProtocol.IsTrustedSource(IPAddress.Parse("203.0.113.5"), trusted));
        Assert.False(ProxyProtocol.IsTrustedSource(null, trusted));
        Assert.False(ProxyProtocol.IsTrustedSource(IPAddress.Loopback, []));
    }

    [Fact]
    public void Trusted_sources_default_to_loopback_and_reject_garbage()
    {
        Assert.Equal(TgWsProxyOptions.DefaultTrustedSources.ToArray(), TgWsProxyOptions.ParseTrustedSources([]).ToArray());
        Assert.Equal(TgWsProxyOptions.DefaultTrustedSources.ToArray(), TgWsProxyOptions.ParseTrustedSources([" ", ""]).ToArray());
        Assert.Equal(new[] { IPAddress.Parse("10.0.0.2") }, TgWsProxyOptions.ParseTrustedSources([" 10.0.0.2 "]).ToArray());

        var error = Assert.Throws<EngineException>(() => TgWsProxyOptions.ParseTrustedSources(["not-an-ip"]));
        Assert.Equal("error.engine.tg.proxyprotocol", error.LocalizationKey);
    }

    [Fact]
    public void Options_carry_the_proxy_protocol_settings_and_stay_off_by_default()
    {
        var defaults = TgWsProxyOptions.From(new TgWsProxySettings(), "0123456789abcdef0123456789abcdef");
        Assert.False(defaults.ProxyProtocol);
        Assert.Equal(TgWsProxyOptions.DefaultTrustedSources.ToArray(), defaults.ProxyProtocolTrustedSources.ToArray());

        var configured = TgWsProxyOptions.From(new TgWsProxySettings
        {
            ProxyProtocol = true,
            ProxyProtocolTrustedSources = ["10.0.0.2"],
        }, "0123456789abcdef0123456789abcdef");
        Assert.True(configured.ProxyProtocol);
        Assert.Equal(new[] { IPAddress.Parse("10.0.0.2") }, configured.ProxyProtocolTrustedSources.ToArray());
    }

    private static (TgWsProxyServer Proxy, ConcurrentQueue<string> Logs) ProxyProtocolServer()
    {
        var logs = new ConcurrentQueue<string>();
        var options = new TgWsProxyOptions
        {
            Secret = Secret,
            PoolSize = 0,
            CfProxyFallback = false,
            Verbose = true,
            ProxyProtocol = true,
        };
        return (new TgWsProxyServer(options, new FakeTgNetwork(), (_, m) => logs.Enqueue(m)), logs);
    }

    /// <summary>Replays a fixed input, optionally one byte per read, and records how much was consumed.</summary>
    private sealed class RecordingStream(byte[] input, int chunk = int.MaxValue) : Stream
    {
        private readonly MemoryStream _input = new(input);

        public int BytesRead { get; private set; }

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var n = _input.Read(buffer.Span[..Math.Min(buffer.Length, chunk)]);
            BytesRead += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count) { }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                _input.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Never delivers data; only cancellation ends a read.</summary>
    private sealed class BlockingStream : Stream
    {
        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count) { }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
