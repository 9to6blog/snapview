using System;
using System.Globalization;
using System.IO;

namespace SnapView.Core
{
    /// <summary>
    /// 녹화 파일 이름을 규칙대로 만든다. <c>{0}</c> 은 시각, <c>{1}</c> 은 무엇을 찍었는지.
    /// 창 제목이 들어올 수 있으므로 파일에 못 쓰는 글자는 바꾸고 길이도 자른다.
    /// 규칙이 틀리면(중괄호가 안 닫혔다든지) 기본 규칙으로 물러선다 — 이름 하나 때문에 녹화를 접지 않는다.
    /// </summary>
    internal static class RecordingNames
    {
        internal const string DefaultPattern = "SnapView_{1}_{0:yyyy-MM-dd_HHmmss}";

        /// <summary>대상 이름은 이만큼까지만. 창 제목은 한없이 길 수 있다.</summary>
        internal const int MaxSubjectLength = 40;

        internal static string Build(string? pattern, DateTime when, string? subject)
        {
            string what = Sanitize(subject, MaxSubjectLength);
            if (string.IsNullOrWhiteSpace(pattern)) pattern = DefaultPattern;

            string name;
            try { name = string.Format(CultureInfo.InvariantCulture, pattern, when, what); }
            catch (FormatException) { name = string.Format(CultureInfo.InvariantCulture, DefaultPattern, when, what); }

            name = Sanitize(name, 200);
            if (name.Length == 0) name = string.Format(CultureInfo.InvariantCulture, DefaultPattern, when, what);
            return name;
        }

        /// <summary>파일 이름에 못 쓰는 글자를 _ 로 바꾸고 앞뒤 공백·점을 걷어낸 뒤 길이를 자른다.</summary>
        internal static string Sanitize(string? text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            char[] bad = Path.GetInvalidFileNameChars();
            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(bad, chars[i]) >= 0) chars[i] = '_';

            string clean = new string(chars).Trim().TrimEnd('.');
            if (clean.Length > maxLength) clean = clean.Substring(0, maxLength).TrimEnd();
            return clean;
        }
    }
}
