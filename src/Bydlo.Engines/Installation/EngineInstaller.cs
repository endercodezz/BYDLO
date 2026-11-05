using System.Formats.Tar;
using System.IO.Compression;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Engines.Installation;

public sealed record InstallProgress(string Stage, double? Fraction);

/// <summary>
/// Downloads pinned engine releases, verifies SHA-256 against <see cref="EngineLock"/> and extracts them into
/// BYDLO's private engines directory. Never installs system services, drivers or anything outside that folder.
/// </summary>
public sealed class EngineInstaller(HttpClient http, EngineLock engineLock, string enginesDirectory, PlatformInfo? hostPlatform = null, PlatformInfo? packagePlatform = null)
{
    public string? PinnedVersion(string id) => engineLock.Find(id)?.Version;

    public string UserDataDirectory => Path.Combine(Path.GetDirectoryName(enginesDirectory)!, "zapret");

    public string? BundledExecutable(string id, PlatformInfo platform, string? baseDirectory = null)
    {
        // Bundled files belong to the running package, which may be emulated on another OS architecture.
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => CpuArch.X64,
            System.Runtime.InteropServices.Architecture.Arm64 => CpuArch.Arm64,
            _ => CpuArch.Other,
        };
        return FindExecutable(id, packagePlatform ?? platform with { Arch = arch },
            Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "engines"));
    }
    public string InstallDirectory(string id, string version) => Path.Combine(enginesDirectory, id, version);

    /// <summary>Path of the executable if this pinned version is already installed.</summary>
    public string? InstalledExecutable(string id, PlatformInfo platform)
    {
        return FindExecutable(id, platform, enginesDirectory);
    }

    private string? FindExecutable(string id, PlatformInfo platform, string root)
    {
        var entry = engineLock.Find(id);
        if (entry is null)
        {
            return null;
        }
        var relative = entry.Assets.TryGetValue(platform.Rid, out var asset) ? asset.Executable : entry.BundledExecutables.GetValueOrDefault(platform.Rid);
        if (relative is null) { return null; }
        var exe = Path.Combine(root, id, entry.Version, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(exe) ? exe : null;
    }

    public bool HasAsset(string id, PlatformInfo platform) =>
        engineLock.Find(id)?.Assets.ContainsKey(platform.Rid) == true;

    public async Task<string> InstallAsync(string id, PlatformInfo platform, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var entry = engineLock.Find(id) ?? throw new InvalidOperationException($"Unknown engine '{id}'.");
        if (!entry.Assets.TryGetValue(platform.Rid, out var asset))
        {
            throw new PlatformNotSupportedException($"{id} has no pinned build for {platform.Rid}.");
        }

        var target = InstallDirectory(id, entry.Version);
        var downloads = Path.Combine(enginesDirectory, ".downloads");
        Directory.CreateDirectory(downloads);
        var fileName = Path.GetFileName(new Uri(asset.Url).AbsolutePath);
        // Unique temp names: concurrent installs of the same version must not share one archive file.
        // The original extension is preserved so extraction still recognizes the format.
        var archive = Path.Combine(downloads, TempArchiveName(fileName));

        progress?.Report(new InstallProgress("download", 0));
        try
        {
            await DownloadAsync(asset.Url, archive, progress, ct).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(archive))
            {
                try { File.Delete(archive); } catch (Exception) { }
            }
            throw;
        }

        progress?.Report(new InstallProgress("verify", null));
        try
        {
            await IntegrityVerifier.VerifyFileAsync(archive, asset.Sha256, ct).ConfigureAwait(false);
        }
        catch (IntegrityException)
        {
            File.Delete(archive);
            throw;
        }
        catch
        {
            // Cancelled hash, unreadable file, malformed digest: never leave the temp archive behind.
            if (File.Exists(archive))
            {
                try { File.Delete(archive); } catch (Exception) { }
            }
            throw;
        }

        progress?.Report(new InstallProgress("extract", null));
        var staging = target + ".staging." + Guid.NewGuid().ToString("N");
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        try
        {
            await ExtractAsync(archive, staging, ct, id == "zapret-native" ? new HashSet<string>(StringComparer.Ordinal) { asset.Executable } : null).ConfigureAwait(false);
            var stagedExe = Path.Combine(staging, asset.Executable.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(stagedExe))
            {
                throw new FileNotFoundException($"Archive did not contain {asset.Executable}; previous installation was kept.");
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stagedExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }

            ct.ThrowIfCancellationRequested();
            await ReplaceDirectoryAsync(staging, target, ct).ConfigureAwait(false);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); } catch (Exception) { }
            }
            if (File.Exists(archive))
            {
                try { File.Delete(archive); } catch (Exception) { }
            }
            throw;
        }
        File.Delete(archive);

        var exe = Path.Combine(target, asset.Executable.Replace('/', Path.DirectorySeparatorChar));
        if (platform.Os != OsFamily.Windows)
        {
            Bydlo.Platform.NativeFiles.MakeExecutable(exe);
        }

        progress?.Report(new InstallProgress("done", 1));
        return exe;
    }

    /// <summary>Unique temp file that keeps the archive extension for format detection.</summary>
    internal static string TempArchiveName(string fileName)
    {
        var suffix = ".tmp." + Guid.NewGuid().ToString("N");
        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^".tar.gz".Length] + suffix + ".tar.gz";
        }
        if (fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^".tgz".Length] + suffix + ".tgz";
        }
        var ext = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(ext) ? fileName + suffix : fileName[..^ext.Length] + suffix + ext;
    }

    /// <summary>
    /// Atomically replaces <paramref name="target"/> with <paramref name="staging"/>.
    /// The previous version is kept in a backup directory until the new one is in place,
    /// so a failed move or cancellation never leaves the engine without a working copy.
    /// </summary>
    internal async Task ReplaceDirectoryAsync(string staging, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Unique backup: concurrent installs of the same version must not share one backup directory,
        // and a failed move must never delete the version that is currently in place.
        var backup = target + ".backup." + Guid.NewGuid().ToString("N");
        var hadTarget = Directory.Exists(target);
        if (hadTarget)
        {
            Directory.Move(target, backup);
        }
        try
        {
            await MoveDirectoryAsync(staging, target, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Directory.Exists(target))
            {
                // A concurrent install published a complete version while we were moving.
                // Keep the winner and drop our displaced copy; nothing is lost.
                if (Directory.Exists(backup))
                {
                    Directory.Delete(backup, recursive: true);
                }
                throw;
            }
            if (hadTarget && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }
            throw;
        }
        catch
        {
            if (Directory.Exists(target))
            {
                // Cancelled after a concurrent install published its copy: keep the winner.
                if (Directory.Exists(backup))
                {
                    Directory.Delete(backup, recursive: true);
                }
            }
            else if (hadTarget && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }
            throw;
        }
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }
    }


    private async Task MoveDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        // Windows file scanners can briefly retain extracted files without FILE_SHARE_DELETE.
        // Retry only its transient access/sharing failures, never suppress a permanent error.
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { Directory.Move(source, destination); return; }
            catch (IOException ex) when ((hostPlatform ?? PlatformInfo.Current).Os == OsFamily.Windows && attempt < 6 && (ex.HResult & 0xffff) is 5 or 32 or 33)
            { await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), ct).ConfigureAwait(false); }
        }
    }

    private async Task DownloadAsync(string url, string path, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total is > 0)
            {
                progress?.Report(new InstallProgress("download", (double)read / total.Value));
            }
        }
    }

    internal static async Task ExtractAsync(string archive, string destination, CancellationToken ct, IReadOnlySet<string>? selectedFiles = null)
    {
        var full = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var e in zip.Entries)
            {
                var path = SafeCombine(full, e.FullName);
                if (selectedFiles is not null && !selectedFiles.Contains(e.FullName)) { continue; }
                if (e.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                e.ExtractToFile(path, overwrite: true);
            }

            return;
        }

        if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            await using var fs = File.OpenRead(archive);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            while (await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
            {
                var path = SafeCombine(full, entry.Name);
                if (selectedFiles is not null && !selectedFiles.Contains(entry.Name)) { continue; }
                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(path);
                }
                else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await entry.ExtractToFileAsync(path, overwrite: true, ct).ConfigureAwait(false);
                }

                // Links and devices are skipped on purpose.
            }

            return;
        }

        throw new NotSupportedException($"Unsupported archive format: {Path.GetFileName(archive)}");
    }

    /// <summary>Prevents "zip slip" path traversal.</summary>
    internal static string SafeCombine(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        return path.StartsWith(root, StringComparison.Ordinal)
            ? path
            : throw new InvalidDataException($"Archive entry escapes the target folder: {relative}");
    }
}


