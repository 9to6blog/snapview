using System.Windows;
using System.Windows.Media;

namespace SnapView.Editor
{
    /// <summary>버튼의 글자·접근성 이름을 유지하면서 템플릿에 벡터 아이콘을 전달한다.</summary>
    public static class ToolbarIcon
    {
        public static readonly DependencyProperty DataProperty = DependencyProperty.RegisterAttached(
            "Data", typeof(Geometry), typeof(ToolbarIcon), new PropertyMetadata(null));

        public static void SetData(DependencyObject element, Geometry value) => element.SetValue(DataProperty, value);
        public static Geometry GetData(DependencyObject element) => (Geometry)element.GetValue(DataProperty);
    }
}
