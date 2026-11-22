using Bydlo.Core.Backup;
using Bydlo.Core.Config;
using Bydlo.Core.Connection;
using Bydlo.Core.Models;

namespace Bydlo.Core.Storage;

public sealed partial class ProfileStore
{
    public async Task<BackupData> CaptureBackupAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = new BackupData { Settings = BackupCopy.Copy(settings), ToolSecrets = await CaptureToolSecretsAsync(ct).ConfigureAwait(false) };
            data.Settings.EnginePaths.Clear();
            data.Settings.Zapret.InstallPath = null;
            foreach (var profile in _doc.Profiles)
            {
                var secretId = profile.Id.ToString();
                ProfileSecrets loaded;
                try
                {
                    loaded = await secrets.LoadAsync(secretId, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new Security.SecretAccessException(secretId, ex);
                }
                if (!BackupArchive.HasRequiredSecrets(profile, loaded))
                {
                    throw new Security.SecretAccessException(secretId);
                }
                data.Profiles.Add(new(BackupCopy.Copy(profile), loaded));
            }
            foreach (var sub in _doc.Subscriptions)
            {
                string? url;
                try
                {
                    url = await GetSubscriptionUrlAsync(sub.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new Security.SecretAccessException($"subscription:{sub.Id}", ex);
                }
                if (string.IsNullOrWhiteSpace(url))
                {
                    throw new Security.SecretAccessException($"subscription:{sub.Id}");
                }
                data.Subscriptions.Add(new(BackupCopy.Copy(sub), url));
            }
            foreach (var chain in _doc.Chains)
            { data.Chains.Add(BackupCopy.Copy(chain)); }
            return data;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Restores credentials and the document together; rolls back on a storage failure.</summary>
    public async Task RestoreBackupAsync(BackupData data, BackupImportMode mode, CancellationToken ct = default)
    {
        data = BackupCopy.Copy(data);
        BackupArchive.Validate(data);
        // Old archives keep XHTTP headers in plain options: move them to secrets
        // before anything is persisted (issue #338).
        foreach (var item in data.Profiles)
        {
            Bydlo.Core.Import.Parsers.XhttpSecretHeaders.Split(item.Profile, item.Secrets);
        }
        foreach (var sub in data.Subscriptions)
        {
            if (sub.Subscription.SourceOptions is null)
            {
                continue;
            }
            foreach (var options in sub.Subscription.SourceOptions.Values)
            {
                Bydlo.Core.Import.Parsers.XhttpSecretHeaders.Strip(options);
            }
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNewerVersion();
            var incoming = new ProfileDocument { Version = _doc.Version, PendingSecretDeletes = [.. _doc.PendingSecretDeletes], Profiles = data.Profiles.Select(p => p.Profile).ToList(), Subscriptions = data.Subscriptions.Select(s => s.Subscription).ToList() };
            if (mode == BackupImportMode.Merge)
            {
                incoming.Profiles.InsertRange(0, _doc.Profiles.Where(p => incoming.Profiles.All(i => i.Id != p.Id)));
                incoming.Subscriptions.InsertRange(0, _doc.Subscriptions.Where(s => incoming.Subscriptions.All(i => i.Id != s.Id)));
            }

            // Chains travel with the profiles they reference; keep only structurally valid,
            // fully resolvable chains so a stale chain can never break the restore.
            // Raw JSON configs were never chainable (issue #187): drop them like any stale chain.
            var chainIds = new HashSet<Guid>(incoming.Profiles.Select(p => p.Id));
            var chainProtocols = incoming.Profiles.ToDictionary(p => p.Id, p => p.Protocol);
            static bool Chainable(IEnumerable<ProtocolKind> protocols) =>
                protocols.All(SingBoxConfigBuilder.ChainSupported.Contains)
                || protocols.All(XrayConfigBuilder.ChainSupported.Contains);
            var incomingChains = data.Chains
                .Where(c => c.Id != Guid.Empty && !string.IsNullOrWhiteSpace(c.Name))
                .Where(c => c.ProfileIds.Count >= ChainValidator.MinHops && c.ProfileIds.Count <= ChainValidator.MaxHops)
                .Where(c => c.ProfileIds.Distinct().Count() == c.ProfileIds.Count)
                .Where(c => c.ProfileIds.All(chainIds.Contains))
                .Where(c => Chainable(c.ProfileIds.Select(id => chainProtocols[id])))
                .Where(c => c.PreferredEngine is null or Models.EngineKind.SingBox or Models.EngineKind.Xray)
                .GroupBy(c => c.Id).Select(g => g.First()).ToList();
            incoming.Chains = mode == BackupImportMode.Merge
                ? [.. _doc.Chains.Where(c => incomingChains.All(i => i.Id != c.Id)), .. incomingChains]
                : incomingChains;
            var writes = data.Profiles.ToDictionary(p => p.Profile.Id.ToString(), p => p.Secrets);
            foreach (var sub in data.Subscriptions)
            { writes.Add(SubscriptionSecretId(sub.Subscription.Id), new ProfileSecrets { Values = { ["url"] = sub.Url } }); }
            if (mode == BackupImportMode.Replace || data.ToolSecrets.Values.Count > 0)
            { RestoreToolSecrets(writes, data.ToolSecrets, mode); }
            var obsolete = mode == BackupImportMode.Replace
                ? _doc.Profiles.Select(p => p.Id.ToString()).Concat(_doc.Subscriptions.Select(s => SubscriptionSecretId(s.Id))).Where(id => !writes.ContainsKey(id)).ToList()
                : [];
            incoming.PendingSecretDeletes = incoming.PendingSecretDeletes
                .Concat(obsolete).Except(writes.Keys, StringComparer.Ordinal).ToList();
            var original = new Dictionary<string, ProfileSecrets>();
            foreach (var id in writes.Keys)
            {
                // Fail closed: an unreadable existing secret cannot be snapshotted for
                // rollback. Proceeding would let a later write failure roll back to
                // Empty and DeleteAsync the still-recoverable original file (issue #344).
                original.Add(id, await secrets.LoadAsync(id, ct).ConfigureAwait(false));
            }
            var attempted = new List<string>();
            try
            {
                foreach (var (id, value) in writes)
                {
                    ct.ThrowIfCancellationRequested();
                    attempted.Add(id);
                    await secrets.SaveAsync(id, value, ct).ConfigureAwait(false);
                }
                await JsonDefaults.WriteAtomicAsync(path, incoming, ct).ConfigureAwait(false);
                _doc = incoming;
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                foreach (var id in attempted)
                {
                    try
                    {
                        var value = original[id];
                        if (value.Values.Count == 0) { await secrets.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false); }
                        else { await secrets.SaveAsync(id, value, CancellationToken.None).ConfigureAwait(false); }
                    }
                    catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
                }
                if (errors.Count > 1)
                {
                    throw new AggregateException("Backup restore and credential rollback failed.", errors);
                }
                throw;
            }

            await DeleteUnreferencedSecretsAsync(obsolete).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Merges device-local tool secrets into the single backup bag without changing its format.</summary>
    private async Task<ProfileSecrets> CaptureToolSecretsAsync(CancellationToken ct)
    {
        var bag = new ProfileSecrets();
        var tg = await secrets.LoadAsync(ConnectionManager.TgWsProxySecretId, ct).ConfigureAwait(false);
        if (tg["secret"] is { } secret) { bag.Values["secret"] = secret; }
        var lan = await secrets.LoadAsync(ConnectionManager.LocalProxySecretId, ct).ConfigureAwait(false);
        if (lan["password"] is { } password) { bag.Values["localproxy-password"] = password; }
        return bag;
    }

    /// <summary>Splits the backup bag back into per-tool secret records. Merge touches only keys present in the archive; Replace clears missing ones.</summary>
    private static void RestoreToolSecrets(Dictionary<string, ProfileSecrets> writes, ProfileSecrets bag, BackupImportMode mode)
    {
        if (mode == BackupImportMode.Replace || bag["secret"] is not null)
        {
            var tg = new ProfileSecrets();
            if (bag["secret"] is { } secret) { tg.Values["secret"] = secret; }
            writes.Add(ConnectionManager.TgWsProxySecretId, tg);
        }
        if (mode == BackupImportMode.Replace || bag["localproxy-password"] is not null)
        {
            var lan = new ProfileSecrets();
            if (bag["localproxy-password"] is { } password) { lan.Values["password"] = password; }
            writes.Add(ConnectionManager.LocalProxySecretId, lan);
        }
    }
}
