namespace Daleel.Web.Services;

/// <summary>
/// Shared, per-circuit UI state for theme (dark/light) and text direction (LTR/RTL). The
/// <see cref="MainLayout"/> owns the <c>MudThemeProvider</c> and re-renders on <see cref="Changed"/>;
/// the theme toggle, the Settings page, and result views all read/update this one source of truth.
/// </summary>
public sealed class LayoutState
{
    /// <summary>Dark by default — this is a media-intelligence console.</summary>
    public bool IsDarkMode { get; private set; } = true;

    /// <summary>Whole-UI right-to-left mirroring (for Arabic-first users).</summary>
    public bool RightToLeft { get; private set; }

    /// <summary>Raised whenever theme or direction changes.</summary>
    public event Action? Changed;

    public void SetDarkMode(bool value)
    {
        if (IsDarkMode == value)
        {
            return;
        }

        IsDarkMode = value;
        Changed?.Invoke();
    }

    public void ToggleDarkMode() => SetDarkMode(!IsDarkMode);

    public void SetRightToLeft(bool value)
    {
        if (RightToLeft == value)
        {
            return;
        }

        RightToLeft = value;
        Changed?.Invoke();
    }

    public void ToggleRightToLeft() => SetRightToLeft(!RightToLeft);
}
