using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Window = System.Windows.Window;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using DataGrid = System.Windows.Controls.DataGrid;
using TreeView = System.Windows.Controls.TreeView;
using TabControl = System.Windows.Controls.TabControl;
using MenuItem = System.Windows.Controls.MenuItem;
using Label = System.Windows.Controls.Label;

namespace RevitAddinManager.Bridge;

/// <summary>
/// Lets the AI see and drive add-in windows (WPF, WinForms, Win32 dialogs and TaskDialogs).
/// Everything goes through the UI dispatcher or Win32 messages, never through ExternalEvent,
/// so it keeps working while an add-in is blocking Revit with a modal dialog.
/// </summary>
public sealed class UiInspector
{
    private const int UiTimeoutMs = 3000;
    private const int MaxNodes = 400;
    private const int MaxItems = 30;

    private readonly Dispatcher dispatcher;
    private readonly uint uiThreadId;
    private readonly string revitFolder;
    private readonly object refsLock = new object();
    private readonly Dictionary<long, Dictionary<int, WeakReference>> refs = new Dictionary<long, Dictionary<int, WeakReference>>();

    /// <summary>
    /// Must be created on Revit's main (UI) thread.
    /// </summary>
    public UiInspector()
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        uiThreadId = NativeMethods.GetCurrentThreadId();
        revitFolder = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty) ?? string.Empty;
    }

    public List<WindowInfo> ListWindows(bool includeRevit)
    {
        var windows = new List<WindowInfo>();
        var wpfHandles = new HashSet<IntPtr>();
        try
        {
            windows.AddRange(OnUi(() => ListWpfWindows(wpfHandles), 1000));
        }
        catch (TimeoutException)
        {
            // UI thread busy: Win32 windows can still be listed below.
        }

        var mainWindow = Process.GetCurrentProcess().MainWindowHandle;
        NativeMethods.EnumThreadWindows(uiThreadId, (hwnd, _) =>
        {
            if (hwnd == mainWindow || wpfHandles.Contains(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return true;
            var className = NativeMethods.GetClassName(hwnd);
            string kind = null;
            if (className == "#32770") kind = HasChildOfClass(hwnd, "DirectUIHWND") ? "taskDialog" : "dialog";
            else if (className.StartsWith("WindowsForms10.")) kind = "winforms";
            if (kind != null)
            {
                windows.Add(new WindowInfo
                {
                    Id = hwnd.ToInt64(),
                    Kind = kind,
                    Title = NativeMethods.GetWindowText(hwnd),
                    Owner = "unknown",
                });
            }
            return true;
        }, IntPtr.Zero);

        return windows.Where(x => includeRevit || x.Owner != "revit").ToList();
    }

    public HashSet<long> SnapshotWindowIds()
    {
        try
        {
            return new HashSet<long>(ListWindows(true).Select(x => x.Id));
        }
        catch (Exception)
        {
            return new HashSet<long>();
        }
    }

    /// <summary>
    /// Windows that appeared since <paramref name="before"/> was taken, e.g. a dialog an add-in opened.
    /// </summary>
    public List<WindowInfo> FindNewWindows(HashSet<long> before)
    {
        try
        {
            return ListWindows(true).Where(x => !before.Contains(x.Id) && x.Owner != "addinManager").ToList();
        }
        catch (Exception)
        {
            return new List<WindowInfo>();
        }
    }

    public object GetTree(long id)
    {
        EnsureExists(id);
        var wpf = OnUi(() => FindWpfRoot(id) != null);
        if (!wpf) return GetWin32Tree(new IntPtr(id));

        return OnUi(() =>
        {
            var root = FindWpfRoot(id);
            var map = new Dictionary<int, WeakReference>();
            var nodes = new List<object>();
            Walk(root, 0, map, nodes);
            lock (refsLock) refs[id] = map;
            return (object)new
            {
                id,
                kind = id < 0 ? "devPane" : "wpf",
                title = RootTitle(id, root),
                type = root.GetType().FullName,
                nodes,
                truncated = nodes.Count >= MaxNodes,
                hint = "Target a node with its ref in ui_action. Refs are valid until the next get_window_tree call.",
            };
        });
    }

    public object Act(long id, int? refNo, string name, string text, string action, string value)
    {
        EnsureExists(id);
        var wpf = OnUi(() => FindWpfRoot(id) != null);
        return wpf ? OnUi(() => ActWpf(id, refNo, name, text, action, value)) : ActWin32(new IntPtr(id), refNo, text, action, value);
    }

    public string Capture(long id)
    {
        EnsureExists(id);
        Directory.CreateDirectory(ViewCapture.CaptureFolder);
        var path = Path.Combine(ViewCapture.CaptureFolder, $"window-{id}-{DateTime.Now:HHmmssfff}.png");

        var renderedByWpf = OnUi(() =>
        {
            var root = FindWpfRoot(id);
            if (root == null) return false;
            RenderWpf(root, path);
            return true;
        });
        if (!renderedByWpf) CaptureWin32(new IntPtr(id), path);
        return path;
    }

    // ---------------- WPF ----------------

    /// <summary>
    /// Negative ids address Add-in Manager's dev panes (-1 is Dev Pane 1); other ids are window handles.
    /// </summary>
    private static FrameworkElement FindWpfRoot(long id) =>
        id < 0 ? DevPane.DevPaneManager.GetRoot((int)-id) : HwndSource.FromHwnd(new IntPtr(id))?.RootVisual as Window;

    private static string RootTitle(long id, FrameworkElement root) => root is Window window ? window.Title : $"Dev Pane {-id}";

    private static void EnsureExists(long id)
    {
        if (id < 0 ? DevPane.DevPaneManager.GetRoot((int)-id) == null : !NativeMethods.IsWindow(new IntPtr(id)))
            throw new InvalidOperationException($"Window {id} does not exist.");
    }

    private List<WindowInfo> ListWpfWindows(HashSet<IntPtr> handles)
    {
        var list = new List<WindowInfo>();
        foreach (var (slot, typeName) in DevPane.DevPaneManager.LoadedPanes())
        {
            list.Add(new WindowInfo { Id = -slot, Kind = "devPane", Title = $"Dev Pane {slot}", Type = typeName, Owner = "addin" });
        }
        foreach (PresentationSource source in PresentationSource.CurrentSources)
        {
            if (source is not HwndSource hwndSource || hwndSource.IsDisposed || hwndSource.RootVisual is not Window window) continue;
            if (!window.IsVisible) continue;
            handles.Add(hwndSource.Handle);
            list.Add(new WindowInfo
            {
                Id = hwndSource.Handle.ToInt64(),
                Kind = "wpf",
                Title = window.Title,
                Type = window.GetType().FullName,
                Owner = Classify(window.GetType().Assembly),
            });
        }
        return list;
    }

    private string Classify(System.Reflection.Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (name == typeof(UiInspector).Assembly.GetName().Name) return "addinManager";
        if (string.IsNullOrEmpty(assembly.Location)) return "addin";
        if (name.StartsWith("Autodesk") || name.StartsWith("AdWindows") || name.StartsWith("Revit")) return "revit";
        if (assembly.Location.StartsWith(revitFolder, StringComparison.OrdinalIgnoreCase)) return "revit";
        return "addin";
    }

    private static void Walk(DependencyObject element, int depth, Dictionary<int, WeakReference> map, List<object> nodes)
    {
        if (nodes.Count >= MaxNodes) return;
        if (element is UIElement ui && !ui.IsVisible) return;

        var info = element is Window ? null : Describe(element);
        if (info != null)
        {
            var refNo = map.Count + 1;
            map[refNo] = new WeakReference(element);
            info["ref"] = refNo;
            info["depth"] = depth;
            nodes.Add(info);
        }

        // These controls are summarized in one node; their template internals are noise.
        if (element is ButtonBase or TextBoxBase or PasswordBox or ComboBox or ListBox or DataGrid or TreeView) return;

        var childDepth = info != null ? depth + 1 : depth;
        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(element, i), childDepth, map, nodes);
    }

    private static Dictionary<string, object> Describe(DependencyObject element)
    {
        Dictionary<string, object> info;
        switch (element)
        {
            case ToggleButton toggle:
                info = Node(toggle, ContentText(toggle));
                info["checked"] = toggle.IsChecked;
                break;
            case ButtonBase button:
                info = Node(button, ContentText(button));
                break;
            case TextBox textBox:
                info = Node(textBox, textBox.Text);
                if (textBox.IsReadOnly) info["readOnly"] = true;
                break;
            case PasswordBox passwordBox:
                info = Node(passwordBox, new string('*', passwordBox.Password.Length));
                break;
            case ComboBox comboBox:
                info = Node(comboBox, comboBox.IsEditable ? comboBox.Text : ItemText(comboBox, comboBox.SelectedItem));
                info["items"] = Items(comboBox);
                info["selectedIndex"] = comboBox.SelectedIndex;
                break;
            case TabControl tabControl:
                info = Node(tabControl, null);
                info["items"] = Items(tabControl);
                info["selectedIndex"] = tabControl.SelectedIndex;
                break;
            case Selector selector:
                info = Node(selector, null);
                info["count"] = selector.Items.Count;
                info["items"] = Items(selector);
                info["selectedIndex"] = selector.SelectedIndex;
                break;
            case TreeView treeView:
                info = Node(treeView, null);
                info["items"] = Items(treeView);
                break;
            case RangeBase range:
                info = Node(range, null);
                info["value"] = range.Value;
                info["min"] = range.Minimum;
                info["max"] = range.Maximum;
                break;
            case Expander expander:
                info = Node(expander, expander.Header?.ToString());
                info["expanded"] = expander.IsExpanded;
                break;
            case MenuItem menuItem:
                info = Node(menuItem, menuItem.Header?.ToString());
                break;
            case Label label:
                info = Node(label, ContentText(label));
                break;
            case TextBlock textBlock when !string.IsNullOrWhiteSpace(textBlock.Text) && !IsInsideControl(textBlock):
                info = Node(textBlock, textBlock.Text);
                break;
            default:
                return null;
        }
        return info;
    }

    private static Dictionary<string, object> Node(FrameworkElement element, string text)
    {
        var info = new Dictionary<string, object> { ["type"] = element.GetType().Name };
        if (!string.IsNullOrEmpty(element.Name)) info["name"] = element.Name;
        var automationId = AutomationProperties.GetAutomationId(element);
        if (!string.IsNullOrEmpty(automationId)) info["automationId"] = automationId;
        if (!string.IsNullOrEmpty(text)) info["text"] = text;
        if (!element.IsEnabled) info["enabled"] = false;
        return info;
    }

    /// <summary>
    /// TextBlocks inside templated controls (labels of TabItems, ListBox rows...) are already reported by their parent.
    /// </summary>
    private static bool IsInsideControl(DependencyObject element)
    {
        var parent = VisualTreeHelper.GetParent(element);
        while (parent != null && parent is not Window)
        {
            if (parent is ButtonBase or ComboBox or ItemsControl or Expander or Label) return true;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return false;
    }

    private static string ContentText(ContentControl control)
    {
        switch (control.Content)
        {
            case null:
                return AutomationProperties.GetName(control);
            case string s:
                return s;
            case DependencyObject d:
                var texts = TextsUnder(d);
                return texts.Count > 0 ? string.Join(" ", texts) : AutomationProperties.GetName(control);
            default:
                return control.Content.ToString();
        }
    }

    private static List<string> TextsUnder(DependencyObject root)
    {
        var texts = new List<string>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0 && texts.Count < 5)
        {
            var current = stack.Pop();
            if (current is TextBlock { Text: { Length: > 0 } text }) texts.Add(text);
            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = count - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(current, i));
            }
        }
        return texts;
    }

    private static List<string> Items(ItemsControl control) =>
        control.Items.Cast<object>().Take(MaxItems).Select(item => ItemText(control, item)).ToList();

    private static string ItemText(ItemsControl control, object item)
    {
        if (item == null) return null;
        if (item is HeaderedContentControl headered) return headered.Header?.ToString();
        if (control.ItemContainerGenerator.ContainerFromItem(item) is DependencyObject container)
        {
            var texts = TextsUnder(container);
            if (texts.Count > 0) return string.Join(" | ", texts);
        }
        return item is ContentControl content ? ContentText(content) : item.ToString();
    }

    private object ActWpf(long id, int? refNo, string name, string text, string action, string value)
    {
        var root = FindWpfRoot(id);
        if (action == "close")
        {
            if (root is not Window window) throw new InvalidOperationException("Only windows can be closed.");
            window.Close();
            return new { done = "closed", window = window.Title };
        }

        var target = FindTarget(root, id, refNo, name, text)
                     ?? throw new InvalidOperationException("Target not found. Call get_window_tree and use a ref, name or exact text.");
        var element = (FrameworkElement)target;
        if (!element.IsEnabled) throw new InvalidOperationException($"{element.GetType().Name} is disabled.");
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);

        switch (action)
        {
            case "click":
                if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
                else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
                else if (peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selectionItem) selectionItem.Select();
                else if (peer?.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand)
                {
                    if (expand.ExpandCollapseState == ExpandCollapseState.Expanded) expand.Collapse();
                    else expand.Expand();
                }
                else throw new InvalidOperationException($"{element.GetType().Name} cannot be clicked.");
                break;
            case "setText":
                if (peer?.GetPattern(PatternInterface.Value) is IValueProvider valueProvider) valueProvider.SetValue(value ?? string.Empty);
                else if (element is TextBox textBox) textBox.Text = value ?? string.Empty;
                else throw new InvalidOperationException($"{element.GetType().Name} does not accept text.");
                break;
            case "select":
                if (element is not Selector selector) throw new InvalidOperationException($"{element.GetType().Name} is not a list.");
                var index = int.TryParse(value, out var i)
                    ? i
                    : selector.Items.Cast<object>().ToList().FindIndex(item => string.Equals(ItemText(selector, item), value, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || index >= selector.Items.Count) throw new InvalidOperationException($"Item '{value}' not found. Items: {string.Join(", ", Items(selector))}");
                selector.SelectedIndex = index;
                break;
            case "check":
            case "uncheck":
                if (element is not ToggleButton toggleButton) throw new InvalidOperationException($"{element.GetType().Name} is not a checkbox.");
                toggleButton.IsChecked = action == "check";
                break;
            case "focus":
                element.Focus();
                break;
            default:
                throw new InvalidOperationException($"Unknown action '{action}'.");
        }
        return new { done = action, target = element.GetType().Name, text = element is ContentControl c ? ContentText(c) : element.Name };
    }

    private DependencyObject FindTarget(FrameworkElement root, long id, int? refNo, string name, string text)
    {
        if (refNo != null)
        {
            lock (refsLock)
            {
                if (refs.TryGetValue(id, out var map) && map.TryGetValue(refNo.Value, out var weak) && weak.Target is DependencyObject d) return d;
            }
            throw new InvalidOperationException($"ref {refNo} is unknown or stale. Call get_window_tree again.");
        }

        DependencyObject byText = null;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is FrameworkElement fe && fe.IsVisible)
            {
                if (!string.IsNullOrEmpty(name) && (fe.Name == name || AutomationProperties.GetAutomationId(fe) == name)) return fe;
                if (!string.IsNullOrEmpty(text) && byText == null)
                {
                    var t = fe switch
                    {
                        ContentControl cc => ContentText(cc),
                        TextBlock tb => tb.Text,
                        _ => null,
                    };
                    if (string.Equals(t?.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        // A TextBlock label inside a button should resolve to the button.
                        byText = fe is TextBlock ? FindClickableAncestor(fe) ?? fe : fe;
                    }
                }
            }
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = count - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(current, i));
        }
        return byText;
    }

    private static DependencyObject FindClickableAncestor(DependencyObject element)
    {
        var parent = VisualTreeHelper.GetParent(element);
        while (parent != null && parent is not Window)
        {
            if (parent is ButtonBase or ListBoxItem or TabItem or MenuItem or TreeViewItem) return parent;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private static void RenderWpf(FrameworkElement window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Nothing to capture: it is hidden or has no size. Show the pane first.");
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ---------------- Win32 / WinForms / TaskDialog ----------------

    private static object GetWin32Tree(IntPtr hwnd)
    {
        var nodes = new List<object>();
        var refNo = 0;
        NativeMethods.EnumChildWindows(hwnd, (child, _) =>
        {
            refNo++;
            if (!NativeMethods.IsWindowVisible(child)) return true;
            var className = NativeMethods.GetClassName(child);
            var text = NativeMethods.GetWindowText(child);
            if (string.IsNullOrWhiteSpace(text) && !className.Contains("EDIT") && className != "Edit") return true;
            nodes.Add(new { @ref = refNo, type = SimplifyClass(className), text, enabled = NativeMethods.IsWindowEnabled(child) });
            return true;
        }, IntPtr.Zero);

        var isTaskDialog = HasChildOfClass(hwnd, "DirectUIHWND");
        return new
        {
            id = hwnd.ToInt64(),
            kind = isTaskDialog ? "taskDialog" : "win32",
            title = NativeMethods.GetWindowText(hwnd),
            nodes,
            hint = isTaskDialog
                ? "TaskDialog text is not exposed as controls: use capture_window to read it, then ui_action click with value ok/cancel/yes/no/close/commandLink1..4."
                : "Target a node with its ref in ui_action.",
        };
    }

    private static object ActWin32(IntPtr hwnd, int? refNo, string text, string action, string value)
    {
        const uint WM_CLOSE = 0x0010;
        const uint WM_SETTEXT = 0x000C;
        const uint BM_CLICK = 0x00F5;
        const uint TDM_CLICK_BUTTON = 0x0400 + 102;

        if (action == "close")
        {
            NativeMethods.PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return new { done = "close" };
        }

        if (action == "click" && refNo == null && HasChildOfClass(hwnd, "DirectUIHWND"))
        {
            var buttonId = TaskDialogButtonId(value ?? text);
            NativeMethods.PostMessage(hwnd, TDM_CLICK_BUTTON, new IntPtr(buttonId), IntPtr.Zero);
            return new { done = "click", taskDialogButton = buttonId };
        }

        var child = FindWin32Child(hwnd, refNo, text)
                    ?? throw new InvalidOperationException("Target not found. Call get_window_tree and use a ref or exact text.");
        switch (action)
        {
            case "click":
                NativeMethods.PostMessage(child, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                break;
            case "setText":
                NativeMethods.SendMessage(child, WM_SETTEXT, IntPtr.Zero, value ?? string.Empty);
                break;
            default:
                throw new InvalidOperationException($"Action '{action}' is not supported for Win32 windows.");
        }
        return new { done = action, target = NativeMethods.GetWindowText(child) };
    }

    private static IntPtr? FindWin32Child(IntPtr hwnd, int? refNo, string text)
    {
        IntPtr? found = null;
        var index = 0;
        NativeMethods.EnumChildWindows(hwnd, (child, _) =>
        {
            index++;
            var match = refNo != null
                ? index == refNo
                : string.Equals(NativeMethods.GetWindowText(child).Replace("&", string.Empty).Trim(),
                    text?.Replace("&", string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
            if (!match) return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static int TaskDialogButtonId(string value)
    {
        if (int.TryParse(value, out var id)) return id;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "ok": return 1;
            case "cancel": return 2;
            case "retry": return 4;
            case "yes": return 6;
            case "no": return 7;
            case "close": return 8;
            case "commandlink1": return 1001;
            case "commandlink2": return 1002;
            case "commandlink3": return 1003;
            case "commandlink4": return 1004;
            default: throw new InvalidOperationException("For a TaskDialog use value ok, cancel, retry, yes, no, close, commandLink1..4 or a numeric button id.");
        }
    }

    private static void CaptureWin32(IntPtr hwnd, string path)
    {
        // PrintWindow is serviced by the window's thread; never wait on it forever.
        var task = Task.Run(() =>
        {
            NativeMethods.GetWindowRect(hwnd, out var rect);
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) throw new InvalidOperationException("Window has no size.");
            using var bitmap = new System.Drawing.Bitmap(width, height);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    NativeMethods.PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
            bitmap.Save(path, ImageFormat.Png);
        });
        if (!task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The window did not respond to capture.");
    }

    private static bool HasChildOfClass(IntPtr hwnd, string className)
    {
        var found = false;
        NativeMethods.EnumChildWindows(hwnd, (child, _) =>
        {
            found = NativeMethods.GetClassName(child) == className;
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    private static string SimplifyClass(string className)
    {
        // WindowsForms10.BUTTON.app.0.141b42a_r6_ad1 -> BUTTON
        if (className.StartsWith("WindowsForms10.")) return className.Split('.')[1];
        return className;
    }

    // ---------------- helpers ----------------

    private T OnUi<T>(Func<T> func, int timeoutMs = UiTimeoutMs)
    {
        if (dispatcher.CheckAccess()) return func();

        Exception error = null;
        var result = default(T);
        var operation = dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                // Never let an exception escape onto Revit's dispatcher.
                error = ex;
            }
        }), DispatcherPriority.Send);

        if (operation.Wait(TimeSpan.FromMilliseconds(timeoutMs)) != DispatcherOperationStatus.Completed)
        {
            operation.Abort();
            throw new TimeoutException("Revit's UI thread is busy.");
        }
        if (error != null) throw error;
        return result;
    }

    private static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        public static string GetClassName(IntPtr hwnd)
        {
            var builder = new StringBuilder(256);
            GetClassName(hwnd, builder, builder.Capacity);
            return builder.ToString();
        }

        public static string GetWindowText(IntPtr hwnd)
        {
            var builder = new StringBuilder(GetWindowTextLength(hwnd) + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            return builder.ToString();
        }
    }
}

public sealed class WindowInfo
{
    public long Id { get; set; }
    public string Kind { get; set; }
    public string Title { get; set; }
    public string Type { get; set; }
    public string Owner { get; set; }
}
