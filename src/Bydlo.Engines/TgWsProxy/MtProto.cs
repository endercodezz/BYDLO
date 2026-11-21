using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Bydlo.Engines.TgWsProxy;

/// <summary>Client handshake data recovered from an obfuscated2 init packet.</summary>
internal sealed record MtProtoHandshake(int Dc, bool IsMedia, uint ProtoTag, byte[] PrekeyAndIv);

/// <summary>
/// MTProto obfuscated2 transport: the 64-byte init packet, key derivation and the four AES-CTR streams
/// the proxy needs to re-encrypt traffic between the client and Telegram.
/// </summary>
internal static class MtProto
{
    public const int HandshakeLength = 64;
    public const int SkipLength = 8;
    public const int PrekeyLength = 32;
    public const int KeyLength = 32;
    public const int IvLength = 16;
    public const int ProtoTagPosition = 56;
    public const int DcIndexPosition = 60;

    public const uint Abridged = 0xEFEFEFEF;
    public const uint Intermediate = 0xEEEEEEEE;
    public const uint PaddedIntermediate = 0xDDDDDDDD;

    // First words that a real client never sends, so the init cannot be confused with another protocol.
    private static readonly uint[] ReservedStarts =
    [
        0x44414548, // HEAD
        0x54534F50, // POST
        0x20544547, // GET
        0xEEEEEEEE,
        0xDDDDDDDD,
        0x02010316, // TLS handshake
        0x4954504F, // OPTI
    ];

    public static bool IsKnownProtoTag(uint tag) => tag is Abridged or Intermediate or PaddedIntermediate;

    /// <summary>Decrypts the client init with the proxy secret; null when the secret or protocol does not match.</summary>
    public static MtProtoHandshake? TryParseHandshake(ReadOnlySpan<byte> init, ReadOnlySpan<byte> secret)
    {
        if (init.Length < HandshakeLength)
        {
            return null;
        }

        var prekeyAndIv = init.Slice(SkipLength, PrekeyLength + IvLength).ToArray();
        using var cipher = new AesCtr(SecretKey(prekeyAndIv.AsSpan(0, PrekeyLength), secret), prekeyAndIv.AsSpan(PrekeyLength));
        var decrypted = cipher.Transform(init[..HandshakeLength]);

        var tag = BinaryPrimitives.ReadUInt32LittleEndian(decrypted.AsSpan(ProtoTagPosition));
        if (!IsKnownProtoTag(tag))
        {
            return null;
        }

        var dcIndex = BinaryPrimitives.ReadInt16LittleEndian(decrypted.AsSpan(DcIndexPosition));
        return new MtProtoHandshake(Math.Abs((int)dcIndex), dcIndex < 0, tag, prekeyAndIv);
    }

    /// <summary>A fresh init packet for the Telegram side (standard obfuscation without a secret).</summary>
    public static byte[] GenerateRelayInit(uint protoTag, short dcIndex)
    {
        var init = new byte[HandshakeLength];
        do
        {
            RandomNumberGenerator.Fill(init);
        }
        while (init[0] == 0xEF
            || ReservedStarts.Contains(BinaryPrimitives.ReadUInt32LittleEndian(init))
            || BinaryPrimitives.ReadUInt32LittleEndian(init.AsSpan(4)) == 0);

        using var cipher = new AesCtr(init.AsSpan(SkipLength, KeyLength), init.AsSpan(SkipLength + KeyLength, IvLength));
        var keystream = cipher.Transform(new byte[HandshakeLength]);

        Span<byte> tail = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(tail, protoTag);
        BinaryPrimitives.WriteInt16LittleEndian(tail[4..], dcIndex);
        RandomNumberGenerator.Fill(tail[6..]);
        for (var i = 0; i < tail.Length; i++)
        {
            init[ProtoTagPosition + i] = (byte)(tail[i] ^ keystream[ProtoTagPosition + i]);
        }

        return init;
    }

    public static MtProtoCrypto CreateCrypto(ReadOnlySpan<byte> clientPrekeyAndIv, ReadOnlySpan<byte> secret, ReadOnlySpan<byte> relayInit)
    {
        // Client side: key = SHA256(prekey + secret); the reverse direction uses the reversed prekey+IV.
        var clientDecrypt = new AesCtr(SecretKey(clientPrekeyAndIv[..PrekeyLength], secret), clientPrekeyAndIv[PrekeyLength..]);
        clientDecrypt.Skip(HandshakeLength);

        var reversed = clientPrekeyAndIv.ToArray();
        Array.Reverse(reversed);
        var clientEncrypt = new AesCtr(SecretKey(reversed.AsSpan(0, PrekeyLength), secret), reversed.AsSpan(PrekeyLength));

        // Telegram side: raw key and IV from our own init packet.
        var relayKeyAndIv = relayInit.Slice(SkipLength, KeyLength + IvLength);
        var telegramEncrypt = new AesCtr(relayKeyAndIv[..KeyLength], relayKeyAndIv[KeyLength..]);
        telegramEncrypt.Skip(HandshakeLength);

        var relayReversed = relayKeyAndIv.ToArray();
        Array.Reverse(relayReversed);
        var telegramDecrypt = new AesCtr(relayReversed.AsSpan(0, KeyLength), relayReversed.AsSpan(KeyLength));

        return new MtProtoCrypto(clientDecrypt, clientEncrypt, telegramEncrypt, telegramDecrypt);
    }

