using MudBlazor;

namespace Daleel.Web.Services;

/// <summary>
/// The Daleel MudBlazor theme. A deep indigo/teal "intelligence console" dark palette with a
/// clean light counterpart, and a font stack that puts Cairo first so Arabic renders crisply.
/// </summary>
public static class DaleelTheme
{
    public static MudTheme Build() => new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#5b8def",          // signal blue
            Secondary = "#22d3ee",        // cyan accent
            Tertiary = "#a78bfa",         // violet
            Background = "#0b0f19",       // near-black navy
            BackgroundGray = "#0f1424",
            Surface = "#141a2b",
            DrawerBackground = "#0d1220",
            DrawerText = "#cdd6f4",
            AppbarBackground = "#0d1220",
            AppbarText = "#e6edf6",
            TextPrimary = "#e6edf6",
            TextSecondary = "#9aa7bd",
            ActionDefault = "#9aa7bd",
            Success = "#34d399",
            Warning = "#fbbf24",
            Error = "#f87171",
            Info = "#60a5fa",
            LinesDefault = "#1e2740",
            TableLines = "#1e2740",
            Divider = "#1e2740",
            OverlayDark = "rgba(5,8,15,0.7)",
        },
        PaletteLight = new PaletteLight
        {
            Primary = "#2f6df6",
            Secondary = "#0891b2",
            Tertiary = "#7c3aed",
            Background = "#f6f8fc",
            Surface = "#ffffff",
            AppbarBackground = "#ffffff",
            AppbarText = "#0b0f19",
            TextPrimary = "#0b0f19",
            TextSecondary = "#52607a",
            Success = "#059669",
            Warning = "#d97706",
            Error = "#dc2626",
            Info = "#2563eb",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "12px",
            DrawerWidthLeft = "260px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = new[] { "Cairo", "Roboto", "Helvetica", "Arial", "sans-serif" }
            },
            H1 = new H1Typography { FontFamily = new[] { "Cairo", "Roboto", "sans-serif" }, FontWeight = "700" },
            H2 = new H2Typography { FontFamily = new[] { "Cairo", "Roboto", "sans-serif" }, FontWeight = "700" },
            H3 = new H3Typography { FontFamily = new[] { "Cairo", "Roboto", "sans-serif" }, FontWeight = "700" },
            H4 = new H4Typography { FontFamily = new[] { "Cairo", "Roboto", "sans-serif" }, FontWeight = "600" },
            H5 = new H5Typography { FontFamily = new[] { "Cairo", "Roboto", "sans-serif" }, FontWeight = "600" },
            H6 = new H6Typography { FontFamily = new[] { "Cairo", "Roboto", "sans-serif" }, FontWeight = "600" },
        }
    };
}
