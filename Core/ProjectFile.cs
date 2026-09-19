using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapView.Core
{
    /// <summary>
    /// 편집 상태를 <b>레이어 살린 채</b> 저장하는 프로젝트 파일(.snapview).
    /// PNG 로 합쳐 버리면 주석을 다시 못 만진다 — 이 파일로 열면 그대로 이어서 편집한다.
    ///
    /// 속은 JSON 하나다: 바탕 그림(PNG를 base64로)과 주석들의 값. 형식에 버전을 적어
    /// 두므로 나중에 필드가 늘어도 옛 파일을 읽을 수 있다. 모르는 필드는 조용히 무시한다.
    /// </summary>
    internal static class ProjectFile
    {
        internal const string Extension = ".snapview";
        internal const string Filter = "SnapView 프로젝트|*.snapview";

        private const int CurrentVersion = 1;

        private sealed class Root
        {
            public string App { get; set; } = "SnapView";
            public int Version { get; set; } = CurrentVersion;
            public int Counter { get; set; } = 1;
            public string Image { get; set; } = "";
            public List<Item> Items { get; set; } = new();
        }

        /// <summary>모든 주석 종류의 필드를 한 자루에 담는다. 종류마다 쓰는 것만 채운다.</summary>
        private sealed class Item
        {
            public string Type { get; set; } = "";

            // 공통
            public string Color { get; set; } = "#FFFF0000";
            public double Thickness { get; set; } = 3;
            public double Opacity { get; set; } = 1;
            public double RotationDeg { get; set; }
            public bool Visible { get; set; } = true;
            public bool Shadow { get; set; }
            public bool Dashed { get; set; }
            public bool Locked { get; set; }
            public string? Name { get; set; }

            // 두 점짜리 (도형·가리개·강조·자르기·그림)
            public double? X1 { get; set; }
            public double? Y1 { get; set; }
            public double? X2 { get; set; }
            public double? Y2 { get; set; }

            public string? Kind { get; set; }
            public bool? Filled { get; set; }
            public bool? BothArrows { get; set; }
            public bool? GradientFill { get; set; }
            public string? Blend { get; set; }

            /// <summary>대상 지우개: 파는 대상 주석의 목록 인덱스.</summary>
            public int? EraseTarget { get; set; }

            public List<double>? Points { get; set; }
            public bool? Highlighter { get; set; }

            public string? Text { get; set; }
            public double? FontSize { get; set; }
            public string? FontFamily { get; set; }
            public bool? Bold { get; set; }
            public bool? Italic { get; set; }
            public bool? OutlineHalo { get; set; }
            public bool? TextBackground { get; set; }

            public bool? UseBlur { get; set; }
            public int? Strength { get; set; }

            public double? CX { get; set; }
            public double? CY { get; set; }
            public double? Radius { get; set; }
            public int? Number { get; set; }
            public double? TipX { get; set; }
            public double? TipY { get; set; }

            public bool? EllipseHole { get; set; }

            // 2판(2026-09)에서 늘어난 것. 없으면 옛 기본값으로 읽는다.
            public string? Head { get; set; }
            public string? DashPattern { get; set; }
            public string? Align { get; set; }
            public double? MaxWidth { get; set; }

            public double? SrcX { get; set; }
            public double? SrcY { get; set; }
            public double? Zoom { get; set; }

            public string? ImagePng { get; set; }
        }

        private static readonly JsonSerializerOptions Options = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        // ===================== 저장 =====================

        internal static void Save(string path, BitmapSource image,
                                  IEnumerable<Annotation> items, int counter)
        {
            var list = new List<Annotation>(items);
            var root = new Root
            {
                Counter = counter,
                Image = Convert.ToBase64String(ImageIO.EncodePng(image))
            };

            foreach (Annotation a in list)
            {
                Item? it = ToItem(a);
                if (it == null) continue;

                if (a.SupportsBlend && a.Blend != BlendMode.Normal)
                    it.Blend = a.Blend.ToString();
                if (a is EraseAnnotation { Target: not null } e)
                {
                    int idx = list.IndexOf(e.Target);
                    if (idx >= 0) it.EraseTarget = idx;
                }
                root.Items.Add(it);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(root, Options));
        }

        private static Item? ToItem(Annotation a)
        {
            var it = new Item
            {
                Color = ToHex(a.Color),
                Thickness = a.Thickness,
                Opacity = a.Opacity,
                RotationDeg = a.RotationDeg,
                Visible = a.Visible,
                Shadow = a.Shadow,
                Dashed = a.Dashed,
                DashPattern = a.Dashed && a.DashPattern != SnapView.Core.DashPattern.Dash
                    ? a.DashPattern.ToString() : null,
                Locked = a.Locked,
                Name = a.Name
            };

            switch (a)
            {
                case ShapeAnnotation s:
                    it.Type = "shape";
                    it.Kind = s.Kind.ToString();
                    (it.X1, it.Y1, it.X2, it.Y2) = (s.Start.X, s.Start.Y, s.End.X, s.End.Y);
                    it.Filled = s.Filled;
                    it.BothArrows = s.BothArrows;
                    it.GradientFill = s.GradientFill;
                    if (s.Head != ArrowHead.Filled) it.Head = s.Head.ToString();
                    return it;

                case PathAnnotation p:
                    it.Type = "path";
                    it.Highlighter = p.Highlighter;
                    it.Points = new List<double>(p.Points.Count * 2);
                    foreach (Point q in p.Points) { it.Points.Add(q.X); it.Points.Add(q.Y); }
                    return it;

                case TextAnnotation t:
                    it.Type = "text";
                    (it.X1, it.Y1) = (t.Origin.X, t.Origin.Y);
                    it.Text = t.Text;
                    it.FontSize = t.FontSize;
                    it.FontFamily = t.FontFamilyName;
                    it.Bold = t.Bold;
                    it.Italic = t.Italic;
                    it.OutlineHalo = t.OutlineHalo;
                    it.TextBackground = t.Background;
                    if (t.MaxWidth > 0) it.MaxWidth = t.MaxWidth;
                    if (t.Align != TextAlign.Left) it.Align = t.Align.ToString();
                    return it;

                case PixelateAnnotation x:
                    it.Type = "pixelate";
                    (it.X1, it.Y1, it.X2, it.Y2) = (x.Start.X, x.Start.Y, x.End.X, x.End.Y);
                    it.UseBlur = x.UseBlur;
                    it.Strength = x.Strength;
                    if (x.Shape != ToolKind.Rectangle) it.Kind = x.Shape.ToString();
                    return it;

                case CounterAnnotation c:
                    it.Type = "counter";
                    (it.CX, it.CY) = (c.Center.X, c.Center.Y);
                    it.Radius = c.Radius;
                    it.Number = c.Number;
                    return it;

                case NumberArrowAnnotation na:
                    it.Type = "numberArrow";
                    (it.CX, it.CY) = (na.Center.X, na.Center.Y);
                    (it.TipX, it.TipY) = (na.Tip.X, na.Tip.Y);
                    it.Radius = na.Radius;
                    it.Number = na.Number;
                    return it;

                case SpotlightAnnotation sp:
                    it.Type = "spotlight";
                    (it.X1, it.Y1, it.X2, it.Y2) = (sp.Start.X, sp.Start.Y, sp.End.X, sp.End.Y);
                    it.EllipseHole = sp.Ellipse;
                    if (sp.Shape != ToolKind.Rectangle) it.Kind = sp.Shape.ToString();
                    return it;

                case MagnifierAnnotation m:
                    it.Type = "magnifier";
                    (it.CX, it.CY) = (m.Center.X, m.Center.Y);
                    (it.SrcX, it.SrcY) = (m.SourceCenter.X, m.SourceCenter.Y);
                    it.Radius = m.Radius;
                    it.Zoom = m.Zoom;
                    return it;

                case ImageAnnotation img when img.Image != null:
                    it.Type = "image";
                    (it.X1, it.Y1, it.X2, it.Y2) = (img.Start.X, img.Start.Y, img.End.X, img.End.Y);
                    it.ImagePng = Convert.ToBase64String(ImageIO.EncodePng(img.Image));
                    return it;

                case EraseAnnotation e:
                    it.Type = "erase";
                    it.Radius = e.Radius;
                    it.Points = new List<double>(e.Points.Count * 2);
                    foreach (Point q in e.Points) { it.Points.Add(q.X); it.Points.Add(q.Y); }
                    return it;
            }
            return null;   // 모르는 종류는 못 담는다(새 버전 파일을 옛 판이 읽을 때의 반대 방향)
        }

        // ===================== 열기 =====================

        internal static (BitmapSource Image, List<Annotation> Items, int Counter) Load(string path)
        {
            Root root = JsonSerializer.Deserialize<Root>(File.ReadAllText(path), Options)
                        ?? throw new InvalidOperationException("프로젝트 파일이 비어 있습니다");
            if (root.App != "SnapView")
                throw new InvalidOperationException("SnapView 프로젝트 파일이 아닙니다");

            BitmapSource image = DecodePng(root.Image);
            var items = new List<Annotation>();
            var pending = new List<(EraseAnnotation Erase, int TargetIndex)>();

            foreach (Item it in root.Items)
            {
                Annotation? a = FromItem(it);
                if (a == null) continue;

                if (a.SupportsBlend && it.Blend != null &&
                    Enum.TryParse(it.Blend, out BlendMode bm))
                    a.Blend = bm;
                if (a is EraseAnnotation er && it.EraseTarget.HasValue)
                    pending.Add((er, it.EraseTarget.Value));

                items.Add(a);
            }

            // 대상 지우개의 대상을 인덱스로 다시 잇는다(전부 만들어진 뒤에야 가능).
            foreach ((EraseAnnotation er, int idx) in pending)
                if (idx >= 0 && idx < items.Count) er.Target = items[idx];

            return (image, items, Math.Max(1, root.Counter));
        }

        private static BitmapSource DecodePng(string base64)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat,
                                               BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (!frame.IsFrozen) frame.Freeze();
            return frame;
        }

        private static Annotation? FromItem(Item it)
        {
            Point P1() => new(it.X1 ?? 0, it.Y1 ?? 0);
            Point P2() => new(it.X2 ?? 0, it.Y2 ?? 0);

            Annotation? a = it.Type switch
            {
                "shape" => new ShapeAnnotation
                {
                    Kind = Enum.TryParse(it.Kind, out ToolKind k) ? k : ToolKind.Rectangle,
                    Start = P1(), End = P2(),
                    Filled = it.Filled ?? false,
                    BothArrows = it.BothArrows ?? false,
                    GradientFill = it.GradientFill ?? false,
                    Head = Enum.TryParse(it.Head, out ArrowHead head) ? head : ArrowHead.Filled
                },
                "path" => MakePath(it),
                "text" => new TextAnnotation
                {
                    Origin = P1(),
                    Text = it.Text ?? "",
                    FontSize = it.FontSize ?? 22,
                    FontFamilyName = it.FontFamily ?? TextAnnotation.DefaultFontFamily,
                    Bold = it.Bold ?? true,
                    Italic = it.Italic ?? false,
                    OutlineHalo = it.OutlineHalo ?? true,
                    Background = it.TextBackground ?? false,
                    MaxWidth = it.MaxWidth ?? 0,
                    Align = Enum.TryParse(it.Align, out TextAlign align) ? align : TextAlign.Left
                },
                "pixelate" => new PixelateAnnotation
                {
                    Start = P1(), End = P2(),
                    UseBlur = it.UseBlur ?? false,
                    Strength = it.Strength ?? 12,
                    Shape = Enum.TryParse(it.Kind, out ToolKind maskShape) ? maskShape : ToolKind.Rectangle
                },
                "counter" => new CounterAnnotation
                {
                    Center = new Point(it.CX ?? 0, it.CY ?? 0),
                    Radius = it.Radius ?? 16,
                    Number = it.Number ?? 1
                },
                "numberArrow" => new NumberArrowAnnotation
                {
                    Center = new Point(it.CX ?? 0, it.CY ?? 0),
                    Tip = new Point(it.TipX ?? 0, it.TipY ?? 0),
                    Radius = it.Radius ?? 16,
                    Number = it.Number ?? 1
                },
                "spotlight" => new SpotlightAnnotation
                {
                    Start = P1(), End = P2(),
                    Shape = Enum.TryParse(it.Kind, out ToolKind holeShape) ? holeShape
                          : (it.EllipseHole == true ? ToolKind.Ellipse : ToolKind.Rectangle)
                },
                "magnifier" => new MagnifierAnnotation
                {
                    Center = new Point(it.CX ?? 0, it.CY ?? 0),
                    SourceCenter = new Point(it.SrcX ?? 0, it.SrcY ?? 0),
                    Radius = it.Radius ?? 45,
                    Zoom = it.Zoom ?? 2
                },
                "image" when it.ImagePng != null => new ImageAnnotation
                {
                    Image = DecodePng(it.ImagePng),
                    Start = P1(), End = P2()
                },
                "erase" => MakeErase(it),
                _ => null   // 모르는 종류(새 판에서 만든 것)는 건너뛴다
            };
            if (a == null) return null;

            a.Color = FromHex(it.Color);
            a.Thickness = it.Thickness;
            a.Opacity = it.Opacity;
            a.RotationDeg = it.RotationDeg;
            a.Visible = it.Visible;
            a.Shadow = it.Shadow;
            a.Dashed = it.Dashed;
            if (Enum.TryParse(it.DashPattern, out DashPattern dashPattern)) a.DashPattern = dashPattern;
            a.Locked = it.Locked;
            a.Name = it.Name;
            return a;
        }

        private static PathAnnotation MakePath(Item it)
        {
            var p = new PathAnnotation { Highlighter = it.Highlighter ?? false };
            AddPoints(it, q => p.Points.Add(q));
            return p;
        }

        private static EraseAnnotation MakeErase(Item it)
        {
            var e = new EraseAnnotation { Radius = it.Radius ?? 10 };
            AddPoints(it, e.Add);
            return e;
        }

        private static void AddPoints(Item it, Action<Point> add)
        {
            if (it.Points == null) return;
            for (int i = 0; i + 1 < it.Points.Count; i += 2)
                add(new Point(it.Points[i], it.Points[i + 1]));
        }

        private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Color FromHex(string s)
        {
            try { return (Color)ColorConverter.ConvertFromString(s); }
            catch { return Colors.Red; }
        }
    }
}
