using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Bydlo.Engines.TgWsProxy;

/// <summary>Upgrade refused by the server (status other than 101).</summary>
internal sealed class WsHandshakeException(int statusCode, string statusLine) : IOException($"HTTP {statusCode}: {statusLine}")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Minimal binary WebSocket client (RFC 6455) as used by Telegram's <c>/apiws</c> endpoint: masked binary frames out,
/// fragmented messages reassembled in, ping answered, close echoed.
/// A receive pump reads from the moment of the upgrade, so a pooled idle connection that the server pings or drops
/// is noticed right away (<see cref="IsClosed"/>) instead of failing when a client gets it.
/// </summary>
internal sealed class RawWebSocket : IAsyncDisposable
{
    public const int MaxMessageLength = 16 * 1024 * 1024;

    private const byte OpContinuation = 0x0;
    private const byte OpText = 0x1;
    private const byte OpBinary = 0x2;
    private const byte OpClose = 0x8;
    private const byte OpPing = 0x9;
    private const byte OpPong = 0xA;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _header = new byte[14];
    private readonly int[] _single = new int[1];
    private byte[] _writeBuffer = new byte[16 * 1024];
    private readonly Channel<byte[]> _messages = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stopPump = new();
    private readonly Task _pump;
    private MemoryStream? _fragments;
    private Exception? _error;
    private int _closed;

    private RawWebSocket(Stream stream)
    {
        _stream = stream;
        _pump = Task.Run(PumpAsync);
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Connects to <paramref name="host"/> and upgrades <paramref name="path"/> with <c>Host: domain</c>.
    /// A non-null <paramref name="sni"/> enables domain fronting (no certificate check, as upstream).
    /// </summary>
    public static async Task<RawWebSocket> ConnectAsync(ITgNetwork network, string host, string domain, string path, TimeSpan timeout,
        CancellationToken ct, string? sni = null, bool secure = true)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stream = secure
            ? await network.ConnectTlsAsync(host, 443, sni ?? domain, sni is null, cts.Token).ConfigureAwait(false)
            : await network.ConnectAsync(host, 80, cts.Token).ConfigureAwait(false);
        try
        {
            return await UpgradeAsync(stream, domain, path, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<RawWebSocket> UpgradeAsync(Stream stream, string domain, string path, CancellationToken ct)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET {path} HTTP/1.1\r\nHost: {domain}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var (statusLine, rest) = await ReadResponseHeadAsync(stream, ct).ConfigureAwait(false);
        var parts = statusLine.Split(' ', 3);
        var status = parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
        if (status != 101)
        {
            throw new WsHandshakeException(status, statusLine);
        }

        return new RawWebSocket(rest.Length == 0 ? stream : new PrefixedStream(rest, stream));
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => WriteFramesAsync(data, null, OpBinary, ct);

    /// <summary>
    /// Sends contiguous <paramref name="data"/> as one binary frame per entry of <paramref name="lengths"/>,
    /// built in a reusable buffer and written at once (issue #359).
    /// </summary>
    public Task SendPacketsAsync(ReadOnlyMemory<byte> data, IReadOnlyList<int> lengths, CancellationToken ct) =>
        WriteFramesAsync(data, lengths, OpBinary, ct);

    /// <summary>Next binary message, or null once the server closed the connection.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        while (await _messages.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (_messages.Reader.TryRead(out var message))
            {
                return message;
            }
        }

        return _error is { } error ? throw new IOException(error.Message, error) : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await WriteFramesAsync(ReadOnlyMemory<byte>.Empty, null, OpClose, cts.Token, force: true).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or NotSupportedException) { }
        }

