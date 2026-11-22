using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Engines.TgWsProxy;

namespace Bydlo.Tests;

/// <summary>
/// The native TgWsProxy port, end to end over in-memory pipes. No socket is opened: the proxy talks to
/// <see cref="FakeTgNetwork"/>, and both the Telegram client and the Telegram server are simulated here.
/// </summary>
public partial class TgWsProxyTests
{
    private static readonly byte[] Secret = Convert.FromHexString("0123456789abcdef0123456789abcdef");

    [Fact]
    public void Aes_ctr_matches_ecb_counter_blocks_and_is_chunk_independent()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var iv = Convert.FromHexString("000000000000000000000000FFFFFFFE"); // counter carries into the next byte
        var data = RandomNumberGenerator.GetBytes(10_000);

        using var aes = Aes.Create();
        aes.Key = key;
        var expected = new byte[data.Length];
        var counter = (byte[])iv.Clone();
        for (var block = 0; block * 16 < data.Length; block++)
        {
            var ks = aes.EncryptEcb(counter, PaddingMode.None);
            for (var i = 0; i < 16 && (block * 16) + i < data.Length; i++)
            {
                expected[(block * 16) + i] = (byte)(data[(block * 16) + i] ^ ks[i]);
            }

            for (var i = 15; i >= 0 && ++counter[i] == 0; i--)
            {
            }
        }

        using var whole = new AesCtr(key, iv);
        Assert.Equal(expected, whole.Transform(data));

        using var chunked = new AesCtr(key, iv);
        var output = new List<byte>();
        foreach (var size in new[] { 1, 15, 16, 17, 4095, 4097 })
        {
            output.AddRange(chunked.Transform(data.AsSpan(output.Count, size)));
        }

