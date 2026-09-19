using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static SnapView.Native.NativeMethods;

namespace SnapView.Core
{
    internal static class ImageIO
    {
        // 목록 자체는 MediaKinds 에 있다. 설치기도 같은 목록을 봐야 하기 때문이다.
        internal static string[] SupportedExtensions => MediaKinds.ImageExtensions;
        internal static string[] VideoExtensions => MediaKinds.VideoExtensions;

        internal static bool IsSupported(string path) => MediaKinds.IsImage(path);
        internal static bool IsVideo(string path) => MediaKinds.IsVideo(path);

        /// <summary>뷰어가 열 수 있는 것(그림 + 동영상).</summary>
        internal static bool IsMedia(string path) => MediaKinds.IsMedia(path);

        // ===================== 불러오기 =====================

        /// <summary>
        /// 파일을 잠그지 않고 디코딩한다(OnLoad). JPEG 의 EXIF 회전 정보도 반영한다.
        /// </summary>
        internal static BitmapSource Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes, writable: false);

            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat,
                                               BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];

            BitmapSource oriented = ApplyExifOrientation(frame);
            if (!oriented.IsFrozen) oriented.Freeze();
            return oriented;
        }

        private static BitmapSource ApplyExifOrientation(BitmapSource frame)
        {
            int orientation;
            try
            {
                if (frame.Metadata is not BitmapMetadata meta) return frame;
                object? raw = meta.ContainsQuery("/app1/ifd/{ushort=274}")
                    ? meta.GetQuery("/app1/ifd/{ushort=274}")
                    : null;
                if (raw == null) return frame;
                orientation = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            }
            catch { return frame; }

            Transform? t = orientation switch
            {
                3 => new RotateTransform(180),
                6 => new RotateTransform(90),
                8 => new RotateTransform(270),
                2 => new ScaleTransform(-1, 1),
                4 => new ScaleTransform(1, -1),
                _ => null
            };
            if (t == null) return frame;

            var transformed = new TransformedBitmap(frame, t);
            transformed.Freeze();
            return transformed;
        }

        /// <summary>
        /// 같은 폴더의 파일들을 탐색기와 같은 순서(숫자 인식)로 나열한다.
        ///
        /// <paramref name="videos"/> 로 <b>같은 종류만</b> 골라 낸다. 그림을 보다가 →를
        /// 눌렀는데 영상이 튀어나오면 보던 흐름이 끊긴다 — 그림 창은 그림만, 재생 창은
        /// 영상만 훑는다.
        /// </summary>
        internal static List<string> Siblings(string filePath, bool videos)
        {
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return new List<string> { filePath };

                var list = Directory.EnumerateFiles(dir)
                                    .Where(f => videos ? IsVideo(f) : IsSupported(f))
                                    .ToList();
                list.Sort(NaturalComparer.Instance);

                // 목록에 없는 확장자를 억지로 연 경우에도 그 파일만은 남아 있어야 한다.
                if (!list.Any(f => string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase)))
                    list.Insert(0, filePath);

                return list;
            }
            catch
            {
                return new List<string> { filePath };
            }
        }

        private sealed class NaturalComparer : IComparer<string>
        {
            internal static readonly NaturalComparer Instance = new();
            public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
        }

        // ===================== 저장 =====================

        internal static BitmapEncoder CreateEncoder(string format, int jpegQuality)
        {
            if (string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase))
                return new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) };
            if (string.Equals(format, "bmp", StringComparison.OrdinalIgnoreCase))
                return new BmpBitmapEncoder();   // 다른 이름으로 저장이 .bmp 를 내놓는다
            return new PngBitmapEncoder();
        }

        internal static void SaveTo(BitmapSource image, string path, int jpegQuality = 92)
            => SaveAs(image, path, Path.GetExtension(path), jpegQuality);

        /// <summary>
        /// 인코더는 <paramref name="formatExt"/> 로 고르되 파일은 <paramref name="path"/> 에 쓴다.
        /// <see cref="Overwrite"/> 가 임시 파일(…snapview.tmp)에 쓸 때 원본 형식을 잃지
        /// 않게 하려고 둔다 — .tmp 로 고르면 전부 PNG 가 된다.
        /// </summary>
        private static void SaveAs(BitmapSource image, string path, string formatExt, int jpegQuality)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string ext = formatExt.TrimStart('.');
            BitmapEncoder encoder = CreateEncoder(ext, jpegQuality);

            BitmapSource toSave = image;
            if (encoder is JpegBitmapEncoder && image.Format != PixelFormats.Bgr24)
                toSave = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
            else if (encoder is BmpBitmapEncoder && image.Format != PixelFormats.Bgr32)
                toSave = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);

            encoder.Frames.Add(BitmapFrame.Create(toSave));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(fs);
        }

        /// <summary>설정한 폴더/이름 규칙으로 자동 저장하고 최종 경로를 돌려준다.</summary>
        /// <summary>
        /// 같은 파일에 덮어쓴다. 바로 쓰지 않고 <b>옆에 임시 파일로 만든 뒤 바꿔치기</b> 한다.
        /// 쓰는 도중에 실패하면 원본이 반쯤 망가진 채로 남기 때문이다.
        /// </summary>
        internal static void Overwrite(BitmapSource image, string path, int jpegQuality = 92)
        {
            string dir = Path.GetDirectoryName(path) ?? ".";
            string temp = Path.Combine(dir, Path.GetFileName(path) + ".snapview.tmp");

            try
            {
                // 인코더는 원본 확장자로 고른다. 임시 파일 확장자(.tmp)를 따르면
                // photo.jpg 를 다시 저장할 때 JPG 자리에 PNG 바이트가 앉는다.
                SaveAs(image, temp, Path.GetExtension(path), jpegQuality);

                // 원본이 있으면 교체, 없으면 그냥 옮긴다.
                if (File.Exists(path)) File.Replace(temp, path, null, ignoreMetadataErrors: true);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        /// <param name="pngBytes">
        /// 이미 인코딩해 둔 PNG. 형식이 png 면 다시 인코딩하지 않고 이대로 쓴다 —
        /// 클립보드와 저장이 같은 이미지를 각자 인코딩하면 캡처 확정 후 멈춤이 두 배가 된다.
        /// </param>
        internal static string SaveAuto(BitmapSource image, Settings s, DateTime when,
                                        byte[]? pngBytes = null)
            => SaveNamed(image, s, s.FileNamePattern, when, null, pngBytes);

        /// <summary>
        /// 영상에서 뜬 한 장면을 저장한다. 이름에 <b>원본 영상 이름</b>이 들어간다 —
        /// 날짜만 있으면 나중에 폴더를 열었을 때 무엇을 찍은 것인지 알 수 없다.
        /// </summary>
        internal static string SaveFrame(BitmapSource image, Settings s, DateTime when, string? source)
            => SaveNamed(image, s, s.FrameNamePattern, when, source);

        private static string SaveNamed(BitmapSource image, Settings s, string pattern,
                                        DateTime when, string? source, byte[]? pngBytes = null)
        {
            string folder = string.IsNullOrWhiteSpace(s.SaveFolder) ? Settings.DefaultSaveFolder : s.SaveFolder;
            Directory.CreateDirectory(folder);

            string ext = s.ImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
            string baseName = BuildName(pattern, when, source);

            string path = Path.Combine(folder, baseName + ext);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(folder, baseName + " (" + i + ")" + ext);

            if (pngBytes != null && ext == ".png") File.WriteAllBytes(path, pngBytes);
            else SaveTo(image, path, s.JpegQuality);
            return path;
        }

        /// <summary>
        /// 이름 규칙을 실제 이름으로 바꾼다. {0} 은 시각, {1} 은 원본 이름.
        ///
        /// 원본 이름이 없으면 그 자리를 빼고 <b>남는 밑줄도 같이 정리</b>한다.
        /// 안 그러면 "스냅뷰__2026-08-23" 처럼 밑줄이 두 개 붙는다.
        /// </summary>
        internal static string BuildName(string pattern, DateTime when, string? source)
        {
            string name = Sanitize(source ?? "");

            // 파일 이름이 너무 길면 경로 길이 제한에 걸린다. 앞쪽만 남긴다.
            if (name.Length > 60) name = name[..60].TrimEnd();

            string made;
            try { made = string.Format(CultureInfo.InvariantCulture, pattern, when, name); }
            catch { made = "SnapView_" + when.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture); }

            if (name.Length == 0)
            {
                made = made.Replace("__", "_");
                made = made.Trim('_', ' ', '-');
            }

            return Sanitize(made);
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim().Length == 0 ? "SnapView" : name.Trim();
        }

        // ===================== 클립보드 =====================

        /// <summary>
        /// DIB 와 PNG 를 함께 올린다. PNG 를 같이 넣어야 크롬·디스코드 등에서
        /// 색이 뭉개지지 않고 붙여넣어진다.
        /// </summary>
        /// <summary>클립보드에 있는 그림을 가져온다. 없으면 null.</summary>
        internal static BitmapSource? ImageFromClipboard()
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsImage()) return null;

                BitmapSource? img = System.Windows.Clipboard.GetImage();
                if (img == null) return null;

                if (!img.IsFrozen && img.CanFreeze) img.Freeze();
                return img;
            }
            catch { return null; }
        }

        /// <summary>PNG 로 인코딩만 한다. 클립보드와 저장이 나눠 쓸 수 있게 바이트로 돌려준다.</summary>
        internal static byte[] EncodePng(BitmapSource image)
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            png.Save(ms);
            return ms.ToArray();
        }

        /// <summary>클립보드에 넣는다. 다른 프로세스가 잠가서 끝내 실패하면 false — 부르는 쪽이 알려야 한다.</summary>
        internal static bool CopyToClipboard(BitmapSource image, byte[]? pngBytes = null)
        {
            var data = new DataObject();
            data.SetImage(image);

            try
            {
                pngBytes ??= EncodePng(image);
                data.SetData("PNG", new MemoryStream(pngBytes, writable: false), autoConvert: false);
            }
            catch { }

            // 클립보드는 다른 프로세스가 잠글 수 있어서 몇 번 재시도한다.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(data, copy: true);
                    return true;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    Thread.Sleep(60);
                }
            }
            return false;
        }

        // ===================== 삭제 =====================

        /// <summary>파일을 휴지통으로 보낸다. 성공하면 true.</summary>
        internal static bool RecycleFile(string path)
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",          // 이중 널 종료 필수
                pTo = null,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
            };
            return SHFileOperationW(ref op) == 0 && !op.fAnyOperationsAborted;
        }
    }
}