        await _stopPump.CancelAsync().ConfigureAwait(false);
        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stopPump.Dispose();
    }

    /// <summary>Reads frames until close, end of stream or error; answers pings while the connection idles in the pool.</summary>
    private async Task PumpAsync()
    {
        var ct = _stopPump.Token;
        try
        {
            while (true)
            {
                var (opcode, payload, fin) = await ReadFrameAsync(ct).ConfigureAwait(false);
                switch (opcode)
                {
                    case OpClose:
                        Interlocked.Exchange(ref _closed, 1);
                        try { await WriteFramesAsync(payload.AsMemory(0, Math.Min(2, payload.Length)), null, OpClose, ct, force: true).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
                        return;
                    case OpPing:
                        try { await WriteFramesAsync(payload, null, OpPong, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                        continue;
                    case OpContinuation or OpText or OpBinary:
                        if (fin && _fragments is null)
                        {
                            await _messages.Writer.WriteAsync(payload, ct).ConfigureAwait(false);
                            continue;
                        }

                        _fragments ??= new MemoryStream();
                        _fragments.Write(payload);
                        if (_fragments.Length > MaxMessageLength)
                        {
                            throw new IOException($"WebSocket message too large: {_fragments.Length} bytes.");
                        }

                        if (fin)
                        {
                            var message = _fragments.ToArray();
                            _fragments = null;
                            await _messages.Writer.WriteAsync(message, ct).ConfigureAwait(false);
                        }

                        continue;
                    default:
                        continue;
                }
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _closed) == 0 && !ct.IsCancellationRequested)
            {
                _error = ex;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _closed, 1);
            _messages.Writer.TryComplete();
        }
    }

    private static int HeaderLength(int length, bool mask) =>
        2 + (length < 126 ? 0 : length < 65536 ? 2 : 8) + (mask ? 4 : 0);

    internal static byte[] BuildFrame(byte opcode, ReadOnlySpan<byte> data, bool mask)
    {
        var frame = new byte[HeaderLength(data.Length, mask) + data.Length];
        WriteFrame(opcode, data, mask, frame);
        return frame;
    }

    /// <summary>Writes one frame into <paramref name="frame"/> and returns its length.</summary>
    private static int WriteFrame(byte opcode, ReadOnlySpan<byte> data, bool mask, Span<byte> frame)
    {
        var length = data.Length;
        frame[0] = (byte)(0x80 | opcode);
        var maskBit = mask ? (byte)0x80 : (byte)0;
        var at = 2;
        if (length < 126)
        {
            frame[1] = (byte)(maskBit | length);
        }
        else if (length < 65536)
        {
            frame[1] = (byte)(maskBit | 126);
            BinaryPrimitives.WriteUInt16BigEndian(frame[2..], (ushort)length);
            at = 4;
        }
        else
        {
            frame[1] = (byte)(maskBit | 127);
            BinaryPrimitives.WriteUInt64BigEndian(frame[2..], (ulong)length);
            at = 10;
        }

        if (!mask)
        {
            data.CopyTo(frame[at..]);
            return at + length;
        }

        var key = frame.Slice(at, 4);
        RandomNumberGenerator.Fill(key);
        var payload = frame.Slice(at + 4, length);
        data.CopyTo(payload);
        ApplyMask(payload, key);
        return at + 4 + length;
    }

    private static void ApplyMask(Span<byte> payload, ReadOnlySpan<byte> key)
    {
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] ^= key[i & 3];
        }
    }

    /// <summary>
    /// Reads the HTTP response head in blocks (issue #359). Bytes that arrive after the blank line
    /// already belong to the WebSocket and are returned as <c>Leftover</c>.
    /// </summary>
    private static async Task<(string StatusLine, byte[] Leftover)> ReadResponseHeadAsync(Stream stream, CancellationToken ct)
    {
        const int Limit = 16 * 1024;
        var head = new byte[Limit];
        var count = 0;
        var end = -1;
        while (count < Limit)
        {
            var n = await stream.ReadAsync(head.AsMemory(count), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            var from = Math.Max(0, count - 3);
            count += n;
            var found = head.AsSpan(from, count - from).IndexOf("\r\n\r\n"u8);
            if (found >= 0)
            {
                end = from + found + 4;
                break;
            }
        }

        var text = Encoding.UTF8.GetString(head, 0, end >= 0 ? end : count);
        var firstLine = text.Split('\n', 2)[0].Trim();
        var rest = end >= 0 && end < count ? head.AsSpan(end, count - end).ToArray() : [];
        return firstLine.Length == 0 ? throw new WsHandshakeException(0, "empty response") : (firstLine, rest);
    }

    private async Task WriteFramesAsync(ReadOnlyMemory<byte> data, IReadOnlyList<int>? lengths, byte opcode, CancellationToken ct, bool force = false)
    {
        if (IsClosed && !force)
        {
            throw new IOException("WebSocket is closed.");
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The shared buffers are only touched under the write lock.
            if (lengths is null)
            {
                _single[0] = data.Length;
                lengths = _single;
            }

            var total = 0;
            for (var i = 0; i < lengths.Count; i++)
            {
                total += HeaderLength(lengths[i], mask: true) + lengths[i];
            }

            if (_writeBuffer.Length < total)
            {
                _writeBuffer = new byte[Math.Max(total, _writeBuffer.Length * 2)];
            }

            var written = 0;
            var offset = 0;
            for (var i = 0; i < lengths.Count; i++)
            {
                written += WriteFrame(opcode, data.Span.Slice(offset, lengths[i]), mask: true, _writeBuffer.AsSpan(written));
                offset += lengths[i];
            }

            await _stream.WriteAsync(_writeBuffer.AsMemory(0, written), ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<(byte Opcode, byte[] Payload, bool Fin)> ReadFrameAsync(CancellationToken ct)
    {
        await _stream.ReadExactlyAsync(_header.AsMemory(0, 2), ct).ConfigureAwait(false);
        var fin = (_header[0] & 0x80) != 0;
        var opcode = (byte)(_header[0] & 0x0F);
        var masked = (_header[1] & 0x80) != 0;
        long length = _header[1] & 0x7F;
        if (length == 126)
        {
            await _stream.ReadExactlyAsync(_header.AsMemory(2, 2), ct).ConfigureAwait(false);
            length = BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(2));
        }
        else if (length == 127)
        {
            await _stream.ReadExactlyAsync(_header.AsMemory(2, 8), ct).ConfigureAwait(false);
            length = (long)BinaryPrimitives.ReadUInt64BigEndian(_header.AsSpan(2));
        }

        if (length is < 0 or > MaxMessageLength)
        {
            throw new IOException($"WebSocket frame too large: {length} bytes.");
        }

        var key = _header.AsMemory(10, 4);
        if (masked)
        {
            await _stream.ReadExactlyAsync(key, ct).ConfigureAwait(false);
        }

        var payload = new byte[length];
        await _stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        if (masked)
        {
            ApplyMask(payload, key.Span);
        }

        return (opcode, payload, fin);
    }
}

/// <summary>Replays bytes read past the HTTP head before continuing with the inner stream.</summary>
internal sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
{
    private int _offset;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < prefix.Length && !buffer.IsEmpty)
        {
            var n = Math.Min(buffer.Length, prefix.Length - _offset);
            prefix.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return ValueTask.FromResult(n);
        }

        return inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override ValueTask DisposeAsync() => inner.DisposeAsync();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
