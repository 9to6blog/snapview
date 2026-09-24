using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static partial class Program
{
    static void TestCaptureSelection(string folder, string? preview)
    {
        Directory.CreateDirectory(folder);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, 2000, 900));
            dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(700, 150, 300, 620));
        }
        var frozen = new RenderTargetBitmap(2000, 900, 96, 96, PixelFormats.Pbgra32); frozen.Render(visual); frozen.Freeze();
        var analysis = AppAssembly.GetType("SnapView.Core.CropBoundaryAnalysis")!.GetMethod("Analyze", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { frozen })!;
        var old = Call(analysis, "Snap", new Point(850, 146), 12.0)!;
        Check("fixture reproduces narrow-card top missed by full-screen scoring", Prop(old, "Y") == null);
        foreach (var sample in new[] { (new Point(697, 400), "X", 700.0), (new Point(1005, 400), "X", 1000.0),
            (new Point(850, 145), "Y", 150.0), (new Point(850, 775), "Y", 770.0) })
        {
            var snapped = Call(analysis, "SnapLocal", sample.Item1, 12.0)!;
            Check("local edge snaps " + sample.Item2 + " to " + sample.Item3, Prop(snapped, sample.Item2) is double v && Near(v, sample.Item3));
        }
        Check("short card edges do not extend across unrelated desktop areas", Prop(Call(analysis, "SnapLocal", new Point(200, 145), 12.0)!, "Y") == null);

        object settings = Create("SnapView.Core.Settings");
        string settingsPath = Path.Combine(folder, "settings.json");
        Action<bool> changed = enabled => { Set(settings, "CaptureBoundarySnap", enabled); File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, settings.GetType())); };
        Window Overlay(bool enabled, double scale = 1)
        {
            var purpose = Enum.Parse(AppAssembly.GetType("SnapView.Capture.OverlayPurpose")!, "Capture");
            var w = (Window)Create("SnapView.Capture.OverlayWindow", frozen, new Int32Rect(0, 0, 2000, 900), true, true, purpose, "", false, false, changed);
            var root = (FrameworkElement)w.Content;
            root.Width = 2000 / scale; root.Height = 900 / scale;
            root.Measure(new Size(root.Width, root.Height)); root.Arrange(new Rect(0, 0, root.Width, root.Height)); root.UpdateLayout();
            ((IList)Field(w, "_windows")!).Clear(); // Do not use the user's actual windows as synthetic snap candidates.
            PutField(w, "_boundaryAnalysis", analysis); PutField(w, "_boundaryAnalysisStarted", true);
            Call(w, "SetBoundarySnap", enabled); Call(w, "UpdateVisuals");
            return w;
        }
        var w = Overlay(true);
        try
        {
            Call(w, "HandlePointerMove", new Point(850, 400));
            Check("region mode never highlights a hovered window", ((FrameworkElement)w.FindName("Hole")).Visibility == Visibility.Collapsed && ((FrameworkElement)w.FindName("SelBorder")).Visibility == Visibility.Collapsed);
            Check("pin is visible before selecting", ((FrameworkElement)w.FindName("SnapOptions")).Visibility == Visibility.Visible && ((ToggleButton)w.FindName("PinBeforeSelection")).IsChecked == true);
            Call(w, "HandlePointerDown", new Point(850, 400), 1); Call(w, "HandlePointerUp", new Point(850, 400));
            Check("single region click does not capture the window", Field(w, "_phase")!.ToString() == "Idle" && ((Rect)Field(w, "_selection")!).IsEmpty && Prop(w, "Result") == null);
            Call(w, "HandlePointerDown", new Point(694, 146), 1); Call(w, "HandlePointerMove", new Point(1005, 775)); Call(w, "HandlePointerUp", new Point(1005, 775));
            Check("pin enables snapping all four drag edges", (Rect)Field(w, "_selection")! == new Rect(700, 150, 300, 620));
            Check("pin is also available in selection toolbar", ((FrameworkElement)w.FindName("ActionBar")).Visibility == Visibility.Visible && ((ToggleButton)w.FindName("PinAfterSelection")).IsChecked == true);
            // Resizing north and south handles must snap just like west and east.
            Call(w, "SetBoundarySnap", false);
            Call(w, "HandlePointerDown", new Point(850, 150), 1); Call(w, "HandlePointerUp", new Point(850, 146));
            Check("disabled pin leaves top handle exactly where released", Near(((Rect)Field(w, "_selection")!).Top, 146));
            Call(w, "SetBoundarySnap", true);
            Call(w, "HandlePointerDown", new Point(850, 146), 1); Call(w, "HandlePointerUp", new Point(850, 147));
            Check("north resize snaps on mouse release", Near(((Rect)Field(w, "_selection")!).Top, 150));
            Call(w, "HandlePointerDown", new Point(850, 770), 1); Call(w, "HandlePointerUp", new Point(850, 777));
            Check("south resize snaps on mouse release", Near(((Rect)Field(w, "_selection")!).Bottom, 770));
            if (preview != null) RenderPreview(w, preview);
            Call(w, "SetBoundarySnap", false);
            Check("both pin buttons show disabled state", ((ToggleButton)w.FindName("PinBeforeSelection")).IsChecked == false && ((ToggleButton)w.FindName("PinAfterSelection")).IsChecked == false);
        }
        finally { w.Close(); }
        var restored = JsonSerializer.Deserialize(File.ReadAllText(settingsPath), settings.GetType())!;
        var next = Overlay((bool)Prop(restored, "CaptureBoundarySnap")!);
        try
        {
            Check("next region capture restores disabled pin", !(bool)Field(next, "_boundarySnap")!);
            Call(next, "HandlePointerDown", new Point(694, 146), 1); Call(next, "HandlePointerUp", new Point(1005, 775));
            Check("pin off bypasses all content snapping on the next capture", (Rect)Field(next, "_selection")! == new Rect(694, 146, 311, 629));
            Check("pin off also bypasses monitor edge snapping", (Point)Call(next, "SnapOverlayPoint", new Point(4, 4), true, true)! == new Point(4, 4));
        }
        finally { next.Close(); }
        var scaled = Overlay(true, 1.5);
        try
        {
            Call(scaled, "HandlePointerDown", new Point(1005 / 1.5, 775 / 1.5), 1);
            Call(scaled, "HandlePointerUp", new Point(694 / 1.5, 146 / 1.5));
            Check("reverse drag snaps all edges at 150 percent DPI", (Int32Rect)Call(scaled, "ToPixels", (Rect)Field(scaled, "_selection")!)! == new Int32Rect(700, 150, 300, 620));
        }
        finally { scaled.Close(); }
    }
}
