using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Bydlo.Core.Models;
using Bydlo.Core.Import.Parsers;
using Bydlo.Core.Serialization;
using Bydlo.Core.Storage;

namespace Bydlo.Core.Backup;

public enum BackupImportMode { Merge, Replace }

public sealed class BackupData
{
    public int Version { get; init; } = 1;
    public AppSettings Settings { get; init; } = new();
    public List<ImportedProfile> Profiles { get; init; } = [];
    public List<BackupSubscription> Subscriptions { get; init; } = [];
    public ProfileSecrets ToolSecrets { get; init; } = new();
    public List<ProxyChain> Chains { get; init; } = [];
}

public sealed record BackupSubscription(Subscription Subscription, string Url);
public sealed record BackupPreview(int AddedProfiles, int ReplacedProfiles, int RemovedProfiles,
    int AddedSubscriptions, int ReplacedSubscriptions, int RemovedSubscriptions);

/// <summary>Versioned, authenticated backup. Only encrypted bytes may be written to disk.</summary>
public static class BackupArchive
{
    // Fixed parameters prevent an untrusted file from choosing excessive KDF work.
    internal static ReadOnlySpan<byte> Header => "BYDLOBK\x01"u8;
    internal const int Iterations = 600_000;
    public const int MaximumFileSize = 32 * 1024 * 1024;
    internal const int Overhead = 8 + 16 + 12 + 16;

    public static BackupImportSession OpenImport(byte[] file, string password) => BackupImportSession.Open(file, password, copy: true);

    /// <summary>
    /// Opens an archive read with <see cref="ReadFileAsync"/>. Ownership of <paramref name="file"/>
    /// moves to the session: it is not copied again and is cleared when the session is disposed (issue #394).
    /// </summary>
    public static BackupImportSession OpenImportOwned(byte[] file, string password) => BackupImportSession.Open(file, password, copy: false);

