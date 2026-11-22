using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace Bydlo.Engines.TgWsProxy;

/// <summary>Cipher suites and key-share groups offered in a ClientHello.</summary>
internal sealed record ClientHelloInfo(IReadOnlySet<int> CipherSuites, IReadOnlyDictionary<int, int> KeyShares)
{
    public static readonly ClientHelloInfo Empty = new(new HashSet<int>(), new Dictionary<int, int>());
}

/// <summary>Verified Fake TLS ClientHello (ee-secret mode).</summary>
internal sealed record FakeTlsHello(byte[] ClientRandom, byte[] SessionId, uint Timestamp);

/// <summary>
/// Fake TLS (ee-secret) masking: the client hides the MTProto init inside a TLS 1.3 ClientHello whose random
/// field is an HMAC of the proxy secret; the proxy answers with a ServerHello signed the same way.
/// </summary>
internal static class FakeTls
{
    public const byte RecordHandshake = 0x16;
    public const byte RecordChangeCipherSpec = 0x14;
    public const byte RecordApplicationData = 0x17;
    public const int MaxRecordPayload = 16384;
    public const int ClientRandomOffset = 11;
    public const int ServerRandomOffset = 11;
    public const int RandomLength = 32;
    public const int SessionIdOffset = 44;
    public const int TimestampTolerance = 120;

    public const int ExtSupportedVersions = 0x002b;
    public const int ExtKeyShare = 0x0033;
    public const int GroupX25519 = 0x001d;
    public const int GroupX25519MlKem768 = 0x11ec;
    private const int X25519MlKem768ClientShare = 1216;
    private const int MlKem768Ciphertext = 1088;

    // Bounds for the single ApplicationData record after CCS. Without a probed site profile stay close to the
    // small record known to pass DPI, far from the ~2 KB classic MTProxy answer.
    public const int AppDataMin = 64;
    private const int FallbackAppDataMin = 64;
    private const int FallbackAppDataMax = 160;

    private static readonly int[] CipherPreference = [0x1301, 0x1303, 0x1302];
    private static readonly byte[] ChangeCipherSpec = [0x14, 0x03, 0x03, 0x00, 0x01, 0x01];

    public static FakeTlsHello? VerifyClientHello(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret, DateTimeOffset now)
    {
        if (data.Length < 43 || data[0] != RecordHandshake || data[5] != 0x01)
        {
            return null;
        }

        var clientRandom = data.Slice(ClientRandomOffset, RandomLength).ToArray();
        var zeroed = data.ToArray();
        zeroed.AsSpan(ClientRandomOffset, RandomLength).Clear();
        var expected = HMACSHA256.HashData(secret, zeroed);

        if (!CryptographicOperations.FixedTimeEquals(expected.AsSpan(0, 28), clientRandom.AsSpan(0, 28)))
        {
            return null;
        }

        Span<byte> ts = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            ts[i] = (byte)(clientRandom[28 + i] ^ expected[28 + i]);
        }

