using System.Collections;
using System.Reflection;
using System.Text.Json;
using Bydlo.Core.Backup;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;
using Bydlo.Core.Routing;
using Bydlo.Core.Serialization;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

/// <summary>Typed backup copy (issue #393): every field survives and no mutable state is shared.</summary>
[Collection(nameof(AllocationMeasurements))]
public sealed class BackupCopyTests
{
    [Fact]
    public void Copy_preserves_every_serialized_field()
    {
        var original = Filled();
        var copy = BackupCopy.Copy(original);
        Assert.Equal(Json(original), Json(copy));
    }

    [Fact]
    public void Copy_shares_no_mutable_container_or_model()
    {
        var original = Filled();
        var copy = BackupCopy.Copy(original);
        AssertNoSharedMutableState(original, copy, "BackupData");
    }

    [Fact]
    public void Mutating_either_snapshot_does_not_change_the_other()
    {
        var original = Filled();
        var copy = BackupCopy.Copy(original);
        var before = Json(original);
        copy.Profiles[0].Secrets.Values["uuid"] = "changed";
        copy.Profiles[0].Profile.Options["sni"] = "changed";
        copy.Subscriptions[0].Subscription.SourceOptions!.First().Value["k"] = "changed";
        copy.Chains[0].ProfileIds.Add(Guid.NewGuid());
        copy.Settings.Routing.DirectDomains.Add("changed.example");
        copy.Settings.ServerGroups[0].ProfileIds.Clear();
        copy.Settings.EnginePaths["x"] = "changed";
        copy.ToolSecrets.Values["secret"] = "changed";
        Assert.Equal(before, Json(original));

        original.Settings.TgWsProxy.DcIps.Add("9:1.1.1.1");
        Assert.DoesNotContain("9:1.1.1.1", copy.Settings.TgWsProxy.DcIps);
    }

    [Fact]
    public void Copy_keeps_case_insensitive_dictionaries()
    {
        var copy = BackupCopy.Copy(Filled());
        Assert.Same(StringComparer.OrdinalIgnoreCase, copy.Profiles[0].Profile.Options.Comparer);
        Assert.Same(StringComparer.OrdinalIgnoreCase, copy.Profiles[0].Secrets.Values.Comparer);
        Assert.Same(StringComparer.OrdinalIgnoreCase, copy.Settings.EnginePaths.Comparer);
    }

