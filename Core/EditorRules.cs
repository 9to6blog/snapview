using System;
using System.Collections.Generic;

namespace SnapView.Core
{
    /// <summary>리본 3줄에 어떤 묶음을 보일지. 도구와 지금 만지는 주석 종류에 따라 정한다.</summary>
    [Flags]
    internal enum EditorPanel
    {
        None = 0,
        Font = 1,          // 글꼴 · 크기 · 굵게 · 기울임 · 외곽선 · 바탕
        TextAlign = 2,     // 왼쪽 · 가운데 · 오른쪽
        Mask = 4,          // 가리기 세기(모자이크 블록 · 흐림 반경 · 마술봉 허용치)
        MaskShape = 8,     // 가리개·강조 모양(사각형 · 타원 · 갤러리)
        Fill = 16,         // 채우기 · 그라데이션
        Line = 32,         // 점선 · 무늬 · 그림자
        ArrowHead = 64,    // 촉 모양 · 양촉
        Magnifier = 128,   // 돋보기 배율
        Counter = 256,     // 다음 번호
        Crop = 512,        // 비율 프리셋 · 폭/높이
        Region = 1024      // 영역 선택 안내
    }

    /// <summary>
    /// "지금 무엇을 보여 줄까"의 규칙. 예전엔 현재 도구만 봐서, 선택 도구로 글자를 골라도
    /// 글꼴 패널이 꺼져 있어 글꼴을 못 바꿨다. 이제 골라 둔 주석의 종류도 같이 본다.
    /// </summary>
    internal static class EditorRules
    {
        internal static EditorPanel Panels(ToolKind tool, IEnumerable<Annotation> editable)
        {
            EditorPanel p = ForTool(tool);
            foreach (Annotation a in editable) p |= ForAnnotation(a);
            return p;
        }

        internal static EditorPanel ForTool(ToolKind tool) => tool switch
        {
            ToolKind.Text => EditorPanel.Font | EditorPanel.TextAlign,
            ToolKind.Mosaic or ToolKind.Blur => EditorPanel.Mask | EditorPanel.MaskShape,
            ToolKind.Wand => EditorPanel.Mask,
            ToolKind.Arrow => EditorPanel.ArrowHead | EditorPanel.Line,
            ToolKind.Line or ToolKind.Pen => EditorPanel.Line,
            ToolKind.Highlighter => EditorPanel.Line,
            ToolKind.Spotlight => EditorPanel.MaskShape,
            ToolKind.Magnifier => EditorPanel.Magnifier,
            ToolKind.Counter or ToolKind.NumberArrow => EditorPanel.Counter,
            ToolKind.Crop => EditorPanel.Crop,
            ToolKind.RegionSelect => EditorPanel.Region,
            _ => ShapeGeometry.IsBoxShape(tool) ? EditorPanel.Fill | EditorPanel.Line : EditorPanel.None
        };

        internal static EditorPanel ForAnnotation(Annotation a) => a switch
        {
            TextAnnotation => EditorPanel.Font | EditorPanel.TextAlign,
            PixelateAnnotation => EditorPanel.Mask | EditorPanel.MaskShape,
            SpotlightAnnotation => EditorPanel.MaskShape,
            MagnifierAnnotation => EditorPanel.Magnifier,
            CounterAnnotation or NumberArrowAnnotation => EditorPanel.Counter,
            PathAnnotation => EditorPanel.Line,
            ShapeAnnotation { Kind: ToolKind.Arrow } => EditorPanel.ArrowHead | EditorPanel.Line,
            ShapeAnnotation { Kind: ToolKind.Line } => EditorPanel.Line,
            ShapeAnnotation => EditorPanel.Fill | EditorPanel.Line,
            _ => EditorPanel.None
        };
    }
}
