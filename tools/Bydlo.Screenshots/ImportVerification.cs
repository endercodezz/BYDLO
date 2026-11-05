using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using ZXing;

internal static class ImportVerification
{
    // Uses a headless clipboard, fake share links and isolated appdata. Never starts any engine.
    public static void Run(MainViewModel shell, string output)
    {
        const string link = "vless://11111111-2222-3333-4444-555555555555@example.invalid:443#Import%20test";
        Bydlo.App.App.Args = ["--mock", link];
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var count = shell.Profiles.Count;
        Require(shell.HasImportPreview && shell.CanConfirmImport, "Launch arguments open preview");
        shell.CancelImportCommand.Execute(null);

        if (OperatingSystem.IsWindows())
        {
            var scope = Path.Combine(output, "ipc-" + Guid.NewGuid().ToString("N"));
            using var primary = new Bydlo.Desktop.WindowsActivationBroker(scope, Bydlo.App.App.EnqueueActivation);
            Require(primary.IsPrimary, "First activation owns the instance");
            primary.Listen();
            Wait(Task.Run(() =>
            {
                using var secondary = new Bydlo.Desktop.WindowsActivationBroker(scope, Bydlo.App.App.EnqueueActivation);
                Require(!secondary.IsPrimary, "Second launch forwards instead of starting another app");
                secondary.Forward(["--mock", link, "ignored.exe"]);
            }));
            WaitUntil(() => shell.HasImportPreview && shell.CanConfirmImport, "Forwarded arguments reach existing window");
            Capture("import-activation", 1180, 780);
            Require(shell.HasImportPreview && shell.CanConfirmImport, "Forwarded arguments reach existing window");
            Bydlo.App.App.EnqueueActivation([link + "queued"]);
            Capture("import-activation-queued", 1180, 780);
            Require(Bydlo.App.App.PendingActivations.Count == 1, "Active preview preserves queued requests");
            shell.CancelImportCommand.Execute(null);
            WaitUntil(() => shell.HasImportPreview && Bydlo.App.App.PendingActivations.IsEmpty, "Queued request opens after cancel");
            Capture("import-activation-next", 1180, 780);
            Require(shell.HasImportPreview && Bydlo.App.App.PendingActivations.IsEmpty, "Queued request opens after cancel");
            shell.CancelImportCommand.Execute(null);
            for (var i = 0; i < 20; i++)
            {
                Wait(Task.Run(() =>
                {
                    using var secondary = new Bydlo.Desktop.WindowsActivationBroker(scope, Bydlo.App.App.EnqueueActivation);
                    Require(!secondary.IsPrimary, "Primary remains active across repeated launches");
                    secondary.Forward([link]);
                }));
                WaitUntil(() => shell.HasImportPreview && shell.CanConfirmImport, "Repeated activation is delivered");
                Require(Bydlo.App.App.PendingActivations.IsEmpty, "Each activation is delivered exactly once");
                shell.CancelImportCommand.Execute(null);
            }
        }

        Wait(shell.PreviewExternalAsync([link]));
        Require(shell.HasImportPreview && shell.CanConfirmImport, "Share-link preview");
        Require(shell.Profiles.Count == count, "Preview must not persist profiles");
        Require(shell.ImportPreviewItems.All(x => !x.Contains("11111111", StringComparison.Ordinal)), "Preview hides credentials");
        Capture("import-preview", 1180, 780);
        Capture("import-preview-compact", 400, 700);
        shell.CancelImportCommand.Execute(null);
        Require(!shell.HasImportPreview && shell.Profiles.Count == count, "Cancel does not import");

        Wait(shell.PreviewExternalAsync(["https://example.invalid/sub?token=secret", "https://example.invalid/sub2?token=secret"]));
        Require(shell.ImportPreviewItems.Count == 2 && shell.CanConfirmImport, "Multiple subscriptions preview without network");
        Require(shell.Services.Profiles.Subscriptions.Count == 0, "Preview must not create subscriptions");
        shell.CancelImportCommand.Execute(null);

        Wait(shell.PreviewExternalAsync([link + ".conf"]));
        Require(shell.CanConfirmImport, "Links ending in file extensions are still links");
        shell.CancelImportCommand.Execute(null);

        var config = Path.Combine(output, "import-test.ovpn");
        File.WriteAllText(config, "client\nremote example.invalid 1194\nproto udp\ndev tun\n");
        Wait(shell.PreviewExternalAsync([config, config]));
        Require(shell.ImportPreviewItems.Count == 2 && shell.CanConfirmImport, "Multiple dropped config paths");
        shell.CancelImportCommand.Execute(null);

        Wait(shell.PreviewExternalAsync(["unsupported"]));
        Require(shell.HasImportPreview && !shell.CanConfirmImport, "Unsupported input cannot be confirmed");
        shell.CancelImportCommand.Execute(null);

        var clipboard = window.Clipboard!;
        foreach (var url in new[] { "https://example.org/docs", "https://example.org/?lang=ru", "https://example.invalid/sub?token=secret" })
        {
            Wait(clipboard.SetTextAsync(url));
            Wait(shell.CheckClipboardAsync());
            Require(shell.ClipboardOffer is null && !shell.HasImportPreview, "Web URLs do not trigger clipboard offers");
        }
        Wait(clipboard.SetTextAsync(link));
        Wait(shell.CheckClipboardAsync());
        Require(shell.ClipboardOffer is not null && !shell.ClipboardOffer.Contains("11111111", StringComparison.Ordinal), "Safe clipboard banner");
        Capture("import-clipboard", 1180, 780);
        shell.DismissClipboardOfferCommand.Execute(null);
        Wait(shell.CheckClipboardAsync());
        Require(shell.ClipboardOffer is null, "Dismissed links are not reoffered");
        shell.Settings.SuggestClipboardLinks = false;
        Wait(clipboard.SetTextAsync(link + "2"));
        Wait(shell.CheckClipboardAsync());
        Require(shell.ClipboardOffer is null, "Disabled clipboard suggestions");
        shell.Settings.SuggestClipboardLinks = true;
        Wait(shell.CheckClipboardAsync());
        Require(shell.ClipboardOffer is not null, "Reenabled clipboard suggestions");
        Wait(shell.AcceptClipboardOfferCommand.ExecuteAsync(null));
        Require(shell.HasImportPreview && shell.ClipboardOffer is null, "Banner acceptance opens preview");
        shell.CancelImportCommand.Execute(null);

        Wait(clipboard.SetTextAsync(link + "3"));
        Wait(shell.CheckClipboardAsync());
        Require(shell.ClipboardOffer is not null, "New protocol link opens another offer");
        Wait(clipboard.SetTextAsync("https://example.org/docs"));
        Wait(shell.CheckClipboardAsync());
        Require(shell.ClipboardOffer is null, "Web URL clears the previous protocol offer");
        Wait(shell.PasteImportCommand.ExecuteAsync(null));
        Require(shell.HasImportPreview && shell.CanConfirmImport, "Explicit paste still accepts arbitrary subscription URLs");
        shell.CancelImportCommand.Execute(null);

        using var bitmap = MakeQr(link);
        Wait(clipboard.SetBitmapAsync(bitmap));
        Wait(shell.PasteImportCommand.ExecuteAsync(null));
        Require(shell.HasImportPreview && shell.CanConfirmImport, "Clipboard QR image decoded");
        Require(shell.Profiles.Count == count, "QR paste requires confirmation");
        Wait(shell.ConfirmImportCommand.ExecuteAsync(null));
        Require(!shell.HasImportPreview && shell.Profiles.Count == count + 1, "Confirm persists profile");

        shell.IsDragOver = true;
        Capture("import-drop", 1180, 780);
        shell.IsDragOver = false;
        shell.Navigate("settings");
        Capture("import-settings", 1180, 900);
        Capture("import-settings-compact", 400, 860);
        window.Close();
        Console.WriteLine("Import verification passed: preview/cancel/confirm, files, subscriptions, clipboard dedup/settings, QR image.");

        void Capture(string name, double width, double height)
        {
            window.Width = width;
            window.Height = height;
            using var renderTime = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            Dispatcher.UIThread.MainLoop(renderTime.Token);

            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(output, name + ".png"));
        }
    }

    internal static WriteableBitmap MakeQr(string payload)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions { Width = 480, Height = 480, Margin = 4 },
        };
        var pixels = writer.Write(payload);
        var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        Marshal.Copy(pixels.Pixels, 0, buffer.Address, pixels.Pixels.Length);
        return bitmap;
    }

    private static void Wait(Task task)
    {
        WaitUntil(() => task.IsCompleted, "Async operation completes");
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        if (condition())
        {
            return;
        }

        // RunJobs deliberately ignores pending OS events, including the Windows dispatcher
        // dispatcher callbacks used by MainWindow to consume activations. Pump a real dispatcher frame.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        poll.Tick += (_, _) =>
        {
            if (condition())
            {
                timeout.Cancel();
            }
        };
        poll.Start();
        try
        {
            Dispatcher.UIThread.MainLoop(timeout.Token);
        }
        finally
        {
            poll.Stop();
        }

        Require(condition(), message + " (15s timeout)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Import verification failed: " + message);
        }
    }
}
