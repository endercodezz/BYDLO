using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Engines.Installation;

namespace Bydlo.Tests;

/// <summary>Atomic engine installation (issue #186). No network, fake HTTP only, temp dirs only.</summary>
public sealed class EngineInstallerTests
{
    [Fact]
    public void Bundled_engine_uses_package_architecture_under_emulation()
    {
        var root = TestDirs.New("bundled-emulation");
        var engineLock = new EngineLock { Engines = [new("sing-box", "1", "MIT", "https://example.invalid", new()
        {
            ["windows-x64"] = new("https://example.invalid/x64.zip", "", "windows-amd64/sing-box.exe"),
            ["windows-arm64"] = new("https://example.invalid/arm64.zip", "", "windows-arm64/sing-box.exe"),
        })] };
        var path = Path.Combine(root, "engines", "sing-box", "1", "windows-amd64", "sing-box.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fake");
        using var http = new HttpClient();
        var installer = new EngineInstaller(http, engineLock, Path.Combine(root, "installed"), packagePlatform: new(OsFamily.Windows, CpuArch.X64));
        var os = new PlatformInfo(OsFamily.Windows, CpuArch.Arm64);
        Assert.Equal(path, installer.BundledExecutable("sing-box", os, root));
        Assert.Null(installer.InstalledExecutable("sing-box", os));
    }

