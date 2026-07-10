namespace Daleel.Web.Services;

/// <summary>
/// Shared, per-circuit UI state for the theme (dark/light). The <see cref="MainLayout"/> owns the
/// <c>MudThemeProvider</c> and re-renders on <see cref="Changed"/>; the theme toggle and the Settings
/// page read/update this one source of truth. Text direction is not stored here — it follows the
/// request culture automatically (Arabic = RTL, English = LTR).
/// </summary>
public sealed class LayoutState
{
    /// <summary>
    /// Dark by default. A user's explicit toggle choice (persisted in localStorage by
    /// AppBarControls, read back by InteractiveProviders on circuit start) overrides this;
    /// the OS theme deliberately does not.
    /// </summary>
    public bool IsDarkMode { get; private set; } = true;

    /// <summary>Raised whenever the theme changes.</summary>
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
}