        var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(ts);
        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp) > TimestampTolerance)
        {
            return null;
        }

        var sessionId = data.Length >= SessionIdOffset + 32 && data[43] == 0x20
            ? data.Slice(SessionIdOffset, 32).ToArray()
            : new byte[32];
        return new FakeTlsHello(clientRandom, sessionId, timestamp);
    }

    /// <summary>Offered cipher suites and key shares; empty on malformed input.</summary>
    public static ClientHelloInfo ParseClientHello(ReadOnlySpan<byte> data)
    {
        try
        {
            var pos = 5 + 4 + 2 + RandomLength;
            pos += 1 + data[pos];

            var suitesLength = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            pos += 2;
            var suitesEnd = pos + suitesLength;
            if (suitesEnd > data.Length)
            {
                return ClientHelloInfo.Empty;
            }

            var suites = new HashSet<int>();
            for (var off = pos; off < suitesEnd - 1; off += 2)
            {
                suites.Add(BinaryPrimitives.ReadUInt16BigEndian(data[off..]));
            }

            pos = suitesEnd;
            pos += 1 + data[pos];

            var extTotal = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            pos += 2;
            var extEnd = Math.Min(pos + extTotal, data.Length);
            var shares = new Dictionary<int, int>();
            while (pos + 4 <= extEnd)
            {
                var type = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
                var length = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
                pos += 4;
                if (type == ExtKeyShare && pos + 2 <= extEnd)
                {
                    var sharesEnd = Math.Min(pos + 2 + BinaryPrimitives.ReadUInt16BigEndian(data[pos..]), extEnd);
                    var off = pos + 2;
                    while (off + 4 <= sharesEnd)
                    {
                        var group = BinaryPrimitives.ReadUInt16BigEndian(data[off..]);
                        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(data[(off + 2)..]);
                        off += 4 + keyLength;
                        if (off <= sharesEnd)
                        {
                            shares.TryAdd(group, keyLength);
                        }
                    }
                }

                pos += length;
            }

            return new ClientHelloInfo(suites, shares);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ClientHelloInfo.Empty;
        }
        catch (IndexOutOfRangeException)
        {
            return ClientHelloInfo.Empty;
        }
    }

    /// <summary>
    /// ServerHello + CCS + one ApplicationData record. Telegram clients parse exactly these three records and check
    /// the HMAC over all of them, so the shape stays fixed while sizes may vary.
    /// </summary>
    public static byte[] BuildServerHello(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> clientRandom, ReadOnlySpan<byte> sessionId, ClientHelloInfo info, int? profiledAppDataLength)
    {
        var (group, keyExchange) = ServerKeyShare(info);
        var extensions = new List<byte>();
        AddU16(extensions, ExtSupportedVersions);
        AddU16(extensions, 2);
        AddU16(extensions, 0x0304);
        AddU16(extensions, ExtKeyShare);
        AddU16(extensions, 4 + keyExchange.Length);
        AddU16(extensions, group);
        AddU16(extensions, keyExchange.Length);
        extensions.AddRange(keyExchange);

        var body = new List<byte> { 0x03, 0x03 };
        body.AddRange(new byte[RandomLength]);
        body.Add((byte)sessionId.Length);
        body.AddRange(sessionId.ToArray());
        AddU16(body, SelectCipherSuite(info));
        body.Add(0x00);
        AddU16(body, extensions.Count);
        body.AddRange(extensions);

        var response = new List<byte> { RecordHandshake, 0x03, 0x03 };
        AddU16(response, body.Count + 4);
        response.Add(0x02);
        response.Add((byte)(body.Count >> 16));
        AddU16(response, body.Count & 0xFFFF);
        response.AddRange(body);
        response.AddRange(ChangeCipherSpec);

        var appDataLength = profiledAppDataLength ?? RandomNumberGenerator.GetInt32(FallbackAppDataMin, FallbackAppDataMax + 1);
        response.Add(RecordApplicationData);
        response.Add(0x03);
        response.Add(0x03);
        AddU16(response, appDataLength);
        response.AddRange(RandomNumberGenerator.GetBytes(appDataLength));

        var final = response.ToArray();
        var hmacInput = new byte[clientRandom.Length + final.Length];
        clientRandom.CopyTo(hmacInput);
        final.CopyTo(hmacInput, clientRandom.Length);
        HMACSHA256.HashData(secret, hmacInput).CopyTo(final, ServerRandomOffset);
        return final;
    }

    public static int ClampAppDataLength(int length) => Math.Clamp(length, AppDataMin, MaxRecordPayload);

    /// <summary>
    /// Length of the first ApplicationData record in a server's first flight; null while more data is needed,
    /// 0 if the flight cannot contain one (alert, TLS 1.2 handshake, garbage).
    /// </summary>
    public static int? FirstAppDataRecordLength(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        while (pos + 5 <= data.Length)
        {
            var type = data[pos];
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 3)..]);
            if (type == RecordApplicationData)
            {
                return length;
            }

            if (type is not (RecordHandshake or RecordChangeCipherSpec))
            {
                return 0;
            }

            pos += 5 + length;
        }

        return null;
    }

    /// <summary>
    /// Measures the first encrypted record the masking site sends after its ServerHello, so our answer has the
    /// same size. Only the raw first flight is read; the TLS handshake is never completed.
    /// </summary>
    public static async Task<int?> ProbeAppDataLengthAsync(ITgNetwork network, string domain, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await using var raw = await network.ConnectAsync(domain, 443, cts.Token).ConfigureAwait(false);
            var tap = new FirstFlightTap(raw);
            await using var tls = new SslStream(tap, leaveInnerStreamOpen: true);
            try
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = domain,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                    RemoteCertificateValidationCallback = (_, _, _, _) => false,
                }, cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (tap.Result is not null)
            {
                // Expected: the tap aborts the handshake as soon as the record size is known.
            }

            return tap.Result is > 0 ? ClampAppDataLength(tap.Result.Value) : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static int SelectCipherSuite(ClientHelloInfo info) =>
        CipherPreference.FirstOrDefault(info.CipherSuites.Contains, CipherPreference[0]);

    private static (int Group, byte[] KeyExchange) ServerKeyShare(ClientHelloInfo info)
    {
        // Mirror the hybrid post-quantum group when the client offers it, as current servers do;
        // the client never derives keys from it.
        if (info.KeyShares.TryGetValue(GroupX25519MlKem768, out var length) && length == X25519MlKem768ClientShare)
        {
            return (GroupX25519MlKem768, [.. RandomNumberGenerator.GetBytes(MlKem768Ciphertext), .. X25519PublicKeyLike()]);
        }

        return (GroupX25519, X25519PublicKeyLike());
    }

    private static byte[] X25519PublicKeyLike()
    {
        // Real public keys are field elements below 2^255: keep the top bit clear.
        var key = RandomNumberGenerator.GetBytes(32);
        key[^1] &= 0x7F;
        return key;
    }

    private static void AddU16(List<byte> buffer, int value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    /// <summary>Passes the TLS handshake through and stops it once the first ApplicationData record header arrives.</summary>
    private sealed class FirstFlightTap(Stream inner) : Stream
    {
        private readonly MemoryStream _flight = new();

        public int? Result { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return 0;
            }

            _flight.Write(buffer.Span[..n]);
            if (FirstAppDataRecordLength(_flight.GetBuffer().AsSpan(0, (int)_flight.Length)) is { } length)
            {
                Result = length;
                throw new IOException("Fake TLS profile captured.");
            }

            return n;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// The client side of a Fake TLS connection after the handshake: reads the payload of ApplicationData records
/// (skipping CCS) and wraps writes into records of at most 16 KB.
/// </summary>
internal sealed class FakeTlsStream(Stream inner) : Stream
{
    private readonly byte[] _header = new byte[5];
    /// <summary>Write-side record buffer; only <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> touches it.</summary>
    private byte[]? _record;

    /// <summary>Read-side scratch for skipped ChangeCipherSpec payloads (a record length is at most 65535).</summary>
    private byte[]? _discard;

    private int _recordLeft;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_recordLeft == 0)
        {
            var got = await inner.ReadAtLeastAsync(_header, _header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (got < _header.Length)
            {
                return 0;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(3));
            if (_header[0] == FakeTls.RecordChangeCipherSpec)
            {
                if (length > 0)
                {
                    // Reads and writes run concurrently, so the discarded CCS payload never uses the write buffer.
                    _discard ??= new byte[ushort.MaxValue];
                    await inner.ReadExactlyAsync(_discard.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (_header[0] != FakeTls.RecordApplicationData)
            {
                return 0;
            }

            _recordLeft = length;
        }

        var n = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, _recordLeft)], cancellationToken).ConfigureAwait(false);
        if (n == 0)
        {
            return 0;
        }

        _recordLeft -= n;
        return n;
    }

    /// <summary>
    /// Writes records through one reusable buffer instead of allocating the wrapped payload (issue #359).
    /// The bytes on the wire are identical to <see cref="Wrap"/>.
    /// </summary>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _record ??= new byte[5 + FakeTls.MaxRecordPayload];
        for (var offset = 0; offset < buffer.Length; offset += FakeTls.MaxRecordPayload)
        {
            var length = WriteRecord(buffer.Span.Slice(offset, Math.Min(FakeTls.MaxRecordPayload, buffer.Length - offset)), _record);
            await inner.WriteAsync(_record.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
    }

    private static int WriteRecord(ReadOnlySpan<byte> chunk, Span<byte> output)
    {
        output[0] = FakeTls.RecordApplicationData;
        output[1] = 0x03;
        output[2] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(output[3..], (ushort)chunk.Length);
        chunk.CopyTo(output[5..]);
        return 5 + chunk.Length;
    }

    public static byte[] Wrap(ReadOnlySpan<byte> data)
    {
        var records = (data.Length + FakeTls.MaxRecordPayload - 1) / FakeTls.MaxRecordPayload;
        var output = new byte[data.Length + (records * 5)];
        var at = 0;
        for (var offset = 0; offset < data.Length; offset += FakeTls.MaxRecordPayload)
        {
            var chunk = data.Slice(offset, Math.Min(FakeTls.MaxRecordPayload, data.Length - offset));
            output[at] = FakeTls.RecordApplicationData;
            output[at + 1] = 0x03;
            output[at + 2] = 0x03;
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(at + 3), (ushort)chunk.Length);
            chunk.CopyTo(output.AsSpan(at + 5));
            at += 5 + chunk.Length;
        }

        return output;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(Wrap(buffer.AsSpan(offset, count)));

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
