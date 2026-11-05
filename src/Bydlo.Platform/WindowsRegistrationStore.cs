using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Bydlo.Platform;

/// <summary>Minimal registry boundary for per-user desktop registrations.</summary>
public interface IWindowsRegistrationStore
{
    string? ReadCommand(string protocol, bool merged);
    string? ReadOwner(string protocol, bool merged);
    void Set(string path, string name, string value);
    void DeleteTree(string path);
    void DeleteValue(string path, string name);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistrationStore : IWindowsRegistrationStore
{
    public string? ReadOwner(string protocol, bool merged)
    {
        using var key = (merged ? Registry.ClassesRoot : Registry.CurrentUser).OpenSubKey(
            (merged ? "" : @"Software\Classes\") + protocol);
        if (key?.GetValue("BYDLO.Owner") is string owner)
        {
            return key.GetValue("BYDLO.OwnerCommand") as string == ReadCommand(protocol, merged) ? owner : null;
        }
        // Older BYDLO builds wrote these exact descriptions before the stable marker existed.
        var description = key?.GetValue(null) as string;
        var command = ReadCommand(protocol, merged);
        var ownExecutable = command is not null && (command.EndsWith("\\BYDLO.exe\" \"%1\"", StringComparison.OrdinalIgnoreCase) ||
            command.EndsWith("\\Bydlo.Desktop.exe\" \"%1\"", StringComparison.OrdinalIgnoreCase));
        return ownExecutable && (description == $"URL:BYDLO {protocol}" ||
            (protocol == "BYDLO.Config" && description == "BYDLO configuration")
            ) ? WindowsImportAssociations.OwnerId : null;
    }

    public string? ReadCommand(string protocol, bool merged)
    {
        using var key = (merged ? Registry.ClassesRoot : Registry.CurrentUser).OpenSubKey(
            (merged ? "" : @"Software\Classes\") + protocol + @"\shell\open\command");
        return key?.GetValue(null) as string;
    }

    public void Set(string path, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(name, value);
    }

    public void DeleteTree(string path) => Registry.CurrentUser.DeleteSubKeyTree(path, false);

    public void DeleteValue(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, false);
    }
}
