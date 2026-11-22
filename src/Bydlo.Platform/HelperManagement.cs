using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Bydlo.Core;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Platform;

public static class HelperManagement
{
    private const string BuildFile = "build.json";

    public static HelperBuild GetBuild(string bundleVersion) => new(AppVersion.Current, bundleVersion);

    public static void WriteInstalledBuild(HelperBuild build)
    {
        NetworkSafetyGuard.EnsureAllowed("write the privileged helper version");
        var path = Path.Combine(HelperPaths.InstallDirectory, BuildFile);
        File.WriteAllText(path, JsonSerializer.Serialize(build, PlatformJsonContext.Default.HelperBuild));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    public static bool IsOutdated(HelperBuild expected)
    {
        if (!IsInstalled) { return false; }
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(HelperPaths.InstallDirectory, BuildFile)), PlatformJsonContext.Default.HelperBuild) != expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    public static bool IsSupported => !OperatingSystem.IsAndroid() && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
    public static bool IsInstalled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) { return IsSupported && Directory.Exists(HelperPaths.InstallDirectory); }
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + HelperConnection.ServiceName);
            return key is not null || Directory.Exists(HelperPaths.InstallDirectory);
        }
    }

    /// <summary>Explicit installation actions invoke the OS administrator consent prompt.</summary>
    public static async Task<bool> ChangeAsync(bool install, bool userConsent, CancellationToken ct = default)
    {
        if (!userConsent) { return false; }
        NetworkSafetyGuard.EnsureAllowed("manage the privileged helper");
        ct.ThrowIfCancellationRequested();
        if (!IsSupported) { throw new PlatformNotSupportedException(); }
        // Root cannot access a normal user's AppImage FUSE mount. Stage before invoking PolKit.
        using var staged = OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE"))
            ? HelperInstallBundle.Stage(AppContext.BaseDirectory, install) : null;
        var executable = staged?.Executable ?? Path.Combine(AppContext.BaseDirectory, "helper", OperatingSystem.IsWindows() ? "Bydlo.Helper.exe" : "Bydlo.Helper");
        if (!File.Exists(executable)) { throw new FileNotFoundException("Helper is missing from this release."); }
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            info = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            info.ArgumentList.Add(install ? "install" : "remove");
            if (install)
            {
                using var identity = WindowsIdentity.GetCurrent();
                info.ArgumentList.Add(identity.User?.Value ?? throw new UnauthorizedAccessException());
            }
        }
        else
        {
            var uid = UnixHelperConnection.CurrentUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (OperatingSystem.IsMacOS())
            {
                var command = ShellQuote(executable) + (install ? " install " + uid : " remove");
                info = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("-e");
                info.ArgumentList.Add("do shell script \"" + command.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\" with administrator privileges");
            }
            else
            {
                info = new ProcessStartInfo("/usr/bin/pkexec") { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add(executable);
                info.ArgumentList.Add(install ? "install" : "remove");
                if (install) { info.ArgumentList.Add(uid); }
            }
        }
        try
        {
            using var process = Process.Start(info);
            if (process is null) { return false; }
            // A staged payload must remain available until the elevated installer finishes reading it.
            await process.WaitForExitAsync(staged is null ? ct : CancellationToken.None).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
