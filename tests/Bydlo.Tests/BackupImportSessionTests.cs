using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Bydlo.Core.Backup;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Serialization;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

public sealed class BackupImportSessionTests
{
    private const string Password = "synthetic backup password";
    private const string Secret = "secret-UUID-password-key-🔑";

    private static BackupData Data()
    {
        var sub = new Subscription { Name = "Подписка" };
        return new()
        {
            Profiles = [new(new Profile { Name = "Сервер", Protocol = ProtocolKind.Trojan, SubscriptionId = sub.Id,
                Options = { [Keys.XhttpHeaders] = Secret } }, new() { Values = { [Keys.Password] = Secret, [Keys.RawConfig] = Secret } })],
            Subscriptions = [new(sub, "https://example.invalid/" + Secret)],
            ToolSecrets = new() { Values = { ["secret"] = Secret } },
        };
    }

    [Fact]
    public void Metadata_contains_only_ids_names_relationships_and_caller_cannot_change_owned_archive()
    {
        var data = Data();
        var archive = BackupArchive.Encrypt(data, Password);
        using var session = BackupArchive.OpenImport(archive, Password);
        Array.Clear(archive);
        Assert.Equal(new BackupProfileMetadata(data.Profiles[0].Profile.Id, "Сервер", data.Subscriptions[0].Subscription.Id), Assert.Single(session.Profiles));
        Assert.Equal(new BackupSubscriptionMetadata(data.Subscriptions[0].Subscription.Id, "Подписка"), Assert.Single(session.Subscriptions));
        Assert.Equal(3, typeof(BackupProfileMetadata).GetProperties().Length);
        Assert.Equal(2, typeof(BackupSubscriptionMetadata).GetProperties().Length);
        Assert.DoesNotContain(typeof(BackupImportSession).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(BackupData) || field.FieldType == typeof(string));
        Assert.Equal(Secret, session.Materialize().Profiles[0].Secrets[Keys.Password]);
        Assert.Equal(Secret, session.Materialize().ToolSecrets["secret"]);
        Assert.Equal(data.Subscriptions[0].Url, session.Materialize().Subscriptions[0].Url);
    }

    [Fact]
    public void Dispose_is_idempotent_and_clears_owned_arrays_and_failed_read_plaintext()
    {
        var session = BackupArchive.OpenImport(BackupArchive.Encrypt(Data(), Password), Password);
        ReadOnlyMemory<byte>? plaintext = null;
        var read = typeof(BackupImportSession).GetMethod("ReadPlaintext", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(bool));
        var error = Assert.Throws<TargetInvocationException>(() => read.Invoke(session, [new Func<ReadOnlyMemory<byte>, bool>(bytes =>
        { plaintext = bytes; throw new IOException("Synthetic read failure"); })]));
        Assert.IsType<IOException>(error.InnerException);
        Assert.NotNull(plaintext);
        Assert.All(plaintext.Value.ToArray(), value => Assert.Equal(0, value));
        var key = Buffer(session, "_key");
        var archive = Buffer(session, "_archive");
        Assert.Contains(key, value => value != 0);
        session.Dispose();
        session.Dispose();
        Assert.All(key, value => Assert.Equal(0, value));
        Assert.All(archive, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => session.Materialize());
        var store = new ProfileStore(Path.Combine(TestDirs.New("disposed-backup"), "profiles.json"), new InMemorySecretStore());
        Assert.Throws<ObjectDisposedException>(() => session.Preview(store, BackupImportMode.Merge));
    }