        output.AddRange(chunked.Transform(data.AsSpan(output.Count)));
        Assert.Equal(expected, output.ToArray());
    }

    [Theory]
    [InlineData(MtProto.Intermediate, 2, false)]
    [InlineData(MtProto.Abridged, 4, true)]
    [InlineData(MtProto.PaddedIntermediate, 10003, false)]
    public void Client_handshake_is_recognised_only_with_the_right_secret(uint tag, int dc, bool media)
    {
        using var client = new TelegramClient(Secret, tag, (short)(media ? -dc : dc));

        var parsed = MtProto.TryParseHandshake(client.Init, Secret);
        Assert.NotNull(parsed);
        Assert.Equal(dc, parsed.Dc);
        Assert.Equal(media, parsed.IsMedia);
        Assert.Equal(tag, parsed.ProtoTag);

        Assert.Null(MtProto.TryParseHandshake(client.Init, RandomNumberGenerator.GetBytes(16)));
    }

    [Fact]
    public void Relay_init_carries_protocol_and_dc_for_telegram()
    {
        for (var i = 0; i < 50; i++)
        {
            var init = MtProto.GenerateRelayInit(MtProto.Abridged, -4);
            Assert.NotEqual(0xEF, init[0]);
            Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(init.AsSpan(4)));

            using var server = new TelegramServer(init);
            Assert.Equal(MtProto.Abridged, server.ProtoTag);
            Assert.Equal(-4, server.DcIndex);
        }
    }

    [Fact]
    public void Splitter_cuts_stream_into_whole_packets()
    {
        var intermediate = new MsgSplitter(MtProto.Intermediate);
        byte[] stream = [.. Packet(5), .. Packet(3), .. Packet(8)];
        Assert.Empty(intermediate.Split(stream.AsSpan(0, 6), stream.AsSpan(0, 6)));
        var parts = intermediate.Split(stream.AsSpan(6, 10), stream.AsSpan(6, 10));
        Assert.Equal(new[] { 9, 7 }, parts.Select(p => p.Length));
        Assert.Equal(stream.AsSpan(9, 7).ToArray(), parts[1]);
        Assert.Equal(12, Assert.Single(intermediate.Split(stream.AsSpan(16), stream.AsSpan(16))).Length);
        Assert.Null(intermediate.Flush());

        var abridged = new MsgSplitter(MtProto.Abridged);
        byte[] small = [0x02, .. new byte[8]];
        byte[] big = [0x7F, 0x00, 0x01, 0x00, .. new byte[256 * 4]];
        Assert.Equal(new[] { 9, 4 + 1024 }, abridged.Split([.. small, .. big], [.. small, .. big]).Select(p => p.Length));

        // Unknown framing: everything passes through unsplit from then on.
        var broken = new MsgSplitter(MtProto.Intermediate);
        Assert.Single(broken.Split(new byte[10], new byte[10]));
        Assert.Single(broken.Split(new byte[3], new byte[3]));

        static byte[] Packet(int length)
        {
            var p = new byte[4 + length];
            BinaryPrimitives.WriteInt32LittleEndian(p, length);
            RandomNumberGenerator.Fill(p.AsSpan(4));
            return p;
        }
    }

    [Fact]
    public void Fake_tls_hello_is_verified_and_answered_with_signed_server_hello()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var hello = FakeTlsClientHello(Secret, now);

        var verified = FakeTls.VerifyClientHello(hello, Secret, now.AddSeconds(30));
        Assert.NotNull(verified);
        Assert.Equal((uint)now.ToUnixTimeSeconds(), verified.Timestamp);
        Assert.Equal(hello.AsSpan(44, 32).ToArray(), verified.SessionId);

        Assert.Null(FakeTls.VerifyClientHello(hello, RandomNumberGenerator.GetBytes(16), now));
        Assert.Null(FakeTls.VerifyClientHello(hello, Secret, now.AddMinutes(5)));

        var info = FakeTls.ParseClientHello(hello);
        Assert.Contains(0x1302, info.CipherSuites);
        Assert.Equal(32, info.KeyShares[FakeTls.GroupX25519]);

        var response = FakeTls.BuildServerHello(Secret, verified.ClientRandom, verified.SessionId, info, profiledAppDataLength: 100);
        Assert.Equal(FakeTls.RecordHandshake, response[0]);
        var serverRandom = response.AsSpan(FakeTls.ServerRandomOffset, 32).ToArray();
        var zeroed = (byte[])response.Clone();
        zeroed.AsSpan(FakeTls.ServerRandomOffset, 32).Clear();
        Assert.Equal(HMACSHA256.HashData(Secret, verified.ClientRandom.Concat(zeroed).ToArray()), serverRandom);
        Assert.Equal(0x1301, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(5 + 4 + 2 + 32 + 1 + 32)));

        // ServerHello, CCS, then exactly one ApplicationData record of the profiled size.
        var helloLength = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(3));
        var ccs = 5 + helloLength;
        Assert.Equal(FakeTls.RecordChangeCipherSpec, response[ccs]);
        Assert.Equal(FakeTls.RecordApplicationData, response[ccs + 6]);
        Assert.Equal(100, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(ccs + 9)));
        Assert.Equal(ccs + 6 + 5 + 100, response.Length);
        Assert.Equal(100, FakeTls.FirstAppDataRecordLength(response));
    }

    [Fact]
    public async Task Fake_tls_stream_unwraps_records_and_skips_ccs()
    {
        var payload = RandomNumberGenerator.GetBytes(40_000);
        byte[] wire = [0x14, 0x03, 0x03, 0x00, 0x01, 0x01, .. FakeTlsStream.Wrap(payload)];

        using var tls = new FakeTlsStream(new MemoryStream(wire));
        var read = new MemoryStream();
        await tls.CopyToAsync(read, 1000);
        Assert.Equal(payload, read.ToArray());
    }

    [Fact]
    public async Task WebSocket_upgrades_reassembles_fragments_and_answers_ping()
    {
        var (client, server) = DuplexPipe.Create();
        var serverTask = Task.Run(async () =>
        {
            var request = await ReadHttpHeadAsync(server);
            Assert.Contains("GET /apiws HTTP/1.1", request, StringComparison.Ordinal);
            Assert.Contains("Host: kws2.web.telegram.org", request, StringComparison.Ordinal);
            Assert.Contains("Sec-WebSocket-Protocol: binary", request, StringComparison.Ordinal);
            await server.WriteAsync("HTTP/1.1 101 Switching Protocols\r\n\r\n"u8.ToArray());

            var (op, payload) = await ReadClientFrameAsync(server);
            Assert.Equal(0x2, op);
            Assert.Equal("hello"u8.ToArray(), payload);

            await server.WriteAsync(Frame(0x9, "p"u8, fin: true));
            await server.WriteAsync(Frame(0x2, "ab"u8, fin: false));
            await server.WriteAsync(Frame(0x0, "cd"u8, fin: true));
            var (pongOp, pong) = await ReadClientFrameAsync(server);
            Assert.Equal(0xA, pongOp);
            Assert.Equal("p"u8.ToArray(), pong);
            await server.WriteAsync(Frame(0x8, [0x03, 0xE8], fin: true));
        });

        var ws = await RawWebSocket.UpgradeAsync(client, "kws2.web.telegram.org", "/apiws", CancellationToken.None);
        await ws.SendAsync("hello"u8.ToArray(), CancellationToken.None);
        Assert.Equal("abcd"u8.ToArray(), await ws.ReceiveAsync(CancellationToken.None));
        Assert.Null(await ws.ReceiveAsync(CancellationToken.None));
        Assert.True(ws.IsClosed);
        await serverTask;
        await ws.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Idle_websocket_notices_server_close_and_answers_ping_without_a_reader(bool closeFrame)
    {
        var (client, server) = DuplexPipe.Create();
        var serverTask = Task.Run(async () =>
        {
            await ReadHttpHeadAsync(server);
            await server.WriteAsync("HTTP/1.1 101 Switching Protocols\r\n\r\n"u8.ToArray());
            await server.WriteAsync(Frame(0x9, "idle"u8, fin: true));
            var (op, payload) = await ReadClientFrameAsync(server);
            Assert.Equal(0xA, op);
            Assert.Equal("idle"u8.ToArray(), payload);
            if (closeFrame)
            {
                await server.WriteAsync(Frame(0x8, [0x03, 0xE8], fin: true));
            }
            else
            {
                await server.DisposeAsync();
            }
        });

        // Nobody calls ReceiveAsync: this is a connection waiting in the pool.
        await using var ws = await RawWebSocket.UpgradeAsync(client, "kws2.web.telegram.org", "/apiws", CancellationToken.None);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ws.IsClosed && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(ws.IsClosed);
        await Assert.ThrowsAsync<IOException>(() => ws.SendAsync(new byte[1], CancellationToken.None));
    }

    [Fact]
    public async Task Pool_hands_out_a_live_connection_after_warmup()
    {
        var network = new FakeTgNetwork();
        var options = new TgWsProxyOptions { Secret = Secret, PoolSize = 1, DcRedirects = new Dictionary<int, string> { [2] = "149.154.167.220" }, CfProxyFallback = false };
        await using var proxy = new TgWsProxyServer(options, network, (_, _) => { });
        await proxy.StartAsync();

        // Warmup opens one regular and one media connection to the configured IP with the real Host name.
        var upstreams = new[] { await network.NextAsync(), await network.NextAsync() };
        foreach (var upstream in upstreams)
        {
            Assert.Equal(("149.154.167.220", 443, true), (upstream.Host, upstream.Port, upstream.Validate));
            var request = await ReadHttpHeadAsync(upstream.Stream);
            Assert.Contains("GET /apiws HTTP/1.1", request, StringComparison.Ordinal);
            await upstream.Stream.WriteAsync("HTTP/1.1 101 Switching Protocols\r\n\r\n"u8.ToArray());
        }

        var pool = proxy.Pool;
        RawWebSocket? ws = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ws is null && DateTime.UtcNow < deadline)
        {
            ws = pool.Get(2, isMedia: false, isTestDc: false);
            await Task.Delay(10);
        }

        Assert.NotNull(ws);
        Assert.False(ws.IsClosed);
        await ws.DisposeAsync();
    }

    [Fact]
    public async Task Listener_is_rebound_after_the_socket_dies()
    {
        var network = new FakeTgNetwork();
        var logs = new ConcurrentQueue<string>();
        await using var proxy = new TgWsProxyServer(new TgWsProxyOptions { Secret = Secret, PoolSize = 0, CfProxyFallback = false }, network, (_, m) => logs.Enqueue(m));
        string? fault = null;
        proxy.Faulted += (_, m) => fault = m;
        await proxy.StartAsync();
        Assert.Equal(1, network.Listens);

        network.Listener!.Fail(new IOException("socket died"));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (network.Listens < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(2, network.Listens);
        Assert.Null(fault);
        Assert.Contains(logs, l => l.Contains("Server restored", StringComparison.Ordinal));

        // The new listener serves clients: a wrong handshake is still answered by the proxy.
        var (clientSide, proxySide) = DuplexPipe.Create();
        network.Listener!.Accept(proxySide);
        await clientSide.WriteAsync(new byte[64]);
        await clientSide.DisposeAsync();
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (!logs.Any(l => l.Contains("bad handshake", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Contains(logs, l => l.Contains("bad handshake", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Proxy_faults_when_the_port_cannot_be_bound_again()
    {
        var network = new FakeTgNetwork();
        await using var proxy = new TgWsProxyServer(new TgWsProxyOptions { Secret = Secret, PoolSize = 0, CfProxyFallback = false }, network, (_, _) => { });
        var faulted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Faulted += (_, m) => faulted.TrySetResult(m);
        await proxy.StartAsync();

        network.ListenError = new IOException("address in use");
        network.Listener!.Fail(new IOException("socket died"));
        var message = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains("socket died", message, StringComparison.Ordinal);
        Assert.Equal(4, network.Listens); // first bind + three attempts
    }

    [Fact]
    public async Task WebSocket_upgrade_failure_reports_status()
    {
        var (client, server) = DuplexPipe.Create();
        _ = Task.Run(async () =>
        {
            await ReadHttpHeadAsync(server);
            await server.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
        });

        var ex = await Assert.ThrowsAsync<WsHandshakeException>(() => RawWebSocket.UpgradeAsync(client, "x", "/apiws", CancellationToken.None));
        Assert.Equal(403, ex.StatusCode);
    }

    [Fact]
    public async Task Proxy_relays_through_cloudflare_websocket_with_reencryption()
    {
        var network = new FakeTgNetwork();
        var options = new TgWsProxyOptions { Secret = Secret, CfProxyDomains = ["example.com"], PoolSize = 0 };
        await using var proxy = new TgWsProxyServer(options, network, (_, _) => { });

        using var client = new TelegramClient(Secret, MtProto.Intermediate, 2);
        var (clientSide, proxySide) = DuplexPipe.Create();
        var handled = proxy.HandleClientAsync(proxySide, "test");

        await clientSide.WriteAsync(client.Init);
        var packets = new[] { RandomNumberGenerator.GetBytes(40), RandomNumberGenerator.GetBytes(1200) };
        await clientSide.WriteAsync(client.Encrypt([.. Intermediate(packets[0]), .. Intermediate(packets[1])]));

        var upstream = await network.NextAsync();
        Assert.Equal(("kws2.example.com", 443, "kws2.example.com", true), (upstream.Host, upstream.Port, upstream.ServerName!, upstream.Validate));
        var request = await ReadHttpHeadAsync(upstream.Stream);
        Assert.Contains("Host: kws2.example.com", request, StringComparison.Ordinal);
        await upstream.Stream.WriteAsync("HTTP/1.1 101 Switching Protocols\r\n\r\n"u8.ToArray());

        var (_, relayInit) = await ReadClientFrameAsync(upstream.Stream);
        using var telegram = new TelegramServer(relayInit);
        Assert.Equal(MtProto.Intermediate, telegram.ProtoTag);
        Assert.Equal(2, telegram.DcIndex);

        // One WebSocket frame per MTProto packet.
        foreach (var expected in packets)
        {
            var (_, frame) = await ReadClientFrameAsync(upstream.Stream);
            Assert.Equal(Intermediate(expected), telegram.Decrypt(frame));
        }

        var answer = Intermediate(RandomNumberGenerator.GetBytes(300));
        await upstream.Stream.WriteAsync(Frame(0x2, telegram.Encrypt(answer), fin: true));
        var received = new byte[answer.Length];
        await clientSide.ReadExactlyAsync(received);
        Assert.Equal(answer, client.Decrypt(received));

        await upstream.Stream.WriteAsync(Frame(0x8, [], fin: true));
        await handled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(40 + 1200 + 8, proxy.Stats.BytesUp);
        Assert.Equal(answer.Length, proxy.Stats.BytesDown);
        Assert.Equal(0, proxy.Stats.ActiveConnections);
    }

    [Fact]
    public async Task Proxy_falls_back_to_direct_tcp_and_inside_fake_tls()
    {
        var network = new FakeTgNetwork();
        var options = new TgWsProxyOptions { Secret = Secret, CfProxyFallback = false, FakeTlsDomain = "example.com", PoolSize = 0 };
        await using var proxy = new TgWsProxyServer(options, network, (_, _) => { });

        using var client = new TelegramClient(Secret, MtProto.PaddedIntermediate, -4);
        var (clientSide, proxySide) = DuplexPipe.Create();
        var handled = proxy.HandleClientAsync(proxySide, "test");

        await clientSide.WriteAsync(FakeTlsClientHello(Secret, DateTimeOffset.UtcNow));
        var head = new byte[5];
        await clientSide.ReadExactlyAsync(head);
        await clientSide.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(3)) + 6]); // ServerHello body + CCS
        await clientSide.ReadExactlyAsync(head);
        Assert.Equal(FakeTls.RecordApplicationData, head[0]);
        await clientSide.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(3))]);

        var tls = new FakeTlsStream(clientSide);
        var message = Intermediate(RandomNumberGenerator.GetBytes(64));
        await tls.WriteAsync((byte[])[.. client.Init, .. client.Encrypt(message)]);

        var upstream = await network.NextAsync();
        Assert.Equal((TgWsProxyOptions.DcDefaultIps[4], 443, (string?)null), (upstream.Host, upstream.Port, upstream.ServerName));
        var relayInit = new byte[64];
        await upstream.Stream.ReadExactlyAsync(relayInit);
        using var telegram = new TelegramServer(relayInit);
        Assert.Equal(-4, telegram.DcIndex);
        var forwarded = new byte[message.Length];
        await upstream.Stream.ReadExactlyAsync(forwarded);
        Assert.Equal(message, telegram.Decrypt(forwarded));

        var answer = Intermediate(RandomNumberGenerator.GetBytes(20));
        await upstream.Stream.WriteAsync(telegram.Encrypt(answer));
        var received = new byte[answer.Length];
        await tls.ReadExactlyAsync(received);
        Assert.Equal(answer, client.Decrypt(received));

        await upstream.Stream.DisposeAsync();
        await handled.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Wrong_secret_is_rejected_without_contacting_telegram()
    {
        var network = new FakeTgNetwork();
        var logs = new ConcurrentQueue<string>();
        await using var proxy = new TgWsProxyServer(new TgWsProxyOptions { Secret = Secret, PoolSize = 0 }, network, (_, m) => logs.Enqueue(m));

        var (clientSide, proxySide) = DuplexPipe.Create();
        var handled = proxy.HandleClientAsync(proxySide, "test");
        using (var stranger = new TelegramClient(RandomNumberGenerator.GetBytes(16), MtProto.Intermediate, 2))
        {
            await clientSide.WriteAsync(stranger.Init);
        }

        await clientSide.DisposeAsync();
        await handled.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, network.Connections);
        Assert.Contains(logs, l => l.Contains("bad handshake", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_tls_probe_gets_redirect_when_fake_tls_is_enabled()
    {
        await using var proxy = new TgWsProxyServer(new TgWsProxyOptions { Secret = Secret, FakeTlsDomain = "example.com", PoolSize = 0 }, new FakeTgNetwork(), (_, _) => { });
        var (clientSide, proxySide) = DuplexPipe.Create();
        var handled = proxy.HandleClientAsync(proxySide, "test");
        await clientSide.WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());
        var text = await new StreamReader(clientSide).ReadToEndAsync();
        Assert.StartsWith("HTTP/1.1 301", text, StringComparison.Ordinal);
        Assert.Contains("Location: https://example.com/", text, StringComparison.Ordinal);
        await handled.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Cloudflare_domain_list_decodes_and_logs_are_censored()
    {
        Assert.Equal("yza.co.uk", CfProxyDomains.Decode("bcd.com"));
        Assert.Equal("Za-1.co.uk", CfProxyDomains.Decode("Bc-1.com"));
        Assert.Equal("plain.net", CfProxyDomains.Decode("plain.net"));
        Assert.Equal(20, CfProxyDomains.Defaults.Count);
        Assert.All(CfProxyDomains.Defaults, d => Assert.True(CfProxyDomains.IsValidDomain(d), d));
        Assert.Equal(new[] { "yza.co.uk" }, CfProxyDomains.ParseList("# comment\nbcd.com\n\nbcd.com\nbad_.com\n"));

        Assert.Equal("kws2.web.telegram.org via kw**.exa****.com", CfProxyDomains.Censor("kws2.web.telegram.org via kws2.example.com"));

        var balancer = new CfProxyDomains();
        balancer.Update(["a.example", "b.example", "c.example"]);
        balancer.Prefer(2, "b.example"); // Update() picks a random domain per DC, so this may already be b.example
        Assert.False(balancer.Prefer(2, "b.example"));
        Assert.True(balancer.Prefer(2, "c.example"));
        Assert.Equal("c.example", balancer.CandidatesFor(2)[0]);
        Assert.Equal(3, balancer.CandidatesFor(2).Distinct().Count());
    }

    [Fact]
    public async Task Engine_refuses_to_open_sockets_under_the_development_lock()
    {
        NetworkSafetyGuard.OverrideForTests(true);
        try
        {
            var engine = new TgWsProxyEngine(new LogRedactor(), new FakeTgNetwork());
            var request = new EngineStartRequest
            {
                Settings = new AppSettings(),
                Secrets = new ProfileSecrets { Values = { ["secret"] = "0123456789abcdef0123456789abcdef" } },
                WorkDirectory = TestDirs.New("tg-native"),
            };

            var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(request));
            Assert.Contains(NetworkSafetyGuard.SafeDevVariable, ex.Message, StringComparison.Ordinal);
            Assert.Equal(EngineState.Failed, engine.State);

            var noSecret = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(request with { Secrets = ProfileSecrets.Empty }));
            Assert.Equal("error.engine.tg.secret", noSecret.LocalizationKey);
        }
        finally
        {
            NetworkSafetyGuard.OverrideForTests(null);
        }
    }

    [Fact]
    public async Task Engine_is_built_in_and_needs_no_installation()
    {
        var availability = await new TgWsProxyEngine(new LogRedactor()).CheckAvailabilityAsync(new AppSettings());
        Assert.True(availability.IsReady);
        Assert.Equal(TgWsProxyEngine.BuiltIn, availability.ExecutablePath);
    }

    // ---------- helpers ----------

    private static byte[] Intermediate(byte[] payload)
    {
        var p = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(p, payload.Length);
        payload.CopyTo(p, 4);
        return p;
    }

    private static byte[] Frame(byte opcode, ReadOnlySpan<byte> payload, bool fin)
    {
        var frame = RawWebSocket.BuildFrame(opcode, payload, mask: false);
        if (!fin)
        {
            frame[0] &= 0x7F;
        }

        return frame;
    }

    private static async Task<string> ReadHttpHeadAsync(Stream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            await stream.ReadExactlyAsync(one);
            head.Add(one[0]);
        }

        return Encoding.ASCII.GetString([.. head]);
    }

    /// <summary>Reads one masked client frame (server side of the WebSocket).</summary>
    private static async Task<(byte Opcode, byte[] Payload)> ReadClientFrameAsync(Stream stream)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header);
        Assert.True((header[1] & 0x80) != 0, "client frames must be masked");
        long length = header[1] & 0x7F;
        if (length == 126)
        {
            var ext = new byte[2];
            await stream.ReadExactlyAsync(ext);
            length = BinaryPrimitives.ReadUInt16BigEndian(ext);
        }
        else if (length == 127)
        {
            var ext = new byte[8];
            await stream.ReadExactlyAsync(ext);
            length = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
        }

        var mask = new byte[4];
        await stream.ReadExactlyAsync(mask);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload);
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] ^= mask[i & 3];
        }

        return ((byte)(header[0] & 0x0F), payload);
    }

    /// <summary>A Fake TLS ClientHello as Telegram builds it: random = HMAC(secret, hello) with the timestamp XORed in.</summary>
    private static byte[] FakeTlsClientHello(byte[] secret, DateTimeOffset now)
    {
        var body = new List<byte> { 0x03, 0x03 };
        body.AddRange(new byte[32]);
        body.Add(32);
        body.AddRange(RandomNumberGenerator.GetBytes(32));
        body.AddRange(new byte[] { 0x00, 0x04, 0x13, 0x02, 0x13, 0x01, 0x01, 0x00 });
        var keyShare = new List<byte> { 0x00, 0x24, 0x00, 0x1d, 0x00, 0x20 };
        keyShare.AddRange(RandomNumberGenerator.GetBytes(32));
        var extensions = new List<byte> { 0x00, 0x33, 0x00, (byte)keyShare.Count };
        extensions.AddRange(keyShare);
        body.Add(0x00);
        body.Add((byte)extensions.Count);
        body.AddRange(extensions);

        var hello = new List<byte> { 0x16, 0x03, 0x01, 0x00, (byte)(body.Count + 4), 0x01, 0x00, 0x00, (byte)body.Count };
        hello.AddRange(body);
        var data = hello.ToArray();

        var digest = HMACSHA256.HashData(secret, data);
        var ts = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(ts, (uint)now.ToUnixTimeSeconds());
        for (var i = 0; i < 4; i++)
        {
            digest[28 + i] ^= ts[i];
        }

        digest.CopyTo(data, FakeTls.ClientRandomOffset);
        return data;
    }

    /// <summary>The Telegram app's side of obfuscated2 with a proxy secret.</summary>
    private sealed class TelegramClient : IDisposable
    {
        private readonly AesCtr _encrypt;
        private readonly AesCtr _decrypt;

        public TelegramClient(byte[] secret, uint tag, short dcIndex)
        {
            var init = RandomNumberGenerator.GetBytes(64);
            init[0] = 0x01;
            BinaryPrimitives.WriteUInt32LittleEndian(init.AsSpan(56), tag);
            BinaryPrimitives.WriteInt16LittleEndian(init.AsSpan(60), dcIndex);

            _encrypt = new AesCtr(SHA256.HashData([.. init.AsSpan(8, 32), .. secret]), init.AsSpan(40, 16));
            var encrypted = _encrypt.Transform(init);
            Init = [.. init.AsSpan(0, 56), .. encrypted.AsSpan(56, 8)];

            var reversed = init.AsSpan(8, 48).ToArray();
            Array.Reverse(reversed);
            _decrypt = new AesCtr(SHA256.HashData([.. reversed.AsSpan(0, 32), .. secret]), reversed.AsSpan(32, 16));
        }

        public byte[] Init { get; }

        public byte[] Encrypt(byte[] data) => _encrypt.Transform(data);

        public byte[] Decrypt(byte[] data) => _decrypt.Transform(data);

        public void Dispose()
        {
            _encrypt.Dispose();
            _decrypt.Dispose();
        }
    }

    /// <summary>Telegram's side of plain obfuscated2 (the relay init the proxy sends).</summary>
    private sealed class TelegramServer : IDisposable
    {
        private readonly AesCtr _decrypt;
        private readonly AesCtr _encrypt;

        public TelegramServer(byte[] init)
        {
            _decrypt = new AesCtr(init.AsSpan(8, 32), init.AsSpan(40, 16));
            var plain = _decrypt.Transform(init);
            ProtoTag = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(56));
            DcIndex = BinaryPrimitives.ReadInt16LittleEndian(plain.AsSpan(60));

            var reversed = init.AsSpan(8, 48).ToArray();
            Array.Reverse(reversed);
            _encrypt = new AesCtr(reversed.AsSpan(0, 32), reversed.AsSpan(32, 16));
        }

        public uint ProtoTag { get; }

        public short DcIndex { get; }

        public byte[] Decrypt(byte[] data) => _decrypt.Transform(data);

        public byte[] Encrypt(byte[] data) => _encrypt.Transform(data);

        public void Dispose()
        {
            _encrypt.Dispose();
            _decrypt.Dispose();
        }
    }
}

