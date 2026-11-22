using Bydlo.Core.Diagnostics;

namespace Bydlo.App.Services;

/// <summary>
/// Manual update instructions for copies the built-in updater does not own (issue #413).
/// A package-manager install is updated by that manager; a portable copy is replaced by hand.
/// </summary>
public static class UpdateGuidance
{
    public static string KeyFor(AppInstallKind kind) => KeyFor(kind, false);

    /// <summary>
    /// A Debian install with a configured BYDLO apt source is updated by apt itself, so the text must
    /// say <c>apt upgrade</c> instead of "download the .deb again" (issue #488).
    /// </summary>
    public static string KeyFor(AppInstallKind kind, bool aptRepositoryConfigured) => kind switch
    {
        AppInstallKind.Debian when aptRepositoryConfigured => "settings.update.manual.deb.repository",
        AppInstallKind.Debian => "settings.update.manual.deb",
        AppInstallKind.Arch => "settings.update.manual.arch",
        AppInstallKind.Nix => "settings.update.manual.nix",
        AppInstallKind.Portable => "settings.update.manual.portable",
        _ => "settings.update.manual.unknown",
    };
}
