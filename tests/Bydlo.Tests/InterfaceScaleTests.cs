using System.Text.Json;
using Bydlo.App.Services;
using Bydlo.App.Views;
using Bydlo.Core.Backup;
using Bydlo.Core.Models;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

/// <summary>Issue #477: interface scale setting, persistence, backup validation and the navigation breakpoint.</summary>
public class InterfaceScaleTests
{
    [Fact]
    public void Fresh_settings_use_100_percent()
    {
        var settings = new AppSettings();
        Assert.Equal(100, settings.InterfaceScale);
        Assert.Equal(1.0, settings.InterfaceScaleFactor);
        Assert.Equal(AppSettings.DefaultInterfaceScale, settings.InterfaceScale);
    }

    [Fact]
    public void Selectable_steps_cover_the_supported_range()
    {
        var steps = AppSettings.InterfaceScaleSteps;
        Assert.Equal(AppSettings.MinInterfaceScale, steps[0]);
        Assert.Equal(AppSettings.MaxInterfaceScale, steps[^1]);
        Assert.Contains(AppSettings.DefaultInterfaceScale, steps);
        Assert.All(steps, step => Assert.Equal(0, (step - AppSettings.MinInterfaceScale) % AppSettings.InterfaceScaleStep));
        Assert.Equal(steps.Count, steps.Distinct().Count());
        Assert.Equal(steps.Order(), steps);
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(-40, 80)]
    [InlineData(79, 80)]
    [InlineData(80, 80)]
    [InlineData(84, 80)]
    [InlineData(85, 90)]
    [InlineData(100, 100)]
    [InlineData(104, 100)]
    [InlineData(105, 110)]
    [InlineData(130, 130)]
    [InlineData(149, 150)]
    [InlineData(150, 150)]
    [InlineData(151, 150)]
    [InlineData(1000, 150)]
    public void Out_of_range_values_are_clamped_and_snapped(int stored, int expected)
    {
        Assert.Equal(expected, AppSettings.ClampInterfaceScale(stored));
        Assert.Contains(expected, AppSettings.InterfaceScaleSteps);
    }

    [Fact]
    public void Normalize_repairs_a_hand_edited_value()
    {
        var settings = new AppSettings { InterfaceScale = 999 };
        Assert.True(settings.NormalizeInterfaceScale());
        Assert.Equal(150, settings.InterfaceScale);
        Assert.False(settings.NormalizeInterfaceScale());

        settings.InterfaceScale = 0;
        Assert.True(settings.NormalizeInterfaceScale());
        Assert.Equal(80, settings.InterfaceScale);
    }

    [Fact]
    public async Task Scale_survives_a_settings_round_trip()
    {
        var path = Path.Combine(TestDirs.New("interface-scale"), "settings.json");
        await new SettingsStore(path).SaveAsync(new AppSettings { InterfaceScale = 130 });
        var reloaded = await new SettingsStore(path).LoadAsync();
        Assert.Equal(130, reloaded.InterfaceScale);
        Assert.Equal(1.3, reloaded.InterfaceScaleFactor, 3);
    }

    [Fact]
    public async Task Loading_repairs_an_unsupported_stored_scale()
    {
        var path = Path.Combine(TestDirs.New("interface-scale-repair"), "settings.json");
        await File.WriteAllTextAsync(path, """{"InterfaceScale": 500}""");
        var reloaded = await new SettingsStore(path).LoadAsync();
        Assert.Equal(AppSettings.MaxInterfaceScale, reloaded.InterfaceScale);
    }

