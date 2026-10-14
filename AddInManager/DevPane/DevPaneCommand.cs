using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace RevitAddinManager.DevPane;

/// <summary>
/// Ribbon entry: reloads Dev Pane 1 from its last .dll, or asks for a .dll the first time.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class DevPaneCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var path = DevPaneManager.LastAssemblyPath(1);
        string className = DevPaneManager.LastClassName(1);
        if (path == null || !File.Exists(path))
        {
            var dialog = new OpenFileDialog { Title = "Dev Pane 1: choose the add-in .dll that contains the pane", Filter = "Assembly (*.dll)|*.dll" };
            if (dialog.ShowDialog() != true) return Result.Cancelled;
            path = dialog.FileName;
            className = null;
        }

        try
        {
            DevPaneManager.Load(commandData.Application, 1, path, className, null, interactive: true);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Dev Pane", ex.Message);
            return Result.Failed;
        }
    }
}
