using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;

// Exercise the same coordinate handlers used by WPF mouse events, without moving
// the user's mouse, opening editor windows, or changing their saved preferences.
static class Program
{
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static readonly Assembly AppAssembly = typeof(SnapView.Editor.EditorWindow).Assembly;
    static readonly List<Window> Windows = new();
    static int passed;
    static object? Call(object o, string method, params object?[] args) => o.GetType().GetMethod(method, Members)!.Invoke(o, args);
    static object? Field(object o, string name) => o.GetType().GetField(name, Members)!.GetValue(o);
    static object? Prop(object o, string name) => o.GetType().GetProperty(name, Members)!.GetValue(o);
    static void Set(object o, string name, object value) => o.GetType().GetProperty(name, Members)!.SetValue(o, value);
    static object Canvas(Window w) => w.FindName("Canvas1");
    static IList Items(Window w) => (IList)Prop(Canvas(w), "Items")!;
    static Point PointOf(object a, string name) => (Point)Prop(a, name)!;
    static void Check(string name, bool condition)
    {
        if (!condition) throw new Exception(name);
        passed++; Console.WriteLine("PASS " + name);
    }
    static void Select(Window w, string tool) => Call(w, "SelectTool", Enum.Parse(AppAssembly.GetType("SnapView.Core.ToolKind")!, tool));
    static void Down(Window w, Point p, bool inside = true) => Call(w, "HandlePointerDown", p, inside, 1);
    static void Move(Window w, Point p, bool pressed = true) => Call(w, "HandlePointerMove", p, pressed);
    static void Up(Window w, Point p) => Call(w, "HandlePointerUp", p);
    static void Drag(Window w, Point from, Point to) { Down(w, from); Move(w, to); Up(w, to); }