/// <summary>Hands every outbound connection of the proxy to the test as the far end of an in-memory pipe.</summary>
internal sealed class FakeTgNetwork : ITgNetwork
{
    private readonly System.Threading.Channels.Channel<Upstream> _connections = System.Threading.Channels.Channel.CreateUnbounded<Upstream>();
    private int _count;

    public int Connections => Volatile.Read(ref _count);

    public Task<Stream> ConnectAsync(string host, int port, CancellationToken ct) => Open(host, port, null, false);

    public Task<Stream> ConnectTlsAsync(string host, int port, string serverName, bool validateCertificate, CancellationToken ct) =>
        Open(host, port, serverName, validateCertificate);

    public Task<string?> GetStringAsync(Uri uri, IReadOnlyDictionary<string, string> pinnedHosts, CancellationToken ct) => Task.FromResult<string?>(null);

    public int Listens => Volatile.Read(ref _listens);

    public FakeListener? Listener { get; private set; }

    /// <summary>When set, every further <see cref="Listen"/> call fails with it.</summary>
    public Exception? ListenError { get; set; }

    private int _listens;

    public ITgListener Listen(string host, int port)
    {
        Interlocked.Increment(ref _listens);
        if (ListenError is { } error)
        {
            throw error;
        }

        Listener = new FakeListener();
        return Listener;
    }

