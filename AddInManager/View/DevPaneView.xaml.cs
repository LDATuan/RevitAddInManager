using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using RevitAddinManager.Model;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace RevitAddinManager.View;

public enum DevPaneStatus
{
    Idle,
    Loaded,
    Error,
}

/// <summary>
/// Chrome shown around a Dev Pane: title, status, Auto/Reload/Load and a warning/error banner.
/// Free of Revit API types so it can be previewed outside Revit.
/// </summary>
public partial class DevPaneView : UserControl
{
    private bool updatingAutoReload;

    public DevPaneView()
    {
        InitializeComponent();
        ThemManager.Apply(Chrome);
        ThemManager.ThemeChanged += () => ThemManager.Apply(Chrome);
        ReloadButton.Click += (_, _) => ReloadClicked?.Invoke();
        LoadButton.Click += (_, _) => LoadClicked?.Invoke();
        AutoReload.Checked += (_, _) => { if (!updatingAutoReload) AutoReloadChanged?.Invoke(true); };
        AutoReload.Unchecked += (_, _) => { if (!updatingAutoReload) AutoReloadChanged?.Invoke(false); };
    }

    public event Action ReloadClicked;
    public event Action LoadClicked;
    public event Action<bool> AutoReloadChanged;

    /// <summary>Hosts the developer's pane content (outside the themed chrome).</summary>
    public ContentControl Host => ContentHost;

    public void SetHeader(string title, string caption, string tooltip, DevPaneStatus status)
    {
        TitleText.Text = title;
        CaptionText.Text = caption;
        CaptionText.Visibility = string.IsNullOrEmpty(caption) ? Visibility.Collapsed : Visibility.Visible;
        CaptionText.ToolTip = tooltip;
        StatusDot.SetResourceReference(Shape.FillProperty, status switch
        {
            DevPaneStatus.Loaded => "SuccessBrush",
            DevPaneStatus.Error => "DangerBrush",
            _ => "TextDisabledBrush",
        });
    }

    public void SetAutoReload(bool value)
    {
        updatingAutoReload = true;
        AutoReload.IsChecked = value;
        updatingAutoReload = false;
    }

    public void ShowMessage(string text, bool isError)
    {
        MessageText.Text = text ?? string.Empty;
        MessageBanner.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        MessageBanner.SetResourceReference(Border.BackgroundProperty, isError ? "DangerSoftBrush" : "WarningSoftBrush");
        MessageText.SetResourceReference(TextBox.ForegroundProperty, isError ? "DangerBrush" : "WarningBrush");
        MessageIcon.SetResourceReference(TextBlock.ForegroundProperty, isError ? "DangerBrush" : "WarningBrush");
        MessageIcon.Text = isError ? "" : "";
    }
}
