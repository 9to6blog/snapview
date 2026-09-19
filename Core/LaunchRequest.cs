using System;
using System.IO;

namespace SnapView.Core
{
    internal enum LaunchAction
    {
        View,
        Edit
    }

    /// <summary>
    /// 탐색기·파일 연결·두 번째 프로세스가 넘기는 "파일을 어떻게 열지" 요청.
    /// 단순 경로만 보내던 예전 IPC 형식도 계속 읽는다.
    /// </summary>
    internal readonly record struct LaunchRequest(string FilePath, LaunchAction Action)
    {
        // 파이프 문자는 윈도우 파일 이름에 들어갈 수 없어서 실제 경로와 헷갈리지 않는다.
        private const string EditPrefix = "SnapView.Edit|";

        internal bool HasFile => !string.IsNullOrEmpty(FilePath);
        internal bool OpensEditor => Action == LaunchAction.Edit;

        internal static LaunchRequest FromArguments(string[] args)
        {
            bool edit = Array.Exists(args,
                a => string.Equals(a, "--edit", StringComparison.OrdinalIgnoreCase));

            foreach (string a in args)
            {
                if (string.IsNullOrWhiteSpace(a) || a.StartsWith("-", StringComparison.Ordinal) ||
                    a.StartsWith("/", StringComparison.Ordinal))
                    continue;

                try
                {
                    string full = Path.GetFullPath(a);
                    if (File.Exists(full))
                        return new LaunchRequest(full, edit ? LaunchAction.Edit : LaunchAction.View);
                }
                catch { }
            }

            return new LaunchRequest(string.Empty, edit ? LaunchAction.Edit : LaunchAction.View);
        }

        internal string ToIpcPayload()
            => OpensEditor && HasFile ? EditPrefix + FilePath : FilePath;

        internal static LaunchRequest FromIpcPayload(string? payload)
        {
            string value = payload ?? string.Empty;
            if (value.StartsWith(EditPrefix, StringComparison.Ordinal))
                return new LaunchRequest(value[EditPrefix.Length..], LaunchAction.Edit);

            return new LaunchRequest(value, LaunchAction.View);
        }
    }
}
