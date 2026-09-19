using System;
using System.Windows;

namespace SnapView.Core
{
    /// <summary>
    /// 좌표·배율 계산만 모아 둔 순수 함수 모음.
    /// 창을 띄우지 않고 콘솔에서 검증할 수 있도록 UI 에서 떼어 놓았다.
    /// </summary>
    internal static class ViewMath
    {
        /// <summary>DIP 사각형을 비트맵 픽셀 사각형으로. 항상 이미지 안쪽으로 잘라 준다.</summary>
        internal static Int32Rect ToPixels(Rect dip, double pxPerDipX, double pxPerDipY, int bmpW, int bmpH)
        {
            if (bmpW <= 0 || bmpH <= 0) return new Int32Rect(0, 0, 0, 0);

            int x = (int)Math.Round(dip.X * pxPerDipX);
            int y = (int)Math.Round(dip.Y * pxPerDipY);
            int w = (int)Math.Round(dip.Width * pxPerDipX);
            int h = (int)Math.Round(dip.Height * pxPerDipY);

            x = Math.Clamp(x, 0, bmpW - 1);
            y = Math.Clamp(y, 0, bmpH - 1);
            w = Math.Clamp(w, 1, bmpW - x);
            h = Math.Clamp(h, 1, bmpH - y);
            return new Int32Rect(x, y, w, h);
        }

        /// <summary>
        /// 드래그 중인 선택 사각형의 네 변을 각각 자석 후보에 맞춘다.
        /// 움직이는 끝점만 맞추면 시작점 쪽(보통 왼쪽·위쪽)은 분석이 늦게 끝났을 때
        /// 영원히 다시 검사되지 않으므로, 좌·우·위·아래를 매번 독립적으로 계산한다.
        /// </summary>
        internal static Rect SnapRectEdges(Rect raw, Func<Point, bool, bool, Point> snapPoint)
        {
            if (raw.IsEmpty || raw.Width <= 0 || raw.Height <= 0) return raw;

            double centerX = raw.Left + raw.Width / 2;
            double centerY = raw.Top + raw.Height / 2;
            double left = snapPoint(new Point(raw.Left, centerY), true, false).X;
            double right = snapPoint(new Point(raw.Right, centerY), true, false).X;
            double top = snapPoint(new Point(centerX, raw.Top), false, true).Y;
            double bottom = snapPoint(new Point(centerX, raw.Bottom), false, true).Y;

            // 아주 작은 영역에서 양쪽이 같은 선으로 붙는 경우에는 해당 축만 원래 값으로
            // 돌린다. 자석 때문에 선택 영역이 0px로 사라지면 안 된다.
            if (right <= left) { left = raw.Left; right = raw.Right; }
            if (bottom <= top) { top = raw.Top; bottom = raw.Bottom; }
            return new Rect(left, top, right - left, bottom - top);
        }

        /// <summary>
        /// 표시 배율(이미지 픽셀 : 화면 물리 픽셀)을 구한다.
        /// stage 크기와 회전 후 이미지 크기는 각각 DIP / 이미지 픽셀 단위다.
        /// </summary>
        internal static double FitZoom(FitMode mode, double stageW, double stageH,
                                       double rotW, double rotH, double dpi,
                                       double minZoom, double maxZoom)
        {
            if (rotW <= 0 || rotH <= 0 || stageW <= 0 || stageH <= 0) return 1.0;

            double zoom = mode switch
            {
                FitMode.Actual => 1.0,
                FitMode.FitWidth => stageW / rotW * dpi,
                FitMode.StretchToFit => Math.Min(stageW / rotW, stageH / rotH) * dpi,
                // 기본값: 창보다 클 때만 줄인다. 작은 그림은 원본 크기 그대로.
                _ => Math.Min(Math.Min(stageW / rotW, stageH / rotH) * dpi, 1.0)
            };
            return Math.Clamp(zoom, minZoom, maxZoom);
        }

        /// <summary>
        /// 표시 내용이 화면 밖으로 새지 않게 원점을 붙든다.
        /// 화면보다 작으면 가운데로 정렬한다.
        /// </summary>
        internal static (double X, double Y) ClampOrigin(double originX, double originY,
                                                         double dispW, double dispH,
                                                         double stageW, double stageH)
        {
            double x = dispW <= stageW ? (stageW - dispW) / 2 : Math.Clamp(originX, stageW - dispW, 0);
            double y = dispH <= stageH ? (stageH - dispH) / 2 : Math.Clamp(originY, stageH - dispH, 0);
            return (x, y);
        }

        /// <summary>
        /// 확대·축소 후에도 기준점(보통 커서) 밑에 있던 내용이 그대로 그 자리에 남도록
        /// 새 원점을 계산한다.
        /// </summary>
        internal static (double X, double Y) ZoomAnchor(double anchorX, double anchorY,
                                                        double originX, double originY,
                                                        double oldEff, double newEff)
        {
            if (oldEff <= 0) return (originX, originY);

            double ux = (anchorX - originX) / oldEff;
            double uy = (anchorY - originY) / oldEff;
            return (anchorX - ux * newEff, anchorY - uy * newEff);
        }

        /// <summary>
        /// 회전·확대를 거친 뒤 표시 내용의 좌상단이 (originX, originY) 에 오도록 하는
        /// TranslateTransform 값. 회전은 이미지 중심을 기준으로 한다.
        /// </summary>
        internal static (double X, double Y) TranslationFor(double originX, double originY,
                                                            double imgW, double imgH,
                                                            double rotW, double rotH, double eff)
        {
            return (originX - eff * (imgW / 2 - rotW / 2),
                    originY - eff * (imgH / 2 - rotH / 2));
        }

        /// <summary>
        /// 떠 있는 것(도구 막대·말풍선) 하나를 <b>모니터 하나</b> 안으로 밀어 넣는다.
        ///
        /// 가상 화면 안으로만 가두면 안 된다. 모니터 크기가 서로 다르면 가상 화면에는
        /// 어느 모니터에도 속하지 않는 빈 구역이 생기고(2560×1440 옆에 1920×1080 을 붙이면
        /// 오른쪽 아래 360줄이 그렇다), 거기 놓인 것은 아무 데도 안 보인다.
        ///
        /// 모니터보다 큰 것은 어차피 다 못 담으므로 왼쪽·위를 맞춘다 —
        /// 잘리더라도 시작 부분은 보여야 한다.
        /// </summary>
        internal static (double Left, double Top) ClampInto(
            double monLeft, double monTop, double monWidth, double monHeight,
            double left, double top, double width, double height)
        {
            left = monWidth >= width
                ? Math.Clamp(left, monLeft, monLeft + monWidth - width)
                : monLeft;

            top = monHeight >= height
                ? Math.Clamp(top, monTop, monTop + monHeight - height)
                : monTop;

            return (left, top);
        }
    }
}
