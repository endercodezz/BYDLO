using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class HelperInstallBundleTests
{
    [UnixFact]
    public void Rejects_linked_payloads_without_touching_the_link_target()
    {
        var source = Directory.CreateTempSubdirectory("helper-link-source-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "helper"));
            var target = Path.Combine(source, "foreign-file");
            File.WriteAllText(target, "unchanged");
            File.CreateSymbolicLink(Path.Combine(source, "helper", "Bydlo.Helper"), target);
            Assert.Throws<IOException>(() => HelperInstallBundle.Stage(source, install: false));
            Assert.Equal("unchanged", File.ReadAllText(target));
        }
        finally { Directory.Delete(source, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stages_helper_and_required_engines_without_executing_them(bool install)
    {
        var source = Directory.CreateTempSubdirectory("helper-source-").FullName;
        string stagedPath;
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "helper"));
            Directory.CreateDirectory(Path.Combine(source, "engines", "zapret"));
            var helper = Path.Combine(source, "helper", "Bydlo.Helper");
            File.WriteAllText(helper, "inert helper fixture");
            File.WriteAllText(Path.Combine(source, "engines", "zapret", "nfqws"), "inert engine fixture");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            using (var bundle = HelperInstallBundle.Stage(source, install))
            {
                stagedPath = bundle.DirectoryPath;
                Assert.NotEqual(source, stagedPath);
                Assert.Equal("inert helper fixture", File.ReadAllText(bundle.Executable));
                Assert.Equal(install, File.Exists(Path.Combine(stagedPath, "engines", "zapret", "nfqws")));
                if (!OperatingSystem.IsWindows())
                {
                    Assert.Equal(File.GetUnixFileMode(helper), File.GetUnixFileMode(bundle.Executable));
                }
            }
            Assert.False(Directory.Exists(stagedPath));
            Assert.True(File.Exists(helper));
        }
        finally { Directory.Delete(source, recursive: true); }
    }
}
