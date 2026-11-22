using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Bydlo.Engines.TgWsProxy;

/// <summary>
/// Native port of Flowseal tg-ws-proxy (MIT): a local MTProto proxy for Telegram clients that carries each
/// connection over Telegram's WebSocket endpoint (<c>kws*.web.telegram.org/apiws</c>), with Cloudflare and direct
/// TCP fallbacks and optional Fake TLS masking. Runs inside BYDLO, no Python or external process.
/// </summary>
public sealed class TgWsProxyServer : IAsyncDisposable
{
    private const int ReadBufferSize = 64 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BadHandshakeDrainTimeout = TimeSpan.FromSeconds(10);
    private const int BadHandshakeDrainLimit = 64 * 1024;
    private static readonly TimeSpan TcpBackoffInitial = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TcpBackoffMax = TimeSpan.FromHours(1);
    private static readonly TimeSpan ServerHelloDelayMin = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan ServerHelloDelayMax = TimeSpan.FromMilliseconds(24);
    private static readonly TimeSpan ListenerRestartDelay = TimeSpan.FromSeconds(1);
    private const int ListenerRestartAttempts = 3;

    private readonly CancellationTokenSource _stop = new();
    private readonly TgWsProxyContext _context;
    private readonly WsPool _pool;
    private readonly FakeTlsReplayCache _replayCache;
    private readonly ConcurrentDictionary<Task, byte> _clients = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset RetryAfter, bool Connecting)> _tcpBackoff = new();
    private readonly List<Task> _background = [];
    private readonly object _listenerGate = new();
    private ITgListener? _listener;
    private int? _appDataLength;
    private int _disposed;

    public TgWsProxyServer(TgWsProxyOptions options, ITgNetwork network, Action<TgLogLevel, string> log, TimeProvider? time = null)
        : this(options, network, log, time, FakeTlsReplayCache.Shared)
    {
    }

    internal TgWsProxyServer(TgWsProxyOptions options, ITgNetwork network, Action<TgLogLevel, string> log,
        TimeProvider? time, FakeTlsReplayCache replayCache)
    {
        _replayCache = replayCache;
        _context = new TgWsProxyContext(options, network, time ?? TimeProvider.System, log, _stop.Token);
        _context.CfDomains.Update(options.CfProxyDomains.Count > 0 ? options.CfProxyDomains : CfProxyDomains.Defaults);
        _pool = new WsPool(_context);
    }

    public TgWsProxyStats Stats => _context.Stats;

    internal WsPool Pool => _pool;

    /// <summary>Raised when the listener dies unexpectedly; the proxy no longer accepts connections.</summary>
    public event EventHandler<string>? Faulted;

    private TgWsProxyOptions Options => _context.Options;

    /// <summary>Probes the Fake TLS profile, binds the listener and starts the pool. Returns once listening.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Options.CfProxyDomains.Count == 0 && Options.CfProxyFallback)
        {
            _background.Add(Task.Run(() => RefreshCfDomainsAsync(_stop.Token), CancellationToken.None));
        }

        if (Options.FakeTlsDomain.Length > 0)
        {
            // Before listening, so a domain that points at this proxy cannot answer the probe itself.
            _appDataLength = await FakeTls.ProbeAppDataLengthAsync(_context.Network, Options.FakeTlsDomain, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            _context.Log(_appDataLength is null ? TgLogLevel.Warning : TgLogLevel.Info, _appDataLength is null
                ? "Fake TLS profile unavailable, ServerHello uses random record sizes"
                : $"Fake TLS profile: first encrypted record {_appDataLength} bytes");
        }

        var bound = _context.Network.Listen(Options.Host, Options.Port);
        lock (_listenerGate)
        {
            _listener = bound;
        }

        LogBanner();
        _background.Add(Task.Run(AcceptLoopAsync, CancellationToken.None));
        _background.Add(Task.Run(() => _pool.MaintainAsync(_stop.Token), CancellationToken.None));
        _background.Add(Task.Run(StatsLoopAsync, CancellationToken.None));
        _pool.Warmup();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        ITgListener? current;
        lock (_listenerGate)
        {
            current = _listener;
        }

        // Unblock AcceptAsync for listeners that ignore the cancellation token.
        try
        {
            current?.Dispose();
        }
        catch (Exception)
        {
        }

        await Task.WhenAll([.. _clients.Keys, .. _background]).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // The accept loop may have published a replacement listener after the
        // first dispose (issue #346): close whatever is current after the loop
        // has exited so no socket stays bound. See also #51.
        ITgListener? last;
        lock (_listenerGate)
        {
            last = _listener;
            _listener = null;
        }

        if (last is not null && !ReferenceEquals(last, current))
        {
            try
            {
                last.Dispose();
            }
            catch (Exception)
            {
            }
        }

        await _pool.CloseAsync().ConfigureAwait(false);
        _context.Log(TgLogLevel.Info, "Stopped. Final stats: " + Stats.Summary());
        _stop.Dispose();
    }

    /// <summary>Serves one accepted client connection (exposed for in-memory tests).</summary>
    internal async Task HandleClientAsync(Stream client, string label, IPAddress? remoteAddress = null)
    {
        Stats.Opened();
        var ct = _stop.Token;
        try
        {
            var init = await ReadClientInitAsync(client, label, remoteAddress, ct).ConfigureAwait(false);
            if (init is null)
            {
                return;
            }

            var (handshakeBytes, stream) = init.Value;
            var handshake = MtProto.TryParseHandshake(handshakeBytes, Options.Secret);
            if (handshake is null)
            {
                Stats.Bad();
                _context.Log(TgLogLevel.Warning, $"[{label}] bad handshake (wrong secret or proto)");
                await DrainAsync(stream, ct).ConfigureAwait(false);
                return;
            }

            var dc = handshake.Dc;
            var isTestDc = Options.ForceTestDc || dc >= 10000;
            if (dc >= 10000)
            {
                _context.Log(TgLogLevel.Info, $"[{label}] test DC{dc} -> DC{dc - 10000}");
                dc -= 10000;
            }

            var mediaTag = handshake.IsMedia ? " media" : "";
            _context.Log(TgLogLevel.Debug, $"[{label}] handshake ok: DC{dc}{mediaTag} proto=0x{handshake.ProtoTag:X8}");

            var relayInit = MtProto.GenerateRelayInit(handshake.ProtoTag, (short)(handshake.IsMedia ? -dc : dc));
            using var crypto = MtProto.CreateCrypto(handshake.PrekeyAndIv, Options.Secret, relayInit);
            var route = new Route(label, dc, handshake.IsMedia, isTestDc, mediaTag);

            var ws = _pool.Get(dc, handshake.IsMedia, isTestDc);
            if (ws is null)
            {
                _context.Log(TgLogLevel.Info, $"[{label}] DC{dc}{mediaTag} WS pool unavailable -> fallback");
                var ok = await FallbackAsync(stream, relayInit, route, crypto, new MsgSplitter(handshake.ProtoTag), ct).ConfigureAwait(false);
                _context.Log(ok ? TgLogLevel.Info : TgLogLevel.Warning, ok
                    ? $"[{label}] DC{dc}{mediaTag} fallback closed"
                    : $"[{label}] DC{dc}{mediaTag} no fallback available");
                return;
            }

            _context.Log(TgLogLevel.Info, $"[{label}] DC{dc}{mediaTag} -> WS pool hit");
            Stats.Ws();
            await BridgeWebSocketAsync(stream, ws, relayInit, route, crypto, new MsgSplitter(handshake.ProtoTag), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _context.Log(TgLogLevel.Debug, $"[{label}] cancelled");
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
            _context.Log(TgLogLevel.Debug, $"[{label}] client disconnected: {ex.Message}");
        }
        catch (Exception ex)
        {
            _context.Log(TgLogLevel.Error, $"[{label}] unexpected: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Stats.Closed();
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<(byte[] Handshake, Stream Stream)?> ReadClientInitAsync(Stream client, string label, IPAddress? remoteAddress, CancellationToken ct)
    {
        if (Options.ProxyProtocol)
        {
            if (!ProxyProtocol.IsTrustedSource(remoteAddress, Options.ProxyProtocolTrustedSources))
            {
                // Bytes of an untrusted peer are never interpreted as a header: they stay client data.
                _context.Log(TgLogLevel.Debug, $"[{label}] PROXY protocol header not expected from this peer");
            }
            else
            {
                var header = await ProxyProtocol.ReadAsync(client, ProxyProtocol.ReadTimeout, ct).ConfigureAwait(false);
                if (header is null)
                {
                    Stats.Bad();
                    _context.Log(TgLogLevel.Warning, $"[{label}] invalid PROXY protocol header, closing");
                    return null;
                }

                label = header.Describe(label);
                _context.Log(TgLogLevel.Debug, $"[{label}] PROXY protocol v1 header accepted ({header.Family})");
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        var first = new byte[1];
        if (await client.ReadAtLeastAsync(first, 1, throwOnEndOfStream: false, timeout.Token).ConfigureAwait(false) == 0)
        {
            _context.Log(TgLogLevel.Debug, $"[{label}] client disconnected before handshake");
            return null;
        }

        var masking = Options.FakeTlsDomain;
        if (first[0] == FakeTls.RecordHandshake && masking.Length > 0)
        {
            var header = new byte[5];
            header[0] = first[0];
            await client.ReadExactlyAsync(header.AsMemory(1), timeout.Token).ConfigureAwait(false);
            var recordLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3));
            var hello = new byte[5 + recordLength];
            header.CopyTo(hello, 0);
            await client.ReadExactlyAsync(hello.AsMemory(5), timeout.Token).ConfigureAwait(false);

            var verified = FakeTls.VerifyClientHello(hello, Options.Secret, _context.Time.GetUtcNow());
            var replayed = verified is not null && !_replayCache.FirstSeen(verified.ClientRandom);
            if (verified is null || replayed)
            {
                _context.Log(TgLogLevel.Debug, $"[{label}] Fake TLS {(replayed ? "hello replayed" : "verify failed")} (size={hello.Length}) -> masking");
                await ProxyToMaskingDomainAsync(client, hello, masking, label, ct).ConfigureAwait(false);
                return null;
            }

            _context.Log(TgLogLevel.Debug, $"[{label}] Fake TLS handshake ok (ts={verified.Timestamp})");
            var serverHello = FakeTls.BuildServerHello(Options.Secret, verified.ClientRandom, verified.SessionId, FakeTls.ParseClientHello(hello), _appDataLength);
            var delay = ServerHelloDelayMin + ((ServerHelloDelayMax - ServerHelloDelayMin) * Random.Shared.NextDouble());
            await Task.Delay(delay, _context.Time, timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(serverHello, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);

            var tls = new FakeTlsStream(client);
            var init = new byte[MtProto.HandshakeLength];
            await tls.ReadExactlyAsync(init, timeout.Token).ConfigureAwait(false);
            return (init, tls);
        }

        if (masking.Length > 0)
        {
            _context.Log(TgLogLevel.Debug, $"[{label}] non-TLS byte 0x{first[0]:X2} -> HTTP redirect");
            var redirect = $"HTTP/1.1 301 Moved Permanently\r\nLocation: https://{masking}/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await client.WriteAsync(Encoding.ASCII.GetBytes(redirect), timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
            return null;
        }

        var handshake = new byte[MtProto.HandshakeLength];
        handshake[0] = first[0];
        await client.ReadExactlyAsync(handshake.AsMemory(1), timeout.Token).ConfigureAwait(false);
        return (handshake, client);
    }

    private async Task<bool> FallbackAsync(Stream client, byte[] relayInit, Route route, MtProtoCrypto crypto, MsgSplitter splitter, CancellationToken ct)
    {
        var table = route.IsTestDc ? TgWsProxyOptions.DcTestIps : TgWsProxyOptions.DcDefaultIps;
        var directIp = table.GetValueOrDefault(route.Dc);

        if (Options.CfProxyFallback && !route.IsTestDc
            && await CfProxyFallbackAsync(client, relayInit, route, crypto, splitter, ct).ConfigureAwait(false))
        {
            return true;
        }

        return directIp is not null && await TcpFallbackAsync(client, directIp, relayInit, route, crypto, ct).ConfigureAwait(false);
    }

    private async Task<bool> CfProxyFallbackAsync(Stream client, byte[] relayInit, Route route, MtProtoCrypto crypto, MsgSplitter splitter, CancellationToken ct)
    {
        _context.Log(TgLogLevel.Info, $"[{route.Label}] DC{route.Dc}{route.MediaTag} -> trying CF proxy");
        foreach (var baseDomain in _context.CfDomains.CandidatesFor(route.Dc))
        {
            var domain = $"kws{route.Dc}.{baseDomain}";
            RawWebSocket ws;
            try
            {
                ws = await RawWebSocket.ConnectAsync(_context.Network, domain, domain, "/apiws", TimeSpan.FromSeconds(10), ct, secure: !Options.DisableSecure).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _context.Log(TgLogLevel.Warning, $"[{route.Label}] DC{route.Dc}{route.MediaTag} CF proxy failed: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (_context.CfDomains.Prefer(route.Dc, baseDomain))
            {
                _context.Log(TgLogLevel.Info, $"[{route.Label}] Switched active CF domain");
            }

            Stats.CfProxy();
            await BridgeWebSocketAsync(client, ws, relayInit, route, crypto, splitter, ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<bool> TcpFallbackAsync(Stream client, string ip, byte[] relayInit, Route route, MtProtoCrypto crypto, CancellationToken ct)
    {
        var now = _context.Time.GetUtcNow();
        var state = _tcpBackoff.GetValueOrDefault(ip);
        if (state.Connecting || now < state.RetryAfter)
        {
            _context.Log(TgLogLevel.Debug, $"[{route.Label}] TCP fallback to {ip}:443 skipped (backoff or connecting)");
            return false;
        }

        _tcpBackoff[ip] = state with { Connecting = true };
        Stream? remote = null;
        try
        {
            try
            {
                _context.Log(TgLogLevel.Info, $"[{route.Label}] TCP fallback to {ip}:443");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                remote = await _context.Network.ConnectAsync(ip, 443, timeout.Token).ConfigureAwait(false);
                await remote.WriteAsync(relayInit, timeout.Token).ConfigureAwait(false);
                await remote.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var failures = state.Failures + 1;
                var delay = TcpBackoffInitial * Math.Pow(2, Math.Min(failures - 1, 7));
                if (delay > TcpBackoffMax)
                {
                    delay = TcpBackoffMax;
                }

                _tcpBackoff[ip] = (failures, _context.Time.GetUtcNow() + delay, false);
                _context.Log(TgLogLevel.Warning, $"[{route.Label}] TCP fallback to {ip}:443 failed: {ex.Message}; retry in {delay.TotalSeconds:0}s");
                return false;
            }

            _tcpBackoff.TryRemove(ip, out _);
            Stats.TcpFallback();
            await BridgeTcpAsync(client, remote, route, crypto, ct).ConfigureAwait(false);

            return true;
        }
        finally
        {
            // Connect may succeed before WriteAsync/FlushAsync fails or is cancelled (#401).
            if (remote is not null)
            {
                await remote.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Client TCP ⇄ Telegram WebSocket with re-encryption; one frame per MTProto packet upstream.</summary>
    private async Task BridgeWebSocketAsync(Stream client, RawWebSocket ws, byte[] relayInit, Route route, MtProtoCrypto crypto, MsgSplitter splitter, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long up = 0, down = 0, upPackets = 0, downPackets = 0;
        var reason = "normal";
        var started = _context.Time.GetTimestamp();

        async Task ClientToWs()
        {
            var buffer = new byte[ReadBufferSize];
            var plain = new byte[ReadBufferSize];
            var lengths = new List<int>();
            try
            {
                while (true)
                {
                    var n = await client.ReadAsync(buffer, session.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        if (splitter.Flush() is { } tail)
                        {
                            await ws.SendAsync(tail, session.Token).ConfigureAwait(false);
                        }

                        break;
                    }

                    Stats.Up(n);
                    up += n;
                    upPackets++;
                    crypto.Upstream(buffer.AsSpan(0, n), plain.AsSpan(0, n));
                    // Packets stay in the splitter's buffer; frames are built in the socket's own buffer (#359).
                    var packets = splitter.SplitInto(buffer.AsSpan(0, n), plain.AsSpan(0, n), lengths);
                    if (lengths.Count > 0)
                    {
                        await ws.SendPacketsAsync(packets, lengths, session.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                reason = $"client: {ex.GetType().Name}";
            }
        }

        async Task WsToClient()
        {
            try
            {
                while (true)
                {
                    var data = await ws.ReceiveAsync(session.Token).ConfigureAwait(false);
                    if (data is null)
                    {
                        if (reason == "normal")
                        {
                            reason = "upstream: ws_close";
                        }

                        break;
                    }

                    Stats.Down(data.Length);
                    down += data.Length;
                    downPackets++;
                    crypto.Downstream(data);
                    await client.WriteAsync(data, session.Token).ConfigureAwait(false);
                    await client.FlushAsync(session.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                reason = $"upstream: {ex.GetType().Name}";
            }
        }

        Task[] tasks = [];
        try
        {
            // Ownership starts before the first write, including failure/cancellation of relayInit (#401).
            await ws.SendAsync(relayInit, ct).ConfigureAwait(false);
            tasks = [Task.Run(ClientToWs, CancellationToken.None), Task.Run(WsToClient, CancellationToken.None)];
            await Task.WhenAny(tasks).ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await ws.DisposeAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            var elapsed = _context.Time.GetElapsedTime(started);
            _context.Log(TgLogLevel.Info, $"[{route.Label}] DC{route.Dc}{(route.IsMedia ? "m" : "")} WS session closed ({reason}): "
                + $"^{TgWsProxyStats.HumanBytes(up)} ({upPackets} pkts) v{TgWsProxyStats.HumanBytes(down)} ({downPackets} pkts) in {elapsed.TotalSeconds:0.0}s");
        }
    }

    /// <summary>Client TCP ⇄ Telegram TCP with re-encryption.</summary>
    private async Task BridgeTcpAsync(Stream client, Stream remote, Route route, MtProtoCrypto crypto, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task Forward(Stream source, Stream target, bool upstream)
        {
            var buffer = new byte[ReadBufferSize];
            var plain = new byte[ReadBufferSize];
            try
            {
                while (true)
                {
                    var n = await source.ReadAsync(buffer, session.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    if (upstream)
                    {
                        Stats.Up(n);
                        crypto.Upstream(buffer.AsSpan(0, n), plain.AsSpan(0, n));
                    }
                    else
                    {
                        Stats.Down(n);
                        crypto.Downstream(buffer.AsSpan(0, n));
                    }

                    await target.WriteAsync(buffer.AsMemory(0, n), session.Token).ConfigureAwait(false);
                    await target.FlushAsync(session.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _context.Log(TgLogLevel.Debug, $"[{route.Label}] forward ended: {ex.Message}");
            }
            catch (OperationCanceledException) { }
        }

        var tasks = new[] { Task.Run(() => Forward(client, remote, true), CancellationToken.None), Task.Run(() => Forward(remote, client, false), CancellationToken.None) };
        try
        {
            await Task.WhenAny(tasks).ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await remote.DisposeAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task ProxyToMaskingDomainAsync(Stream client, byte[] initial, string domain, string label, CancellationToken ct)
    {
        Stream upstream;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            upstream = await _context.Network.ConnectAsync(domain, 443, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _context.Log(TgLogLevel.Warning, $"[{label}] masking: cannot connect to {domain}:443: {ex.Message}");
            return;
        }

        Stats.Masked();
        _context.Log(TgLogLevel.Debug, $"[{label}] masking -> {domain}:443");
        await using (upstream.ConfigureAwait(false))
        {
            await upstream.WriteAsync(initial, ct).ConfigureAwait(false);
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);

            async Task Relay(Stream source, Stream target)
            {
                try { await source.CopyToAsync(target, 16384, session.Token).ConfigureAwait(false); }
                catch (Exception) { }
            }

            await Task.WhenAny(Relay(client, upstream), Relay(upstream, client)).ConfigureAwait(false);
            await session.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken ct)
    {
        // Reading a little before closing looks like a real server instead of an instant reset.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BadHandshakeDrainTimeout);
        var buffer = new byte[4096];
        var remaining = BadHandshakeDrainLimit;
        try
        {
            while (remaining > 0)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), timeout.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                remaining -= n;
            }
        }
        catch (Exception) { }
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            ITgListener? snapshot;
            lock (_listenerGate)
            {
                snapshot = _listener;
            }

            if (snapshot is null)
            {
                return;
            }

            Stream stream;
            string label;
            IPAddress? remoteAddress;
            try
            {
                // The blocking AcceptAsync call itself stays outside the lock so
                // DisposeAsync can still take the gate while we are waiting.
                (stream, label, remoteAddress) = await snapshot.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // E.g. after sleep or a network change: bind the port again instead of failing the tool.
                _context.Log(TgLogLevel.Warning, "Listening socket died, restarting: " + ex.Message);
                if (!await RestartListenerAsync(ct).ConfigureAwait(false))
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Faulted?.Invoke(this, "TgWsProxy stopped accepting connections: " + ex.Message);
                    }

                    return;
                }

                continue;
            }

            var task = HandleClientAsync(stream, label, remoteAddress);
            _clients.TryAdd(task, 0);
            _ = task.ContinueWith(t => _clients.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task<bool> RestartListenerAsync(CancellationToken ct)
    {
        ITgListener? old;
        lock (_listenerGate)
        {
            old = _listener;
            _listener = null;
        }

        try
        {
            old?.Dispose();
        }
        catch (Exception)
        {
        }

        for (var attempt = 1; attempt <= ListenerRestartAttempts; attempt++)
        {
            try
            {
                await Task.Delay(ListenerRestartDelay, _context.Time, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    return false;
                }

                var fresh = _context.Network.Listen(Options.Host, Options.Port);
                bool cancelled;
                lock (_listenerGate)
                {
                    cancelled = ct.IsCancellationRequested;
                    if (!cancelled)
                    {
                        _listener = fresh;
                    }
                }

                if (cancelled)
                {
                    // DisposeAsync already ran (or is running): the loop is
                    // exiting, so nobody would close this socket otherwise.
                    // Disposing here keeps the port from staying bound (#346).
                    try
                    {
                        fresh.Dispose();
                    }
                    catch (Exception)
                    {
                    }

                    return false;
                }

                _context.Log(TgLogLevel.Warning, $"Server restored, listening on {Options.Host}:{Options.Port}");
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    return false;
                }

                _context.Log(TgLogLevel.Error, $"Failed to restart the listener (attempt {attempt}/{ListenerRestartAttempts}): {ex.Message}");
            }
        }

        return false;
    }

    private async Task StatsLoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), _context.Time, _stop.Token).ConfigureAwait(false);
                _context.Log(TgLogLevel.Info, "stats: " + Stats.Summary());
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshCfDomainsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var query = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            string? text;
            try
            {
                text = await _context.Network.GetStringAsync(new Uri(CfProxyDomains.ListUri + "?" + query), CfProxyDomains.PinnedHosts, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _context.Log(TgLogLevel.Debug, "CF proxy domain list download failed: " + ex.Message);
                text = null;
            }

            var pool = text is null ? new List<string>() : CfProxyDomains.ParseList(text);
            if (pool.Count >= CfProxyDomains.MinValidDomains)
            {
                _context.CfDomains.Update(pool);
                _context.Log(TgLogLevel.Info, $"CF proxy domain pool updated ({pool.Count} domains)");
            }
            else
            {
                _context.Log(TgLogLevel.Warning, "CF proxy domain refresh failed or returned too few domains; keeping the current pool");
            }

            try { await Task.Delay(TimeSpan.FromHours(1), _context.Time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void LogBanner()
    {
        _context.Log(TgLogLevel.Info, "Telegram MTProto WS Bridge Proxy (native)");
        _context.Log(TgLogLevel.Info, $"Listening on {Options.Host}:{Options.Port}");
        if (Options.FakeTlsDomain.Length > 0)
        {
            _context.Log(TgLogLevel.Info, $"Fake TLS: {Options.FakeTlsDomain}");
        }

        foreach (var (dc, ip) in Options.DcRedirects.OrderBy(p => p.Key))
        {
            _context.Log(TgLogLevel.Info, $"Target DC{dc}: {ip}");
        }

        _context.Log(TgLogLevel.Info, Options.CfProxyFallback
            ? $"CF proxy: enabled ({(Options.CfProxyDomains.Count > 0 ? "custom domains" : "auto")})"
            : "CF proxy: disabled");
        if (Options.ProxyProtocol)
        {
            _context.Log(TgLogLevel.Info, "PROXY protocol v1: enabled, trusted sources "
                + string.Join(", ", Options.ProxyProtocolTrustedSources));
        }
    }

    private sealed record Route(string Label, int Dc, bool IsMedia, bool IsTestDc, string MediaTag);
}
