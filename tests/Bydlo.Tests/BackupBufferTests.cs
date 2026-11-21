using System.Security.Cryptography;
using System.Text.Json;
using Bydlo.Core.Backup;
using Bydlo.Core.Models;
using Bydlo.Core.Serialization;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

/// <summary>Bounded backup buffers (issue #394): same format, fewer copies, limits enforced early.</summary>
public sealed class BackupBufferTests
{
    private const string Password = "a long backup password for buffers";

    private sealed class UnknownLengthStream(byte[] data) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            // Short reads exercise fragmented accumulation.
            var n = Math.Min(Math.Min(count, 7919), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Reports a length that does not match its content (file changed while reading).</summary>
    private sealed class LyingLengthStream(byte[] data, long reported) : MemoryStream(data)
    {
        public override long Length => reported;
    }

    private static BackupData Data() => new()
    {
        Profiles = [new(new Profile { Name = "Buffer server", Protocol = ProtocolKind.Vless, Host = "h.example", Port = 443 },
            new ProfileSecrets { Values = { [Keys.Uuid] = "buffer-uuid" } })],
        ToolSecrets = new ProfileSecrets { Values = { ["secret"] = "buffer-tool-secret" } },
    };

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, BackupArchive.MaximumFileSize)]
    [InlineData(false, 0)]
    [InlineData(false, 100_000)]
    [InlineData(false, BackupArchive.MaximumFileSize)]
    public async Task Reads_up_to_the_exact_limit(bool seekable, int size)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        Stream stream = seekable ? new MemoryStream(bytes) : new UnknownLengthStream(bytes);
        var read = await BackupArchive.ReadFileAsync(stream);
        Assert.Equal(bytes, read);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejects_one_byte_over_the_limit(bool seekable)
    {
        var bytes = new byte[BackupArchive.MaximumFileSize + 1];
        Stream stream = seekable ? new MemoryStream(bytes) : new UnknownLengthStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchive.ReadFileAsync(stream));
    }

    [Theory]
    [InlineData(10, 5)]
    [InlineData(10, 20)]
    public async Task Rejects_streams_whose_length_changes_while_reading(int actual, long reported)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BackupArchive.ReadFileAsync(new LyingLengthStream(new byte[actual], reported)));
    }

    [Fact]
    public async Task New_archives_are_read_by_the_previous_format_and_vice_versa()
    {
        var data = Data();
        var current = BackupArchive.Encrypt(data, Password);
        var legacyRead = JsonSerializer.Deserialize(LegacyDecrypt(current, Password), JsonDefaults.Options.Info<BackupData>())!;
        Assert.Equal("buffer-uuid", legacyRead.Profiles[0].Secrets[Keys.Uuid]);

        var legacy = LegacyEncrypt(data, Password);
        Assert.Equal("buffer-tool-secret", BackupArchive.Decrypt(legacy, Password).ToolSecrets["secret"]);
        var file = await BackupArchive.ReadFileAsync(new UnknownLengthStream(legacy));
        using var session = BackupArchive.OpenImportOwned(file, Password);
        Assert.Equal("Buffer server", Assert.Single(session.Profiles).Name);
    }

    [Fact]
    public void Owned_session_rejects_wrong_password_tampering_and_truncation()
    {
        var archive = BackupArchive.Encrypt(Data(), Password);
        Assert.ThrowsAny<CryptographicException>(() => BackupArchive.OpenImportOwned(archive.ToArray(), "a different long password"));
        var tampered = archive.ToArray();
        tampered[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => BackupArchive.OpenImportOwned(tampered, Password));
        Assert.Throws<InvalidDataException>(() => BackupArchive.OpenImportOwned(archive[..(BackupArchive.Overhead - 1)], Password));
    }

    [Fact]
    public void Owned_archive_is_cleared_when_the_session_is_disposed()
    {
        var archive = BackupArchive.Encrypt(Data(), Password);
        var session = BackupArchive.OpenImportOwned(archive, Password);
        session.Dispose();
        Assert.All(archive, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Bounded_buffer_accepts_the_limit_and_rejects_one_more_byte()
    {
        using var buffer = new BoundedBuffer(52, 1000);
        buffer.GetSpan(948);
        buffer.Advance(948);
        Assert.Equal(1000, buffer.WrittenCount);
        Assert.Throws<InvalidDataException>(() => buffer.GetSpan(1));
    }

    [Fact]
    public void Oversized_export_fails_while_serializing()
    {
        var data = new BackupData();
        var config = new string('x', 1024 * 1024);
        for (var i = 0; i < 40; i++)
        {
            data.Profiles.Add(new(new Profile { Name = "p" + i, Protocol = ProtocolKind.WireGuard, Host = "h", Port = 1 },
                new ProfileSecrets { Values = { [Keys.RawConfig] = config } }));
        }
        Assert.Throws<InvalidDataException>(() => BackupArchive.Encrypt(data, Password)); // warm the pool
        var start = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => BackupArchive.Encrypt(data, Password));
        var current = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        // The previous implementation serialized the whole payload before checking the limit.
        JsonSerializer.SerializeToUtf8Bytes(data, JsonDefaults.Options.Info<BackupData>());
        var legacy = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(current * 2 < legacy, $"current={current} legacy={legacy}");
    }

    [Fact]
    public void Export_allocates_less_than_the_previous_implementation()
    {
        var data = new BackupData();
        var config = new string('x', 128 * 1024);
        for (var i = 0; i < 200; i++)
        {
            data.Profiles.Add(new(new Profile { Name = "p" + i, Protocol = ProtocolKind.WireGuard, Host = "h", Port = 1 },
                new ProfileSecrets { Values = { [Keys.RawConfig] = config + i } }));
        }
        BackupArchive.Encrypt(data, Password); // warm the pool
        var start = GC.GetAllocatedBytesForCurrentThread();
        BackupArchive.Encrypt(data, Password);
        var current = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        LegacyEncrypt(data, Password);
        var legacy = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(current * 3 < legacy * 2, $"current={current} legacy={legacy}");
    }

    // Verbatim copies of the v1 implementation before issue #394, used as the compatibility oracle.
    private static byte[] LegacyEncrypt(BackupData data, string password)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(data, JsonDefaults.Options.Info<BackupData>());
        var output = new byte[BackupArchive.Overhead + plaintext.Length];
        "BYDLOBK\x01"u8.CopyTo(output);
        RandomNumberGenerator.Fill(output.AsSpan(8, 16));
        RandomNumberGenerator.Fill(output.AsSpan(24, 12));
        var key = Rfc2898DeriveBytes.Pbkdf2(password, output.AsSpan(8, 16), 600_000, HashAlgorithmName.SHA256, 32);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(24, 12), plaintext, output.AsSpan(BackupArchive.Overhead), output.AsSpan(36, 16), output.AsSpan(0, 24));
        return output;
    }

    private static byte[] LegacyDecrypt(byte[] file, string password)
    {
        Assert.True(file.AsSpan(0, 8).SequenceEqual("BYDLOBK\x01"u8));
        var key = Rfc2898DeriveBytes.Pbkdf2(password, file.AsSpan(8, 16), 600_000, HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[file.Length - BackupArchive.Overhead];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(file.AsSpan(24, 12), file.AsSpan(BackupArchive.Overhead), file.AsSpan(36, 16), plaintext, file.AsSpan(0, 24));
        return plaintext;
    }
}
