namespace Bydlo.App.Services;

/// <summary>
/// Layout math for the interface scale (issue #477). The scale is applied once by a
/// <c>LayoutTransformControl</c> at the root of the shell, so every control keeps working in
/// unscaled layout units; only the width available to the shell shrinks as the scale grows.
/// </summary>
public static class LayoutMetrics
{
    /// <summary>Below this unscaled width the sidebar collapses into bottom navigation (phones, narrow windows).</summary>
    public const double CompactWidth = 760;

    /// <summary>
    /// Unscaled layout width of a shell that occupies <paramref name="scaledWidth"/> at
    /// <paramref name="scale"/>. The navigation switch compares this value, so the breakpoint
    /// stays at the same physical size for every interface scale.
    /// </summary>
    public static double EffectiveWidth(double scaledWidth, double scale) =>
        scale > 0 && double.IsFinite(scale) ? scaledWidth / scale : scaledWidth;

    /// <summary>True when the sidebar must collapse into bottom navigation.</summary>
    public static bool IsCompact(double effectiveWidth) => effectiveWidth < CompactWidth;
}
