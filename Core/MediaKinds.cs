using System;
using System.IO;
using System.Linq;

namespace SnapView.Core
{
    /// <summary>
    /// 이 앱이 다루는 파일 종류.
    ///
    /// 목록을 <see cref="ImageIO"/> 안에 두면 설치기가 못 쓴다 — ImageIO 는 WPF 디코더와
    /// Win32 를 잔뜩 끌고 오는데 설치기는 그런 게 필요 없다. 그런데 <b>설치기도 파일 연결을
    /// 등록</b>하므로 같은 목록을 봐야 한다. 실제로 여기가 갈라져 있어서, 설치기는 그림만
    /// 등록하고 본체는 "이미 등록됨" 이라고 판단해 동영상 연결이 영영 안 붙은 적이 있다.
    /// 목록은 한 군데에만 둔다.
    /// </summary>
    internal static class MediaKinds
    {
        /// <summary>
        /// 그림으로 읽어 볼 확장자.
        ///
        /// 윈도우의 이미지 처리기(WIC)가 읽는 것을 그대로 따라간다. 기본으로 들어 있는 것
        /// 말고도 스토어에서 깔리는 확장(WebP · HEIF · AVIF · JPEG XL)과 카메라 RAW 가
        /// 있으면 그것도 읽힌다 — 없으면 열 때 그렇다고 말해 준다. 목록에서 미리 빼 버리면
        /// 코덱이 깔려 있는 사람도 못 열게 되므로 넉넉히 넣는다.
        /// </summary>
        internal static readonly string[] ImageExtensions =
        {
            ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".gif",
            ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif",
            ".jxr", ".wdp", ".jxl", ".dds",
            // 카메라 RAW (Microsoft Raw Image Extension 이 있으면 읽힌다)
            ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".pef", ".srw", ".raf"
        };

        /// <summary>
        /// 재생해 볼 동영상. 그림처럼 픽셀을 만질 수는 없다.
        ///
        /// 윈도우의 재생 엔진(Media Foundation)이 다루는 범위다. MKV·WEBM 은 윈도우 10
        /// 이후로 기본 지원이고, 나머지는 코덱이 깔려 있어야 한다.
        /// </summary>
        internal static readonly string[] VideoExtensions =
        {
            ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".asf", ".mkv", ".webm",
            ".mpg", ".mpeg", ".mpe", ".m1v", ".m2v", ".ts", ".m2ts", ".mts",
            ".3gp", ".3g2", ".ogv", ".ogm", ".divx", ".flv", ".f4v", ".vob"
        };

        /// <summary>윈도우에 "이걸로 열 수 있다" 고 알릴 확장자 전부.</summary>
        internal static string[] All => ImageExtensions.Concat(VideoExtensions).ToArray();

        internal static bool IsImage(string path)
            => ImageExtensions.Contains(Extension(path));

        internal static bool IsVideo(string path)
            => VideoExtensions.Contains(Extension(path));

        /// <summary>뷰어가 열 수 있는 것(그림 + 동영상).</summary>
        internal static bool IsMedia(string path) => IsImage(path) || IsVideo(path);

        /// <summary>
        /// 목록에 없는 확장자라도 <b>일단 열어는 본다</b>.
        ///
        /// 세상의 모든 확장자를 적어 둘 수는 없고, 사용자가 굳이 골라서 연 파일을
        /// "목록에 없다" 는 이유로 거절하는 건 도움이 안 된다. 그림으로 한 번,
        /// 안 되면 동영상으로 한 번 해 보고 그래도 안 되면 그때 말해 준다.
        /// (자동 탐색 목록·파일 연결 등록에는 안 쓴다 — 그건 아는 것만 넣는다)
        /// </summary>
        internal static bool MightOpen(string path) => !string.IsNullOrWhiteSpace(path);

        /// <summary>
        /// 확장자를 소문자로. ".mp4" 처럼 확장자만 넘겨도 그대로 통한다 —
        /// 등록할 때는 파일 이름이 아니라 확장자 목록을 그대로 물어보기 때문이다.
        /// </summary>
        private static string Extension(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext) && path.StartsWith(".", StringComparison.Ordinal)) ext = path;
            return ext.ToLowerInvariant();
        }
    }
}
