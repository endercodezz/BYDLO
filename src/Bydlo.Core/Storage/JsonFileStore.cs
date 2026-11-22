using System.Text.Json;
using System.Text.Json.Serialization;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Serialization;

namespace Bydlo.Core.Storage;

internal static class JsonDefaults
{
    /// <summary>Indented, string enums, nulls omitted; source-generated (see <see cref="CoreStoreJsonContext"/>).</summary>
    public static readonly JsonSerializerOptions Options = CoreStoreJsonContext.Default.Options;

    /// <summary>Writes atomically (temp file + fsync + move) so a crash never leaves a half-written file.</summary>
    public static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fs, value, Options.Info<T>(), ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
        BackupLastGood(path);
    }

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var fs = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync(fs, Options.Info<T>(), ct).ConfigureAwait(false);
            BackupLastGood(path);
            return value;
        }
        catch (JsonException)
        {
            // Keep the broken file under a unique name so repeated failures never overwrite evidence.
            SaveCorruptCopy(path);
            if (TryRestoreBackup<T>(path, ct, out var restored))
            {
                return restored;
            }

            return default;
        }
    }

    internal static void BackupLastGood(string path)
    {
        try
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static void SaveCorruptCopy(string path)
    {
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var candidate = $"{path}.corrupt-{stamp}-{Guid.NewGuid().ToString("N")[..8]}.json";
            File.Copy(path, candidate, overwrite: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static bool TryRestoreBackup<T>(string path, CancellationToken ct, out T? restored)
    {
        restored = default;
        var backup = path + ".bak";
        if (!File.Exists(backup))
        {
            return false;
        }

        try
        {
            using var fs = File.OpenRead(backup);
            restored = JsonSerializer.Deserialize(fs, Options.Info<T>());
            return restored is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task<(T? Value, StoreLoadStatus Status)> ReadWithStatusAsync<T>(
        string path, Func<T, int>? versionSelector, int currentVersion, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return (default, StoreLoadStatus.Missing);
        }

        T? value;
        try
        {
            await using (var fs = File.OpenRead(path))
            {
                value = await JsonSerializer.DeserializeAsync(fs, Options.Info<T>(), ct).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            SaveCorruptCopy(path);
            if (TryRestoreBackup<T>(path, ct, out var restored) && restored is not null)
            {
                if (versionSelector is not null && versionSelector(restored) > currentVersion)
                {
                    return (restored, StoreLoadStatus.NewerVersion);
                }

                return (restored, StoreLoadStatus.RecoveredFromBackup);
            }

            return (default, StoreLoadStatus.ResetEmpty);
        }

        if (value is null)
        {
            SaveCorruptCopy(path);
            if (TryRestoreBackup<T>(path, ct, out var restored) && restored is not null)
            {
                return (restored, StoreLoadStatus.RecoveredFromBackup);
            }

            return (default, StoreLoadStatus.ResetEmpty);
        }

        BackupLastGood(path);
        if (versionSelector is not null && versionSelector(value) > currentVersion)
        {
            return (value, StoreLoadStatus.NewerVersion);
        }

        return (value, StoreLoadStatus.Ok);
    }
}

/// <summary>How a JSON store file was loaded (issue #204).</summary>
public enum StoreLoadStatus
{
    Ok,
    Missing,
    RecoveredFromBackup,
    ResetEmpty,
    NewerVersion,
}

#pragma warning disable CA1001 // lives for the whole app lifetime, like ProfileStore below
public sealed class SettingsStore(string path)
#pragma warning restore CA1001
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How the settings file was loaded (issue #204). Used for the startup toast.</summary>
    public StoreLoadStatus LoadStatus { get; private set; } = StoreLoadStatus.Ok;

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        var (settings, status) = await JsonDefaults.ReadWithStatusAsync<AppSettings>(path, null, 0, ct).ConfigureAwait(false);
        LoadStatus = status;
        var result = settings ?? new AppSettings();
        result.MigrateLegacyTheme();
        result.NormalizeInterfaceScale();
        return result;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        // Rapid preference changes must not concurrently write or move the same temporary file.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await JsonDefaults.WriteAtomicAsync(path, settings, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}

/// <summary>How a subscription refresh treats provider nodes that were not saved locally (issue #185).</summary>
public enum SubscriptionMode
{
    /// <summary>Refresh adds every new provider node (legacy behavior, default for old subscriptions).</summary>
    FollowAll = 0,

    /// <summary>Refresh only updates already saved nodes; deselected and brand-new nodes are ignored.</summary>
    SelectedOnly = 1,
}

public sealed class Subscription
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>Subscription URLs often embed tokens, so the URL itself is stored in the secret store.</summary>
    public DateTimeOffset? LastUpdated { get; set; }

    /// <summary>Traffic counters from the last <c>subscription-userinfo</c> header (bytes).</summary>
    public long? Upload { get; set; }

    public long? Download { get; set; }

    public long? Total { get; set; }

    public DateTimeOffset? Expires { get; set; }

    /// <summary>Provider display name from the <c>profile-title</c> header (untrusted text).</summary>
    public string? Title { get; set; }

    /// <summary>Provider-suggested refresh interval from <c>profile-update-interval</c> (hours, 1-168).</summary>
    public long? UpdateIntervalHours { get; set; }

    /// <summary>Provider support link from <c>support-url</c>, shown as plain text (never a command).</summary>
    public string? SupportUrl { get; set; }

    /// <summary>Provider announcement from <c>announce</c>, shown as plain text.</summary>
    public string? Announce { get; set; }

    /// <summary>Per-subscription User-Agent override. Null or empty means the default <c>BYDLO/x.y</c>.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Refresh policy for nodes that are not saved locally. Missing in old files means <see cref="SubscriptionMode.FollowAll"/>.</summary>
    public SubscriptionMode Mode { get; set; } = SubscriptionMode.FollowAll;

    /// <summary>Last provider options, used to identify local overrides during refresh.</summary>
    public Dictionary<Guid, Dictionary<string, string>>? SourceOptions { get; init; }
}

internal sealed class ProfileDocument
{
    public int Version { get; set; } = 1;

    public List<Profile> Profiles { get; set; } = [];

    public List<Subscription> Subscriptions { get; set; } = [];

    public List<ProxyChain> Chains { get; set; } = [];

    // IDs only: retain failed secret cleanup across retries and restarts.
    public List<string> PendingSecretDeletes { get; set; } = [];
}

/// <summary>Profile list persisted as JSON; secrets are delegated to <see cref="ISecretStore"/>.</summary>
#pragma warning disable CA1001 // lives for the whole app lifetime
public sealed partial class ProfileStore(string path, ISecretStore secrets, TimeProvider? time = null)
#pragma warning restore CA1001
{
    internal const int CurrentVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProfileDocument _doc = new();

    /// <summary>How profiles.json was loaded (issue #204). Newer versions are never overwritten.</summary>
    public StoreLoadStatus LoadStatus { get; private set; } = StoreLoadStatus.Ok;

    public bool IsNewerVersion => LoadStatus == StoreLoadStatus.NewerVersion || _doc.Version > CurrentVersion;

    private void ThrowIfNewerVersion()
    {
        if (IsNewerVersion)
        {
            throw new InvalidOperationException("Profiles file was created by a newer BYDLO version. Update BYDLO instead of overwriting it.");
        }
    }

    public IReadOnlyList<Profile> Profiles => _doc.Profiles;

    public IReadOnlyList<Subscription> Subscriptions => _doc.Subscriptions;

    public ISecretStore Secrets => secrets;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var (doc, status) = await JsonDefaults.ReadWithStatusAsync<ProfileDocument>(path, d => d.Version, CurrentVersion, ct).ConfigureAwait(false);
        LoadStatus = status;
        _doc = doc ?? new ProfileDocument();
        _doc.Profiles ??= [];
        _doc.Subscriptions ??= [];
        _doc.Chains ??= [];
        _doc.PendingSecretDeletes ??= [];
        await MigrateXhttpSecretHeadersAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Profile>> AddAsync(IEnumerable<ImportedProfile> imported, Guid? subscriptionId = null, CancellationToken ct = default)
    {
        var added = new List<Profile>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNewerVersion();
            var items = imported.ToList();
            if (items.Select(p => p.Profile.Id).Distinct().Count() != items.Count || items.Any(p => _doc.Profiles.Any(existing => existing.Id == p.Profile.Id)))
            { throw new InvalidDataException("New profiles must have unique IDs."); }
            var written = new List<string>();
            try
            {
                foreach (var item in items)
                {
                    item.Profile.SubscriptionId = subscriptionId ?? item.Profile.SubscriptionId;
                    written.Add(item.Profile.Id.ToString());
                    Bydlo.Core.Import.Parsers.XhttpSecretHeaders.Split(item.Profile, item.Secrets);
                    await secrets.SaveAsync(written[^1], item.Secrets, ct).ConfigureAwait(false);
                    added.Add(item.Profile);
                }
                var next = new ProfileDocument { Version = _doc.Version, PendingSecretDeletes = [.. _doc.PendingSecretDeletes], Profiles = [.. _doc.Profiles, .. added], Subscriptions = [.. _doc.Subscriptions], Chains = [.. _doc.Chains] };
                await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
                _doc = next;
            }
            catch
            {
                foreach (var id in written) { await secrets.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false); }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        return added;
    }

    public Task UpdateAsync(Profile profile, CancellationToken ct = default) =>
        UpdateAsync(profile, null, ct);

    public async Task UpdateAsync(Profile profile, ProfileSecrets? updatedSecrets, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNewerVersion();
            var index = _doc.Profiles.FindIndex(p => p.Id == profile.Id);
            if (index < 0)
            {
                return;
            }

            var next = new ProfileDocument { Version = _doc.Version, PendingSecretDeletes = [.. _doc.PendingSecretDeletes], Profiles = [.. _doc.Profiles], Subscriptions = [.. _doc.Subscriptions], Chains = [.. _doc.Chains] };
            next.Profiles[index] = profile;
            var id = profile.Id.ToString();
            ProfileSecrets? previous = null;
            ProfileSecrets? toSave = updatedSecrets;
            if (updatedSecrets is not null)
            {
                previous = await secrets.LoadAsync(id, ct).ConfigureAwait(false);
            }
            else if (profile.Options.TryGetValue(Bydlo.Core.Models.Keys.XhttpExtra, out var raw) &&
                Bydlo.Core.Import.Parsers.XhttpSecretHeaders.TryExtractHeaders(raw, out _, out var splitHeaders) &&
                splitHeaders is not null)
            {
                // Without accompanying secrets, headers edited into options would
                // persist as plaintext (issue #338): move them first, touching the
                // secret store only when headers are actually present.
                var current = await secrets.LoadAsync(id, ct).ConfigureAwait(false);
                previous = new ProfileSecrets { Values = new Dictionary<string, string>(current.Values, StringComparer.OrdinalIgnoreCase) };
                toSave = current;
            }
            try
            {
                if (toSave is not null)
                {
                    Bydlo.Core.Import.Parsers.XhttpSecretHeaders.Split(profile, toSave);
                    await secrets.SaveAsync(id, toSave, ct).ConfigureAwait(false);
                }
                await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
                _doc = next;
            }
            catch
            {
                if (previous is not null)
                {
                    await secrets.SaveAsync(id, previous, CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNewerVersion();
            await RemoveProfilesAsync([id], null, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Subscription> AddSubscriptionAsync(string name, string url, CancellationToken ct = default)
    {
        var sub = new Subscription { Name = name };
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNewerVersion();
            var secretId = SubscriptionSecretId(sub.Id);
            // Detached snapshot: publish only after the atomic write succeeds (issue #399).
            var next = new ProfileDocument { Version = _doc.Version, PendingSecretDeletes = [.. _doc.PendingSecretDeletes], Profiles = [.. _doc.Profiles], Subscriptions = [.. _doc.Subscriptions, sub], Chains = [.. _doc.Chains] };
            try
            {
                await secrets.SaveAsync(secretId, new ProfileSecrets { Values = { ["url"] = url } }, ct).ConfigureAwait(false);
                await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
                _doc = next;
            }
            catch
            {
                await secrets.DeleteAsync(secretId, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        return sub;
    }

    /// <summary>Saves a previewed subscription and its credentials together, without downloading it again.</summary>
    public async Task<Subscription> AddSubscriptionWithProfilesAsync(string name, string url, IReadOnlyList<ImportedProfile> profiles, Bydlo.Core.Import.SubscriptionMetadata? metadata = null, SubscriptionMode mode = SubscriptionMode.FollowAll, string? userAgent = null, CancellationToken ct = default)
    {
        userAgent = userAgent?.Trim();
        if (userAgent?.Any(char.IsControl) == true)
        { throw new ArgumentException("User-Agent must not contain control characters.", nameof(userAgent)); }
        if (userAgent?.Length > 256)
        { throw new ArgumentException("User-Agent is too long (max 256 characters).", nameof(userAgent)); }
        var sub = new Subscription
        {
            Name = name,
            LastUpdated = (time ?? TimeProvider.System).GetUtcNow(),
            Upload = metadata?.Upload,
            Download = metadata?.Download,
            Total = metadata?.Total,
            Expires = metadata?.Expires,
            Title = metadata?.Title,
            UpdateIntervalHours = metadata?.UpdateIntervalHours,
            SupportUrl = metadata?.SupportUrl,
            Announce = metadata?.Announce,
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent,
            Mode = mode,
            SourceOptions = [],
        };
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNewerVersion();
            if (profiles.Count == 0 || profiles.Select(p => p.Profile.Id).Distinct().Count() != profiles.Count ||
                profiles.Any(p => _doc.Profiles.Any(existing => existing.Id == p.Profile.Id)))
            { throw new InvalidDataException("A new subscription requires new profiles."); }
            var written = new List<string>();
            try
            {
                written.Add(SubscriptionSecretId(sub.Id));
                await secrets.SaveAsync(written[0], new ProfileSecrets { Values = { ["url"] = url } }, ct).ConfigureAwait(false);
                foreach (var item in profiles)
                {
                    written.Add(item.Profile.Id.ToString());
                    Bydlo.Core.Import.Parsers.XhttpSecretHeaders.Split(item.Profile, item.Secrets);
                    await secrets.SaveAsync(written[^1], item.Secrets, ct).ConfigureAwait(false);
                    item.Profile.SubscriptionId = sub.Id;
                    sub.SourceOptions!.Add(item.Profile.Id, new(item.Profile.Options, StringComparer.OrdinalIgnoreCase));
                }
                var next = new ProfileDocument { Version = _doc.Version, PendingSecretDeletes = [.. _doc.PendingSecretDeletes], Profiles = [.. _doc.Profiles, .. profiles.Select(p => p.Profile)], Subscriptions = [.. _doc.Subscriptions, sub], Chains = [.. _doc.Chains] };
                await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
                _doc = next;
            }
            catch
            {
                foreach (var id in written) { await secrets.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false); }
                throw;
            }
        }
        finally { _gate.Release(); }
        return sub;
    }

    public async Task<string?> GetSubscriptionUrlAsync(Guid subscriptionId, CancellationToken ct = default) =>
        (await secrets.LoadAsync(SubscriptionSecretId(subscriptionId), ct).ConfigureAwait(false))["url"];

    public async Task RenameSubscriptionAsync(Guid subscriptionId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        { throw new ArgumentException("Subscription name must not be empty.", nameof(name)); }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNewerVersion();
            var sub = _doc.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId)
                ?? throw new InvalidDataException("Subscription not found.");
            // Detached snapshot: publish only after the atomic write succeeds (issue #399).
            var renamed = CopySubscription(sub);
            renamed.Name = name.Trim();
            var next = new ProfileDocument { Version = _doc.Version, PendingSecretDeletes = [.. _doc.PendingSecretDeletes], Profiles = [.. _doc.Profiles], Subscriptions = _doc.Subscriptions.Select(item => item.Id == subscriptionId ? renamed : item).ToList(), Chains = [.. _doc.Chains] };
            await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
            _doc = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetSubscriptionUserAgentAsync(Guid subscriptionId, string? userAgent, CancellationToken ct = default)
    {
        userAgent = userAgent?.Trim();
        if (userAgent?.Any(char.IsControl) == true)
        { throw new ArgumentException("User-Agent must not contain control characters.", nameof(userAgent)); }
        if (userAgent?.Length > 256)
        { throw new ArgumentException("User-Agent is too long (max 256 characters).", nameof(userAgent)); }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNewerVersion();
            var sub = _doc.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId)
                ?? throw new InvalidDataException("Subscription not found.");
            // Detached snapshot: publish only after the atomic write succeeds (issue #399).
            var updated = CopySubscription(sub);
            updated.UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent;
            var next = new ProfileDocument { Version = _doc.Version, PendingSecretDeletes = [.. _doc.PendingSecretDeletes], Profiles = [.. _doc.Profiles], Subscriptions = _doc.Subscriptions.Select(item => item.Id == subscriptionId ? updated : item).ToList(), Chains = [.. _doc.Chains] };
            await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
            _doc = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Subscription CopySubscription(Subscription source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        LastUpdated = source.LastUpdated,
        Upload = source.Upload,
        Download = source.Download,
        Total = source.Total,
        Expires = source.Expires,
        Title = source.Title,
        UpdateIntervalHours = source.UpdateIntervalHours,
        SupportUrl = source.SupportUrl,
        Announce = source.Announce,
        UserAgent = source.UserAgent,
        Mode = source.Mode,
        SourceOptions = source.SourceOptions,
    };

    public async Task SetSubscriptionModeAsync(Guid subscriptionId, SubscriptionMode mode, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode))
        { throw new ArgumentOutOfRangeException(nameof(mode)); }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNewerVersion();
            var sub = _doc.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId)
                ?? throw new InvalidDataException("Subscription not found.");
            if (sub.Mode == mode)
            {
                return;
            }
            var next = new ProfileDocument
            {
                Version = _doc.Version,
                PendingSecretDeletes = [.. _doc.PendingSecretDeletes],
                Profiles = [.. _doc.Profiles],
                Subscriptions = _doc.Subscriptions.Select(item => item.Id == subscriptionId
                    ? new Subscription
                    {
                        Id = item.Id, Name = item.Name, LastUpdated = item.LastUpdated,
                        Upload = item.Upload, Download = item.Download, Total = item.Total, Expires = item.Expires,
                        Title = item.Title, UpdateIntervalHours = item.UpdateIntervalHours,
                        SupportUrl = item.SupportUrl, Announce = item.Announce, UserAgent = item.UserAgent,
                        Mode = mode, SourceOptions = item.SourceOptions,
                    }
                    : item).ToList(),
                Chains = [.. _doc.Chains],
            };
            await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
            _doc = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a subscription together with its profiles and all related secrets.</summary>
    public async Task RemoveSubscriptionAsync(Guid subscriptionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNewerVersion();
            var ids = _doc.Profiles.Where(p => p.SubscriptionId == subscriptionId).Select(p => p.Id).ToHashSet();
            await RemoveProfilesAsync(ids, subscriptionId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string SubscriptionSecretId(Guid id) => $"subscription:{id}";

    // Caller holds _gate. Publish a detached snapshot before deleting any credentials.
    private async Task RemoveProfilesAsync(HashSet<Guid> ids, Guid? subscriptionId, CancellationToken ct)
    {
        var next = new ProfileDocument
        {
            Version = _doc.Version,
            Profiles = _doc.Profiles.Where(p => !ids.Contains(p.Id)).ToList(),
            Subscriptions = _doc.Subscriptions.Where(s => s.Id != subscriptionId).ToList(),
            Chains = _doc.Chains.Select(c => new ProxyChain
            {
                Id = c.Id, Name = c.Name, PreferredEngine = c.PreferredEngine, AddedAt = c.AddedAt, IsFavorite = c.IsFavorite,
                ProfileIds = c.ProfileIds.Where(id => !ids.Contains(id)).ToList(),
            }).Where(c => c.ProfileIds.Count >= ChainValidator.MinHops).ToList(),
            PendingSecretDeletes = _doc.PendingSecretDeletes
                .Concat(ids.Select(id => id.ToString()))
                .Concat(subscriptionId is { } subId ? [SubscriptionSecretId(subId)] : Array.Empty<string>())
                .Distinct(StringComparer.Ordinal).ToList(),
        };
        await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
        _doc = next;

        // Once committed, caller cancellation must not interrupt cleanup. Keep the persisted
        // queue on any failure, so another removal (including after restart) can retry it.
        var active = next.Profiles.Select(p => p.Id.ToString())
            .Concat(next.Subscriptions.Select(s => SubscriptionSecretId(s.Id))).ToHashSet(StringComparer.Ordinal);
        foreach (var secretId in next.PendingSecretDeletes.Where(id => !active.Contains(id)))
        {
            await secrets.DeleteAsync(secretId, CancellationToken.None).ConfigureAwait(false);
        }
        var pending = next.PendingSecretDeletes;
        next.PendingSecretDeletes = [];
        try { await JsonDefaults.WriteAtomicAsync(path, next, CancellationToken.None).ConfigureAwait(false); }
        catch { next.PendingSecretDeletes = pending; throw; }
    }
}
