using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Bydlo.Tests;

/// <summary>Issue #243: maximized/normal/narrow window scaling stays centered and switches navigation.</summary>
public class WindowScalingTests
{
    private static double ReadCompactWidth(string root)
    {
        var code = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Services", "LayoutMetrics.cs"));
        var m = Regex.Match(code, @"CompactWidth\s*=\s*(\d+(?:\.\d+)?)");
        Assert.True(m.Success);
        return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Compact_breakpoint_parsed_from_code_and_used_for_iscompact()
    {
        var root = RepositoryRoot();
        var width = ReadCompactWidth(root);
        Assert.Equal(760, width);
        // The shell reports its scaled width; the view model compares the effective unscaled width (issue #477).
        var code = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "MainView.axaml.cs"));
        Assert.Contains("vm.LayoutWidth = e.NewSize.Width", code, StringComparison.Ordinal);
        // Real condition, not literals: just below switches, at and above do not.
        Assert.True(759 < width);
        Assert.False(760 < width);
        Assert.False(1180 < width);
    }

    [Fact]
    public void Navigation_switches_between_sidebar_and_bottom()
    {
        var root = RepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "MainView.axaml"));
        Assert.Contains("IsVisible=\"{Binding !IsCompact}\"", view, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsCompact}\"", view, StringComparison.Ordinal);
        // Sidebar visible when wide, bottom nav when compact.
        Assert.Contains("DesktopSidebar", view, StringComparison.Ordinal);
        Assert.Contains("navbottom", view, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxWidth_containers_are_centered_structurally()
    {
        var root = RepositoryRoot();
        XNamespace ns = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var files = new[]
        {
            Path.Combine(root, "src", "Bydlo.App", "Themes", "Styles.axaml"),
            Path.Combine(root, "src", "Bydlo.App", "Views", "ConnectionsView.axaml"),
            Path.Combine(root, "src", "Bydlo.App", "Views", "RoutingView.axaml"),
            Path.Combine(root, "src", "Bydlo.App", "Views", "MainView.axaml"),
        };
        var centered = 0;
        var pageWidths = new HashSet<string> { "880", "1016", "1040", "1080" };
        foreach (var file in files)
        {
            var doc = XDocument.Load(file);
            foreach (var el in doc.Descendants())
            {
                var maxWidth = el.Attribute("MaxWidth")?.Value;
                if (maxWidth is null || !pageWidths.Contains(maxWidth))
                {
                    continue;
                }
                var horizontal = el.Attribute("HorizontalAlignment")?.Value;
                Assert.Equal("Center", horizontal);
                centered++;
            }
        }
        Assert.True(centered >= 2);

        // Styles.axaml setters: each MaxWidth setter block must contain HorizontalAlignment Center.
        var styles = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Themes", "Styles.axaml"));
        Assert.Contains("StackPanel.page", styles, StringComparison.Ordinal);
        Assert.Contains(".desktop Border.workspace", styles, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment", styles, StringComparison.Ordinal);
        Assert.Contains("Center", styles, StringComparison.Ordinal);
        // Connection library style is setter-based.
        var connections = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "ConnectionsView.axaml"));
        Assert.Contains("Grid.connection-library", connections, StringComparison.Ordinal);
        Assert.Contains("Center", connections, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
