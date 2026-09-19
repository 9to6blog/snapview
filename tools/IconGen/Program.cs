using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// SnapView 앱 아이콘 생성기.
// ICO 항목은 PNG 가 아니라 고전 DIB 로 써야 한다 — csc 의 Win32 리소스 변환기가
// PNG 항목을 읽지 못해 CS7065 로 빌드가 깨진다.

string outPath = @"C:\Users\main\SnapView\assets\app.ico";
int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

static GraphicsPath Rounded(float x, float y, float w, float h, float r)
{
    var p = new GraphicsPath();
    float d = r * 2;
    p.AddArc(x, y, d, d, 180, 90);
    p.AddArc(x + w - d, y, d, d, 270, 90);
    p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    p.AddArc(x, y + h - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

static Bitmap Draw(int s)
{
    var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);

    float u = s / 32f;   // 32px 기준 도안을 비례 확대

    using (var path = Rounded(1.5f * u, 1.5f * u, 29 * u, 29 * u, 7 * u))
    using (var brush = new LinearGradientBrush(
               new RectangleF(0, 0, s, s),
               Color.FromArgb(255, 92, 176, 255),
               Color.FromArgb(255, 20, 94, 204),
               LinearGradientMode.ForwardDiagonal))
        g.FillPath(brush, path);

    // 캡처 프레임 코너 브래킷
    using (var pen = new Pen(Color.White, Math.Max(1.5f, 2.6f * u))
    { StartCap = LineCap.Round, EndCap = LineCap.Round })
    {
        float m = 8.5f * u, far = s - m, len = 5f * u;
        g.DrawLine(pen, m, m + len, m, m); g.DrawLine(pen, m, m, m + len, m);
        g.DrawLine(pen, far - len, m, far, m); g.DrawLine(pen, far, m, far, m + len);
        g.DrawLine(pen, far, far - len, far, far); g.DrawLine(pen, far, far, far - len, far);
        g.DrawLine(pen, m + len, far, m, far); g.DrawLine(pen, m, far, m, far - len);
    }

    // 가운데 렌즈
    float dot = 5f * u;
    using (var white = new SolidBrush(Color.White))
        g.FillEllipse(white, (s - dot) / 2, (s - dot) / 2, dot, dot);

    return bmp;
}

static byte[] ToDib(Bitmap bmp)
{
    int s = bmp.Width;
    var rect = new Rectangle(0, 0, s, s);
    BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var pixels = new byte[data.Stride * s];
    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
    int stride = data.Stride;
    bmp.UnlockBits(data);

    int maskRow = ((s + 31) / 32) * 4;      // AND 마스크는 행마다 4바이트 정렬
    int xorSize = s * s * 4;
    int andSize = maskRow * s;

    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);

    w.Write(40u);                 // biSize
    w.Write(s);                   // biWidth
    w.Write(s * 2);               // biHeight = XOR + AND 를 합친 높이
    w.Write((ushort)1);           // biPlanes
    w.Write((ushort)32);          // biBitCount
    w.Write(0u);                  // BI_RGB
    w.Write((uint)(xorSize + andSize));
    w.Write(0); w.Write(0);
    w.Write(0u); w.Write(0u);

    for (int y = s - 1; y >= 0; y--)          // DIB 는 아래에서 위로
        w.Write(pixels, y * stride, s * 4);
    w.Write(new byte[andSize]);               // 알파를 쓰므로 마스크는 전부 0

    w.Flush();
    return ms.ToArray();
}

var images = new List<byte[]>();
foreach (int s in sizes)
{
    using Bitmap bmp = Draw(s);
    images.Add(ToDib(bmp));
}

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort)0);                 // reserved
    w.Write((ushort)1);                 // type = icon
    w.Write((ushort)sizes.Length);

    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];   // 256 은 0 으로 적는다
        w.Write(dim);
        w.Write(dim);
        w.Write((byte)0);               // 팔레트 색 수
        w.Write((byte)0);               // reserved
        w.Write((ushort)1);             // planes
        w.Write((ushort)32);            // bpp
        w.Write((uint)images[i].Length);
        w.Write((uint)offset);
        offset += images[i].Length;
    }
    foreach (byte[] img in images) w.Write(img);
}

// 윈도우가 실제로 읽을 수 있는지 확인
using var probe = new Icon(outPath, 32, 32);
Console.WriteLine($"OK  {outPath}  {new FileInfo(outPath).Length} bytes  " +
                  $"{sizes.Length} sizes  probe={probe.Width}x{probe.Height}");