    [Fact]
    public void Copy_tolerates_null_members_so_validation_still_rejects_them()
    {
        var data = new BackupData { Settings = null!, Profiles = null!, Subscriptions = null!, ToolSecrets = null!, Chains = null! };
        var copy = BackupCopy.Copy(data);
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(copy));
    }

    [Fact]
    public void Copy_allocates_far_less_than_a_json_round_trip()
    {
        // Repeatable allocation benchmark from issue #393: 200 profiles with 128 KiB configs.
        var data = new BackupData();
        var config = new string('x', 128 * 1024);
        for (var i = 0; i < 200; i++)
        {
            data.Profiles.Add(new(new Profile { Name = "p" + i, Protocol = ProtocolKind.WireGuard, Host = "h", Port = 1 },
                new ProfileSecrets { Values = { [Keys.RawConfig] = config + i } }));
        }
        BackupCopy.Copy(data);
        var start = GC.GetAllocatedBytesForCurrentThread();
        BackupCopy.Copy(data);
        var typed = GC.GetAllocatedBytesForCurrentThread() - start;

        var info = JsonDefaults.Options.Info<BackupData>();
        start = GC.GetAllocatedBytesForCurrentThread();
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(data, info), info);
        var json = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.True(typed * 20 < json, $"typed={typed} json={json}");
    }

    private static string Json(BackupData data) => JsonSerializer.Serialize(data, JsonDefaults.Options.Info<BackupData>());

    // ---- reflection helpers -------------------------------------------------------------

    private static BackupData Filled() => (BackupData)Fill(typeof(BackupData), 0);

    private static object Fill(Type type, int depth)
    {
        if (type == typeof(string)) { return "v" + depth; }
        if (type == typeof(bool)) { return true; }
        if (type == typeof(int)) { return 7; }
        if (type == typeof(long)) { return 7L; }
        if (type == typeof(Guid)) { return Guid.NewGuid(); }
        if (type == typeof(DateTimeOffset)) { return new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero); }
        if (type == typeof(Uri)) { return new Uri("https://example.test/list"); }
        if (type.IsEnum) { var values = Enum.GetValues(type); return values.GetValue(values.Length - 1)!; }
        if (Nullable.GetUnderlyingType(type) is { } inner) { return Fill(inner, depth); }
        if (type == typeof(DpiSettings)) { return new DpiSettings { Fragment = "tls", Mux = "smux", Padding = true }; }
        if (type == typeof(OrderedRoutingRule)) { return new OrderedRoutingRule(RoutingCondition.Domain, "a.example", RoutingAction.Direct); }
        if (type == typeof(RuleSetSource)) { return new RuleSetSource("tag", new Uri("https://example.test/r.srs"), new string('a', 64), 1024); }
        if (type == typeof(GitHubRelease)) { return new GitHubRelease("v9", "https://example.test/r"); }
        if (type == typeof(Contributor)) { return new Contributor("someone", 3); }
        if (type == typeof(ImportedProfile)) { return new ImportedProfile((Profile)Fill(typeof(Profile), depth + 1), (ProfileSecrets)Fill(typeof(ProfileSecrets), depth + 1)); }
        if (type == typeof(BackupSubscription)) { return new BackupSubscription((Subscription)Fill(typeof(Subscription), depth + 1), "https://example.test/sub"); }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(type)!;
            list.Add(Fill(type.GetGenericArguments()[0], depth + 1));
            return list;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = type.GetGenericArguments();
            var dict = (IDictionary)Activator.CreateInstance(type)!;
            dict.Add(Fill(args[0], depth + 1), Fill(args[1], depth + 1));
            return dict;
        }
        var instance = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Cannot create {type}");
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is null || property.GetIndexParameters().Length > 0) { continue; }
            var value = Fill(property.PropertyType, depth + 1);
            if (property.GetValue(instance) is IDictionary existing && value is IDictionary filled)
            {
                existing.Clear();
                foreach (DictionaryEntry entry in filled) { existing.Add(entry.Key, entry.Value); }
                continue;
            }
            property.SetValue(instance, value);
        }
        return instance;
    }

    private static bool IsImmutable(Type type) =>
        type.IsValueType || type == typeof(string) || type == typeof(Uri)
        // Positional records with only immutable members may be shared by design.
        || type == typeof(DpiSettings) || type == typeof(OrderedRoutingRule) || type == typeof(RuleSetSource)
        || type == typeof(GitHubRelease) || type == typeof(Contributor);

    private static void AssertNoSharedMutableState(object? left, object? right, string path)
    {
        if (left is null || right is null) { return; }
        var type = left.GetType();
        if (IsImmutable(type)) { return; }
        Assert.False(ReferenceEquals(left, right), $"{path} is shared between snapshots");
        if (left is IDictionary leftDict && right is IDictionary rightDict)
        {
            foreach (DictionaryEntry entry in leftDict)
            { AssertNoSharedMutableState(entry.Value, rightDict[entry.Key], $"{path}[{entry.Key}]"); }
            return;
        }
        if (left is IList leftList && right is IList rightList)
        {
            for (var i = 0; i < leftList.Count; i++) { AssertNoSharedMutableState(leftList[i], rightList[i], $"{path}[{i}]"); }
            return;
        }
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) { continue; }
            AssertNoSharedMutableState(property.GetValue(left), property.GetValue(right), $"{path}.{property.Name}");
        }
    }
}
