using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;

namespace RevitAddinManager.DevPane;

/// <summary>
/// One permanently registered dockable pane. Revit only ever sees this provider; the developer's pane content
/// is swapped in and out of <see cref="ContentHost"/> on every reload.
/// </summary>
public sealed class DevPaneSlot : IDockablePaneProvider
{
    private static readonly string[] SlotGuids =
    {
        "6F3B2C9E-6A3E-4B7B-9C1D-0D5E7A1B2C01",
        "6F3B2C9E-6A3E-4B7B-9C1D-0D5E7A1B2C02",
        "6F3B2C9E-6A3E-4B7B-9C1D-0D5E7A1B2C03",
    };

    private readonly TextBlock caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox message = new TextBox
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(6, 4, 6, 4),
        MaxHeight = 400,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Visibility = Visibility.Collapsed,
    };
    private readonly CheckBox autoReload = new CheckBox
    {
        Content = "Auto",
        ToolTip = "Reload automatically whenever the .dll is rebuilt",
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0),
    };
    private bool updatingAutoReload;

    public DevPaneSlot(int number)
    {
        Number = number;
        Id = new DockablePaneId(new Guid(SlotGuids[number - 1]));
        Root = BuildUi();
        ShowIdle();
    }

    public int Number { get; }
    public DockablePaneId Id { get; }
    public string Title => $"Dev Pane {Number}";

    /// <summary>Everything Revit hosts: toolbar, message area and the developer's content.</summary>
    public FrameworkElement Root { get; }

    public ContentControl ContentHost { get; } = new ContentControl();

    /// <summary>What is loaded now (or was loaded before Revit restarted).</summary>
    public DevPaneConfig Config { get; set; }

    public LoadedPane Current { get; set; }

    public event Action ReloadClicked;
    public event Action LoadClicked;
    public event Action<bool> AutoReloadChanged;

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = Root;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Right,
#if !R19
            MinimumWidth = 300,
            MinimumHeight = 200,
#endif
        };
#if !(R14 || R15 || R16)
        data.VisibleByDefault = false;
#endif
    }

    public void ShowIdle()
    {
        caption.Text = Config == null
            ? $"{Title}: nothing loaded. Load… a .dll with an IDockablePaneProvider or UserControl."
            : $"{Title}: {Config.ClassName ?? "?"} (press Reload)";
        ShowMessage(null, false);
    }

    public void ShowLoaded(LoadedPane pane, IReadOnlyList<string> warnings)
    {
        caption.Text = $"{pane.TypeName}  ·  v{pane.Version}  ·  {pane.LoadedAt:HH:mm:ss}";
        caption.ToolTip = pane.AssemblyPath;
        ShowMessage(warnings.Count > 0 ? string.Join("\n", warnings) : null, false);
    }

    public void ShowError(string text) => ShowMessage(text, true);

    public void SetAutoReload(bool value)
    {
        updatingAutoReload = true;
        autoReload.IsChecked = value;
        updatingAutoReload = false;
    }

    private void ShowMessage(string text, bool isError)
    {
        message.Text = text ?? string.Empty;
        message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        message.Background = new SolidColorBrush(isError ? Color.FromRgb(0xFD, 0xE7, 0xE7) : Color.FromRgb(0xFF, 0xF4, 0xD6));
        message.Foreground = new SolidColorBrush(isError ? Color.FromRgb(0x9B, 0x1C, 0x1C) : Color.FromRgb(0x7A, 0x4B, 0x00));
    }

    private FrameworkElement BuildUi()
    {
        var reload = new Button { Content = "Reload", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
        reload.Click += (_, _) => ReloadClicked?.Invoke();
        var load = new Button { Content = "Load…", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
        load.Click += (_, _) => LoadClicked?.Invoke();
        autoReload.Checked += (_, _) => { if (!updatingAutoReload) AutoReloadChanged?.Invoke(true); };
        autoReload.Unchecked += (_, _) => { if (!updatingAutoReload) AutoReloadChanged?.Invoke(false); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(autoReload);
        buttons.Children.Add(reload);
        buttons.Children.Add(load);

        var bar = new DockPanel { Margin = new Thickness(6, 4, 6, 4), LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(caption);

        var header = new Border
        {
            Child = bar,
            Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var root = new DockPanel { Background = Brushes.White, LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(message, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(message);
        root.Children.Add(ContentHost);
        return root;
    }
}

public sealed class DevPaneConfig
{
    public int Slot { get; set; }
    public string AssemblyPath { get; set; }
    public string ClassName { get; set; }
    public bool AutoReload { get; set; }
}

public sealed class LoadedPane
{
    public string AssemblyPath { get; set; }
    public string TypeName { get; set; }
    public string Version { get; set; }
    public DateTime LoadedAt { get; set; }
    public object Provider { get; set; }
    public FrameworkElement Content { get; set; }

    /// <summary>Undoes whatever the loader hooked up (e.g. AssemblyResolve on .NET Framework).</summary>
    public Action ReleaseLoader { get; set; }
}
