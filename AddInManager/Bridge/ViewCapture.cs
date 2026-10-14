using System.IO;
using Autodesk.Revit.DB;

namespace RevitAddinManager.Bridge;

/// <summary>
/// Renders a Revit view to a PNG file so the MCP server can hand it to the AI as an image.
/// </summary>
public static class ViewCapture
{
    public static string CaptureFolder => Path.Combine(Path.GetTempPath(), "RevitAddinManager", "captures");

    public static string Capture(Document doc, Autodesk.Revit.DB.View view, string tag, int pixelSize = 1400)
    {
        if (view == null) throw new InvalidOperationException("No view to capture.");
        if (!view.CanBePrinted) throw new InvalidOperationException($"View '{view.Name}' cannot be exported as an image.");

        var folder = Path.Combine(CaptureFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = Path.Combine(folder, tag),
            FitDirection = FitDirectionType.Horizontal,
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ImageResolution = ImageResolution.DPI_150,
            PixelSize = pixelSize,
            ZoomType = ZoomFitType.FitToPage,
        };
        options.SetViewsAndSheets(new List<ElementId> { view.Id });
        doc.ExportImage(options);

        // Revit appends the view type and name to FilePath, so pick up whatever it wrote.
        return Directory.GetFiles(folder, "*.png").FirstOrDefault()
               ?? throw new InvalidOperationException("Revit did not produce an image.");
    }
}
