namespace Bydlo.Platform;

/// <summary>Copies an AppImage payload off its user-owned FUSE mount before elevation.</summary>
internal sealed class HelperInstallBundle : IDisposable
{
    private HelperInstallBundle(string directory) { DirectoryPath = directory; }

    internal string DirectoryPath { get; }
    internal string Executable => Path.Combine(DirectoryPath, "helper", "Bydlo.Helper");

    internal static HelperInstallBundle Stage(string applicationDirectory, bool install)
    {
        var bundle = new HelperInstallBundle(Directory.CreateTempSubdirectory("bydlo-helper-").FullName);
        try
        {
            CopyTree(Path.Combine(applicationDirectory, "helper"), Path.Combine(bundle.DirectoryPath, "helper"));
            if (install)
            {
                CopyTree(Path.Combine(applicationDirectory, "engines"), Path.Combine(bundle.DirectoryPath, "engines"));
            }
            return bundle;
        }
        catch { bundle.Dispose(); throw; }
    }

    private static void CopyTree(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        { throw new IOException("Linked helper bundle paths are forbidden."); }
        Directory.CreateDirectory(destination);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            { throw new IOException("Linked helper bundle files are forbidden."); }
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) { CopyTree(entry, target); }
            else
            {
                File.Copy(entry, target);
                if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(target, File.GetUnixFileMode(entry)); }
            }
        }
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
