using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using RevitAddinManager.View;

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

    private readonly DevPaneView view = new DevPaneView();

    public DevPaneSlot(int number)
    {
        Number = number;
        Id = new DockablePaneId(new Guid(SlotGuids[number - 1]));
        view.ReloadClicked += () => ReloadClicked?.Invoke();
        view.LoadClicked += () => LoadClicked?.Invoke();
        view.AutoReloadChanged += enabled => AutoReloadChanged?.Invoke(enabled);
        ShowIdle();
    }

    public int Number { get; }
    public DockablePaneId Id { get; }
    public string Title => $"Dev Pane {Number}";

    /// <summary>Everything Revit hosts: toolbar, message area and the developer's content.</summary>
    public FrameworkElement Root => view;

    public ContentControl ContentHost => view.Host;

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
        var caption = Config == null
            ? "Nothing loaded. Load… a .dll with an IDockablePaneProvider or UserControl."
            : $"{Config.ClassName ?? "?"} (press Reload)";
        view.SetHeader(Title, caption, null, DevPaneStatus.Idle);
        view.ShowMessage(null, false);
    }

    public void ShowLoaded(LoadedPane pane, IReadOnlyList<string> warnings)
    {
        view.SetHeader(pane.TypeName, $"v{pane.Version}  ·  {pane.LoadedAt:HH:mm:ss}  ·  {Title}", pane.AssemblyPath, DevPaneStatus.Loaded);
        view.ShowMessage(warnings.Count > 0 ? string.Join("\n", warnings) : null, false);
    }

    public void ShowError(string text)
    {
        view.SetHeader(Current?.TypeName ?? Title, "Load failed", Current?.AssemblyPath, DevPaneStatus.Error);
        view.ShowMessage(text, true);
    }

    public void SetAutoReload(bool value) => view.SetAutoReload(value);
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
