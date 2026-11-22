using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Bydlo.App.Services;
using Bydlo.Core.Import;
using Bydlo.App.ViewModels;

namespace Bydlo.App.Views;

public sealed partial class MainView : UserControl
{
    /// <summary>Below this width the sidebar collapses into bottom navigation (phones, narrow windows).</summary>
    public const double CompactWidth = LayoutMetrics.CompactWidth;

    public MainView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPasteKey, RoutingStrategies.Bubble);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(RoutingView.RuleDragFormat))
        {
            return;
        }

        var supported = e.DataTransfer.TryGetFiles()?.Any(f => ExternalImportInput.IsFile(f.Name)) == true;
        e.DragEffects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        if (DataContext is MainViewModel vm)
        {
            vm.IsDragOver = supported;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsDragOver = false;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Formats.Contains(RoutingView.RuleDragFormat))
        {
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.IsDragOver = false;
            var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath())
                .OfType<string>().Where(ExternalImportInput.IsFile).ToArray() ?? [];
            e.Handled = true;
            if (paths.Length > 0)
            {
                if (vm.IsAddServerOpen) { await vm.AddServerWizard.PreviewFilesAsync(paths); }
                else { await vm.PreviewExternalAsync(paths); }
            }
        }
    }

    private async void OnPasteKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel { IsAddServerOpen: true } shell)
        {
            shell.AddServerWizard.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && DataContext is MainViewModel { IsChainEditorOpen: true } chainShell)
        {
            chainShell.ChainEditor.CloseCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Text boxes retain their normal paste behavior. The import button also accepts QR images.
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (!e.Handled && e.Key == Key.V && e.KeyModifiers == modifier && DataContext is MainViewModel vm)
        {
            e.Handled = true;
            if (vm.IsAddServerOpen) { await vm.AddServerWizard.PasteCommand.ExecuteAsync(null); }
            else { await vm.PasteImportCommand.ExecuteAsync(null); }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is MainViewModel vm)
        {
            vm.Services.TopLevel = () => TopLevel.GetTopLevel(this);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (DataContext is MainViewModel vm)
        {
            // The interface scale shrinks the layout width, so the shell reports its scaled width
            // and the view model compares the effective unscaled width (issue #477).
            vm.LayoutWidth = e.NewSize.Width;
        }
    }
}
