using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Bydlo.Core.Serialization;
using Bydlo.Core.Storage;

namespace Bydlo.Core.Backup;

public sealed record BackupProfileMetadata(Guid Id, string Name, Guid? SubscriptionId);
public sealed record BackupSubscriptionMetadata(Guid Id, string Name);

/// <summary>
/// Owns ciphertext and a clearable derived key, never a password or a decrypted object graph.
/// This reduces plaintext lifetime; it does not protect against process-memory access.
/// Dispose may safely race with materialization. Callers own any returned BackupData.
/// </summary>
public sealed class BackupImportSession : IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _archive;
    private readonly byte[] _key;
    private bool _disposed;

    public IReadOnlyList<BackupProfileMetadata> Profiles { get; private set; } = [];
    public IReadOnlyList<BackupSubscriptionMetadata> Subscriptions { get; private set; } = [];

    private BackupImportSession(byte[] archive, byte[] key) { _archive = archive; _key = key; }

    internal static BackupImportSession Open(byte[] file, string password, bool copy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (file.Length < BackupArchive.Overhead || file.Length > BackupArchive.MaximumFileSize ||
            !file.AsSpan(0, 8).SequenceEqual(BackupArchive.Header))
        { throw new InvalidDataException("Unsupported backup."); }
        // Never retain a caller-owned array that can change after authentication; an owned
        // array (OpenImportOwned) is already exclusive to the session.
        var archive = copy ? file.ToArray() : file;
        var key = Rfc2898DeriveBytes.Pbkdf2(password, archive.AsSpan(8, 16), BackupArchive.Iterations, HashAlgorithmName.SHA256, 32);
        var session = new BackupImportSession(archive, key);
        try
        {
            session.ReadPlaintext(plaintext =>
            {
                // Validate the complete schema with the same deserializer and validator as apply,
                // replacing credential strings before any deserialization into managed strings.
                var data = ReadMetadata(plaintext);
                session.Profiles = Array.AsReadOnly(data.Profiles.Select(p =>
                    new BackupProfileMetadata(p.Profile.Id, p.Profile.Name, p.Profile.SubscriptionId)).ToArray());
                session.Subscriptions = Array.AsReadOnly(data.Subscriptions.Select(s =>
                    new BackupSubscriptionMetadata(s.Subscription.Id, s.Subscription.Name)).ToArray());
                return true;
            });
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    public BackupPreview Preview(ProfileStore store, BackupImportMode mode)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var profiles = Profiles.Select(p => p.Id).ToHashSet();
            var subscriptions = Subscriptions.Select(s => s.Id).ToHashSet();
            var replaced = store.Profiles.Count(p => profiles.Contains(p.Id));
            var replacedSubs = store.Subscriptions.Count(s => subscriptions.Contains(s.Id));
            return new(profiles.Count - replaced, replaced,
                mode == BackupImportMode.Replace ? store.Profiles.Count - replaced : 0,
                subscriptions.Count - replacedSubs, replacedSubs,
                mode == BackupImportMode.Replace ? store.Subscriptions.Count - replacedSubs : 0);
        }
    }

    /// <summary>Materialize only immediately before restore. The caller must not retain the result.</summary>
    public BackupData Materialize()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadPlaintext(plaintext =>
            {
                var data = JsonSerializer.Deserialize(plaintext.Span, JsonDefaults.Options.Info<BackupData>())
                    ?? throw new InvalidDataException("Invalid backup.");
                BackupArchive.Validate(data);
                return data;
            });
        }
    }

    private T ReadPlaintext<T>(Func<ReadOnlyMemory<byte>, T> read) => BackupArchive.DecryptWith(_key, _archive, read);

    private static BackupData ReadMetadata(ReadOnlyMemory<byte> plaintext)
    {
        using var sanitized = new MemoryStream();
        using (var writer = new Utf8JsonWriter(sanitized))
        {
            var reader = new Utf8JsonReader(plaintext.Span);
            if (!reader.Read()) { throw new InvalidDataException("Invalid backup."); }
            CopyValue(ref reader, writer, false, false);
            if (reader.Read()) { throw new InvalidDataException("Invalid backup."); }
        }
        var data = JsonSerializer.Deserialize(sanitized.GetBuffer().AsSpan(0, (int)sanitized.Length),
            JsonDefaults.Options.Info<BackupData>()) ?? throw new InvalidDataException("Invalid backup.");
        BackupArchive.Validate(data);
        return data;
    }

    private static void CopyValue(ref Utf8JsonReader reader, Utf8JsonWriter writer, bool secret, bool options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                writer.WriteStartObject();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = reader.GetString() ?? throw new InvalidDataException("Invalid property.");
                    writer.WritePropertyName(name);
                    if (!reader.Read()) { throw new InvalidDataException("Invalid backup."); }
                    // Options from legacy archives can contain XHTTP credentials. Only version
                    // affects structural validation (ShadowTLS v1). SourceOptions may also carry them.
                    var hidden = secret || (options && !name.Equals("version", StringComparison.OrdinalIgnoreCase)) ||
                        name.Equals("secrets", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("toolSecrets", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("url", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("sourceOptions", StringComparison.OrdinalIgnoreCase);
                    CopyValue(ref reader, writer, hidden, name.Equals("options", StringComparison.OrdinalIgnoreCase));
                }
                if (reader.TokenType != JsonTokenType.EndObject) { throw new InvalidDataException("Invalid backup."); }
                writer.WriteEndObject();
                break;
            case JsonTokenType.StartArray:
                writer.WriteStartArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                { CopyValue(ref reader, writer, secret, options); }
                if (reader.TokenType != JsonTokenType.EndArray) { throw new InvalidDataException("Invalid backup."); }
                writer.WriteEndArray();
                break;
            case JsonTokenType.String when secret:
                // Preserve empty/whitespace semantics, including escaped Unicode whitespace,
                // without ever calling GetString on a credential. Clear the entire rented buffer.
                var chars = ArrayPool<char>.Shared.Rent(reader.ValueSpan.Length);
                try
                {
                    var length = reader.CopyString(chars);
                    writer.WriteStringValue(length == 0 ? "" : chars.AsSpan(0, length).IsWhiteSpace() ? " " : "x");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars.AsSpan()));
                    ArrayPool<char>.Shared.Return(chars);
                }
                break;
            case JsonTokenType.String:
                writer.WriteStringValue(reader.GetString());
                break;
            default:
                writer.WriteRawValue(reader.ValueSpan, skipInputValidation: false);
                break;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            CryptographicOperations.ZeroMemory(_key);
            CryptographicOperations.ZeroMemory(_archive);
        }
    }
}
