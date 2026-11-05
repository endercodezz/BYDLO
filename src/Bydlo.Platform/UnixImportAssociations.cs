using System.Xml.Linq;
using Bydlo.Core.Models;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Platform.Processes;

namespace Bydlo.Platform;

/// <summary>Explicit, per-user registration. Construction never writes or launches helpers.</summary>
public sealed class UnixImportAssociations
{
    internal const string DesktopId = "org.bydlo.import.desktop";
    internal const string BundleId = "org.bydlo.import";
    private const string LaunchServices = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";
    private readonly OsFamily _os;
    private readonly string _executable;
    private readonly string _home;
    private readonly string _data;
    private readonly string _config;
    private readonly IProcessRunner _runner;
    private readonly IMacImportHandlers _mac;
    private readonly bool _native;

    public UnixImportAssociations(string executable) : this(PlatformInfo.Current.Os, executable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("XDG_DATA_HOME"), Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        new SystemProcessRunner(), new MacImportHandlers()) { _native = true; }

    internal UnixImportAssociations(OsFamily os, string executable, string home, string? data, string? config,
        IProcessRunner runner, IMacImportHandlers mac)
    {
        if (os is not (OsFamily.Linux or OsFamily.MacOS)) { throw new PlatformNotSupportedException(); }
        if (!executable.StartsWith('/') || executable.Any(char.IsControl)) { throw new ArgumentException("An absolute executable path is required.", nameof(executable)); }
        _os = os;
        _executable = executable;
        _home = home;
        _data = !string.IsNullOrEmpty(data) && Path.IsPathFullyQualified(data) ? data : Path.Combine(home, ".local", "share");
        _config = !string.IsNullOrEmpty(config) && Path.IsPathFullyQualified(config) ? config : Path.Combine(home, ".config");
        _runner = runner;
        _mac = mac;
    }

    private string DesktopPath => Path.Combine(_data, "applications", DesktopId);
    private string BundlePath => Path.Combine(_home, "Applications", "BYDLO Import.app");
    private string OwnerPath => _os == OsFamily.Linux ? DesktopPath + ".owner" : Path.Combine(BundlePath, "bydlo-owner");

