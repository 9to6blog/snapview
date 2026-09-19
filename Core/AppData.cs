using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SnapView.Core
{
    /// <summary>
    /// SnapView 가 이 PC 에 남기는 것들.
    ///
    /// 그림이나 영상을 따로 캐시해 두지는 않는다 — 썸네일은 화면에 보일 때 읽고 창을 닫으면
    /// 사라진다. 디스크에 남는 것은 <b>설정</b>과 <b>기록</b> 둘뿐이고, 그중 기록에는
    /// 열어 본 파일의 경로가 들어간다. 지우고 싶은 게 보통 그것이라 여기서 다룬다.
    /// </summary>
    internal static class AppData
    {
        /// <summary>남아 있는 것 하나.</summary>
        internal readonly record struct Item(string Name, string Path, long Bytes, bool Clearable);

        internal static IReadOnlyList<Item> Stored()
        {
            var list = new List<Item>();

            Add(list, "기록", Log.FilePath, clearable: true);
            Add(list, "설정", Settings.ConfigPath, clearable: false);

            return list;
        }

        private static void Add(List<Item> list, string name, string path, bool clearable)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 0) list.Add(new Item(name, path, fi.Length, clearable));
            }
            catch { }
        }

        /// <summary>지울 수 있는 것들의 크기 합.</summary>
        internal static long ClearableBytes()
        {
            long total = 0;
            foreach (Item i in Stored()) if (i.Clearable) total += i.Bytes;
            return total;
        }

        /// <summary>기록에 남아 있는 줄 수. 못 읽으면 0.</summary>
        internal static int HistoryLines()
        {
            try
            {
                if (!File.Exists(Log.FilePath)) return 0;

                int n = 0;
                using var reader = new StreamReader(Log.FilePath);
                while (reader.ReadLine() != null) n++;
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 남은 기록을 지운다. 지금 쓰고 있는 파일이라 <b>지우지 않고 비운다</b> —
        /// 파일을 없애 버리면 다음에 쓸 때 잠깐 어긋날 수 있다.
        /// </summary>
        internal static bool Clear(out string error)
        {
            error = "";
            try
            {
                if (File.Exists(Log.FilePath)) File.WriteAllText(Log.FilePath, "");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>사람이 읽는 크기.</summary>
        internal static string SizeText(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024)
                return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }
    }
}
