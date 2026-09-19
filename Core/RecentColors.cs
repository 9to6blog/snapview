using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Media;

namespace SnapView.Core
{
    /// <summary>최근 쓴 색 여섯 칸. 스포이드로 집은 색·직접 고른 색이 팔레트 옆에 남는다.</summary>
    internal static class RecentColors
    {
        internal const int Max = 6;

        internal static void Push(List<Color> list, Color c)
        {
            list.Remove(c);
            list.Insert(0, c);
            while (list.Count > Max) list.RemoveAt(list.Count - 1);
        }

        internal static string Serialize(IEnumerable<Color> list)
        {
            var sb = new StringBuilder();
            foreach (Color c in list)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(c.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        internal static List<Color> Parse(string? text)
        {
            var list = new List<Color>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (ColorConverter.ConvertFromString(part.Trim()) is Color c && !list.Contains(c)) list.Add(c);
                }
                catch { /* 깨진 항목은 건너뛴다 */ }
                if (list.Count >= Max) break;
            }
            return list;
        }
    }
}
