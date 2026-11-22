using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Bydlo.App.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    /// <summary>
    /// Closes the "Add server" menu after the chosen import flow starts. The hide is deferred so the
    /// command binding is still alive when the menu item finishes its click.
    /// </summary>
    private void OnImportSourceClick(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => AddServerEntry.Flyout?.Hide(), DispatcherPriority.Background);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var stacked = e.NewSize.Width < 680;
        StatusTitle.FontSize = stacked ? 24 : 32;
        Grid.SetColumn(SessionAction, stacked ? 0 : 1);
        SessionControls.ColumnSpacing = stacked ? 0 : 16;
        Grid.SetRow(SessionAction, stacked ? 1 : 0);
        SessionAction.Margin = stacked ? new Thickness(0, 12, 0, 0) : default;

        Grid.SetColumn(ConnectionScope, stacked ? 0 : 1);
        ConnectionFields.ColumnSpacing = stacked ? 0 : 24;
        Grid.SetRow(ConnectionScope, stacked ? 1 : 0);
        ConnectionScope.Margin = stacked ? new Thickness(0, 10, 0, 0) : default;
    }
}
