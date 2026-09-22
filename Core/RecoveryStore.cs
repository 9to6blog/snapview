using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace SnapView.Core
{
    /// <summary>
    /// 편집 중인 것을 몇 초마다 몰래 저장해 두는 곳. 앱이 죽거나 실수로 닫아도 다음에
    /// "이어서 할까요?" 를 물을 수 있다. 프로젝트 파일(.snapview) 형식 그대로다.
    /// </summary>
    internal sealed class RecoveryStore
    {
        private readonly string _path;

        internal RecoveryStore() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapView")) { }

        internal RecoveryStore(string folder)
        {
            _path = Path.Combine(folder, "recover" + ProjectFile.Extension);
        }

        internal string FilePath => _path;
        internal bool Exists => File.Exists(_path);
        internal DateTime SavedAt => Exists ? File.GetLastWriteTime(_path) : DateTime.MinValue;

        /// <summary>임시 파일에 쓰고 바꿔치기한다. 쓰다 죽어도 반쪽짜리가 남지 않게.</summary>
        internal void Save(BitmapSource image, IEnumerable<Annotation> items, int counter, IEnumerable<EditorGuide>? guides = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            ProjectFile.Save(tmp, image, items, counter, guides);
            File.Move(tmp, _path, overwrite: true);
        }

        internal (BitmapSource Image, List<Annotation> Items, int Counter) Load() => ProjectFile.Load(_path);
        internal (BitmapSource Image, List<Annotation> Items, int Counter) Load(out List<EditorGuide> guides) => ProjectFile.Load(_path, out guides);

        internal void Clear()
        {
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
            try { if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); } catch { }
        }
    }
}
