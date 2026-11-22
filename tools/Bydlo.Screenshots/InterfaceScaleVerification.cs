using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;

/// <summary>
/// Issue #477: the interface scale is one layout transform at the shell root, so the content keeps
/// working in unscaled units and the navigation breakpoint does not move with the setting.
/// Runs in Mock Mode only; no engine, no network.
/// </summary>
internal static class InterfaceScaleVerification
{
    public static void Run(MainViewModel shell, string output)
    {
        shell.FinishOnboardingCommand.Execute(null);
        Wait(shell.Connections.ImportTextAsync("trojan://pw@scale.example.invalid:443#Scale"));
        Require(shell.Profiles.Count >= 1, "A mock profile is listed");

        foreach (var scale in new[] { 80, 100, 150 })
        {
            shell.Settings.InterfaceScale = scale;
            Pump();
            Require(shell.InterfaceScale == scale, "The setting reaches the shell: " + scale);
            Require(shell.Services.Settings.InterfaceScale == scale, "The setting is persisted: " + scale);

            var window = Open(shell, 1180, 780);
            var content = Content(window);
            var expected = window.ClientSize.Width / (scale / 100.0);
            Require(Math.Abs(content.Bounds.Width - expected) < 2,
                $"The layout transform divides the width at {scale}%: {content.Bounds.Width:0.#} vs {expected:0.#}");
            Require(!shell.IsCompact, "A 1180-wide window keeps the sidebar at " + scale + "%");
            Require(ConnectAction(window).IsEffectivelyVisible, "The connect action stays visible at " + scale + "%");
            Shoot(window, Path.Combine(output, $"interface-scale-{scale}.png"));
            window.Close();
        }

        // The breakpoint compares the unscaled width: 1140 / 1.5 = 760 keeps the sidebar, 1139 switches.
        shell.Settings.InterfaceScale = 150;
        Pump();
        var wide = Open(shell, 1140, 780);
        Require(!shell.IsCompact, "1140 at 150% is exactly the breakpoint and keeps the sidebar");
        wide.Close();
        var narrow = Open(shell, 1139, 780);
        Require(shell.IsCompact, "1139 at 150% switches to bottom navigation");
        Require(BottomNavigation(narrow).IsEffectivelyVisible, "Bottom navigation is visible below the breakpoint");
        Shoot(narrow, Path.Combine(output, "interface-scale-150-compact.png"));
        narrow.Close();

        // 80% must not switch earlier than 100%: the same window keeps the sidebar at both scales.
        shell.Settings.InterfaceScale = 80;
        Pump();
        var small = Open(shell, 700, 780);
        Require(!shell.IsCompact, "700 at 80% leaves 875 unscaled units and keeps the sidebar");
        small.Close();

        // The appearance page must offer the scale and the reset at the largest scale, keyboard included.
        shell.Settings.InterfaceScale = 150;
        shell.Navigate("settings");
        Pump();
        var settings = Open(shell, 1180, 780);
        var picker = settings.GetVisualDescendants().OfType<ComboBox>()
            .Single(combo => ReferenceEquals(combo.ItemsSource, SettingsViewModel.InterfaceScales));
        Require(picker.IsEffectivelyVisible && picker.Focusable, "The scale picker is visible and focusable");
        Require(Equals(picker.SelectedItem, 150), "The scale picker shows the saved value");
        var reset = settings.GetVisualDescendants().OfType<Button>()
            .Single(button => ReferenceEquals(button.Command, shell.Settings.ResetInterfaceScaleCommand));
        Require(reset.IsEffectivelyVisible && reset.Focusable, "The reset button is visible and focusable");
        Shoot(settings, Path.Combine(output, "interface-scale-150-settings.png"));
        settings.Close();

        shell.Settings.ResetInterfaceScaleCommand.Execute(null);
        Pump();
        Require(shell.InterfaceScale == 100, "Reset returns the shell to 100%");
        Require(shell.Services.Settings.InterfaceScale == 100, "Reset persists 100%");

        Console.WriteLine("Interface scale verification passed: layout transform, breakpoint and reset (Mock Mode, no network).");
    }

    private static Window Open(MainViewModel shell, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = new MainView { DataContext = shell } };
        window.Show();
        Pump();
        return window;
    }

    private static Control Content(Window window) =>
        window.GetVisualDescendants().OfType<LayoutTransformControl>().Single().Child
            ?? throw new InvalidOperationException("The shell root has no scaled content.");

    private static Button ConnectAction(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("connect-action"));

    private static RadioButton BottomNavigation(Window window) =>
        window.GetVisualDescendants().OfType<RadioButton>().First(button => button.Classes.Contains("navbottom"));

    private static void Shoot(Window window, string path)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame: " + path);
        frame.Save(path);
        Console.WriteLine("saved " + Path.GetFileName(path));
    }

    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }

    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Require(task.IsCompleted, "Operation completes");
        task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
