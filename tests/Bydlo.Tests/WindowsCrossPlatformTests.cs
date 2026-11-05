using Bydlo.Platform.Processes;
using System.Security.Cryptography;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.OpenVpn;
using Bydlo.Engines.Zapret;
using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class WindowsCrossPlatformTests
{
    private static readonly PlatformInfo Windows = new(OsFamily.Windows, CpuArch.Arm64);

    [Theory]
    [InlineData(OsFamily.Windows)]
    [InlineData(OsFamily.Linux)]
    [InlineData(OsFamily.MacOS)]
    public void Secret_store_selection_uses_target_platform_without_opening_native_vault(OsFamily os)
    {
        var windows = new InMemorySecretStore();
        var portable = new InMemorySecretStore();
        Assert.Same(os == OsFamily.Windows ? windows : portable,
            SecretStoreFactory.Select(new(os, CpuArch.X64), () => windows, () => portable));
    }

    [Fact]
    public async Task Windows_probe_selects_windows_curl_and_null_device_with_fake_processes()
    {
        var runner = new FakeProcessRunner { OnCompletion = _ => (0, "BYDLO_RESULT:200:0.01") };
        await new TestZapretProbe(runner, Windows).CheckWithRunnerAsync(["https://example.com/"], CancellationToken.None);
        Assert.Equal(3, runner.Completed.Count);
        Assert.All(runner.Completed, spec =>
        {
            Assert.Equal("curl.exe", spec.FileName);
            Assert.Contains("NUL", spec.Arguments);
            Assert.DoesNotContain("/dev/null", spec.Arguments);
        });
    }

    [Theory]
    [InlineData(@"C:\Program Files\BYDLO\BYDLO.exe", true)]
    [InlineData(@"\\server\share\BYDLO.exe", true)]
    [InlineData(@"C:relative.exe", false)]
    [InlineData(@"\rooted.exe", false)]
    [InlineData("/usr/bin/bydlo", false)]
    public void Windows_paths_do_not_use_host_path_rules(string path, bool absolute)
    {
        Assert.Equal(absolute, new TargetPathRules(OsFamily.Windows).IsAbsolute(path));
        if (absolute) { Assert.Equal($"\"{path}\" \"%1\"", WindowsImportAssociations.OpenCommand(path)); }
        else { Assert.Throws<ArgumentException>(() => WindowsImportAssociations.OpenCommand(path)); }
    }

    [Fact]
    public void Windows_strategy_paths_and_user_lists_are_resolved_on_any_host()
    {
        var parsed = ZapretStrategyParser.Parse("winws.exe --hostlist=\"%LISTS%list-general-user.txt\" --dpi-desync-fake-tls=\"%BIN%tls.bin\"",
            @"C:\Program Files\Zapret", GameFilterValues.From("disabled"), @"D:\User Lists", Windows);
        Assert.Equal([@"--hostlist=D:\User Lists\list-general-user.txt", @"--dpi-desync-fake-tls=C:\Program Files\Zapret\bin\tls.bin"], parsed.Arguments);
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void Windows_registrations_preserve_other_protocol_handlers()
    {
        const string exe = @"C:\Program Files\BYDLO\BYDLO.exe";
        var store = new Registrations();
        Assert.False(new AutostartService(Windows, @"C:\Program Files\dotnet\dotnet.exe", store).IsSupported);
        var service = new AutostartService(Windows, exe, store);
        Assert.True(service.IsSupported);
        Assert.Empty(store.Values);
        service.SetEnabled(true);
        Assert.Contains($"\"{exe}\" --autostart", store.Values.Values);
        service.SetEnabled(false);
        Assert.Empty(store.Values);
        store.Commands["vless"] = "another application";
        Assert.Throws<InvalidOperationException>(() => WindowsImportAssociations.Register(exe, store));
        Assert.Empty(store.Values);
        store.Commands.Clear();
        WindowsImportAssociations.Register(exe, store);
        Assert.Equal(WindowsImportAssociations.OpenCommand(exe), store.Values[@"Software\Classes\vless\shell\open\command|"]);
        store.Commands["vless"] = WindowsImportAssociations.OpenCommand(exe);
        store.Commands["vmess"] = "another application";
        WindowsImportAssociations.Unregister(exe, store);
        Assert.Contains(@"Software\Classes\vless", store.DeletedTrees);
        Assert.DoesNotContain(@"Software\Classes\vmess", store.DeletedTrees);
    }

    [Fact]
    public async Task Windows_notification_limits_are_checked_before_native_call()
    {
        var backend = new Notifications();
        var runner = new FakeProcessRunner();
        using var notifications = new DesktopNotifications(runner, Windows, backend);
        await notifications.ShowAsync(0, "title", "message");
        Assert.Null(backend.Title);
        await notifications.ShowAsync(42, new string('t', 100), new string('m', 300));
        Assert.Equal(63, backend.Title!.Length);
        Assert.Equal(255, backend.Message!.Length);
        Assert.Empty(runner.Completed);
    }

    [Fact]
    public async Task Windows_openvpn_owns_service_event_until_stop_without_Win32()
    {
        var events = new FakeExitEvents();
        var runner = new FakeProcessRunner { StartupOutput = { "Initialization Sequence Completed" } };
        await using var engine = new OpenVpnEngine(runner, new LogRedactor(), new FakeLocator(Windows),
            new FakePrivileges(true), new Driver(), events);
        await engine.StartAsync(new() { Settings = new(), WorkDirectory = TestDirs.New("windows-openvpn"),
            Secrets = new() { Values = { [Keys.RawConfig] = "client\ndev tun\nremote server.example 1194" } } });
        var spec = Assert.Single(runner.Started);
        Assert.Contains("--service", spec.Arguments);
        Assert.Equal(events.Name, spec.WindowsExitEventName);
        Assert.Contains("ovpn-dco", spec.Arguments);
        Assert.DoesNotContain("--disable-dco", spec.Arguments);
        Assert.False(events.Disposed);
        await engine.StopAsync();
        Assert.True(events.Signaled && events.Disposed);
    }

    [Theory]
    [InlineData("Local\\BYDLO-OpenVPN-00112233445566778899aabbccddeeff", true)]
    [InlineData("Global\\OtherEvent", false)]
    [InlineData("Local\\BYDLO-OpenVPN-not-a-guid", false)]
    public void Recovery_signals_only_owned_windows_events(string name, bool accepted)
    {
        var ops = new ExitOperations();
        Assert.Equal(accepted, TerminationPolicy.Request(OsFamily.Windows, name, ops));
        Assert.Equal(accepted ? name : null, ops.EventName);
    }

    [Fact]
    public async Task Dpapi_storage_policy_handles_round_trip_corruption_and_deletion_without_native_API()
    {
        var protection = new Protection();
        var directory = TestDirs.New("fake-dpapi");
        var store = new DpapiSecretStore(directory, protection);
        await store.SaveAsync("profile", new() { Values = { [Keys.Password] = "secret" } });
        Assert.Equal("secret", (await store.LoadAsync("profile"))[Keys.Password]);
        protection.Corrupted = true;
        Assert.Null((await store.LoadAsync("profile"))[Keys.Password]);
        await store.DeleteAsync("profile");
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void Owned_import_associations_can_move_and_unregister_from_another_directory()
    {
        var store = new Registrations();
        var oldCommand = WindowsImportAssociations.OpenCommand(@"C:\old\BYDLO.exe");
        foreach (var protocol in WindowsImportAssociations.Protocols)
        {
            store.Commands[protocol] = oldCommand;
            store.Set($@"Software\Classes\{protocol}", "BYDLO.Owner", WindowsImportAssociations.OwnerId);
            store.Set($@"Software\Classes\{protocol}", "BYDLO.OwnerCommand", oldCommand);
        }
        WindowsImportAssociations.Register(@"C:\new\BYDLO.exe", store);
        Assert.Equal(WindowsImportAssociations.OpenCommand(@"C:\new\BYDLO.exe"),
            store.Values[@"Software\Classes\vless\shell\open\command|"]);
        WindowsImportAssociations.Unregister(@"C:\third\BYDLO.exe", store);
        Assert.Contains(@"Software\Classes\vless", store.DeletedTrees);
    }

    private sealed class Registrations : IWindowsRegistrationStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Dictionary<string, string> Commands { get; } = [];
        public List<string> DeletedTrees { get; } = [];
        public string? ReadCommand(string protocol, bool merged) => Commands.GetValueOrDefault(protocol);
        public string? ReadOwner(string protocol, bool merged) =>
            Values.GetValueOrDefault($@"Software\Classes\{protocol}|BYDLO.OwnerCommand") == ReadCommand(protocol, merged)
                ? Values.GetValueOrDefault($@"Software\Classes\{protocol}|BYDLO.Owner") : null;
        public void Set(string path, string name, string value)
        {
            Values[path + "|" + name] = value;
            if (path.EndsWith(@"\shell\open\command", StringComparison.Ordinal) && name == "")
            {
                Commands[path.Split('\\')[2]] = value;
            }
        }
        public void DeleteTree(string path) => DeletedTrees.Add(path);
        public void DeleteValue(string path, string name) => Values.Remove(path + "|" + name);
    }

    private sealed class Notifications : IWindowsNotificationBackend
    {
        public string? Title { get; private set; }
        public string? Message { get; private set; }
        public Task ShowAsync(nint window, string title, string message, CancellationToken ct)
        { Title = title; Message = message; return Task.CompletedTask; }
    }

    private sealed class Driver : IOpenVpnDriver
    {
        public bool IsInstalled => true;
        public Task InstallAsync(bool userConsent, CancellationToken ct = default) => throw new InvalidOperationException();
    }

    private sealed class ExitOperations : IProcessExitOperations
    {
        public string? EventName { get; private set; }
        public bool SignalUnix() => throw new InvalidOperationException("Must not select Unix for a Windows target");
        public bool SignalWindowsEvent(string name) { EventName = name; return true; }
        public bool CloseWindow() => true;
    }

    private sealed class Protection : IDataProtection
    {
        public bool Corrupted { get; set; }
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x55)).ToArray();
        public byte[] Unprotect(byte[] cipher)
        {
            if (Corrupted) { throw new CryptographicException(); }
            return cipher.Select(b => (byte)(b ^ 0x55)).ToArray();
        }
    }
}