    private static byte[] ZipWith(params (string Name, string Content)[] files)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        return bytes.ToArray();
    }

    private static EngineLock LockFor(byte[] archive, string executable = "tool/engine-bin") =>
        new()
        {
            Engines =
            [
                new("test-engine", "1.0", "MIT", "https://example.invalid",
                    new() { ["linux-x64"] = new("https://example.invalid/archive.zip", Convert.ToHexString(SHA256.HashData(archive)), executable) }),
            ],
        };

    private sealed class ArchiveHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    private static void AssertNoLitter(string root)
    {
        var engineDir = Path.Combine(root, "test-engine");
        if (Directory.Exists(engineDir))
        {
            Assert.Empty(Directory.GetDirectories(engineDir, "*.staging.*"));
            Assert.Empty(Directory.GetDirectories(engineDir, "*.backup.*"));
        }
        var downloads = Path.Combine(root, ".downloads");
        if (Directory.Exists(downloads))
        {
            Assert.Empty(Directory.GetFiles(downloads));
        }
    }

    [Fact]
    public async Task Replace_fresh_target_leaves_no_backup_or_staging()
    {
        var root = TestDirs.New("installer-replace-fresh");
        var staging = Path.Combine(root, "staging");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "engine-bin"), "new");

        using var http = new HttpClient();
        await new EngineInstaller(http, new EngineLock(), root).ReplaceDirectoryAsync(staging, target, CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "engine-bin")));
        Assert.False(Directory.Exists(staging));
        Assert.Empty(Directory.GetDirectories(root, "*.backup.*"));
    }

    [Fact]
    public async Task Replace_moves_old_version_to_backup_then_removes_it()
    {
        var root = TestDirs.New("installer-replace-success");
        var staging = Path.Combine(root, "staging");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(staging, "engine-bin"), "new");
        File.WriteAllText(Path.Combine(target, "engine-bin"), "old");

        using var http = new HttpClient();
        await new EngineInstaller(http, new EngineLock(), root).ReplaceDirectoryAsync(staging, target, CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "engine-bin")));
        Assert.False(Directory.Exists(staging));
        Assert.Empty(Directory.GetDirectories(root, "*.backup.*"));
    }

    [Fact]
    public async Task Replace_restores_backup_when_move_fails()
    {
        var root = TestDirs.New("installer-replace-failure");
        var missingStaging = Path.Combine(root, "no-such-staging");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "engine-bin"), "old");

        using var failHttp = new HttpClient();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new EngineInstaller(failHttp, new EngineLock(), root).ReplaceDirectoryAsync(missingStaging, target, CancellationToken.None));

        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "engine-bin")));
        Assert.Empty(Directory.GetDirectories(root, "*.backup.*"));
        Assert.False(Directory.Exists(missingStaging));
    }

    [Fact]
    public async Task Replace_cancelled_before_swap_keeps_old_version()
    {
        var root = TestDirs.New("installer-replace-cancel");
        var staging = Path.Combine(root, "staging");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(staging, "engine-bin"), "new");
        File.WriteAllText(Path.Combine(target, "engine-bin"), "old");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var cancelHttp = new HttpClient();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new EngineInstaller(cancelHttp, new EngineLock(), root).ReplaceDirectoryAsync(staging, target, cts.Token));

        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "engine-bin")));
        Assert.Empty(Directory.GetDirectories(root, "*.backup.*"));
    }

    [Fact]
    public async Task Install_replaces_old_version_and_missing_exe_keeps_previous()
    {
        var root = TestDirs.New("installer-cycle");
        var archive = ZipWith(("tool/engine-bin", "new-binary"));
        using var http = new HttpClient(new ArchiveHandler(archive));
        var installer = new EngineInstaller(http, LockFor(archive), root);
        var platform = new PlatformInfo(OsFamily.Linux, CpuArch.X64);

        var exe = await installer.InstallAsync("test-engine", platform);
        Assert.Equal("new-binary", File.ReadAllText(exe));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True((File.GetUnixFileMode(exe) & UnixFileMode.UserExecute) != 0);
        }

        var badArchive = ZipWith(("tool/wrong-file", "junk"));
        using var badHttp = new HttpClient(new ArchiveHandler(badArchive));
        var badInstaller = new EngineInstaller(badHttp, LockFor(badArchive), root);

        await Assert.ThrowsAsync<FileNotFoundException>(() => badInstaller.InstallAsync("test-engine", platform));
        Assert.Equal("new-binary", File.ReadAllText(exe));
        AssertNoLitter(root);
    }

    [Fact]
    public async Task Install_cancelled_before_swap_keeps_old_version()
    {
        var root = TestDirs.New("installer-cancel");
        var archive = ZipWith(("tool/engine-bin", "new-binary"));
        using var http = new HttpClient(new ArchiveHandler(archive));
        var installer = new EngineInstaller(http, LockFor(archive), root);
        var platform = new PlatformInfo(OsFamily.Linux, CpuArch.X64);
        var exe = await installer.InstallAsync("test-engine", platform);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.InstallAsync("test-engine", platform, ct: cts.Token));

        Assert.Equal("new-binary", File.ReadAllText(exe));
        AssertNoLitter(root);
    }

    [Fact]
    public async Task Install_cancelled_during_download_keeps_old_version()
    {
        var root = TestDirs.New("installer-cancel-midflow");
        var firstArchive = ZipWith(("tool/engine-bin", "old-binary"));
        using var firstHttp = new HttpClient(new ArchiveHandler(firstArchive));
        var platform = new PlatformInfo(OsFamily.Linux, CpuArch.X64);
        var exe = await new EngineInstaller(firstHttp, LockFor(firstArchive), root)
            .InstallAsync("test-engine", platform);

        var cts = new CancellationTokenSource();
        using var http = new HttpClient(new CancelAfterHeadersHandler(cts));
        var installer = new EngineInstaller(http, LockFor(ZipWith(("tool/engine-bin", "new-binary"))), root);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.InstallAsync("test-engine", platform, ct: cts.Token));

        Assert.Equal("old-binary", File.ReadAllText(exe));
        AssertNoLitter(root);
    }

    [Fact]
    public async Task Install_rejects_tampered_archive_and_keeps_previous()
    {
        var root = TestDirs.New("installer-tampered");
        var archive = ZipWith(("tool/engine-bin", "good-binary"));
        using var http = new HttpClient(new ArchiveHandler(archive));
        var platform = new PlatformInfo(OsFamily.Linux, CpuArch.X64);
        var installer = new EngineInstaller(http, LockFor(archive), root);
        var exe = await installer.InstallAsync("test-engine", platform);

        var tamperedLock = new EngineLock
        {
            Engines =
            [
                new("test-engine", "1.0", "MIT", "https://example.invalid",
                    new() { ["linux-x64"] = new("https://example.invalid/archive.zip", new string('0', 64), "tool/engine-bin") }),
            ],
        };
        using var tamperedHttp = new HttpClient(new ArchiveHandler(archive));
        await Assert.ThrowsAsync<IntegrityException>(() =>
            new EngineInstaller(tamperedHttp, tamperedLock, root).InstallAsync("test-engine", platform));

        Assert.Equal("good-binary", File.ReadAllText(exe));
        AssertNoLitter(root);
    }

    private sealed class CancelAfterHeadersHandler(CancellationTokenSource cts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cts.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("cancelled"u8.ToArray()) });
        }
    }

    [Fact]
    public async Task Concurrent_installs_of_same_version_leave_working_copy()
    {
        var root = TestDirs.New("installer-concurrent");
        var firstArchive = ZipWith(("tool/engine-bin", "old-binary"));
        using var firstHttp = new HttpClient(new ArchiveHandler(firstArchive));
        var platform = new PlatformInfo(OsFamily.Linux, CpuArch.X64);
        var firstExe = await new EngineInstaller(firstHttp, LockFor(firstArchive), root)
            .InstallAsync("test-engine", platform);
        Assert.Equal("old-binary", File.ReadAllText(firstExe));

        var archive = ZipWith(("tool/engine-bin", "same-binary"));
        var engineLock = LockFor(archive);
        using var httpA = new HttpClient(new ArchiveHandler(archive));
        using var httpB = new HttpClient(new ArchiveHandler(archive));

        // Either serialized back-to-back (both succeed) or one loses the move race (throws)
        // while the winner's copy stands. The store must always end with a working version.
        var taskA = new EngineInstaller(httpA, engineLock, root).InstallAsync("test-engine", platform);
        var taskB = new EngineInstaller(httpB, engineLock, root).InstallAsync("test-engine", platform);
        var results = await Task.WhenAll(
            taskA.ContinueWith(t => (Ok: t.IsCompletedSuccessfully, Path: t.IsCompletedSuccessfully ? t.Result : null)),
            taskB.ContinueWith(t => (Ok: t.IsCompletedSuccessfully, Path: t.IsCompletedSuccessfully ? t.Result : null)));

        Assert.True(results.Any(r => r.Ok), "At least one concurrent install must succeed.");
        var exe = Assert.Single(results.Where(r => r.Ok).Select(r => r.Path).Distinct());
        Assert.True(File.Exists(exe));
        Assert.Equal("same-binary", File.ReadAllText(exe));
        AssertNoLitter(root);
    }
}


