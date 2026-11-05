using Avalonia.Controls;
using Avalonia.Threading;
using Bydlo.App.ViewModels;
using Bydlo.Core.Import;

namespace Bydlo.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var processingActivation = false;
        async void ProcessActivations()
        {
            if (processingActivation) { return; }
            processingActivation = true;
            try
            {
                while (DataContext is MainViewModel { CanReceiveActivation: true } vm &&
                    App.PendingActivations.TryDequeue(out var inputs))
                {
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                    if (inputs.Length > 0)
                    {
                        await vm.PreviewExternalAsync(inputs);
                    }
                }
            }
            finally { processingActivation = false; }
        }
        void RequestActivation() => Dispatcher.UIThread.Post(ProcessActivations);
        void ViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!App.PendingActivations.IsEmpty) { RequestActivation(); }
        }
        MainViewModel? observed = null;
        Opened += (_, _) =>
        {
            observed = DataContext as MainViewModel;
            if (observed is not null) { observed.PropertyChanged += ViewModelChanged; }
            App.ActivationRequested += RequestActivation;
            RequestActivation();
        };
        Closed += (_, _) =>
        {
            App.ActivationRequested -= RequestActivation;
            if (observed is not null) { observed.PropertyChanged -= ViewModelChanged; }
        };
        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                var inputs = ExternalImportInput.LaunchInputs(App.Args).ToArray();
                if (inputs.Length > 0)
                {
                    await vm.PreviewExternalAsync(inputs);
                }

                await vm.CheckClipboardAsync();
            }
        };
        Activated += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                await vm.CheckClipboardAsync();
            }
        };
    }
}
