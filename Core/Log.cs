using System;
using System.IO;
using System.Text;

namespace SnapView.Core
{
    /// <summary>
    /// 드물게 나는 사고를 나중에 증명하려고 남기는 최소한의 기록.
    /// 평소 동작에는 아무것도 안 쓴다 — 뭔가 어긋났을 때만 한 줄.
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 256 * 1024;
        private static readonly object Gate = new();

        internal static string FilePath => Path.Combine(Settings.ConfigDirectory, "log.txt");

        internal static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Settings.ConfigDirectory);

                    // 무한정 자라지 않게. 넘치면 그냥 새로 시작한다.
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes) fi.Delete();

                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { /* 기록 실패로 앱이 흔들려선 안 된다 */ }
        }
    }
}
