using System.Xml.Linq;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class UnixImportAssociationsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "bydlo-associations-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _runner = new() { OnCompletion = _ => (0, "") };
    private readonly MacHandlers _mac = new();
    private const string Executable = "/opt/BYDLO space/$`percent%/BYDLO";

    private UnixImportAssociations Create(OsFamily os) => new(os, Executable, _home, null, null, _runner, _mac);
    private string Desktop => Path.Combine(_home, ".local", "share", "applications", UnixImportAssociations.DesktopId);
    private string Bundle => Path.Combine(_home, "Applications", "BYDLO Import.app");

    [Theory]
    [InlineData(OsFamily.Linux)]
    [InlineData(OsFamily.MacOS)]
    public void Construction_has_no_side_effects(OsFamily os)
    {
        _ = Create(os);
        Assert.False(Directory.Exists(_home));
        Assert.Empty(_runner.Completed);
        Assert.Empty(_mac.Set);
    }

    [Theory]
    [InlineData(OsFamily.Linux)]
    [InlineData(OsFamily.MacOS)]
    public async Task Conflicts_on_last_scheme_refuse_before_any_writes(OsFamily os)
    {
        _mac.OtherScheme = "tuic";
        _runner.OnCompletion = p => (0, p.Arguments[^1] == "x-scheme-handler/tuic" ? "other.desktop\n" : "");
        await Assert.ThrowsAsync<ImportAssociationConflictException>(() => Create(os).RegisterAsync());
        Assert.False(Directory.Exists(_home));
        Assert.Empty(_mac.Set);
        Assert.All(_runner.Completed, p => Assert.Equal("query", p.Arguments[0]));
    }

    [Fact]
    public async Task Linux_registers_links_without_setting_file_defaults_and_removes_only_own_choices()
    {
        var service = Create(OsFamily.Linux);
        await service.RegisterAsync();
        Assert.Contains(" %U", File.ReadAllText(Desktop));
        Assert.Contains("%%", File.ReadAllText(Desktop));
        Assert.Equal(7, _runner.Completed.Count(p => p.Arguments[0] == "default"));
        Assert.All(_runner.Completed.Where(p => p.Arguments[0] == "default"),
            p => Assert.StartsWith("x-scheme-handler/", p.Arguments[2]));
        Assert.All(_runner.Completed, p => Assert.False(p.AffectsNetwork));
        var config = Path.Combine(_home, ".config");
        Directory.CreateDirectory(config);
        var mimeapps = Path.Combine(config, "mimeapps.list");
        File.WriteAllText(mimeapps, $"[Default Applications]\nx-scheme-handler/vless=other.desktop;{UnixImportAssociations.DesktopId};\ntext/plain=editor.desktop;\n[Added Associations]\ntext/plain={UnixImportAssociations.DesktopId};editor.desktop;\n");
        await service.UnregisterAsync();
        Assert.False(File.Exists(Desktop));
        Assert.DoesNotContain(UnixImportAssociations.DesktopId, File.ReadAllText(mimeapps));
        Assert.Contains("x-scheme-handler/vless=other.desktop;", File.ReadAllText(mimeapps));
        Assert.Contains("text/plain=editor.desktop;", File.ReadAllText(mimeapps));
    }

    [Fact]
    public async Task Linux_legacy_registration_can_move_to_another_executable_and_be_removed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Desktop)!);
        File.WriteAllText(Desktop, UnixImportAssociations.LinuxDesktop("/old/BYDLO"));
        File.WriteAllText(Desktop + ".owner", "/old/BYDLO");
        await Create(OsFamily.Linux).RegisterAsync();
        Assert.Equal(UnixImportAssociations.LinuxDesktop(Executable), File.ReadAllText(Desktop));
        var moved = new UnixImportAssociations(OsFamily.Linux, "/new/BYDLO", _home, null, null, _runner, _mac);
        await moved.UnregisterAsync();
        Assert.False(File.Exists(Desktop));
    }

    [Fact]
    public async Task Linux_removal_does_not_require_config_directory()
    {
        var service = Create(OsFamily.Linux);
        await service.RegisterAsync();
        await service.UnregisterAsync();
        Assert.False(File.Exists(Desktop));
    }

    [Fact]
    public void Desktop_exec_escapes_metacharacters_without_changing_the_executable_name()
    {
        var desktop = UnixImportAssociations.LinuxDesktop("/opt/ --autostart/quote\" slash\\ dollar$ backtick` percent%/BYDLO");
        Assert.Contains("/ --autostart/", desktop);
        Assert.Contains("quote\\\\\"", desktop);
        Assert.Contains("slash\\\\\\\\", desktop);
        Assert.Contains("dollar\\\\$", desktop);
        Assert.Contains("backtick\\\\`", desktop);
        Assert.Contains("percent%%", desktop);
        Assert.Contains("/BYDLO\" %U\n", desktop);
    }

    [Fact]
    public void AppleScript_escapes_the_executable_as_data_before_shell_quoting()
    {
        var script = UnixImportAssociations.MacScript("/opt/quote\" slash\\ dollar$ backtick`/BYDLO");
        Assert.Contains("quoted form of \"/opt/quote\\\" slash\\\\ dollar$ backtick`/BYDLO\"", script);
        Assert.Contains("quoted form of (inputValue as text)", script);
    }

    [Theory]
    [InlineData(OsFamily.Linux)]
    [InlineData(OsFamily.MacOS)]
    public async Task Unowned_destination_is_preserved(OsFamily os)
    {
        var destination = os == OsFamily.Linux ? Desktop : Path.Combine(Bundle, "Contents", "Info.plist");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "foreign content");
        var service = Create(os);
        await Assert.ThrowsAsync<ImportAssociationConflictException>(() => service.RegisterAsync());
        await service.UnregisterAsync();
        Assert.Equal("foreign content", File.ReadAllText(destination));
    }

    [Fact]
    public async Task Mac_builds_and_signs_event_handler_bundle_before_setting_protocols()
    {
        _runner.OnCompletion = p =>
        {
            if (p.FileName == "/usr/bin/osacompile")
            {
                var script = File.ReadAllText(p.Arguments[2]);
                Assert.Contains("on open location inputUrl", script);
                Assert.Contains("on open inputFiles", script);
                Assert.Contains("quoted form of (inputValue as text)", script);
                Directory.CreateDirectory(Path.Combine(Bundle, "Contents"));
                File.WriteAllText(Path.Combine(Bundle, "Contents", "Info.plist"), "<plist><dict><key>CFBundleIdentifier</key><string>old</string></dict></plist>");
            }
            return (0, "");
        };
        var service = Create(OsFamily.MacOS);
        await service.RegisterAsync();
        var plist = XDocument.Load(Path.Combine(Bundle, "Contents", "Info.plist")).ToString();
        Assert.Contains("CFBundleURLTypes", plist);
        Assert.Contains("CFBundleDocumentTypes", plist);
        Assert.Contains("Alternate", plist);
        Assert.Contains("conf", plist);
        Assert.Contains("ovpn", plist);
        Assert.DoesNotContain(">old<", plist);
        Assert.All(WindowsImportAssociations.Protocols, scheme => Assert.Contains($">{scheme}<", plist));
        Assert.Equal(7, _mac.Set.Count);
        Assert.Contains(_runner.Completed, p => p.FileName == "/usr/bin/codesign");
        await service.UnregisterAsync();
        Assert.Contains(_runner.Completed, p => p.Arguments[0] == "-u");
        Assert.False(Directory.Exists(Bundle));
    }

    [Fact]
    public async Task Failed_helper_does_not_report_success_or_set_defaults()
    {
        _runner.OnCompletion = p => p.FileName == "update-desktop-database" ? (1, "failure") : (0, "");
        await Assert.ThrowsAsync<IOException>(() => Create(OsFamily.Linux).RegisterAsync());
        Assert.DoesNotContain(_runner.Completed, p => p.Arguments[0] == "default");
    }

    [Fact]
    public void Desktop_file_uris_are_decoded_before_preview()
    {
        var path = Path.Combine(_home, "test space #1.conf");
        Assert.Equal(new[] { path, "vless://example" }, ExternalImportInput.LaunchInputs([new Uri(path).AbsoluteUri, "vless://example"]).ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_home)) { Directory.Delete(_home, recursive: true); }
    }

    private sealed class MacHandlers : IMacImportHandlers
    {
        public string? OtherScheme { get; set; }
        public List<string> Set { get; } = [];
        public string? DefaultHandler(string scheme) => scheme == OtherScheme ? "org.other.app" : null;
        public void SetDefaultHandler(string scheme, string bundleId)
        {
            Assert.Equal(UnixImportAssociations.BundleId, bundleId);
            Set.Add(scheme);
        }
    }
}
