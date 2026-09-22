using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapView.Core;

internal static partial class SelfTest
{
    private static byte[] EditorPixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(bytes, image.PixelWidth * 4, 0);
        return bytes;
    }

    private static void TestEditorRegressions(string tmp)
    {
        Section("편집기 회귀: 가리기 중첩 · 독립 크기/색상 · 선 스타일");
        const int w = 160, h = 120;
        var bytes = new byte[w * h * 4];
        var random = new Random(42);
        for (int i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = (byte)random.Next(256); bytes[i + 1] = (byte)random.Next(256);
            bytes[i + 2] = (byte)random.Next(256); bytes[i + 3] = 255;
        }
        BitmapSource source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bytes, w * 4);
        source.Freeze();
        var mosaic = new PixelateAnnotation { Start = new Point(), End = new Point(w, h), Strength = 24 };
        var blur = new PixelateAnnotation { Start = new Point(20, 20), End = new Point(140, 100), Strength = 8, UseBlur = true };
        BitmapSource masked = AnnotationRenderer.Flatten(source, new[] { mosaic });
        BitmapSource expected = AnnotationRenderer.Flatten(masked, new[] { blur });
        BitmapSource actual = AnnotationRenderer.Flatten(source, new Annotation[] { mosaic, blur });
        Check("모자이크 위 흐림은 가려진 픽셀을 읽음", EditorPixels(expected).SequenceEqual(EditorPixels(actual)));
        Check("흐림 단독 결과로 모자이크를 덮지 않음", !EditorPixels(actual).SequenceEqual(EditorPixels(AnnotationRenderer.Flatten(source, new[] { blur }))));
        var magnifier = new MagnifierAnnotation { SourceCenter = new Point(45, 50), Center = new Point(112, 66), Radius = 15, DisplayRadius = 32 };
        Check("돋보기도 모자이크 아래 원본을 드러내지 않음",
            EditorPixels(AnnotationRenderer.Flatten(masked, new[] { magnifier })).SequenceEqual(
                EditorPixels(AnnotationRenderer.Flatten(source, new Annotation[] { mosaic, magnifier }))));
        mosaic.Visible = false;
        Check("숨긴 가리개는 뒤의 흐림에 반영되지 않음",
            EditorPixels(AnnotationRenderer.Flatten(source, new[] { blur })).SequenceEqual(
                EditorPixels(AnnotationRenderer.Flatten(source, new Annotation[] { mosaic, blur }))));
        mosaic.Visible = true;
        var red = Solid(w, h, Colors.Red); var blue = Solid(w, h, Colors.Blue);
        AnnotationRenderer.Flatten(red, new[] { blur });
        Check("바탕 변경 후 흐림 캐시는 이전 픽셀을 남기지 않음",
            PixelAt(AnnotationRenderer.Flatten(blue, new[] { blur }), 80, 60).B == 255);

        magnifier.DragHandle(0, new Point(162, 66));
        Check("확대 창 크기만 독립 변경", magnifier.DisplayRadius == 50 && magnifier.Radius == 15);
        magnifier.DragHandle(2, new Point(65, 50));
        Check("원본 범위 크기만 독립 변경", magnifier.Radius == 20 && magnifier.DisplayRadius == 50 && magnifier.Zoom == 2.5);
        magnifier.DragHandle(1, new Point(48, 54));
        Check("원본 중심 이동은 두 크기를 유지", magnifier.SourceCenter == new Point(48, 54) && magnifier.DisplayRadius == 50 && magnifier.Radius == 20);
        var magClone = (MagnifierAnnotation)magnifier.Clone();
        Check("돋보기 복제는 독립 크기 유지", magClone.Radius == 20 && magClone.DisplayRadius == 50);
        magnifier.Scale(2, 2);
        Check("이미지 크기 변경은 돋보기 양쪽을 함께 확대", magnifier.Radius == 40 && magnifier.DisplayRadius == 100);

        var number = new NumberArrowAnnotation { Tip = new Point(40, 40), Center = new Point(40, 40), Thickness = 3 };
        number.EnsureVisibleArrow(new Vector(1, 1));
        Check("클릭만 해도 번호 화살촉이 원 밖에 보임", (number.Center - number.Tip).Length >= number.Radius + 27.9);
        Check("기본 화살촉을 늘려도 시작점의 번호는 고정", number.Center == new Point(40, 40));
        number.Tip = number.Center + new Vector(-2, 0); number.EnsureVisibleArrow(new Vector(1, 1));
        Check("짧은 드래그에서도 번호는 고정하고 끝에 화살촉 노출", number.Tip.X < number.Center.X - number.Radius && number.Center == new Point(40, 40));
        Point distant = number.Tip = new Point(150, 60); number.EnsureVisibleArrow(new Vector(1, 1));
        Check("충분히 긴 번호 화살표는 그대로 유지", number.Tip == distant);
        Check("원본 원 안쪽 전체가 돋보기 이동 대상", magClone.PartAt(new Point(48, 69)) == MagnifierPart.Source);
        Check("원본 원 테두리도 돋보기 이동 대상", magClone.PartAt(new Point(28, 54)) == MagnifierPart.Source);
        Check("확대 창 내부는 확대 창 이동 대상", magClone.PartAt(magClone.Center) == MagnifierPart.Display);
        Check("두 원 바깥은 이동 대상 아님", magClone.PartAt(new Point(300, 300)) == MagnifierPart.None);

        var shape = new ShapeAnnotation { Kind = ToolKind.Rectangle, Start = new Point(12, 12), End = new Point(148, 108), Color = Colors.Red, FillColor = Colors.Blue, Thickness = 4, Filled = true };
        var filled = AnnotationRenderer.Flatten(Solid(w, h, Colors.White), new[] { shape });
        Check("채우기 색은 파랑", PixelAt(filled, 80, 60).B == 255 && PixelAt(filled, 80, 60).R == 0);
        Check("테두리 색은 별도 빨강", PixelAt(filled, 12, 60).R == 255 && PixelAt(filled, 12, 60).B == 0);
        Check("채우기 색 복제 유지", ((ShapeAnnotation)shape.Clone()).FillColor == Colors.Blue);
        shape.GradientFill = true;
        Color gradient = PixelAt(AnnotationRenderer.Flatten(Solid(w, h, Colors.White), new[] { shape }), 80, 45);
        Check("그라데이션도 채우기 색 사용", gradient.B > gradient.R);
        string path = Path.Combine(tmp, "independent-styles.snapview");
        ProjectFile.Save(path, source, new Annotation[] { shape, magClone }, 2);
        var loaded = ProjectFile.Load(path);
        Check("프로젝트 채우기 색 왕복", ((ShapeAnnotation)loaded.Items[0]).FillColor == Colors.Blue);
        Check("프로젝트 독립 돋보기 크기 왕복", loaded.Items[1] is MagnifierAnnotation m && m.Radius == 20 && m.DisplayRadius == 50);
        var smallLens = new MagnifierAnnotation { Radius = 1000, DisplayRadius = 16 };
        ProjectFile.Save(path, source, new[] { smallLens }, 1);
        Check("큰 원본 범위와 작은 창도 저장 후 크기 유지", ProjectFile.Load(path).Items[0] is MagnifierAnnotation small && small.Radius == 1000 && small.DisplayRadius == 16);
        shape.FillColor = null; shape.GradientFill = false;
        Check("기존 도형은 선 색으로 채우기 호환", PixelAt(AnnotationRenderer.Flatten(blue, new[] { shape }), 80, 60).R == 255);

        var white = Solid(w, h, Colors.White);
        foreach (ToolKind kind in new[] { ToolKind.Line, ToolKind.Rectangle, ToolKind.Triangle, ToolKind.Ellipse })
        {
            var line = new ShapeAnnotation { Kind = kind, Start = new Point(12, 20), End = new Point(148, kind == ToolKind.Line ? 20 : 100), Thickness = 2, Color = Colors.Black };
            byte[] solid = EditorPixels(AnnotationRenderer.Flatten(white, new[] { line }));
            byte[]? last = null;
            foreach (DashPattern pattern in Enum.GetValues<DashPattern>())
            {
                line.Dashed = true; line.DashPattern = pattern;
                byte[] dash = EditorPixels(AnnotationRenderer.Flatten(white, new[] { line }));
                Check($"{kind} {pattern} 실제 점선 픽셀", !solid.SequenceEqual(dash) && (last == null || !last.SequenceEqual(dash)));
                last = dash;
            }
        }
        var pen = new PathAnnotation { Highlighter = true, Blend = BlendMode.Multiply, Dashed = true, Thickness = 2, Points = { new Point(10, 60), new Point(150, 60) } };
        byte[] dashPen = EditorPixels(AnnotationRenderer.Flatten(white, new[] { pen }));
        pen.DashPattern = DashPattern.Dot;
        Check("형광펜 점선 종류 변경은 캐시에도 반영", !dashPen.SequenceEqual(EditorPixels(AnnotationRenderer.Flatten(white, new[] { pen }))));
        foreach (bool numbered in new[] { false, true })
        {
            Annotation arrow = numbered
                ? new NumberArrowAnnotation { Center = new Point(25, 60), Tip = new Point(145, 60), Color = Colors.Black }
                : new ShapeAnnotation { Kind = ToolKind.Arrow, Start = new Point(25, 60), End = new Point(145, 60), Color = Colors.Black };
            int CountInk(double thickness)
            {
                arrow.Thickness = thickness;
                BitmapSource rendered = AnnotationRenderer.Flatten(white, new[] { arrow });
                int count = 0;
                for (int y = 40; y < 80; y++) for (int x = 137; x < 146; x++) if (PixelAt(rendered, x, y).R < 128) count++;
                return count;
            }
            int thin = CountInk(1), medium = CountInk(2), thick = CountInk(3);
            Check($"{(numbered ? "번호 " : "")}화살촉은 1·2·3 굵기에서 커짐", thin < medium && medium < thick, $"{thin}, {medium}, {thick}");
        }
    }
}