    static Window Editor(string folder)
    {
        object settings = Activator.CreateInstance(AppAssembly.GetType("SnapView.Core.Settings")!, true)!;
        Set(settings, "SaveFolder", folder); Set(settings, "FileNamePattern", "interaction");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 800, 480));
            dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(80, 80, 600, 310));
        }
        var image = new RenderTargetBitmap(800, 480, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze();
        var window = (Window)Activator.CreateInstance(typeof(SnapView.Editor.EditorWindow), Members, null, new[] { image, settings }, null)!;
        Windows.Add(window);
        ((DispatcherTimer)Field(window, "_autosave")!).Stop();
        var root = (FrameworkElement)window.Content;
        root.Width = 1300; root.Height = 810;
        root.Measure(new Size(1300, 810)); root.Arrange(new Rect(0, 0, 1300, 810)); root.UpdateLayout();
        Call(window, "Relayout"); root.UpdateLayout();
        return window;
    }

    static void TestNumberArrows(string folder)
    {
        var w = Editor(folder); Select(w, "NumberArrow");
        var start = new Point(100, 100); var tip = new Point(270, 170);
        Down(w, start);
        object active = Prop(Canvas(w), "Active")!;
        Check("number stays at the pressed start", PointOf(active, "Center") == start);
        Check("initial arrow is visible outside number", (PointOf(active, "Tip") - start).Length > (double)Prop(active, "Radius")!);
        Move(w, tip);
        Check("dragged end is the arrow tip", PointOf(active, "Tip") == tip && PointOf(active, "Center") == start);
        Up(w, tip);
        Check("drag release registers one arrow", Items(w).Count == 1 && Prop(Canvas(w), "Active") == null);
        Check("registered arrow is selected and counter advances once", ReferenceEquals(Prop(Canvas(w), "Selected"), Items(w)[0]) && (int)Field(w, "_counter")! == 2);

        start = new Point(400, 110); tip = new Point(610, 180);
        Down(w, start); Up(w, start);
        Check("first click waits for the arrow tip", Items(w).Count == 1 && Prop(Canvas(w), "Active") != null);
        Move(w, tip, pressed: false);
        Check("tip previews with no button held", PointOf(Prop(Canvas(w), "Active")!, "Tip") == tip);
        Down(w, new Point(800, 480), inside: false);
        Check("outside click does not complete pending arrow", Items(w).Count == 1);
        Down(w, tip); Up(w, tip);
        Check("second click completes exactly one arrow", Items(w).Count == 2 && Prop(Canvas(w), "Active") == null && (int)Field(w, "_counter")! == 3);
        Check("click placement keeps start and end direction", PointOf(Items(w)[1]!, "Center") == start && PointOf(Items(w)[1]!, "Tip") == tip);
        Call(w, "Undo"); Check("undo removes the completed arrow", Items(w).Count == 1 && (int)Field(w, "_counter")! == 2);
        Call(w, "Redo"); Check("redo restores the completed arrow", Items(w).Count == 2 && PointOf(Items(w)[1]!, "Tip") == tip);
        Down(w, new Point(400, 340)); Up(w, new Point(400, 340)); Call(w, "CancelActive");
        Move(w, new Point(550, 360), false);
        Check("cancel leaves no pending arrow or counter increment", Items(w).Count == 2 && Prop(Canvas(w), "Active") == null && (int)Field(w, "_counter")! == 3);
        Down(w, new Point(400, 340)); Up(w, new Point(400, 340)); Select(w, "Line");
        Check("changing tools cancels an unfinished two-click arrow", Items(w).Count == 2 && Prop(Canvas(w), "Active") == null);
        Select(w, "NumberArrow"); Down(w, new Point(400, 340)); Up(w, new Point(400, 340)); Call(w, "Undo");
        Check("undo during placement cancels only the unfinished arrow", Items(w).Count == 2 && Prop(Canvas(w), "Active") == null);
        Select(w, "NumberArrow"); Down(w, new Point(100, 330)); Up(w, new Point(250, 400));
        Check("release position works even without intermediate mouse moves", Items(w).Count == 3 && PointOf(Items(w)[2]!, "Tip") == new Point(250, 400));
    }

    static Window TestMagnifiers(string folder)
    {
        var w = Editor(folder); Select(w, "Magnifier");
        var source = new Point(150, 300); var lens = new Point(490, 180);
        Down(w, source); object mag = Prop(Canvas(w), "Active")!;
        Move(w, lens);
        Check("first held drag positions lens immediately", PointOf(mag, "Center") == lens && PointOf(mag, "SourceCenter") == source);
        Up(w, lens);
        Check("release registers and selects magnifier", Items(w).Count == 1 && Prop(Canvas(w), "Active") == null && ReferenceEquals(Prop(Canvas(w), "Selected"), mag));
        var insideSource = new Point(155, 327); var delta = new Vector(30, -50);
        Drag(w, insideSource, insideSource + delta);
        Check("source circle body moves on first press with magnifier tool", Items(w).Count == 1 && PointOf(mag, "SourceCenter") == source + delta && PointOf(mag, "Center") == lens);
        Check("source body drag preserves independent radii", (double)Prop(mag, "Radius")! == 42 && (double)Prop(mag, "DisplayRadius")! == 84);
        Call(w, "Undo"); mag = Items(w)[0]!;
        Check("one undo restores source position", PointOf(mag, "SourceCenter") == source && PointOf(mag, "Center") == lens);
        Call(w, "Redo"); mag = Items(w)[0]!; source += delta;
        Check("redo restores source movement", PointOf(mag, "SourceCenter") == source);
        Drag(w, new Point(480, 200), new Point(530, 210));
        lens += new Vector(50, 10);
        Check("registered lens moves without creating another annotation", Items(w).Count == 1 && PointOf(mag, "Center") == lens && PointOf(mag, "SourceCenter") == source);

        Select(w, "Select"); Call(w, "SetSelection", new object?[] { null });
        Drag(w, source + new Vector(5, 25), source + new Vector(30, 45)); source += new Vector(25, 20);
        Check("unselected source moves in a single drag with selection tool", PointOf(mag, "SourceCenter") == source && PointOf(mag, "Center") == lens);
        double displayRadius = (double)Prop(mag, "DisplayRadius")!;
        Point sourceHandle = source + new Vector((double)Prop(mag, "Radius")!, 0);
        Drag(w, sourceHandle, source + new Vector(65, 0));
        Check("source resize handle still adjusts only source size", (double)Prop(mag, "Radius")! == 65 && (double)Prop(mag, "DisplayRadius")! == displayRadius);

        Call(w, "SaveResult", false);
        Down(w, source + new Vector(0, 25)); Up(w, source + new Vector(0, 25));
        Check("clicking source without moving does not dirty saved document", !(bool)Field(w, "_dirty")!);
        Down(w, source); Up(w, source);
        Check("clicking source center handle also leaves saved document clean", !(bool)Field(w, "_dirty")!);
        Select(w, "Magnifier"); Down(w, new Point(650, 400)); Up(w, new Point(650, 400));
        Check("single click places default magnifier once", Items(w).Count == 2 && Prop(Canvas(w), "Active") == null);
        return w;
    }

    static Point[] EditHandles(Window w, object a) => (Point[])Call(Canvas(w), "EditHandles", a)!;
    static bool Near(Point a, Point b) => (a - b).Length < 0.001;
    static bool Near(double a, double b) => Math.Abs(a - b) < 0.001;

    static void TestNumberArrowEditing(string folder, string? preview)
    {
        var w = Editor(folder); Select(w, "NumberArrow");
        Drag(w, new Point(180, 180), new Point(350, 250));
        object arrow = Items(w)[0]!;
        Point center = PointOf(arrow, "Center"), tip = PointOf(arrow, "Tip");
        Drag(w, tip, tip + new Vector(60, -25)); tip += new Vector(60, -25);
        Check("registered tip moves immediately with number-arrow tool", Items(w).Count == 1 && PointOf(arrow, "Tip") == tip && PointOf(arrow, "Center") == center);
        Drag(w, center, center + new Vector(-35, 25)); center += new Vector(-35, 25);
        Check("registered number position moves independently", PointOf(arrow, "Center") == center && PointOf(arrow, "Tip") == tip);
        Point body = center + (tip - center) * 0.5;
        Vector move = new Vector(24, 32);
        Drag(w, body, body + move); center += move; tip += move;
        Check("dragging shaft moves the whole arrow without adding a layer", Items(w).Count == 1 && PointOf(arrow, "Tip") == tip && PointOf(arrow, "Center") == center);
        Call(w, "SetSelection", new object?[] { null });
        Drag(w, tip, tip + new Vector(12, 5)); tip += new Vector(12, 5);
        Check("unselected arrow tip also edits on the first drag", Items(w).Count == 1 && PointOf(arrow, "Tip") == tip);
        Select(w, "Select");
        Drag(w, tip, tip + new Vector(-12, -5)); tip += new Vector(-12, -5);
        Check("selection tool keeps independent tip editing", PointOf(arrow, "Tip") == tip && PointOf(arrow, "Center") == center);

        Select(w, "NumberArrow"); Call(w, "SetSelection", arrow);
        for (int handle = 2; handle < 6; handle++)
        {
            Point oldCenter = PointOf(arrow, "Center"), oldTip = PointOf(arrow, "Tip");
            double oldRadius = (double)Prop(arrow, "Radius")!, oldThickness = (double)Prop(arrow, "Thickness")!;
            Rect bounds = (Rect)Prop(arrow, "Bounds")!;
            Point[] corners = { bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft };
            Point anchor = corners[(handle - 2 + 2) % 4];
            Vector diagonal = corners[handle - 2] - anchor;
            Point grab = EditHandles(w, arrow)[handle];
            double factor = handle % 2 == 0 ? 1.4 : 0.75;
            Down(w, grab);
            Move(w, grab + diagonal * 0.1);
            Move(w, grab + diagonal * (factor - 1));
            Up(w, grab + diagonal * (factor - 1));
            Check($"corner {handle} scales both positions around the opposite corner", Near(PointOf(arrow, "Center"), anchor + (oldCenter - anchor) * factor) && Near(PointOf(arrow, "Tip"), anchor + (oldTip - anchor) * factor));
            Check($"corner {handle} scales number, shaft and arrowhead together", Near((double)Prop(arrow, "Radius")!, oldRadius * factor) && Near((double)Prop(arrow, "Thickness")!, oldThickness * factor));
            Call(w, "Undo"); arrow = Items(w)[0]!;
            Check($"corner {handle} is one undo step", Near(PointOf(arrow, "Center"), oldCenter) && Near((double)Prop(arrow, "Radius")!, oldRadius));
            Call(w, "Redo"); arrow = Items(w)[0]!;
            Check($"corner {handle} redo preserves scaled size", Near((double)Prop(arrow, "Radius")!, oldRadius * factor));
            Call(w, "SetSelection", arrow);
        }
        Call(w, "SaveResult", false);
        foreach (Point handle in EditHandles(w, arrow)) { Down(w, handle); Up(w, handle); }
        Check("clicking any handle without dragging keeps saved document clean", !(bool)Field(w, "_dirty")!);
        int count = Items(w).Count;
        Drag(w, new Point(640, 350), new Point(720, 420));
        Check("blank-area drag still creates the next numbered arrow", Items(w).Count == count + 1 && (int)Field(w, "_counter")! == 3);

        // Handle padding is measured in screen pixels, even when zoomed out.
        arrow = Items(w)[1]!;
        foreach (double zoom in new[] { 0.4, 1.0, 2.5 })
        {
            Set(Canvas(w), "Scale", zoom);
            Rect b = (Rect)Prop(arrow, "Bounds")!;
            Point[] handles = EditHandles(w, arrow);
            Check($"resize corners stay separated from endpoints at {zoom} zoom", Near((b.Left - handles[2].X) * zoom, 20) && (handles[4] - handles[0]).Length * zoom >= 20);
        }
        Set(Canvas(w), "Scale", 1.0);
        Set(arrow, "Center", new Point(20, 80)); Set(arrow, "Tip", new Point(190, 135));
        Point marginHandle = EditHandles(w, arrow)[2];
        double beforeRadius = (double)Prop(arrow, "Radius")!;
        Down(w, marginHandle, inside: false); Up(w, marginHandle + new Vector(-30, -20));
        Check("resize handle in image margin remains usable with release-only movement", (double)Prop(arrow, "Radius")! > beforeRadius);
        Select(w, "Select"); Call(w, "SetSelection", arrow);
        Point selectCorner = EditHandles(w, arrow)[4]; beforeRadius = (double)Prop(arrow, "Radius")!;
        Drag(w, selectCorner, selectCorner + new Vector(30, 20));
        Check("selection tool supports the same whole-arrow resizing", (double)Prop(arrow, "Radius")! > beforeRadius);
        Call(w, "SetSelection", Items(w)[0]);
        Call(w, "HideSavedToast");
        Call(w, "Relayout");
        if (preview != null) RenderPreview(w, preview);
    }

    static void RenderPreview(Window w, string path)
    {
        var root = (FrameworkElement)w.Content; root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }

    static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    static void TestToast(Window w, string? preview)
    {
        var beforeFocus = Keyboard.FocusedElement;
        Call(w, "SaveResult", false);
        var toast = (FrameworkElement)w.FindName("SaveToast");
        string path = (string)Field(w, "_imageSavePath")!;
        Check("successful save shows toast and actual file path", toast.Visibility == Visibility.Visible && File.Exists(path) && ((TextBlock)w.FindName("SaveToastPath")).Text == path);
        Check("save toast preserves keyboard focus and clean state", ReferenceEquals(beforeFocus, Keyboard.FocusedElement) && !(bool)Field(w, "_dirty")!);
        var timer = (DispatcherTimer)Field(w, "_saveToastTimer")!;
        Check("toast dismissal timer is running", timer.IsEnabled && timer.Interval == TimeSpan.FromSeconds(3.5));
        if (preview != null)
        {
            var root = (FrameworkElement)w.Content; root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(preview); png.Save(stream);
        }
        timer.Interval = TimeSpan.FromMilliseconds(20); Pump(TimeSpan.FromMilliseconds(60));
        Check("toast automatically disappears", toast.Visibility == Visibility.Collapsed && !timer.IsEnabled);
        Call(w, "SaveResult", false);
        Check("repeat save shows toast again for same file", toast.Visibility == Visibility.Visible && (string)Field(w, "_imageSavePath")! == path);
        Call(w, "ShowSavedToast", path, true);
        Check("project save has a distinct success label", ((TextBlock)w.FindName("SaveToastMessage")).Text == "프로젝트를 저장했습니다");
        Call(w, "HideSavedToast");
        Check("hiding toast stops its timer", toast.Visibility == Visibility.Collapsed && !timer.IsEnabled);
    }

    [STAThread]
    static int Main(string[] args)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "SnapViewInteraction-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Load the real theme without the production App's tray/IPC startup.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (var theme = Assembly.GetExecutingAssembly().GetManifestResourceStream("EditorTestTheme.xaml")!)
            {
                XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XElement dictionary = XDocument.Load(theme).Root!.Element(ns + "Application.Resources")!.Element(ns + "ResourceDictionary")!;
                dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
                app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            }
            Directory.CreateDirectory(tmp);
            TestNumberArrows(Path.Combine(tmp, "arrows"));
            TestNumberArrowEditing(Path.Combine(tmp, "arrow-edits"), args.Length > 1 ? Path.GetFullPath(args[1]) : null);
            Window w = TestMagnifiers(Path.Combine(tmp, "magnifiers"));
            TestToast(w, args.Length > 0 ? Path.GetFullPath(args[0]) : null);
            Console.WriteLine($"RESULT: {passed} interaction checks passed"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally
        {
            foreach (Window w in Windows) Call(w, "HideSavedToast");
            string full = Path.GetFullPath(tmp);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("SnapViewInteraction-", StringComparison.Ordinal) && Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
    }
}
