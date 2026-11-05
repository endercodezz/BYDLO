using System.Runtime.Versioning;

namespace Bydlo.Platform;

/// <summary>Opt-in per-user registrations. Never changes file defaults or requires elevation.</summary>
public static class WindowsImportAssociations
{
    internal const string OwnerId = "org.bydlo.import";
    public static IReadOnlyList<string> Protocols { get; } = Array.AsReadOnly(new[] { "vless", "vmess", "trojan", "ss", "hy2", "hysteria2", "tuic" });

    public static string OpenCommand(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (executable.Contains('"') || !new Bydlo.Core.Platform.TargetPathRules(Bydlo.Core.Models.OsFamily.Windows).IsAbsolute(executable))
        {
            throw new ArgumentException("An absolute executable path is required.", nameof(executable));
        }

        return $"\"{executable}\" \"%1\"";
    }

    [SupportedOSPlatform("windows")]
    public static void Register(string executable) => Register(executable, new WindowsRegistrationStore());

    public static void Register(string executable, IWindowsRegistrationStore store)
    {
        var command = OpenCommand(executable);
        // Do not silently replace another application's custom protocol handler.
        foreach (var protocol in Protocols)
        {
            if (store.ReadCommand(protocol, merged: true) is string other && other != command &&
                store.ReadOwner(protocol, merged: true) != OwnerId)
            {
                throw new InvalidOperationException("Another application already handles this protocol.");
            }
        }

        store.Set(@"Software\Classes\BYDLO.Config", "", "BYDLO configuration");
        store.Set(@"Software\Classes\BYDLO.Config", "BYDLO.Owner", OwnerId);
        store.Set(@"Software\Classes\BYDLO.Config", "BYDLO.OwnerCommand", command);
        store.Set(@"Software\Classes\BYDLO.Config\shell\open\command", "", command);
        foreach (var extension in new[] { ".conf", ".ovpn" })
        {
            store.Set($@"Software\Classes\{extension}\OpenWithProgids", "BYDLO.Config", "");
        }

        foreach (var protocol in Protocols)
        {
            store.Set($@"Software\Classes\{protocol}", "", $"URL:BYDLO {protocol}");
            store.Set($@"Software\Classes\{protocol}", "URL Protocol", "");
            store.Set($@"Software\Classes\{protocol}", "BYDLO.Owner", OwnerId);
            store.Set($@"Software\Classes\{protocol}", "BYDLO.OwnerCommand", command);
            store.Set($@"Software\Classes\{protocol}\shell\open\command", "", command);
        }
    }

    [SupportedOSPlatform("windows")]
    public static void Unregister(string executable) => Unregister(executable, new WindowsRegistrationStore());

    public static void Unregister(string executable, IWindowsRegistrationStore store)
    {
        var command = OpenCommand(executable);
        foreach (var protocol in Protocols.Append("BYDLO.Config"))
        {
            var path = $@"Software\Classes\{protocol}";
            if (store.ReadCommand(protocol, merged: false) == command || store.ReadOwner(protocol, merged: false) == OwnerId)
            {
                store.DeleteTree(path);
            }
        }

        foreach (var extension in new[] { ".conf", ".ovpn" })
        {
            store.DeleteValue($@"Software\Classes\{extension}\OpenWithProgids", "BYDLO.Config");
        }
    }

}
