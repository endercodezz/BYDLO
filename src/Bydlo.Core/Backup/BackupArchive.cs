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

    public static BackupImportSession OpenImport(byte[] file, string password) => BackupImportSession.Open(file, password);

    public static byte[] Encrypt(BackupData data, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        Validate(data);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(data, JsonDefaults.Options.Info<BackupData>());
        byte[]? key = null;
        try
        {
            if (plaintext.Length > MaximumFileSize - Overhead) { throw new InvalidDataException("Backup is too large."); }
            var output = new byte[Overhead + plaintext.Length];
            Header.CopyTo(output);
            RandomNumberGenerator.Fill(output.AsSpan(8, 16));
            RandomNumberGenerator.Fill(output.AsSpan(24, 12));
            key = Rfc2898DeriveBytes.Pbkdf2(password, output.AsSpan(8, 16), Iterations, HashAlgorithmName.SHA256, 32);
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(24, 12), plaintext, output.AsSpan(Overhead), output.AsSpan(36, 16), output.AsSpan(0, 24));
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (key is not null) { CryptographicOperations.ZeroMemory(key); }
        }
    }

    public static BackupData Decrypt(byte[] file, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (file.Length < Overhead || file.Length > MaximumFileSize || !file.AsSpan(0, 8).SequenceEqual(Header))
        { throw new InvalidDataException("Unsupported backup."); }
        var key = Rfc2898DeriveBytes.Pbkdf2(password, file.AsSpan(8, 16), Iterations, HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[file.Length - Overhead];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(file.AsSpan(24, 12), file.AsSpan(Overhead), file.AsSpan(36, 16), plaintext, file.AsSpan(0, 24));
            var data = JsonSerializer.Deserialize(plaintext, JsonDefaults.Options.Info<BackupData>()) ?? throw new InvalidDataException("Invalid backup.");
            Validate(data);
            return data;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
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
