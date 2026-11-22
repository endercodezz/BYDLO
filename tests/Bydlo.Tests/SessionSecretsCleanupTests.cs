using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines.SingBox;
using Bydlo.Engines.Xray;

namespace Bydlo.Tests;

/// <summary>Covers issue #201: session configs with secrets must not survive Stop, failed start or crashes.</summary>
public sealed class SessionSecretsCleanupTests
{
    private static SingBoxEngine SingBox(FakeProcessRunner runner) =>
        new(runner, new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64), FakeExe()),
            new FakePrivileges(false));

    private static XrayEngine Xray(FakeProcessRunner runner) =>
        new(runner, new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64), FakeExe()));

    private static string FakeExe() =>
        "C:/fake/engine.exe".Replace('/', Path.DirectorySeparatorChar);

    private static EngineStartRequest SingBoxRequest(string work) => new()
    {
        Profile = new Profile { Name = "t", Protocol = ProtocolKind.Trojan, Host = "t.example.com", Port = 443 },
        Secrets = new ProfileSecrets { Values = { [Keys.Password] = "pw-123456" } },
        Settings = new AppSettings(),
        WorkDirectory = work,
    };

    private static EngineStartRequest XrayRequest(string work) => new()
    {
        Profile = new Profile { Name = "t", Protocol = ProtocolKind.Trojan, Host = "t.example.com", Port = 443 },
        Secrets = new ProfileSecrets { Values = { [Keys.Password] = "pw-123456" } },
        Settings = new AppSettings(),
        WorkDirectory = work,
    };

    [Fact]
    public async Task SingBox_config_is_removed_after_stop()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "INFO sing-box started" } };
        var engine = SingBox(runner);
        var work = TestDirs.New("sb-clean-stop");
        await engine.StartAsync(SingBoxRequest(work));
        var config = Path.Combine(work, "sing-box.json");
        Assert.True(File.Exists(config));
        await engine.StopAsync();
        Assert.False(File.Exists(config));
    }

    [Fact]
    public async Task Xray_config_is_removed_after_stop()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "Xray started" } };
        var engine = Xray(runner);
        var work = TestDirs.New("xray-clean-stop");
        await engine.StartAsync(XrayRequest(work));
        var config = Path.Combine(work, "xray.json");
        Assert.True(File.Exists(config));
        await engine.StopAsync();
        Assert.False(File.Exists(config));
    }

    [Fact]
    public async Task SingBox_config_is_removed_after_failed_start()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "FATAL start service: bad config" } };
        var engine = SingBox(runner);
        var work = TestDirs.New("sb-clean-fail");
        var start = engine.StartAsync(SingBoxRequest(work));
        // Wait for the fake process instead of a fixed delay: a slow runner made runner.Last null.
        await WaitForAsync(() => runner.Last is not null);
        runner.Last!.Crash(1);
        await Assert.ThrowsAsync<EngineException>(() => start);
        Assert.False(File.Exists(Path.Combine(work, "sing-box.json")));
    }

    [Fact]
    public async Task Xray_config_is_removed_after_failed_start()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "Failed to start" } };
        var engine = Xray(runner);
        var work = TestDirs.New("xray-clean-fail");
        var start = engine.StartAsync(XrayRequest(work));
        // Wait for the fake process instead of a fixed delay: a slow runner made runner.Last null.
        await WaitForAsync(() => runner.Last is not null);
        runner.Last!.Crash(1);
        await Assert.ThrowsAsync<EngineException>(() => start);
        Assert.False(File.Exists(Path.Combine(work, "xray.json")));
    }

    [Fact]
    public async Task SingBox_config_is_removed_after_unexpected_exit()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "sing-box started" } };
        var engine = SingBox(runner);
        var work = TestDirs.New("sb-clean-crash");
        await engine.StartAsync(SingBoxRequest(work));
        Assert.True(File.Exists(Path.Combine(work, "sing-box.json")));
        runner.Last!.Crash(3);
        await WaitForAsync(() => engine.State == EngineState.Failed);
        Assert.False(File.Exists(Path.Combine(work, "sing-box.json")));
    }

    [Fact]
    public async Task Xray_config_is_removed_after_unexpected_exit()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "Xray started" } };
        var engine = Xray(runner);
        var work = TestDirs.New("xray-clean-crash");
        await engine.StartAsync(XrayRequest(work));
        Assert.True(File.Exists(Path.Combine(work, "xray.json")));
        runner.Last!.Crash(3);
        await WaitForAsync(() => engine.State == EngineState.Failed);
        Assert.False(File.Exists(Path.Combine(work, "xray.json")));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "engine did not reach the expected state in time");
    }
}