    private static byte[] SecretKey(ReadOnlySpan<byte> prekey, ReadOnlySpan<byte> secret)
    {
        Span<byte> input = stackalloc byte[prekey.Length + secret.Length];
        prekey.CopyTo(input);
        secret.CopyTo(input[prekey.Length..]);
        return SHA256.HashData(input);
    }
}

/// <summary>The four cipher streams of one proxied connection.</summary>
internal sealed class MtProtoCrypto(AesCtr clientDecrypt, AesCtr clientEncrypt, AesCtr telegramEncrypt, AesCtr telegramDecrypt) : IDisposable
{
    /// <summary>Client ciphertext → plaintext (written into <paramref name="plain"/>) → Telegram ciphertext (in place).</summary>
    public void Upstream(Span<byte> data, Span<byte> plain)
    {
        clientDecrypt.Transform(data, plain);
        telegramEncrypt.Transform(plain, data);
    }

    /// <summary>Telegram ciphertext → client ciphertext, in place.</summary>
    public void Downstream(Span<byte> data)
    {
        telegramDecrypt.TransformInPlace(data);
        clientEncrypt.TransformInPlace(data);
    }

    public void Dispose()
    {
        clientDecrypt.Dispose();
        clientEncrypt.Dispose();
        telegramEncrypt.Dispose();
        telegramDecrypt.Dispose();
    }
}

/// <summary>
/// Splits the client byte stream into whole MTProto transport packets so each one goes out as its own
/// WebSocket frame (Telegram's WebSocket endpoint expects packet-aligned frames).
/// </summary>
internal sealed class MsgSplitter(uint protoTag)
{
    private byte[] _cipher = new byte[4096];
    private byte[] _plain = new byte[4096];
    private int _length;
    private bool _disabled;

    private int _pending;

    /// <summary>
    /// Appends a chunk and reports the whole packets now available, without allocating (issue #359):
    /// the packets are contiguous in the returned memory and <paramref name="lengths"/> receives
    /// their sizes. The memory stays valid only until the next call to this splitter.
    /// </summary>
    public ReadOnlyMemory<byte> SplitInto(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> plain, List<int> lengths)
    {
        lengths.Clear();
        Consume(_pending);
        _pending = 0;
        if (cipher.IsEmpty)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        Append(cipher, plain);
        if (_disabled)
        {
            lengths.Add(_length);
            _pending = _length;
            return _cipher.AsMemory(0, _length);
        }

        var offset = 0;
        while (offset < _length)
        {
            var packet = NextPacketLength(offset, _length - offset);
            if (packet is null)
            {
                break;
            }

            if (packet <= 0)
            {
                // Unknown framing: stop splitting and pass everything through as is.
                lengths.Add(_length - offset);
                offset = _length;
                _disabled = true;
                break;
            }

            lengths.Add(packet.Value);
            offset += packet.Value;
        }

        _pending = offset;
        return _cipher.AsMemory(0, offset);
    }

    /// <summary>Allocating form of <see cref="SplitInto"/>, kept for tests and rare paths.</summary>
    public List<byte[]> Split(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> plain)
    {
        var lengths = new List<int>();
        var packets = SplitInto(cipher, plain, lengths).Span;
        var parts = new List<byte[]>(lengths.Count);
        var at = 0;
        foreach (var length in lengths)
        {
            parts.Add(packets.Slice(at, length).ToArray());
            at += length;
        }

        return parts;
    }

    public byte[]? Flush()
    {
        Consume(_pending);
        _pending = 0;
        if (_length == 0)
        {
            return null;
        }

        var tail = _cipher.AsSpan(0, _length).ToArray();
        _length = 0;
        return tail;
    }

    private int? NextPacketLength(int offset, int available) => protoTag switch
    {
        MtProto.Abridged => NextAbridged(offset, available),
        MtProto.Intermediate or MtProto.PaddedIntermediate => NextIntermediate(offset, available),
        _ => 0,
    };

    private int? NextAbridged(int offset, int available)
    {
        var first = _plain[offset];
        int payload, header;
        if (first is 0x7F or 0xFF)
        {
            if (available < 4)
            {
                return null;
            }

            payload = (_plain[offset + 1] | (_plain[offset + 2] << 8) | (_plain[offset + 3] << 16)) * 4;
            header = 4;
        }
        else
        {
            payload = (first & 0x7F) * 4;
            header = 1;
        }

        if (payload <= 0)
        {
            return 0;
        }

        return available < header + payload ? null : header + payload;
    }

    private int? NextIntermediate(int offset, int available)
    {
        if (available < 4)
        {
            return null;
        }

        var payload = (int)(BinaryPrimitives.ReadUInt32LittleEndian(_plain.AsSpan(offset)) & 0x7FFFFFFF);
        if (payload <= 0)
        {
            return 0;
        }

        return (long)available < 4L + payload ? null : 4 + payload;
    }

    private void Append(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> plain)
    {
        if (_length + cipher.Length > _cipher.Length)
        {
            var size = Math.Max(_cipher.Length * 2, _length + cipher.Length);
            Array.Resize(ref _cipher, size);
            Array.Resize(ref _plain, size);
        }

        cipher.CopyTo(_cipher.AsSpan(_length));
        plain.CopyTo(_plain.AsSpan(_length));
        _length += cipher.Length;
    }

    private void Consume(int count)
    {
        if (count == 0)
        {
            return;
        }

        _cipher.AsSpan(count, _length - count).CopyTo(_cipher);
        _plain.AsSpan(count, _length - count).CopyTo(_plain);
        _length -= count;
    }
}
