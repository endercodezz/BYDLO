using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Net;
using Bydlo.Core.Processes;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines;
using Bydlo.Engines.Installation;
using Bydlo.Engines.Zapret;
using Bydlo.Platform;

namespace Bydlo.Tests;

public class ZapretPlatformTests
{
    [Theory]
    [InlineData(OsFamily.Linux, CpuArch.X64)]
    [InlineData(OsFamily.Linux, CpuArch.Arm64)]
    [InlineData(OsFamily.MacOS, CpuArch.Arm64)]
    public void Native_assets_are_pinned_for_supported_platforms(OsFamily os, CpuArch arch)
    {
        var entry = EngineLock.LoadEmbedded().Find("zapret-native")!;
        Assert.True(entry.Assets.ContainsKey(new PlatformInfo(os, arch).Rid));
        Assert.Equal(64, entry.Assets[new PlatformInfo(os, arch).Rid].Sha256.Length);
    }

    [Fact]
    public void Bundled_then_managed_then_custom_installation_and_default_strategy()
    {
        var root = TestDirs.New("zapret-resolution");
        var bundle = Path.Combine(root, "app");
        var data = Path.Combine(root, "data");
        var engineLock = EngineLock.LoadEmbedded();
        var entry = engineLock.Find("zapret")!;
        var platform = new PlatformInfo(OsFamily.Windows, CpuArch.X64);
        using var http = new HttpClient();
        // This fixture models a Windows x64 package even when the test host is macOS ARM64.
        var installer = new EngineInstaller(http, engineLock, Path.Combine(data, "engines"), packagePlatform: platform);
        var locator = new EngineLocator(installer, platform, bundle);
        var bundledRoot = CreateFlowseal(Path.Combine(bundle, "engines", "zapret", entry.Version, "zapret-discord-youtube-1.10.4"));
        var managedRoot = CreateFlowseal(Path.Combine(installer.InstallDirectory("zapret", entry.Version), "zapret-discord-youtube-1.10.4"));
        var customRoot = CreateFlowseal(Path.Combine(root, "custom"));
        var engine = new ZapretEngine(new FakeProcessRunner(), new LogRedactor(), locator, new FakePrivileges(true));
        var settings = new AppSettings { Zapret = new() { InstallPath = customRoot } };
        Assert.Equal(bundledRoot, engine.ResolveInstallation(settings)!.Root);
        File.Delete(Path.Combine(bundledRoot, "bin", "winws.exe"));
        Assert.Equal(managedRoot, engine.ResolveInstallation(settings)!.Root);
        File.Delete(Path.Combine(managedRoot, "bin", "winws.exe"));
        Assert.Equal(customRoot, engine.ResolveInstallation(settings)!.Root);
        Assert.StartsWith(data, engine.ResolveInstallation(settings)!.UserListsDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fresh_Windows_install_starts_without_opening_tools_or_choosing_strategy()
    {
        var root = CreateFlowseal(TestDirs.New("zapret-first-start"));
        var runner = new FakeProcessRunner();
        runner.StartupOutput.Add("capture is started");
        await using var engine = new ZapretEngine(runner, new LogRedactor(), new FakeLocator(new(OsFamily.Windows, CpuArch.X64)), new FakePrivileges(true));
        await engine.StartAsync(new() { Settings = new() { Zapret = new() { InstallPath = root } }, WorkDirectory = TestDirs.New("session") });
        Assert.Equal(EngineState.Running, engine.State);
        Assert.EndsWith("winws.exe", Assert.Single(runner.Started).FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void User_lists_migrate_once_and_arguments_use_data_paths_without_changing_release()
    {
        var oldRoot = CreateFlowseal(TestDirs.New("zapret-old"));
        var previous = new ZapretInstallation(oldRoot);
        previous.WriteUserList(ZapretUserList.General, "saved.example");
        var data = TestDirs.New("zapret-user-data");
        var install = new ZapretInstallation(oldRoot, data);
        install.ImportUserListsFrom(previous);
        install.WriteUserList(ZapretUserList.General, "new.example");
        install.ImportUserListsFrom(previous);
        Assert.Equal("new.example\n", install.ReadUserList(ZapretUserList.General));
        Assert.Equal("saved.example\n", previous.ReadUserList(ZapretUserList.General));
        var args = ZapretStrategyParser.Parse("start bin/winws.exe --hostlist=\"%LISTS%list-general-user.txt\" --hostlist=\"%LISTS%list-general.txt\"", oldRoot, GameFilterValues.From("disabled"), data).Arguments;
        Assert.Contains("--hostlist=" + Path.Combine(data, "list-general-user.txt"), args);
        Assert.Contains("--hostlist=" + Path.Combine(oldRoot, "lists", "list-general.txt"), args);
    }

    [Theory]
    [InlineData(OsFamily.Linux)]
    [InlineData(OsFamily.MacOS)]
    public async Task Native_lifecycle_removes_rules_after_stop_crash_and_failed_apply(OsFamily os)
    {
        foreach (var scenario in new[] { "stop", "crash", "apply-fails", "dispose" })
        {
            var root = TestDirs.New("native-lifecycle");
            var exe = Path.Combine(root, os == OsFamily.MacOS ? "tpws" : "nfqws");
            File.WriteAllText(exe, "fake executable; never run");
            var runner = new FakeProcessRunner();
            runner.StartupOutput.Add("capture is started");
            var firewall = new FakeFirewall { FailApply = scenario == "apply-fails" };
            var arch = os == OsFamily.MacOS ? CpuArch.Arm64 : CpuArch.X64;
            await using var engine = new ZapretEngine(runner, new LogRedactor(), new FakeLocator(new(os, arch)), new FakePrivileges(true), firewall);
            var request = new EngineStartRequest { Settings = new() { Zapret = new() { InstallPath = root } }, WorkDirectory = TestDirs.New("session") };
            var source = engine.ResolveInstallation(request.Settings)!;
            source.WriteUserList(ZapretUserList.General, "custom.example");
            if (scenario == "apply-fails")
            {
                await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(request));
                Assert.True(runner.Last!.Stopped);
            }
            else
            {
                await engine.StartAsync(request);
                Assert.Equal(1, firewall.Applied);
                if (os == OsFamily.Linux)
                {
                    var spec = Assert.Single(runner.Started);
                    Assert.Equal(exe, spec.FileName);
                    Assert.NotEqual(source.UserListsDirectory, spec.WorkingDirectory);
                    Assert.Equal("custom.example\n", File.ReadAllText(Path.Combine(spec.WorkingDirectory!, "list-general-user.txt")));
                    if (OperatingSystem.IsLinux())
                    {
                        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                            File.GetUnixFileMode(Path.GetDirectoryName(spec.WorkingDirectory)!));
                        Assert.True(File.GetUnixFileMode(spec.WorkingDirectory!).HasFlag(UnixFileMode.OtherExecute));
                        Assert.True(File.GetUnixFileMode(Path.Combine(spec.WorkingDirectory!, "list-general-user.txt")).HasFlag(UnixFileMode.OtherRead));
                    }
                }

                if (scenario == "crash")
                {
                    runner.Last!.Crash();
                    Assert.Equal(EngineState.Failed, engine.State);
                }
                else if (scenario == "dispose") { await engine.DisposeAsync(); }
                else { await engine.StopAsync(); }
            }

            Assert.True(firewall.Removed > 0);
            Assert.DoesNotContain(runner.Started, s => s.FileName.EndsWith(".bat", StringComparison.Ordinal));
            Assert.Equal("custom.example\n", source.ReadUserList(ZapretUserList.General));
            if (os == OsFamily.Linux)
            {
                Assert.False(Directory.Exists(Assert.Single(runner.Started).WorkingDirectory));
            }
        }
    }

    [Fact]
    public void Native_presets_keep_platform_capabilities_separate()
    {
        var install = new ZapretInstallation(TestDirs.New("native-args"));
        install.EnsureUserLists();
        var linux = NativeZapretStrategy.Build(OsFamily.Linux, install, "native-split", "udp");
        Assert.Contains("--qnum=21770", linux);
        Assert.Contains("--filter-udp=443,1024-65535", linux);
        Assert.Contains("--filter-tcp=80,443", linux);
        Assert.Contains("--dpi-desync-fwmark=0x40000000", linux);
        Assert.Equal(2, linux.Count(a => a == "--hostlist=default-hosts.txt"));
        Assert.Equal(2, linux.Count(a => a == "--hostlist=list-general-user.txt"));
        Assert.Equal(2, linux.Count(a => a == "--hostlist-exclude=list-exclude-user.txt"));
        Assert.Equal(2, linux.Count(a => a == "--ipset-exclude=ipset-exclude-user.txt"));
        var mac = NativeZapretStrategy.Build(OsFamily.MacOS, install, "native-split", "disabled");
        Assert.Contains("--user=root", mac);
        Assert.Contains("--hostlist=" + Path.Combine(install.UserListsDirectory, "default-hosts.txt"), mac);
        Assert.Contains("--bind-addr=127.0.0.1", mac);
        Assert.DoesNotContain(mac, a => a.Contains("udp", StringComparison.Ordinal) || a.Contains("qnum", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(OsFamily.Windows, CpuArch.Arm64, AvailabilityStatus.NotImplemented)]
    [InlineData(OsFamily.Android, CpuArch.Arm64, AvailabilityStatus.Unsupported)]
    public async Task Unsupported_platforms_never_claim_readiness(OsFamily os, CpuArch arch, AvailabilityStatus expected)
    {
        await using var engine = new ZapretEngine(new FakeProcessRunner(), new LogRedactor(), new FakeLocator(new(os, arch)), new FakePrivileges(true));
        Assert.Equal(expected, (await engine.CheckAvailabilityAsync(new())).Status);
    }

    [Fact]
    public void Nft_rules_use_private_scope_bypass_mark_and_validated_exclusions()
    {
        var lease = new ZapretFirewallLease(Guid.NewGuid().ToString("N"), OsFamily.Linux);
        var rules = ZapretFirewallRules.Build(lease, "80,443", "443,1024-65535", ["198.51.100.0/24", "2001:db8::/32"]);
        Assert.Contains("table inet bydlo_", rules, StringComparison.Ordinal);
        Assert.Contains("queue num 21770 bypass", rules, StringComparison.Ordinal);
        Assert.Contains("meta mark & 0x40000000 != 0 return", rules, StringComparison.Ordinal);
        Assert.Contains("ip6 daddr 2001:db8::/32 return", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("flush ruleset", rules, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => ZapretFirewallRules.Build(lease, "80; flush ruleset", "443", []));
        Assert.Equal("error.engine.zapret.exclusion", Assert.Throws<Bydlo.Core.Engines.EngineException>(() => ZapretFirewallRules.Build(lease, "80", "443", ["1.2.3.4; flush ruleset"])).LocalizationKey);
        Assert.Throws<InvalidDataException>(() => ZapretFirewallRules.Scope(lease with { Id = "../other" }));
    }

    [Fact]
    public void Pf_rules_exclude_root_local_networks_and_unsupported_traffic()
    {
        var lease = new ZapretFirewallLease(Guid.NewGuid().ToString("N"), OsFamily.MacOS);
        var rules = ZapretFirewallRules.Build(lease, "80,443", "443", ["198.51.100.0/24"]);
        Assert.StartsWith("com.apple/bydlo_", ZapretFirewallRules.Scope(lease), StringComparison.Ordinal);
        Assert.Contains("route-to (lo0 127.0.0.1)", rules, StringComparison.Ordinal);
        Assert.Contains("user > root", rules, StringComparison.Ordinal);
        Assert.Contains("198.51.100.0/24", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("inet6", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("udp", rules, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OsFamily.Linux)]
    [InlineData(OsFamily.MacOS)]
    public async Task Rule_journal_is_written_before_apply_and_scoped_cleanup_is_idempotent(OsFamily os)
    {
        var dir = TestDirs.New("firewall-journal");
        string? table = null;
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec =>
            {
                if (spec.Arguments.SequenceEqual(new[] { "-s", "nat" })) { return (0, "rdr-anchor \"com.apple/*\" all"); }
                if (spec.Arguments.SequenceEqual(new[] { "-s", "rules" })) { return (0, "anchor \"com.apple/*\" all"); }
                if (spec.Arguments.SequenceEqual(new[] { "list", "tables" })) { return (0, table ?? ""); }
                if (spec.Arguments.Contains("-f") && !spec.Arguments.Contains("-c") && !spec.Arguments.Contains("-n"))
                {
                    var journal = JsonSerializer.Deserialize<ZapretFirewallLease>(File.ReadAllText(Path.Combine(dir, "firewall.json")))!;
                    table = "table inet " + ZapretFirewallRules.Scope(journal);
                }
                if (spec.Arguments.Contains("delete")) { table = null; }
                return (0, spec.Arguments.Contains("-E") ? "Token : 123456" : "ok");
            },
        };
        await using var firewall = new ZapretFirewall(runner, dir);
        await firewall.ApplyAsync(os, "80,443", "443", []);
        Assert.True(firewall.HasPendingRecovery);
        await firewall.RemoveAsync();
        await firewall.RemoveAsync();
        Assert.False(firewall.HasPendingRecovery);
        if (os == OsFamily.Linux) { Assert.Contains(runner.Completed, s => s.Arguments.Contains("delete")); }
        else
        {
            Assert.Contains(runner.Completed, s => s.Arguments.SequenceEqual(new[] { "-X", "123456" }));
            Assert.DoesNotContain(runner.Completed, s => s.Arguments.Contains("-d"));
        }
        Assert.All(runner.Completed, s => Assert.True(s.AffectsNetwork));
    }

    [Fact]
    public async Task Failed_recovery_retains_journal_for_retry()
    {
        var dir = TestDirs.New("firewall-recovery");
        var lease = new ZapretFirewallLease(Guid.NewGuid().ToString("N"), OsFamily.Linux);
        File.WriteAllText(Path.Combine(dir, "firewall.json"), JsonSerializer.Serialize(lease));
        var runner = new FakeProcessRunner { CompletionExitCode = 1 };
        var firewall = new ZapretFirewall(runner, dir);
        await Assert.ThrowsAsync<InvalidOperationException>(() => firewall.RemoveAsync());
        Assert.True(firewall.HasPendingRecovery);
        runner.CompletionExitCode = 0; // No table after reboot: absence is a successful cleanup.
        await firewall.RemoveAsync();
        Assert.False(firewall.HasPendingRecovery);
        await firewall.DisposeAsync();
    }

    [Fact]
    public async Task Missing_Pf_anchors_fail_without_enabling_or_replacing_global_rules()
    {
        var runner = new FakeProcessRunner();
        await using var firewall = new ZapretFirewall(runner, TestDirs.New("pf-anchors"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => firewall.ApplyAsync(OsFamily.MacOS, "80,443", "443", []));
        Assert.DoesNotContain(runner.Completed, s => s.Arguments.Contains("-E") || s.Arguments.Contains("-f"));
        Assert.False(firewall.HasPendingRecovery);
    }

    [Fact]
    public async Task Native_installer_extracts_only_selected_executable_with_fake_HTTP()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "native/nfqws", "unrelated/platform-file.txt" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("fake file, no executable content");
            }
        }

        var archive = bytes.ToArray();
        var engineLock = new EngineLock
        {
            Engines = [new("zapret-native", "test", "MIT", "https://example.invalid",
                new() { ["linux-x64"] = new("https://example.invalid/archive.zip", Convert.ToHexString(SHA256.HashData(archive)), "native/nfqws") })],
        };
        using var http = new HttpClient(new ArchiveHandler(archive));
        var root = TestDirs.New("selected-extraction");
        var installer = new EngineInstaller(http, engineLock, root);
        var exe = await installer.InstallAsync("zapret-native", new(OsFamily.Linux, CpuArch.X64));
        Assert.True(File.Exists(exe));
        Assert.False(File.Exists(Path.Combine(root, "zapret-native", "test", "unrelated", "platform-file.txt")));
    }

    [Fact]
    public async Task Cancellation_after_process_start_cleans_up_and_stops_fake_process()
    {
        var root = TestDirs.New("cancel-native");
        File.WriteAllText(Path.Combine(root, "nfqws"), "fake file");
        var runner = new FakeProcessRunner();
        var firewall = new FakeFirewall();
        await using var engine = new ZapretEngine(runner, new LogRedactor(), new FakeLocator(new(OsFamily.Linux, CpuArch.X64)), new FakePrivileges(true), firewall);
        using var cts = new CancellationTokenSource();
        var starting = engine.StartAsync(new() { Settings = new() { Zapret = new() { InstallPath = root } }, WorkDirectory = TestDirs.New("cancel-session") }, cts.Token);
        Assert.NotNull(runner.Last);
        cts.Cancel();
        await Assert.ThrowsAsync<EngineException>(() => starting);
        Assert.True(runner.Last.Stopped);
        Assert.True(firewall.Removed > 0);
        Assert.Equal(0, firewall.Applied);
    }

    [Fact]
    public async Task Partial_firewall_apply_failure_rolls_back_using_durable_journal()
    {
        var dir = TestDirs.New("partial-firewall");
        var applied = false;
        var deleted = false;
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec =>
            {
                if (spec.Arguments.Contains("-f") && !spec.Arguments.Contains("-c"))
                {
                    Assert.True(File.Exists(Path.Combine(dir, "firewall.json")));
                    applied = true;
                    return (1, "fake apply failed after creating table");
                }
                if (spec.Arguments.SequenceEqual(new[] { "list", "tables" }) && applied)
                {
                    var lease = JsonSerializer.Deserialize<ZapretFirewallLease>(File.ReadAllText(Path.Combine(dir, "firewall.json")))!;
                    return (0, "table inet " + ZapretFirewallRules.Scope(lease));
                }
                if (spec.Arguments.Contains("delete")) { deleted = true; }
                return (0, "ok");
            },
        };
        await using var firewall = new ZapretFirewall(runner, dir);
        await Assert.ThrowsAsync<InvalidOperationException>(() => firewall.ApplyAsync(OsFamily.Linux, "80,443", "443", []));
        Assert.True(deleted);
        Assert.False(firewall.HasPendingRecovery);
    }

    [Fact]
    public async Task Second_instance_cannot_remove_active_firewall_rules()
    {
        var dir = TestDirs.New("firewall-ownership");
        var firstRunner = new FakeProcessRunner();
        await using var first = new ZapretFirewall(firstRunner, dir);
        await first.ApplyAsync(OsFamily.Linux, "80,443", "443", []);
        var secondRunner = new FakeProcessRunner();
        var second = new ZapretFirewall(secondRunner, dir);
        await Assert.ThrowsAsync<IOException>(() => second.ApplyAsync(OsFamily.Linux, "80,443", "443", []));
        Assert.Empty(secondRunner.Completed);
        Assert.True(first.HasPendingRecovery);
        await first.RemoveAsync();
        await second.DisposeAsync();
    }

    private sealed class ArchiveHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    private static string CreateFlowseal(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "lists"));
        foreach (var name in new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" })
        {
            File.WriteAllText(Path.Combine(root, "bin", name), "not executable");
        }
        File.WriteAllText(Path.Combine(root, "general.bat"), "start bin/winws.exe --wf-tcp=80,443");
        return root;
    }

    private sealed class FakeFirewall : IZapretFirewall
    {
        public int Applied { get; private set; }
        public int Removed { get; private set; }
        public bool FailApply { get; init; }

        public Task ApplyAsync(OsFamily os, string tcpPorts, string udpPorts, IReadOnlyList<string> excludedNetworks, CancellationToken ct = default)
        {
            Applied++;
            return FailApply ? Task.FromException(new IOException("fake apply failure")) : Task.CompletedTask;
        }

        public Task RemoveAsync() { Removed++; return Task.CompletedTask; }
    }
}
