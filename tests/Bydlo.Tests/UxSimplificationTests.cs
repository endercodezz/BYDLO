using System.Xml.Linq;

namespace Bydlo.Tests;

/// <summary>Issue #436: one primary action per screen, secondary actions behind one menu.</summary>
public class UxSimplificationTests
{
    private static readonly XNamespace Ns = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ImportSources = ["link", "file", "qr", "clipboard", "subscription", "catalog", "manual"];

    [Fact]
    public void Home_empty_state_has_one_add_server_entry()
    {
        var entry = AddServerEntry();
        Assert.Equal("primary", (string?)entry.Attribute("Classes"));
        Assert.Equal("{l:T wizard.title}", (string?)entry.Attribute("AutomationProperties.Name"));
        Assert.NotNull(entry.Element(Ns + "Button.Flyout"));
    }

    [Fact]
    public void Home_import_sources_live_inside_the_add_server_menu()
    {
        var menu = AddServerEntry().Element(Ns + "Button.Flyout")!.Element(Ns + "Flyout")!.Element(Ns + "StackPanel")!;
        var items = menu.Elements(Ns + "Button").ToArray();
        Assert.Equal(ImportSources, items.Select(item => (string)item.Attribute("CommandParameter")!).ToArray());
        Assert.All(items, item =>
        {
            Assert.Equal("{Binding Shell.OpenImportCommand}", (string?)item.Attribute("Command"));
            Assert.Equal("menu-item", (string?)item.Attribute("Classes"));
        });
    }

    [Fact]
    public void Home_has_no_directly_visible_import_shortcuts()
    {
        var shortcuts = Load("HomeView.axaml").Descendants(Ns + "Button")
            .Where(button => (string?)button.Attribute("Command") == "{Binding Shell.OpenImportCommand}")
            .ToArray();
        Assert.NotEmpty(shortcuts);
        Assert.All(shortcuts, button => Assert.Contains(button.Ancestors(), ancestor => ancestor.Name == Ns + "Flyout"));

        var view = File.ReadAllText(Path.Combine(Root(), "src", "Bydlo.App", "Views", "HomeView.axaml"));
        Assert.DoesNotContain("home.import.options", view, StringComparison.Ordinal);
        Assert.DoesNotContain("empty-actions", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_keeps_the_primary_connection_action_and_server_picker()
    {
        var view = File.ReadAllText(Path.Combine(Root(), "src", "Bydlo.App", "Views", "HomeView.axaml"));
        Assert.Contains("Classes=\"primary connect-action\"", view, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ToggleConnectionCommand}\"", view, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding Shell.SelectedProfile}\"", view, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding StatusText}\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_and_chain_cards_show_three_actions()
    {
        var actions = Load("ConnectionsView.axaml").Descendants(Ns + "StackPanel")
            .Where(panel => (string?)panel.Attribute("Classes") == "profile-actions")
            .ToArray();
        Assert.Equal(2, actions.Length);
        Assert.All(actions, panel => Assert.Equal(3, panel.Elements(Ns + "Button").Count()));
    }

    [Fact]
    public void Server_delete_stays_a_separate_confirmed_action()
    {
        var buttons = Load("ConnectionsView.axaml").Descendants(Ns + "Button").ToArray();
        foreach (var (name, command) in new[]
                 {
                     ("{l:T connections.delete}", "DeleteCommand"),
                     ("{l:T chain.delete}", "DeleteChainCommand"),
                 })
        {
            var delete = buttons.Single(button => (string?)button.Attribute("AutomationProperties.Name") == name);
            Assert.Null(delete.Attribute("Command"));
            var confirm = delete.Element(Ns + "Button.Flyout")!.Element(Ns + "Flyout")!
                .Descendants(Ns + "Button").Single();
            Assert.Contains(command, (string)confirm.Attribute("Command")!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Favorite_and_edit_actions_moved_into_the_card_menu()
    {
        var menuItems = Load("ConnectionsView.axaml").Descendants(Ns + "Button")
            .Where(button => (string?)button.Attribute("Classes") == "menu-item")
            .ToArray();
        Assert.NotEmpty(menuItems);
        Assert.All(menuItems, item => Assert.Contains(item.Ancestors(), ancestor => ancestor.Name == Ns + "Flyout"));
        Assert.Contains(menuItems, item => (string?)item.Attribute("Content") == "{l:T chain.edit}");
        Assert.Contains(menuItems, item => (string?)item.Attribute("AutomationProperties.Name") == "{l:T connections.favorite}");
        Assert.Contains(menuItems, item => (string?)item.Attribute("Content") == "{l:T profile.edit.title}");
    }

    [Fact]
    public void Connections_has_one_add_entry_and_no_duplicate_catalog_button()
    {
        var buttons = Load("ConnectionsView.axaml").Descendants(Ns + "Button").ToArray();
        Assert.DoesNotContain(buttons, button => (string?)button.Attribute("CommandParameter") == "catalog");
        Assert.Contains(buttons, button => (string?)button.Attribute("Command") == "{Binding Shell.OpenAddServerCommand}");
    }

    [Fact]
    public void Import_command_keeps_every_source_parameter()
    {
        var vm = File.ReadAllText(Path.Combine(Root(), "src", "Bydlo.App", "ViewModels", "MainViewModel.cs"));
        foreach (var source in new[] { "catalog", "file", "qr", "manual", "subscription" })
        {
            Assert.Contains($"case \"{source}\":", vm, StringComparison.Ordinal);
        }

        // "link" and "clipboard" stay on the default branch.
        Assert.Contains("default:", vm, StringComparison.Ordinal);
        Assert.Contains("source == \"clipboard\"", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_menu_labels_exist_in_both_languages()
    {
        var keys = new[]
        {
            "wizard.title", "wizard.manual", "import.link.title", "home.import.file", "home.import.qr",
            "home.import.clipboard", "home.import.subscription", "home.import.drop", "catalog.title",
            "connections.actions", "connections.favorite", "chain.actions",
        };
        foreach (var language in new[] { "en", "ru" })
        {
            var json = File.ReadAllText(Path.Combine(Root(), "src", "Bydlo.App", "Assets", "Strings", language + ".json"));
            foreach (var key in keys)
            {
                Assert.Contains($"\"{key}\"", json, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Inventory_document_lists_the_moved_buttons()
    {
        var doc = File.ReadAllText(Path.Combine(Root(), "docs", "UX-SIMPLIFICATION.md"));
        Assert.Contains("Публичные серверы", doc, StringComparison.Ordinal);
        Assert.Contains("Другие способы импорта", doc, StringComparison.Ordinal);
        Assert.Contains("избранное", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("удаление", doc, StringComparison.OrdinalIgnoreCase);
    }

    private static XElement AddServerEntry() => Load("HomeView.axaml").Descendants(Ns + "Button")
        .Single(button => (string?)button.Attribute(X + "Name") == "AddServerEntry");

    private static XDocument Load(string view) => XDocument.Load(Path.Combine(Root(), "src", "Bydlo.App", "Views", view));

    private static string Root()
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