    public async Task<Upstream> NextAsync() => await _connections.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private Task<Stream> Open(string host, int port, string? serverName, bool validate)
    {
        Interlocked.Increment(ref _count);
        var (proxySide, testSide) = DuplexPipe.Create();
        _connections.Writer.TryWrite(new Upstream(host, port, serverName, validate, testSide));
        return Task.FromResult(proxySide);
    }

    public sealed record Upstream(string Host, int Port, string? ServerName, bool Validate, Stream Stream);

    /// <summary>Accepts connections pushed by the test; <see cref="Fail"/> simulates a dead listening socket.</summary>
    public sealed class FakeListener : ITgListener
    {
        private readonly System.Threading.Channels.Channel<Stream> _incoming = System.Threading.Channels.Channel.CreateUnbounded<Stream>();
        private Exception? _failure;

        public void Accept(Stream stream) => _incoming.Writer.TryWrite(stream);

        public void Fail(Exception error)
        {
            _failure = error;
            _incoming.Writer.TryComplete();
        }

        public async Task<(Stream Stream, string Label, IPAddress? RemoteAddress)> AcceptAsync(CancellationToken ct)
        {
            try
            {
                return (await _incoming.Reader.ReadAsync(ct), "fake", IPAddress.Loopback);
            }
            catch (System.Threading.Channels.ChannelClosedException) when (_failure is not null)
            {
                throw _failure;
            }
        }

        public void Dispose() => _incoming.Writer.TryComplete();
    }
}

/// <summary>Two connected in-memory streams; disposing one end signals end-of-stream to the other.</summary>
internal sealed class DuplexPipe(PipeReader reader, PipeWriter writer) : Stream
{
    private readonly Stream _read = reader.AsStream();
    private readonly Stream _write = writer.AsStream();

    public static (Stream A, Stream B) Create()
    {
        var ab = new Pipe();
        var ba = new Pipe();
        return (new DuplexPipe(ba.Reader, ab.Writer), new DuplexPipe(ab.Reader, ba.Writer));
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _read.ReadAsync(buffer, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _write.WriteAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _read.ReadAsync(buffer, offset, count, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _write.WriteAsync(buffer, offset, count, cancellationToken);

    public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

    public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

    public override void Flush() => _write.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _write.Dispose();
            _read.Dispose();
        }

        base.Dispose(disposing);
    }
}
