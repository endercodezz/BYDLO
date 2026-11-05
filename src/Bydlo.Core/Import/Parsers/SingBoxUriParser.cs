using Bydlo.Core.Models;
using static Bydlo.Core.Import.ImportHelpers;

namespace Bydlo.Core.Import.Parsers;

internal sealed class SingBoxUriParser : IProfileParser
{
    internal const string ShadowPassword = "shadow_password";
    internal const string PrivateKey = "private_key";
    internal const string Passphrase = "private_key_passphrase";

    private static ProtocolKind? Protocol(string scheme) => scheme.ToLowerInvariant() switch
    {
        "anytls" => ProtocolKind.AnyTls,
        "shadowtls" => ProtocolKind.ShadowTls,
        "naive+https" or "naive+quic" => ProtocolKind.Naive,
        "ssh" => ProtocolKind.Ssh,
        _ => null,
    };

    public bool CanParse(string input)
    {
        var end = input.IndexOf("://", StringComparison.Ordinal);
        return end > 0 && Protocol(input[..end]) is not null;
    }

    public ImportedProfile Parse(string input)
    {
        var scheme = input[..input.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant();
        var protocol = Protocol(scheme) ?? throw new ImportException("Unsupported proxy scheme.");
        var uri = ParseUri(input, scheme);
        var query = Query(uri);
        var profile = new Profile { Protocol = protocol, Host = Host(uri), Port = Port(uri, protocol == ProtocolKind.Ssh ? 22 : 443, input) };
        profile.Name = Fragment(uri, profile.Host);
        var secrets = new ProfileSecrets();
        var auth = uri.UserInfo.Split(':', 2).Select(Uri.UnescapeDataString).ToArray();

        switch (protocol)
        {
            case ProtocolKind.AnyTls:
                secrets.Values[Keys.Password] = Required(auth[0]);
                CopyTls();
                break;
            case ProtocolKind.ShadowTls:
                var version = query.GetValueOrDefault("version", "3");
                if (version is not ("1" or "2" or "3"))
                {
                    throw new ImportException("Unsupported ShadowTLS version.");
                }
                profile.Options["version"] = version;
                profile.Options[Keys.Method] = Required(query.GetValueOrDefault("method"));
                secrets.Values[Keys.Password] = Required(query.GetValueOrDefault("ss_password"));
                if (version != "1")
                {
                    secrets.Values[ShadowPassword] = Required(auth[0]);
                }
                CopyTls();
                break;
            case ProtocolKind.Naive:
                secrets.Values[Keys.Username] = Required(auth[0]);
                secrets.Values[Keys.Password] = Required(auth.ElementAtOrDefault(1));
                profile.Options["quic"] = scheme == "naive+quic" ? "true" : "false";
                Copy(query, profile, ("sni", Keys.Sni));
                break;
            case ProtocolKind.Ssh:
                secrets.Values[Keys.Username] = Required(auth[0]);
                if (auth.Length == 2)
                {
                    secrets.Values[Keys.Password] = Required(auth[1]);
                }
                if (query.TryGetValue(PrivateKey, out var key))
                {
                    secrets.Values[PrivateKey] = Required(key);
                }
                if (query.TryGetValue(Passphrase, out var passphrase))
                {
                    secrets.Values[Passphrase] = Required(passphrase);
                }
                if (secrets[Keys.Password] is null && secrets[PrivateKey] is null)
                {
                    throw new ImportException("SSH link requires a password or private key.");
                }
                Copy(query, profile, ("host_key", "host_key"));
                break;
        }

        return new ImportedProfile(profile, secrets);

        void CopyTls()
        {
            Copy(query, profile, ("sni", Keys.Sni), ("alpn", Keys.Alpn), ("fp", Keys.Fingerprint));
            if (query.GetValueOrDefault("insecure") is "1" or "true" ||
                query.GetValueOrDefault("allowInsecure") is "1" or "true")
            {
                profile.Options[Keys.AllowInsecure] = "true";
            }
        }
    }

    private static string Required(string? value) => !string.IsNullOrEmpty(value)
        ? value
        : throw new ImportException("Proxy link is missing a required field.");
}
