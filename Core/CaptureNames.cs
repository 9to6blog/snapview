using System;
using System.Globalization;

namespace SnapView.Core
{
    internal static class CaptureNames
    {
        internal const string TimestampFormat = "yyyy-MM-dd_HHmmss_fff";
        internal const string DefaultPattern = "SnapView_{0:yyyy-MM-dd_HHmmss_fff}_{2:N}";
        internal const string FramePattern = "SnapView_{1}_{0:yyyy-MM-dd_HHmmss_fff}_{2:N}";

        // Every automatically generated name has a complete timestamp and a fresh UUID.
        // Custom prefixes still work; missing identity fields are added rather than lost.
        internal static string Build(string? pattern, DateTime when, string? source)
        {
            string subject = RecordingNames.Sanitize(source, 60);
            string stamp = when.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            Guid uuid = Guid.NewGuid();
            string name;
            try { name = string.Format(CultureInfo.InvariantCulture, string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern, when, subject, uuid); }
            catch (FormatException) { name = "SnapView_" + stamp; }
            name = RecordingNames.Sanitize(name, 160);
            if (name.Length == 0) name = "SnapView";
            if (subject.Length == 0) name = name.Replace("__", "_").Trim('_', ' ', '-');
            if (!name.Contains(stamp, StringComparison.Ordinal)) name += "_" + stamp;
            if (!name.Contains(uuid.ToString("N"), StringComparison.OrdinalIgnoreCase) && !name.Contains(uuid.ToString("D"), StringComparison.OrdinalIgnoreCase))
                name += "_" + uuid.ToString("N");
            return name;
        }
    }
}
