using System;
using System.Globalization;

namespace SnapView.Core
{
    /// <summary>창의 자리·크기·최대화 여부를 설정 문자열 하나로. 편집기가 열릴 때마다 같은 자리에 뜨게.</summary>
    internal sealed record WindowMemory(double Left, double Top, double Width, double Height, bool Maximized)
    {
        internal const double MinWidth = 300;
        internal const double MinHeight = 200;

        internal string Format() => string.Join(",",
            Left.ToString("0.#", CultureInfo.InvariantCulture),
            Top.ToString("0.#", CultureInfo.InvariantCulture),
            Width.ToString("0.#", CultureInfo.InvariantCulture),
            Height.ToString("0.#", CultureInfo.InvariantCulture),
            Maximized ? "1" : "0");

        internal static bool TryParse(string? text, out WindowMemory? memory)
        {
            memory = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 5) return false;

            var v = new double[4];
            for (int i = 0; i < 4; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) ||
                    double.IsNaN(v[i]) || double.IsInfinity(v[i])) return false;
            if (v[2] < MinWidth || v[3] < MinHeight) return false;

            bool max = parts[4].Trim() is "1" or "true" or "True";
            memory = new WindowMemory(v[0], v[1], v[2], v[3], max);
            return true;
        }
    }
}