    [Fact]
    public async Task Settings_without_the_field_keep_the_default()
    {
        var path = Path.Combine(TestDirs.New("interface-scale-legacy"), "settings.json");
        await File.WriteAllTextAsync(path, """{"LocalProxyPort": 2080}""");
        var reloaded = await new SettingsStore(path).LoadAsync();
        Assert.Equal(AppSettings.DefaultInterfaceScale, reloaded.InterfaceScale);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(150)]
    public void Backup_validation_accepts_supported_scales(int scale)
    {
        var data = new BackupData();
        data.Settings.InterfaceScale = scale;
        BackupArchive.Validate(data);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(79)]
    [InlineData(151)]
    [InlineData(1000)]
    public void Backup_validation_rejects_out_of_range_scales(int scale)
    {
        var data = new BackupData();
        data.Settings.InterfaceScale = scale;
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(data));
    }

    [Fact]
    public void Backup_import_copies_the_scale()
    {
        var data = new BackupData();
        data.Settings.InterfaceScale = 120;
        var restored = BackupArchive.ImportSettings(data, new AppSettings { InterfaceScale = 80 }, BackupImportMode.Replace);
        Assert.Equal(120, restored.InterfaceScale);
    }

    [Fact]
    public void Backup_import_snaps_an_unsupported_scale()
    {
        var data = new BackupData();
        data.Settings.InterfaceScale = 145;
        var restored = BackupArchive.ImportSettings(data, new AppSettings(), BackupImportMode.Merge);
        Assert.Equal(150, restored.InterfaceScale);
    }

    [Fact]
    public void Backup_round_trip_keeps_the_scale()
    {
        var data = new BackupData();
        data.Settings.InterfaceScale = 90;
        var decrypted = BackupArchive.Decrypt(BackupArchive.Encrypt(data, "correct horse battery staple"), "correct horse battery staple");
        Assert.Equal(90, decrypted.Settings.InterfaceScale);
    }

    [Theory]
    [InlineData(1180, 1.0, false)]
    [InlineData(760, 1.0, false)]
    [InlineData(759, 1.0, true)]
    [InlineData(1180, 1.5, false)]
    [InlineData(1140, 1.5, false)]
    [InlineData(1139, 1.5, true)]
    [InlineData(1180, 0.8, false)]
    [InlineData(608, 0.8, false)]
    [InlineData(607, 0.8, true)]
    public void Navigation_switch_uses_the_effective_unscaled_width(double windowWidth, double scale, bool compact)
    {
        var effective = LayoutMetrics.EffectiveWidth(windowWidth, scale);
        Assert.Equal(compact, LayoutMetrics.IsCompact(effective));
    }

    [Fact]
    public void Effective_width_ignores_a_broken_scale()
    {
        Assert.Equal(900, LayoutMetrics.EffectiveWidth(900, 0));
        Assert.Equal(900, LayoutMetrics.EffectiveWidth(900, double.NaN));
        Assert.Equal(900, LayoutMetrics.EffectiveWidth(900, double.PositiveInfinity));
    }

    [Fact]
    public void Shell_recomputes_the_navigation_switch_when_the_scale_changes()
    {
        // 1180 at 100% is a sidebar layout; the same window at 150% leaves 786 unscaled units,
        // and 1140 at 150% lands exactly on the breakpoint.
        Assert.False(LayoutMetrics.IsCompact(LayoutMetrics.EffectiveWidth(1180, 1.0)));
        Assert.False(LayoutMetrics.IsCompact(LayoutMetrics.EffectiveWidth(1180, 1.5)));
        Assert.True(LayoutMetrics.IsCompact(LayoutMetrics.EffectiveWidth(1100, 1.5)));
        Assert.Equal(MainView.CompactWidth, LayoutMetrics.CompactWidth);
    }

    [Fact]
    public void Shell_applies_the_scale_as_a_layout_transform()
    {
        var root = RepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "MainView.axaml"));
        Assert.Contains("<LayoutTransformControl LayoutTransform=\"{Binding InterfaceScaleTransform}\">", view, StringComparison.Ordinal);
        Assert.Contains("</LayoutTransformControl>", view, StringComparison.Ordinal);
        // The transform must wrap the whole shell, not a single page.
        Assert.True(view.IndexOf("<LayoutTransformControl", StringComparison.Ordinal) <
            view.IndexOf("<Grid RowDefinitions=\"Auto,Auto,*,Auto\"", StringComparison.Ordinal));

        var code = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "MainView.axaml.cs"));
        Assert.Contains("vm.LayoutWidth = e.NewSize.Width", code, StringComparison.Ordinal);
        Assert.DoesNotContain("vm.IsCompact = e.NewSize.Width", code, StringComparison.Ordinal);

        var shell = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "ViewModels", "MainViewModel.cs"));
        Assert.Contains("public ITransform InterfaceScaleTransform => new ScaleTransform(", shell, StringComparison.Ordinal);
        Assert.Contains("LayoutMetrics.IsCompact(LayoutMetrics.EffectiveWidth(LayoutWidth, InterfaceScaleFactor))", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Appearance_page_offers_the_scale_and_a_reset()
    {
        var root = RepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "AppearanceSettingsView.axaml"));
        Assert.Contains("ItemsSource=\"{x:Static vm:SettingsViewModel.InterfaceScales}\"", view, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding InterfaceScale}\"", view, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ResetInterfaceScaleCommand}\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Scale_strings_exist_in_both_languages()
    {
        var root = RepositoryRoot();
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "en.json")))!;
        var ru = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "ru.json")))!;
        string[] keys = ["settings.scale", "settings.scale.hint", "settings.scale.reset", "settings.scale.reset.done"];
        foreach (var key in keys)
        {
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english), "Missing en: " + key);
            Assert.True(ru.TryGetValue(key, out var russian) && !string.IsNullOrWhiteSpace(russian), "Missing ru: " + key);
        }
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