    [Fact]
    public void Authentication_and_complete_validation_happen_before_preview()
    {
        var archive = BackupArchive.Encrypt(Data(), Password);
        Assert.ThrowsAny<CryptographicException>(() => BackupArchive.OpenImport(archive, "wrong"));
        archive[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => BackupArchive.OpenImport(archive, Password));
        Assert.Throws<InvalidDataException>(() => BackupArchive.OpenImport(archive[..30], Password));
        var data = Data();
        data.Settings.LocalProxyPort = 0;
        Assert.Throws<InvalidDataException>(() => BackupArchive.OpenImport(EncryptUnchecked(data), Password));
        data = Data();
        data.Profiles[0].Secrets.Values[Keys.Password] = "";
        Assert.Throws<InvalidDataException>(() => BackupArchive.OpenImport(EncryptUnchecked(data), Password));
        data = Data();
        data.Profiles.Add(data.Profiles[0]);
        Assert.Throws<InvalidDataException>(() => BackupArchive.OpenImport(EncryptUnchecked(data), Password));
        data = Data();
        data.Subscriptions[0] = data.Subscriptions[0] with { Url = "\u2003\t" };
        Assert.Throws<InvalidDataException>(() => BackupArchive.OpenImport(EncryptUnchecked(data), Password));
    }

    [Fact]
    public void Sanitizer_removes_escaped_secret_values_and_legacy_headers_before_deserialization()
    {
        var data = Data();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(data, JsonDefaults.Options.Info<BackupData>());
        try
        {
            var method = typeof(BackupImportSession).GetMethod("ReadMetadata", BindingFlags.NonPublic | BindingFlags.Static)!;
            var sanitized = (BackupData)method.Invoke(null, [new ReadOnlyMemory<byte>(plaintext)])!;
            Assert.Equal("x", sanitized.Profiles[0].Secrets[Keys.Password]);
            Assert.Equal("x", sanitized.Profiles[0].Secrets[Keys.RawConfig]);
            Assert.Equal("x", sanitized.Profiles[0].Profile.Options[Keys.XhttpHeaders]);
            Assert.Equal("x", sanitized.Subscriptions[0].Url);
            Assert.Equal("x", sanitized.ToolSecrets["secret"]);
            Assert.Equal("Сервер", sanitized.Profiles[0].Profile.Name);
            Assert.Equal(Secret, data.Profiles[0].Secrets[Keys.Password]);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    [Fact]
    public async Task Concurrent_dispose_and_materialization_do_not_leave_live_buffers()
    {
        var session = BackupArchive.OpenImport(BackupArchive.Encrypt(Data(), Password), Password);
        var key = Buffer(session, "_key");
        var reads = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try { Assert.Equal(Secret, session.Materialize().Profiles[0].Secrets[Keys.Password]); }
            catch (ObjectDisposedException) { }
        })).ToArray();
        session.Dispose();
        await Task.WhenAll(reads);
        Assert.All(key, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => session.Materialize());
    }

    [Theory]
    [InlineData(BackupImportMode.Merge)]
    [InlineData(BackupImportMode.Replace)]
    public async Task Preview_matches_existing_semantics_and_restore_without_password(BackupImportMode mode)
    {
        var data = Data();
        var store = new ProfileStore(Path.Combine(TestDirs.New("session-restore"), "profiles.json"), new InMemorySecretStore());
        await store.AddAsync([new(data.Profiles[0].Profile, new()), new(new Profile { Name = "Other" }, new())]);
        using var session = BackupArchive.OpenImport(BackupArchive.Encrypt(data, Password), Password);
        Assert.Equal(BackupArchive.Preview(data, store, mode), session.Preview(store, mode));
        await store.RestoreBackupAsync(session.Materialize(), mode);
        Assert.Equal(mode == BackupImportMode.Merge ? 2 : 1, store.Profiles.Count);
        Assert.Equal(Secret, (await store.Secrets.LoadAsync(data.Profiles[0].Profile.Id.ToString()))[Keys.Password]);
    }

    private static byte[] Buffer(BackupImportSession session, string name) =>
        (byte[])typeof(BackupImportSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;

    private static byte[] EncryptUnchecked(BackupData data)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(data, JsonDefaults.Options.Info<BackupData>());
        var archive = new byte[BackupArchive.Overhead + plaintext.Length];
        BackupArchive.Header.CopyTo(archive);
        RandomNumberGenerator.Fill(archive.AsSpan(8, 28));
        var key = Rfc2898DeriveBytes.Pbkdf2(Password, archive.AsSpan(8, 16), BackupArchive.Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(archive.AsSpan(24, 12), plaintext, archive.AsSpan(BackupArchive.Overhead), archive.AsSpan(36, 16), archive.AsSpan(0, 24));
            return archive;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }
}
