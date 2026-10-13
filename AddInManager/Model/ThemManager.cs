using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace RevitAddinManager.Model;

public static class ThemManager
{
    // Loaded once and shared. Every view already merges Default.xaml (control styles + light palette);
    // switching theme only appends a palette on top, so styles/templates are kept and only brushes update.
    private static ResourceDictionary lightPalette;
    private static ResourceDictionary darkPalette;

    private static ResourceDictionary LightPalette => lightPalette ??= Load("Palette.Light.xaml");
    private static ResourceDictionary DarkPalette => darkPalette ??= Load("Palette.Dark.xaml");

    public static bool IsDark { get; private set; }

    /// <summary>Raised after the theme changes; panes outside the main window (Trace, Dev Panes) re-apply it.</summary>
    public static event Action ThemeChanged;

    /// <summary>
    /// Apply the current theme on startup, or toggle between Light and Dark.
    /// </summary>
    public static void ChangeThem(bool isStartup)
    {
        try
        {
            if (!isStartup) SetTheme(!IsDark);
            Apply(App.FrmAddInManager);
        }
        catch (Exception e)
        {
            MessageBox.Show(e.ToString());
        }
    }

    public static void SetTheme(bool dark)
    {
        IsDark = dark;
        ThemeChanged?.Invoke();
    }

    /// <summary>Overlay the current palette on <paramref name="element"/> (which must merge Default.xaml).</summary>
    public static void Apply(FrameworkElement element)
    {
        if (element == null) return;
        var dictionaries = element.Resources.MergedDictionaries;
        var current = IsDark ? DarkPalette : LightPalette;
        var overlay = dictionaries.Count > 0 ? dictionaries[dictionaries.Count - 1] : null;
        if (overlay == current) return;
        if (overlay == LightPalette || overlay == DarkPalette) dictionaries.Remove(overlay);
        // Default.xaml already carries the light palette, so light needs no overlay.
        if (IsDark) dictionaries.Add(current);
    }

    private static ResourceDictionary Load(string file) => new()
    {
        Source = new Uri($"/RevitAddinManager;component/Themes/Modern/{file}", UriKind.RelativeOrAbsolute)
    };
}
