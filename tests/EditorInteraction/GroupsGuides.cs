using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

static partial class Program
{
    static object Annotation(string type) => Activator.CreateInstance(AppAssembly.GetType("SnapView.Core." + type)!, true)!;
    static void PutField(object o, string name, object value) => o.GetType().GetField(name, Members)!.SetValue(o, value);
    static IList Selection(Window w) => (IList)Prop(Canvas(w), "SelectedMany")!;
    static void TestGroupsAndGuides(string folder, string? preview)
    {
        var w = Editor(folder);
        var list = (ListBox)w.FindName("LayerList");
        Check("new image starts with visible layer zero", list.Items.Count == 1 && ((FrameworkElement)list.Items[0]).ToolTip.ToString()!.Contains("레이어 0"));
        object shape = Annotation("ShapeAnnotation");
        Set(shape, "Start", new Point(120, 140)); Set(shape, "End", new Point(250, 260));
        Set(shape, "Kind", Enum.Parse(AppAssembly.GetType("SnapView.Core.ToolKind")!, "Rectangle"));
        object text = Annotation("TextAnnotation"); Set(text, "Origin", new Point(280, 180)); Set(text, "Text", "그룹 안의 텍스트");
        Items(w).Add(shape); Items(w).Add(text); Selection(w).Add(shape); Selection(w).Add(text);
        Call(w, "GroupSelection");
        string id = (string)Prop(shape, "GroupId")!;
        Check("grouping keeps separate editable layers", Items(w).Count == 2 && !string.IsNullOrEmpty(id) && (string)Prop(text, "GroupId")! == id);
        Check("grouping preserves original draw order", ReferenceEquals(Items(w)[0], shape) && ReferenceEquals(Items(w)[1], text));
        Call(w, "SetSelection", shape);
        Check("picking a group member selects the whole group", Selection(w).Count == 2);
        Vector spacing = PointOf(text, "Origin") - PointOf(shape, "Start");
        PutField(w, "_alignToBackground", true);
        var alignType = AppAssembly.GetType("SnapView.Core.AlignMode")!;
        Call(w, "AlignSelected", Enum.Parse(alignType, "CenterH"));
        Rect a = (Rect)Prop(shape, "Bounds")!, b = (Rect)Prop(text, "Bounds")!; a.Union(b);
        Check("group centers against background image", Near(a.X + a.Width / 2, 400));
        Check("background alignment preserves spacing within group", Near(PointOf(text, "Origin"), PointOf(shape, "Start") + spacing));
        Call(w, "AlignSelected", Enum.Parse(alignType, "Bottom"));
        a = (Rect)Prop(shape, "Bounds")!; a.Union((Rect)Prop(text, "Bounds")!);
        Check("group bottom aligns to background", Near(a.Bottom, 480));
        Call(w, "DuplicateSelection", new Vector(16, 16));
        Check("duplicate gets its own group identity", Items(w).Count == 4 && (string)Prop(Items(w)[2]!, "GroupId")! != id && Equals(Prop(Items(w)[2]!, "GroupId"), Prop(Items(w)[3]!, "GroupId")));
        Call(w, "Undo"); shape = Items(w)[0]!; text = Items(w)[1]!;
        Check("undo group duplication preserves the original group", Items(w).Count == 2 && Equals(Prop(shape, "GroupId"), Prop(text, "GroupId")));
        Call(w, "SetSingleSelection", text);
        Call(w, "AlignSelected", Enum.Parse(alignType, "Left"));
        Check("a layer within a group can align individually", Near(((Rect)Prop(text, "Bounds")!).Left, 0) && !Near(((Rect)Prop(shape, "Bounds")!).Left, 0));
        Call(w, "SetSelection", shape); Call(w, "UngroupSelection");
        Check("ungroup retains objects and clears membership", Items(w).Count == 2 && Prop(shape, "GroupId") == null && Prop(text, "GroupId") == null);
        Call(w, "Undo"); shape = Items(w)[0]!; text = Items(w)[1]!; Call(w, "SetSelection", shape);
        Check("undo restores group membership", Prop(shape, "GroupId") != null && Equals(Prop(shape, "GroupId"), Prop(text, "GroupId")));
        id = (string)Prop(shape, "GroupId")!;
        ((HashSet<string>)Field(w, "_collapsedGroups")!).Add(id); Call(w, "RefreshLayers");
        Check("collapsed group retains header and background row", list.Items.Count == 2 && ((FrameworkElement)list.Items[0]).Tag is string);
        ((HashSet<string>)Field(w, "_collapsedGroups")!).Clear(); Call(w, "RefreshLayers");
        Check("expanded group exposes editable child rows", list.Items.Count == 4);

        double scale = (double)Prop(Canvas(w), "Scale")!;
        Call(w, "StartGuide", false, new Point(300 * scale, 50 * scale)); Call(w, "FinishGuide", new Point(300 * scale, 50 * scale));
        Call(w, "StartGuide", true, new Point(50 * scale, 220 * scale)); Call(w, "FinishGuide", new Point(50 * scale, 220 * scale));
        IList guides = (IList)Prop(Canvas(w), "ManualGuides")!;
        Check("rulers add horizontal and vertical guides", guides.Count == 2);
        var cells = (IEnumerable)AppAssembly.GetType("SnapView.Core.GuideMeasurements")!.GetMethod("Cells", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { guides, 800.0, 480.0 })!;
        Rect[] regions = cells.Cast<Rect>().ToArray();
        Check("guide measurements include image edges and all four regions", regions.Length == 4 && regions[0] == new Rect(0, 0, 300, 220) && regions[3] == new Rect(300, 220, 500, 260));
        Call(w, "StartDiagonalGuide", new Point(100, 100)); Call(w, "FinishGuide", new Point(400 * scale, 500 * scale));
        Check("guide dropped outside image is removed", guides.Count == 2);
        Call(w, "StartDiagonalGuide", new Point(100, 50)); Call(w, "FinishGuide", new Point(400 * scale, 450 * scale));
        Check("diagonal uses image pixel length independent of zoom", guides.Count == 3 && Near((double)Prop(guides[2]!, "Length")!, 500));
        Call(w, "Undo"); guides = (IList)Prop(Canvas(w), "ManualGuides")!;
        Check("guide creation is undoable", guides.Count == 2);
        Call(w, "Redo"); guides = (IList)Prop(Canvas(w), "ManualGuides")!;
        Check("guide redo restores diagonal geometry", guides.Count == 3 && Near((double)Prop(guides[2]!, "Length")!, 500));

        Directory.CreateDirectory(folder); string path = Path.Combine(folder, "groups-guides.snapview");
        Type project = AppAssembly.GetType("SnapView.Core.ProjectFile")!;
        project.GetMethod("Save", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { path, Field(w, "_image"), Items(w), 1, guides });
        object?[] args = { path, null };
        object loaded = project.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(m => m.Name == "Load" && m.GetParameters().Length == 2).Invoke(null, args)!;
        IList loadedItems = (IList)loaded.GetType().GetField("Item2")!.GetValue(loaded)!;
        Check("project round-trip preserves groups", Prop(loadedItems[0]!, "GroupId") != null && Equals(Prop(loadedItems[0]!, "GroupId"), Prop(loadedItems[1]!, "GroupId")));
        Check("project round-trip preserves diagonal and axis guides", ((IList)args[1]!).Count == 3 && Near((double)Prop(((IList)args[1]!)[2]!, "Length")!, 500));
        Call(w, "SaveResult", false); string png = (string)Field(w, "_imageSavePath")!; byte[] before = File.ReadAllBytes(png);
        ((ToggleButton)w.FindName("TbRulers")).IsChecked = false; Call(w, "OnRulersToggled", w, new RoutedEventArgs());
        Call(w, "SaveResult", false);
        Check("guides and dimensions never enter image output", before.SequenceEqual(File.ReadAllBytes(png)));
        ((ToggleButton)w.FindName("TbRulers")).IsChecked = true; Call(w, "OnRulersToggled", w, new RoutedEventArgs());
        Call(w, "HideSavedToast"); Call(w, "SetSelection", Items(w)[0]); Call(w, "UpdateStatus");
        if (preview != null) RenderPreview(w, preview);
    }
}
