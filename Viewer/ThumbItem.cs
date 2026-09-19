using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SnapView.Viewer
{
    /// <summary>
    /// 아래쪽 썸네일 줄에 들어가는 항목 하나.
    /// 그림은 화면에 실제로 나타날 때 처음 읽는다(폴더에 수천 장이 있어도 버티게).
    /// </summary>
    public sealed class ThumbItem : INotifyPropertyChanged
    {
        internal const int ThumbHeight = 72;

        private BitmapSource? _thumb;
        private bool _started;

        public ThumbItem(string path) => Path = path;

        public string Path { get; }

        public string Name => System.IO.Path.GetFileName(Path);

        public BitmapSource? Thumb
        {
            get => _thumb;
            private set
            {
                _thumb = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>한 번만 읽는다. 실패하면 빈 칸으로 둔다.</summary>
        internal void BeginLoad()
        {
            if (_started) return;
            _started = true;

            string path = Path;
            Task.Run(() =>
            {
                BitmapSource? made = null;
                try
                {
                    // 원본을 통째로 디코딩하지 않도록 DecodePixelHeight 를 먼저 지정한다.
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmp.DecodePixelHeight = ThumbHeight;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    made = bmp;
                }
                catch
                {
                    made = null;   // 깨진 파일은 그냥 빈 칸
                }

                if (made == null) return;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => Thumb = made));
            });
        }

        public override string ToString() => Name;
    }
}
