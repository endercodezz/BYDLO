using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using Bydlo.App.Services;
using Bydlo.Core.Import;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly ClipboardSuggestionHistory _clipboardHistory = new();
    private bool _preparingImport;
    private ImportResult? _pendingImport;
    private readonly List<string> _pendingSubscriptions = [];
    private string? _clipboardLink;
    private bool _readingClipboard;

    public ObservableCollection<string> ImportPreviewItems { get; } = [];

    public bool CanReceiveActivation => !_preparingImport && !IsImportBusy && !HasImportPreview && !IsAddServerOpen;

    [ObservableProperty] private bool _isDragOver;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseWorkspace))]
    private bool _hasImportPreview;
    [ObservableProperty] private bool _canConfirmImport;
    [ObservableProperty] private bool _isImportBusy;
    [ObservableProperty] private string? _clipboardOffer;

    public async Task CheckClipboardAsync()
    {
        if (_readingClipboard || !Services.Settings.SuggestClipboardLinks)
        {
            return;
        }

        _readingClipboard = true;
        try
        {
            var text = Services.TopLevel()?.Clipboard is { } cb ? await cb.TryGetTextAsync() : null;
            if (text?.Trim() != _clipboardLink)
            {
                DismissClipboardOffer();
            }

            if (_clipboardHistory.TryOffer(text, Services.Settings.SuggestClipboardLinks))
            {
                _clipboardLink = text!.Trim();
                var kind = ExternalImportInput.LinkKind(text);
                ClipboardOffer = Loc.T("import.clipboard.offer", kind == "subscription" ? Loc.Instance["import.subscription"] : kind!);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Clipboard may temporarily belong to another application. Retry on the next activation.
        }
        finally
        {
            _readingClipboard = false;
        }
    }

    [RelayCommand]
    private async Task AcceptClipboardOffer()
    {
        var text = _clipboardLink;
        DismissClipboardOffer();
        if (text is not null)
        {
            await PreviewExternalAsync([text]);
        }
    }

    [RelayCommand]
    public void DismissClipboardOffer()
    {
        _clipboardLink = null;
        ClipboardOffer = null;
    }

    [RelayCommand]
    private async Task PasteImport()
    {
        await Guard(async () =>
        {
            if (Services.TopLevel()?.Clipboard is not { } cb)
            {
                return;
            }

            using var bitmap = await cb.TryGetBitmapAsync();
            if (bitmap is not null)
            {
                var payload = QrDecoder.Decode(bitmap);
                if (payload is null)
                {
                    ShowToast(Loc.Instance["import.qr.none"], true);
                    return;
                }

                await PreviewExternalAsync([payload]);
                return;
            }

            var text = await cb.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowToast(Loc.Instance["import.clipboard.empty"], true);
                return;
            }

            await PreviewExternalAsync([text]);
        });
    }

    public async Task PreviewExternalAsync(IEnumerable<string> inputs)
    {
        if (_preparingImport || IsImportBusy)
        {
            return;
        }

        _preparingImport = true;
        try
        {
            var prepared = await Guard(async () =>
            {
                CancelImport();
                var profiles = new List<Bydlo.Core.Models.ImportedProfile>();
                var errors = 0;
                foreach (var input in inputs)
                {
                    if (ProfileImporter.LooksLikeSubscriptionUrl(input))
                    {
                        // Fetching happens only after confirmation. The URL never appears in the preview.
                        _pendingSubscriptions.Add(input.Trim());
                        ImportPreviewItems.Add(Loc.Instance["import.subscription"]);
                        continue;
                    }

                    ImportResult result;
                    if (ExternalImportInput.LinkKind(input) is null && ExternalImportInput.IsFile(input))
                    {
                        if (ExternalImportInput.IsImage(input))
                        {
                            var payload = QrDecoder.DecodeFile(input);
                            result = payload is null ? new ImportResult([], ["QR"]) : Services.Importer.ImportText(payload);
                            if (payload is not null && ProfileImporter.LooksLikeSubscriptionUrl(payload))
                            {
                                _pendingSubscriptions.Add(payload.Trim());
                                ImportPreviewItems.Add(Loc.Instance["import.subscription"]);
                                continue;
                            }
                        }
                        else
                        {
                            result = await Services.Importer.ImportFileAsync(input);
                        }
                    }
                    else
                    {
                        result = Services.Importer.ImportText(input);
                    }

                    profiles.AddRange(result.Profiles);
                    errors += result.Errors.Count;
                }

                _pendingImport = new ImportResult(profiles, []);
                foreach (var profile in profiles)
                {
                    ImportPreviewItems.Add($"{profile.Profile.Name} · {profile.Profile.Protocol}");
                }

                if (errors > 0)
                {
                    ImportPreviewItems.Add(Loc.T("import.skipped", errors));
                }

                if (ImportPreviewItems.Count == 0)
                {
                    ImportPreviewItems.Add(Loc.Instance["import.none"]);
                }

                CanConfirmImport = profiles.Count > 0 || _pendingSubscriptions.Count > 0;
                HasImportPreview = true;
            });
            if (!prepared)
            {
                CancelImport();
            }
        }
        finally
        {
            _preparingImport = false;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(nameof(CanReceiveActivation)));
        }
    }

    [RelayCommand]
    private void CancelImport()
    {
        HasImportPreview = false;
        CanConfirmImport = false;
        _pendingImport = null;
        _pendingSubscriptions.Clear();
        ImportPreviewItems.Clear();
    }

    [RelayCommand]
    private async Task ConfirmImport()
    {
        if (!CanConfirmImport || IsImportBusy)
        {
            return;
        }

        IsImportBusy = true;
        try
        {
            var result = _pendingImport;
            var subscriptions = _pendingSubscriptions.ToArray();
            if (await Guard(async () =>
            {
                if (result is { IsEmpty: false })
                {
                    await Connections.AddImportedAsync(result);
                    _pendingImport = null;
                }

                foreach (var subscription in subscriptions)
                {
                    await Connections.ImportExternalSubscriptionAsync(subscription);
                    _pendingSubscriptions.Remove(subscription);
                }
            }))
            {
                CancelImport();
                Navigate("connections");
            }
        }
        finally
        {
            IsImportBusy = false;
        }
    }
}
