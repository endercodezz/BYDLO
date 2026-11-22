using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Bydlo.Core.Backup;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Routing;
using Bydlo.Core.Storage;

namespace Bydlo.Core.Serialization;

/// <summary>
/// Reflection-free JSON plumbing shared by every BYDLO assembly. Trimmed and Native AOT builds cannot use
/// reflection-based System.Text.Json, so every options instance carries a source-generated resolver and every call
/// passes a <see cref="JsonTypeInfo{T}"/>. A type missing from a context fails loudly in tests instead of in release.
/// </summary>
public static class BydloJson
{
    /// <summary>Default serializer settings (same as <c>new JsonSerializerOptions()</c>) bound to <paramref name="resolver"/>.</summary>
    public static JsonSerializerOptions Create(IJsonTypeInfoResolver resolver)
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Metadata for <typeparamref name="T"/> from the resolver configured on <paramref name="options"/>.</summary>
    public static JsonTypeInfo<T> Info<T>(this JsonSerializerOptions options) =>
        (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));

    /// <summary>Core types with default settings.</summary>
    internal static JsonSerializerOptions Core { get; } = Create(CoreJsonContext.Default);

    /// <summary>Independent copy through a JSON round trip with default settings.</summary>
    internal static T DeepClone<T>(T value)
    {
        var info = Core.Info<T>();
        return JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, info), info)!;
    }
}

/// <summary>Root types that Core serializes; nested types are generated transitively.</summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(ProfileSecrets))]
[JsonSerializable(typeof(Subscription))]
[JsonSerializable(typeof(ProxyChain))]
[JsonSerializable(typeof(ServerGroup))]
[JsonSerializable(typeof(List<ChainHop>))]
[JsonSerializable(typeof(ProfileDocument))]
[JsonSerializable(typeof(BackupData))]
[JsonSerializable(typeof(RoutingRuleBook))]
[JsonSerializable(typeof(List<OrderedRoutingRule>))]
[JsonSerializable(typeof(ZapretSelectionReport))]
[JsonSerializable(typeof(HelperRequest))]
[JsonSerializable(typeof(HelperReply))]
[JsonSerializable(typeof(HelperBuild))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IEnumerable<KeyValuePair<string, string>>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ImportIdentity))]
[JsonSerializable(typeof(NodeIdentityKey))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

/// <summary>Store format: indented, enums as strings, nulls omitted. Roots are what <c>JsonDefaults</c> persists.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(Subscription))]
[JsonSerializable(typeof(ProxyChain))]
[JsonSerializable(typeof(ProfileDocument))]
[JsonSerializable(typeof(BackupData))]
[JsonSerializable(typeof(UpdateRecoveryJournal))]
internal sealed partial class CoreStoreJsonContext : JsonSerializerContext;

/// <summary>De-duplication key of an imported profile (property order is the serialized identity).</summary>
internal sealed record ImportIdentity(ProtocolKind Protocol, string Host, int Port, EngineKind? PreferredEngine,
    IEnumerable<KeyValuePair<string, string>> Options, IEnumerable<KeyValuePair<string, string>> Secrets);

/// <summary>Identity of a subscription node across refreshes (property order is the serialized identity).</summary>
internal sealed record NodeIdentityKey(ProtocolKind Protocol, string Host, int Port, IEnumerable<KeyValuePair<string, string>> Credentials);
