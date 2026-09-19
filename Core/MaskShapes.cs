using System.Windows;
using System.Windows.Media;

namespace SnapView.Core
{
    /// <summary>
    /// 가리개(모자이크·흐림)와 강조가 사각형 말고 다른 모양으로 오려 낼 때 쓰는 윤곽.
    /// 도형 갤러리의 지오메트리를 그대로 빌려 쓰므로, 갤러리에 도형이 늘면 여기도 같이 는다.
    /// </summary>
    internal static class MaskShapes
    {
        internal static Geometry Outline(ToolKind kind, Rect b)
        {
            Geometry g = kind switch
            {
                ToolKind.Ellipse => new EllipseGeometry(new Point(b.X + b.Width / 2, b.Y + b.Height / 2),
                                                        b.Width / 2, b.Height / 2),
                ToolKind.Rectangle => new RectangleGeometry(b),
                _ => ShapeGeometry.Build(kind, b) ?? new RectangleGeometry(b)
            };
            if (g.CanFreeze && !g.IsFrozen) g.Freeze();
            return g;
        }

        /// <summary>가리개·강조 모양으로 고를 수 있는 것: 사각형·타원 + 갤러리 전부.</summary>
        internal static bool IsMaskShape(ToolKind kind)
            => kind is ToolKind.Rectangle or ToolKind.Ellipse || ShapeGeometry.IsBoxShape(kind);
    }
}