    public async Task RegisterAsync()
    {
        EnsureNativeAllowed();
        // Preflight every scheme and the destination before making any changes.
        foreach (var scheme in WindowsImportAssociations.Protocols)
        {
            var handler = _os == OsFamily.Linux
                ? (await Run("xdg-mime", "query", "default", "x-scheme-handler/" + scheme)).Trim()
                : _mac.DefaultHandler(scheme);
            var own = _os == OsFamily.Linux ? DesktopId : BundleId;
            if (!string.IsNullOrEmpty(handler) && handler != own) { throw new ImportAssociationConflictException(); }
        }

        var destinationExists = _os == OsFamily.Linux ? File.Exists(DesktopPath) : Directory.Exists(BundlePath);
        if ((destinationExists || File.Exists(OwnerPath)) && !IsOwner()) { throw new ImportAssociationConflictException(); }
        if (_os == OsFamily.Linux)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopPath)!);
            File.WriteAllText(DesktopPath, LinuxDesktop(_executable));
            File.WriteAllText(OwnerPath, BundleId);
            await Run("update-desktop-database", Path.GetDirectoryName(DesktopPath)!);
            foreach (var scheme in WindowsImportAssociations.Protocols)
            {
                await Run("xdg-mime", "default", DesktopId, "x-scheme-handler/" + scheme);
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BundlePath)!);
            var script = Path.Combine(Path.GetDirectoryName(BundlePath)!, ".bydlo-import-" + Guid.NewGuid().ToString("N") + ".applescript");
            try
            {
                File.WriteAllText(script, MacScript(_executable));
                await Run("/usr/bin/osacompile", "-o", BundlePath, script);
                File.WriteAllText(OwnerPath, BundleId);
                var plist = Path.Combine(BundlePath, "Contents", "Info.plist");
                await Run("/usr/bin/plutil", "-convert", "xml1", plist);
                var document = XDocument.Load(plist);
                var dict = document.Root!.Element("dict")!;
                SetPlist(dict, "CFBundleIdentifier", new XElement("string", BundleId));
                SetPlist(dict, "CFBundleURLTypes", new XElement("array", new XElement("dict",
                    new XElement("key", "CFBundleURLName"), new XElement("string", BundleId),
                    new XElement("key", "CFBundleURLSchemes"), new XElement("array", WindowsImportAssociations.Protocols.Select(p => new XElement("string", p))))));
                SetPlist(dict, "CFBundleDocumentTypes", new XElement("array", new XElement("dict",
                    new XElement("key", "CFBundleTypeName"), new XElement("string", "BYDLO configuration"),
                    new XElement("key", "CFBundleTypeRole"), new XElement("string", "Viewer"),
                    new XElement("key", "LSHandlerRank"), new XElement("string", "Alternate"),
                    new XElement("key", "CFBundleTypeExtensions"), new XElement("array", new XElement("string", "conf"), new XElement("string", "ovpn")))));
                document.Save(plist);
                // osacompile produces an ad-hoc signature; refresh it after editing the metadata.
                await Run("/usr/bin/codesign", "--force", "--sign", "-", BundlePath);
                await Run(LaunchServices, "-f", BundlePath);
                foreach (var scheme in WindowsImportAssociations.Protocols) { _mac.SetDefaultHandler(scheme, BundleId); }
            }
            finally { File.Delete(script); }
        }
    }

    public async Task UnregisterAsync()
    {
        EnsureNativeAllowed();
        if (!IsOwner()) { return; }
        if (_os == OsFamily.Linux)
        {
            // xdg-mime has no unset command. Remove only our ID, retaining all other choices.
            foreach (var path in new[] { _config, Path.GetDirectoryName(DesktopPath)! }
                .Where(Directory.Exists).SelectMany(p => Directory.EnumerateFiles(p, "*mimeapps.list")))
            {
                File.WriteAllLines(path, File.ReadAllLines(path).Select(RemoveDesktopId));
            }
            File.Delete(DesktopPath);
            File.Delete(OwnerPath);
            await Run("update-desktop-database", Path.GetDirectoryName(DesktopPath)!);
        }
        else
        {
            await Run(LaunchServices, "-u", BundlePath);
            // Only this fixed, ownership-checked generated bundle is removed.
            Directory.Delete(BundlePath, recursive: true);
        }
    }

    private bool IsOwner()
    {
        if (!File.Exists(OwnerPath)) { return false; }
        var owner = File.ReadAllText(OwnerPath);
        if (owner == BundleId) { return true; }
        // Recognize legacy registrations only when their generated content matches the old owner path.
        if (!owner.StartsWith('/') || owner.Any(char.IsControl)) { return false; }
        if (_os == OsFamily.Linux)
        {
            return File.Exists(DesktopPath) && File.ReadAllText(DesktopPath) == LinuxDesktop(owner);
        }
        var plist = Path.Combine(BundlePath, "Contents", "Info.plist");
        if (!File.Exists(plist)) { return false; }
        try
        {
            var dict = XDocument.Load(plist).Root?.Element("dict");
            return dict?.Elements("key").Any(k => k.Value == "CFBundleIdentifier" &&
                k.NextNode is XElement value && value.Value == BundleId) == true;
        }
        catch (System.Xml.XmlException) { return false; }
    }
    private void EnsureNativeAllowed()
    {
        if (_native) { NetworkSafetyGuard.EnsureAllowed("change desktop import associations"); }
    }

    private async Task<string> Run(string file, params string[] args)
    {
        var result = await _runner.RunToCompletionAsync(new ProcessSpec
        {
            FileName = file, Arguments = args, DisplayName = "Import associations", AffectsNetwork = false,
            WorkingDirectory = _home,
        }, TimeSpan.FromSeconds(15));
        if (result.ExitCode != 0) { throw new IOException("Could not update desktop import associations."); }
        return result.Output;
    }

    internal static string RemoveDesktopId(string line)
    {
        var equals = line.IndexOf('=');
        if (equals < 0 || line.StartsWith('#')) { return line; }
        var values = line[(equals + 1)..].Split(';').Where(v => v != DesktopId);
        return line[..(equals + 1)] + string.Join(';', values);
    }

    internal static string LinuxDesktop(string executable) => AutostartService.LinuxRegistration(executable, "%U") +
        "NoDisplay=true\nMimeType=text/plain;application/x-openvpn-profile;application/x-openvpn;text/x-openvpn-profile;" +
        string.Concat(WindowsImportAssociations.Protocols.Select(p => "x-scheme-handler/" + p + ";")) + "\n";

    internal static string MacScript(string executable)
    {
        var escaped = executable.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return $$"""
            on forwardInputs(inputs)
                set commandText to quoted form of "{{escaped}}"
                repeat with inputValue in inputs
                    set commandText to commandText & " " & quoted form of (inputValue as text)
                end repeat
                do shell script commandText & " >/dev/null 2>&1 &"
            end forwardInputs
            on open location inputUrl
                forwardInputs({inputUrl})
            end open location
            on open inputFiles
                set paths to {}
                repeat with inputFile in inputFiles
                    set end of paths to POSIX path of inputFile
                end repeat
                forwardInputs(paths)
            end open
            on run
                forwardInputs({})
            end run
            """;
    }

    private static void SetPlist(XElement dict, string key, XElement value)
    {
        var existing = dict.Elements("key").FirstOrDefault(e => e.Value == key);
        if (existing is not null) { existing.NextNode?.Remove(); existing.Remove(); }
        dict.Add(new XElement("key", key), value);
    }
}

public sealed class ImportAssociationConflictException() : InvalidOperationException("Another application owns an import association.");