    /// <summary>
    /// Reads an archive with a hard size limit (issue #394). A stream with a known length gets one
    /// exactly sized buffer after the limit check; an unknown length grows geometrically but never
    /// past <see cref="MaximumFileSize"/> + 1 bytes.
    /// </summary>
    public static async Task<byte[]> ReadFileAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long? known = null;
        try { if (stream.CanSeek) { known = stream.Length - stream.Position; } }
        catch (NotSupportedException) { }
        if (known is { } length)
        {
            if (length > MaximumFileSize) { throw new InvalidDataException("Backup is too large."); }
            var exact = new byte[length];
            try { await stream.ReadExactlyAsync(exact, ct).ConfigureAwait(false); }
            catch (EndOfStreamException ex) { throw new InvalidDataException("Backup changed while reading.", ex); }
            var probe = new byte[1];
            if (await stream.ReadAsync(probe, ct).ConfigureAwait(false) != 0)
            { throw new InvalidDataException("Backup changed while reading."); }
            return exact;
        }
        var buffer = new byte[Math.Min(64 * 1024, MaximumFileSize + 1)];
        var count = 0;
        while (true)
        {
            if (count == buffer.Length)
            {
                if (buffer.Length > MaximumFileSize) { throw new InvalidDataException("Backup is too large."); }
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, MaximumFileSize + 1L));
            }
            var read = await stream.ReadAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
            if (read == 0) { break; }
            count += read;
            if (count > MaximumFileSize) { throw new InvalidDataException("Backup is too large."); }
        }
        return count == buffer.Length ? buffer : buffer.AsSpan(0, count).ToArray();
    }

    public static byte[] Encrypt(BackupData data, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        Validate(data);
        // Cheap lower bound first: large raw configs fail before any buffer is allocated.
        if (MinimumPayloadSize(data) > MaximumFileSize - Overhead) { throw new InvalidDataException("Backup is too large."); }
        // Serialize straight after the header into one pooled buffer, enforcing the limit while
        // writing, then encrypt in place (issue #394). The buffer holds plaintext until encrypted,
        // so it is always cleared before it returns to the pool.
        using var buffer = new BoundedBuffer(Overhead, MaximumFileSize);
        byte[]? key = null;
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JsonDefaults.Options.Encoder, Indented = JsonDefaults.Options.WriteIndented }))
            {
                JsonSerializer.Serialize(writer, data, JsonDefaults.Options.Info<BackupData>());
            }
            var total = buffer.WrittenCount;
            var output = buffer.Array.AsSpan(0, total);
            Header.CopyTo(output);
            RandomNumberGenerator.Fill(output.Slice(8, 16));
            RandomNumberGenerator.Fill(output.Slice(24, 12));
            key = Rfc2898DeriveBytes.Pbkdf2(password, output.Slice(8, 16), Iterations, HashAlgorithmName.SHA256, 32);
            using var aes = new AesGcm(key, 16);
            var body = output[Overhead..];
            aes.Encrypt(output.Slice(24, 12), body, body, output.Slice(36, 16), output[..24]);
            return output.ToArray();
        }
        finally
        {
            if (key is not null) { CryptographicOperations.ZeroMemory(key); }
        }
    }

    /// <summary>
    /// Lower bound of the serialized size: every string value appears at least once in UTF-8.
    /// Escaping and property names only add bytes, so exceeding the limit here is conclusive.
    /// </summary>
    internal static long MinimumPayloadSize(BackupData data)
    {
        long total = 0;
        static long Bytes(string? value) => value is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(value);
        static long Dictionary(Dictionary<string, string>? values) =>
            values?.Sum(entry => Bytes(entry.Key) + Bytes(entry.Value)) ?? 0;
        foreach (var item in data.Profiles)
        {
            total += Dictionary(item.Secrets.Values) + Dictionary(item.Profile.Options)
                + Bytes(item.Profile.Name) + Bytes(item.Profile.Host);
        }
        foreach (var sub in data.Subscriptions)
        {
            total += Bytes(sub.Url) + Bytes(sub.Subscription.Name)
                + (sub.Subscription.SourceOptions?.Values.Sum(Dictionary) ?? 0);
        }
        return total + Dictionary(data.ToolSecrets.Values);
    }

    public static BackupData Decrypt(byte[] file, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (file.Length < Overhead || file.Length > MaximumFileSize || !file.AsSpan(0, 8).SequenceEqual(Header))
        { throw new InvalidDataException("Unsupported backup."); }
        var key = Rfc2898DeriveBytes.Pbkdf2(password, file.AsSpan(8, 16), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            return DecryptWith(key, file, plaintext =>
            {
                var data = JsonSerializer.Deserialize(plaintext.Span, JsonDefaults.Options.Info<BackupData>()) ?? throw new InvalidDataException("Invalid backup.");
                Validate(data);
                return data;
            });
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>
    /// Authenticates and decrypts into a pooled buffer that is cleared on every path. The reader only
    /// sees plaintext after the tag has been verified and must not retain the span.
    /// </summary>
    internal static T DecryptWith<T>(byte[] key, byte[] archive, Func<ReadOnlyMemory<byte>, T> read)
    {
        var length = archive.Length - Overhead;
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        var plaintext = rented.AsMemory(0, length);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(archive.AsSpan(24, 12), archive.AsSpan(Overhead), archive.AsSpan(36, 16), plaintext.Span, archive.AsSpan(0, 24));
            return read(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext.Span);
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static BackupPreview Preview(BackupData data, ProfileStore store, BackupImportMode mode)
    {
        Validate(data);
        var profiles = data.Profiles.Select(p => p.Profile.Id).ToHashSet();
        var subscriptions = data.Subscriptions.Select(s => s.Subscription.Id).ToHashSet();
        var replacedProfiles = store.Profiles.Count(p => profiles.Contains(p.Id));
        var replacedSubscriptions = store.Subscriptions.Count(s => subscriptions.Contains(s.Id));
        return new(profiles.Count - replacedProfiles, replacedProfiles,
            mode == BackupImportMode.Replace ? store.Profiles.Count - replacedProfiles : 0,
            subscriptions.Count - replacedSubscriptions, replacedSubscriptions,
            mode == BackupImportMode.Replace ? store.Subscriptions.Count - replacedSubscriptions : 0);
    }

    public static AppSettings ImportSettings(BackupData data, AppSettings current, BackupImportMode mode)
    {
        var settings = BackupCopy.Copy(data.Settings);
        // Device integration is never activated or changed by restoring a backup.
        settings.EnginePaths = new(current.EnginePaths, StringComparer.OrdinalIgnoreCase);
        settings.Zapret.InstallPath = current.Zapret.InstallPath;
        settings.StartWithSystem = current.StartWithSystem;
        settings.ConnectOnStartup = current.ConnectOnStartup;
        settings.MockMode = current.MockMode;
        settings.OnboardingCompleted = current.OnboardingCompleted;
        // GitHub consents are decisions about this device; a backup never grants network access (issue #438).
        settings.ConsentAutomaticUpdates = current.ConsentAutomaticUpdates;
        settings.ConsentNotifications = current.ConsentNotifications;
        settings.ConsentThemes = current.ConsentThemes;
        settings.ConsentsInitialized = current.ConsentsInitialized;
        // Custom theme files live next to the data directory and are not part of the backup.
        settings.CustomThemeId = current.CustomThemeId;
        settings.MigrateLegacyTheme();
        if (settings.Theme == ThemePreference.Custom && settings.CustomThemeId is null)
        {
            settings.Theme = ThemePreference.System;
        }
        if (mode == BackupImportMode.Merge)
        {
            settings.ServerGroups = [.. current.ServerGroups.Where(g => settings.ServerGroups.All(i => i.Id != g.Id)), .. settings.ServerGroups];
            var r = settings.Routing;
            var c = current.Routing;
            r.DirectDomains = Union(c.DirectDomains, r.DirectDomains);
            r.ProxyDomains = Union(c.ProxyDomains, r.ProxyDomains);
            r.BlockDomains = Union(c.BlockDomains, r.BlockDomains);
            r.DirectCidrs = Union(c.DirectCidrs, r.DirectCidrs);
            r.DirectProcesses = Union(c.DirectProcesses, r.DirectProcesses);
            r.OrderedRules = [.. c.OrderedRules.Concat(r.OrderedRules).Distinct()];
            r.RuleSetSources = [.. c.RuleSetSources.Concat(r.RuleSetSources).DistinctBy(source => source.Tag)];
        }
        return settings;
    }

    private static List<string> Union(List<string> current, List<string> incoming) =>
        [.. current.Concat(incoming).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Secret keys a profile must carry (issue #344). An empty list means the protocol
    /// allows credential-free profiles (plain HTTP/SOCKS5, Hysteria2 without auth): an
    /// empty secret bag exports and restores normally instead of aborting.
    /// ShadowTLS v1 carries only the inner password; v2/v3 additionally require the
    /// outer shadow password (the parser omits it for v1).
    /// </summary>
    public static IReadOnlyList<string> RequiredSecretKeys(Profile profile) => profile.Protocol switch
    {
        ProtocolKind.Vless or ProtocolKind.Vmess => [Keys.Uuid],
        ProtocolKind.Trojan or ProtocolKind.Shadowsocks or ProtocolKind.AnyTls => [Keys.Password],
        ProtocolKind.ShadowTls => profile.Options.TryGetValue("version", out var version) && version == "1"
            ? [Keys.Password]
            : [Keys.Password, SingBoxUriParser.ShadowPassword],
        ProtocolKind.Naive => [Keys.Username, Keys.Password],
        ProtocolKind.Tuic => [Keys.Uuid, Keys.Password],
        ProtocolKind.WireGuard or ProtocolKind.AmneziaWG or ProtocolKind.OpenVpn
            or ProtocolKind.SingBoxConfig or ProtocolKind.XrayConfig => [Keys.RawConfig],
        // Ssh accepts either a password or a private key (checked in HasRequiredSecrets).
        // Hysteria2/Http/Socks5 allow credential-free profiles.
        _ => [],
    };

    /// <summary>True when every secret key the profile requires is present and non-empty.</summary>
    public static bool HasRequiredSecrets(Profile profile, ProfileSecrets secrets)
    {
        if (profile.Protocol == ProtocolKind.Ssh)
        {
            // The parser always stores the username; auth is a password or a private key.
            return secrets[Keys.Username] is not null
                && (secrets[Keys.Password] is not null || secrets[SingBoxUriParser.PrivateKey] is not null);
        }
        return RequiredSecretKeys(profile).All(key => secrets[key] is not null);
    }

    internal static void Validate(BackupData data)
    {
        if (data.Version != 1 || data.Settings is null || data.Profiles is null || data.Subscriptions is null || data.ToolSecrets?.Values is null || data.Chains is null)
        { throw new InvalidDataException("Invalid backup."); }
        var s = data.Settings;
        // Every collection the import path enumerates must be present: a null list passed validation
        // and threw later in ImportSettings or after it was persisted.
        if (s.ServerGroups is null || s.Contributors is null ||
            s.ServerGroups.Any(g => g is null || g.Id == Guid.Empty || g.Name is null || g.ProfileIds is null || g.ProbeUrl is null) ||
            s.Contributors.Any(c => c is null || c.Login is null))
        { throw new InvalidDataException("Invalid settings."); }
        if (s.Routing is null || s.DpiDefaults is null || s.Zapret is null || s.TgWsProxy is null || s.EnginePaths is null ||
            s.Routing.DirectDomains is null || s.Routing.ProxyDomains is null || s.Routing.BlockDomains is null ||
            s.Routing.DirectCidrs is null || s.Routing.DirectProcesses is null || s.Routing.OrderedRules is null ||
            s.Routing.RemoteDns is null || s.Routing.DirectDns is null ||
            s.Routing.FakeIpExceptions is null || s.Routing.RemoteDnsDomains is null || s.Routing.DirectDnsDomains is null ||
            s.Routing.RemoteDnsRuleSets is null || s.Routing.DirectDnsRuleSets is null || s.Routing.PresetCountry is null ||
            !Enum.IsDefined(s.Routing.Preset) || !Enum.IsDefined(s.Routing.RemoteDnsTransport) || !Enum.IsDefined(s.Routing.DirectDnsTransport) ||
            s.Zapret.TestTargets is null || s.Zapret.GameFilter is not ("disabled" or "all" or "tcp" or "udp") ||
            s.TgWsProxy.DcIps is null || s.TgWsProxy.CfProxyDomains is null || s.TgWsProxy.Host is null || s.TgWsProxy.FakeTlsDomain is null ||
            s.LocalProxyUsername is null ||
            s.LocalProxyPort is < 1 or > 65534 || s.TgWsProxy.Port is < 1 or > 65535 ||
            !Enum.IsDefined(s.Theme) || !Enum.IsDefined(s.Mode) || !Enum.IsDefined(s.MockScenario) || !Enum.IsDefined(s.IpCheckProvider) ||
            s.Language is not ("auto" or "en" or "ru"))
        { throw new InvalidDataException("Invalid settings."); }
        if (s.DpiDefaults.Fragment is null || s.DpiDefaults.Mux is null || s.DpiDefaults.Size is null || s.DpiDefaults.Interval is null ||
            s.Routing.OrderedRules.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.Value) ||
                !Enum.IsDefined(rule.Condition) || !Enum.IsDefined(rule.Action)) ||
            new[] { s.Routing.FakeIpExceptions, s.Routing.RemoteDnsDomains, s.Routing.DirectDnsDomains,
                s.Routing.RemoteDnsRuleSets, s.Routing.DirectDnsRuleSets }.Any(values => values.Any(value => value is null)))
        { throw new InvalidDataException("Invalid routing or DPI settings."); }
        try { s.DpiDefaults.ValidateFormat(); }
        catch (Bydlo.Core.Engines.EngineException ex) { throw new InvalidDataException("Invalid DPI settings.", ex); }
        try { new Routing.RoutingRuleBook(s.Routing.OrderedRules, s.Routing.RuleSetSources).Validate(); }
        catch (Bydlo.Core.Engines.EngineException ex) { throw new InvalidDataException("Invalid routing settings.", ex); }
        var ids = new HashSet<Guid>();
        foreach (var sub in data.Subscriptions)
        {
            if (sub?.Subscription is null || sub.Subscription.Id == Guid.Empty || !ids.Add(sub.Subscription.Id) ||
                sub.Subscription.Name is null || string.IsNullOrWhiteSpace(sub.Url))
            { throw new InvalidDataException("Invalid subscription."); }
        }
        var profileIds = new HashSet<Guid>();
        foreach (var item in data.Profiles)
        {
            if (item?.Profile is not { } p || item.Secrets?.Values is null || p.Id == Guid.Empty || !profileIds.Add(p.Id) ||
                p.Options is null || p.Name is null || p.Host is null || !Enum.IsDefined(p.Protocol) ||
                p.Port is < 0 or > 65535 || (p.SubscriptionId is { } id && !ids.Contains(id)))
            { throw new InvalidDataException("Invalid profile."); }
            if (!HasRequiredSecrets(p, item.Secrets))
            { throw new InvalidDataException($"Invalid profile '{p.Id}': credentials are missing."); }
        }
        var chainIds = new HashSet<Guid>();
        foreach (var chain in data.Chains)
        {
            // Referential integrity (hop ids) is resolved at restore time against the merged
            // document, so validation here is structural only.
            if (chain is null || chain.Id == Guid.Empty || !chainIds.Add(chain.Id) ||
                chain.Name is null || chain.ProfileIds is null ||
                chain.ProfileIds.Count is < 2 or > 50 ||
                chain.ProfileIds.Distinct().Count() != chain.ProfileIds.Count ||
                chain.ProfileIds.Any(id => id == Guid.Empty) ||
                (chain.PreferredEngine is { } kind && !Enum.IsDefined(kind)))
            { throw new InvalidDataException("Invalid chain."); }
        }
    }
}
