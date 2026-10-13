using System.Windows;
using System.Windows.Input;

namespace RevitAddinManager.Themes.Controls;

/// <summary>
/// Attached properties used by the "ModernWindow" style (custom chrome title bar).
/// </summary>
public static class ModernWindow
{
    /// <summary>Extra content (e.g. icon buttons) shown in the title bar, left of the caption buttons.</summary>
    public static readonly DependencyProperty TitleBarContentProperty = DependencyProperty.RegisterAttached(
        "TitleBarContent", typeof(object), typeof(ModernWindow), new PropertyMetadata(null));

    public static object GetTitleBarContent(DependencyObject obj) => obj.GetValue(TitleBarContentProperty);

    public static void SetTitleBarContent(DependencyObject obj, object value) => obj.SetValue(TitleBarContentProperty, value);

    /// <summary>Wires the minimize / maximize / restore / close caption buttons of the template.</summary>
    public static readonly DependencyProperty EnableCaptionCommandsProperty = DependencyProperty.RegisterAttached(
        "EnableCaptionCommands", typeof(bool), typeof(ModernWindow), new PropertyMetadata(false, OnEnableCaptionCommandsChanged));

    public static bool GetEnableCaptionCommands(DependencyObject obj) => (bool)obj.GetValue(EnableCaptionCommandsProperty);

    public static void SetEnableCaptionCommands(DependencyObject obj, bool value) => obj.SetValue(EnableCaptionCommandsProperty, value);

    private static void OnEnableCaptionCommandsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || !(bool)e.NewValue) return;
        window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(window)));
        window.CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(window)));
    }
}
