using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapView.Core
{
    /// <summary>
    /// 여러 장을 움직이는 GIF 한 파일로 엮는다.
    ///
    /// WPF 의 <c>GifBitmapEncoder</c> 는 여러 장을 담아 주긴 하지만
    /// <b>프레임 간격도 반복 설정도 안 써 준다</b>. 그대로 저장하면 전부 0 초 간격이라
    /// 재생기마다 제멋대로 돌아가고, 한 번 돌고 멈추기도 한다.
    ///
    /// 그래서 여기서는 한 장씩 GIF 로 인코딩(색 줄이기·LZW 압축은 WPF 에 맡긴다)한 다음,
    /// 그 안에서 <b>이미지 블록만 꺼내</b> 간격·반복 블록과 함께 직접 이어 붙인다.
    /// GIF89a 규격 그대로라 어디서든 재생된다.
    /// </summary>
    internal static class GifWriter
    {
        private const byte ExtensionIntroducer = 0x21;
        private const byte GraphicControlLabel = 0xF9;
        private const byte ApplicationLabel = 0xFF;
        private const byte ImageSeparator = 0x2C;
        private const byte Trailer = 0x3B;

        /// <summary>
        /// <paramref name="frames"/> 를 <paramref name="path"/> 에 움직이는 GIF 로 쓴다.
        /// <paramref name="delayMs"/> 는 프레임 간격(밀리초).
        /// </summary>
        internal static void Save(IReadOnlyList<BitmapSource> frames, string path, int delayMs)
        {
            using FileStream fs = File.Create(path);
            Save(frames, fs, delayMs);
        }

        internal static void Save(IReadOnlyList<BitmapSource> frames, Stream output, int delayMs)
        {
            if (frames.Count == 0) throw new ArgumentException("프레임이 없습니다.", nameof(frames));

            using var gif = new Session(output, frames[0].PixelWidth, frames[0].PixelHeight, leaveOpen: true);
            foreach (BitmapSource frame in frames) gif.Add(frame, delayMs);
        }

        /// <summary>
        /// 한 장씩 <b>바로바로 파일에 흘려 보내는</b> 쓰기. 녹화가 이걸 쓴다.
        ///
        /// 프레임을 다 모아 뒀다가 마지막에 저장하면 메모리가 감당이 안 된다.
        /// 1920×1080 한 장이 8MB 라 10fps 로 30초만 찍어도 2GB 가 넘는다.
        /// </summary>
        internal sealed class Session : IDisposable
        {
            private readonly Stream _out;
            private readonly bool _leaveOpen;
            private bool _closed;
            private long _targetMs;     // 부르는 쪽이 원한 간격의 합
            private long _writtenMs;    // 실제로 파일에 적은 간격의 합

            /// <summary>파일에 적힌 간격의 합(ms). 영상 길이가 실제와 맞는지 볼 때 쓴다.</summary>
            internal int WrittenMs => (int)_writtenMs;

            internal int Width { get; }
            internal int Height { get; }
            internal int FrameCount { get; private set; }

            internal Session(Stream output, int width, int height, bool leaveOpen = false)
            {
                _out = output;
                _leaveOpen = leaveOpen;
                Width = Math.Max(1, width);
                Height = Math.Max(1, height);

                WriteHeader(_out, Width, Height);
                WriteLoopForever(_out);
            }

            /// <summary>한 장을 이어 붙인다. 크기가 다르면 첫 장 크기로 맞춘다.</summary>
            internal void Add(BitmapSource frame, int delayMs)
            {
                if (_closed) throw new ObjectDisposedException(nameof(Session));

                // GIF 의 간격 단위는 1/100 초다. 장마다 따로 반올림하면 33.3ms(30fps)가 30ms 가 되어
                // 영상이 10% 빨리 감긴다. 원한 시각과 적은 시각의 차이를 다음 장에 넘겨 전체 길이를 맞춘다.
                // 0·1 은 재생기마다 제멋대로 굴러서(100ms 로 치기도 한다) 최소 2(=20ms)로 붙든다.
                _targetMs += Math.Max(0, delayMs);
                int delayUnits = (int)Math.Round((_targetMs - _writtenMs) / 10.0);
                if (delayUnits < 2) delayUnits = 2;
                _writtenMs += delayUnits * 10L;

                byte[] single = EncodeSingleGif(frame, Width, Height);
                ImageBlock block = ExtractImageBlock(single);

                WriteGraphicControl(_out, delayUnits);
                _out.Write(block.Bytes, block.Start, block.Length);
                FrameCount++;
            }

            public void Dispose()
            {
                if (_closed) return;
                _closed = true;

                _out.WriteByte(Trailer);
                _out.Flush();
                if (!_leaveOpen) _out.Dispose();
            }
        }

        // ---------------------------------------------------------------- 블록 쓰기

        /// <summary>GIF89a 머리말. 전역 색표는 안 쓰고 프레임마다 자기 색표를 갖게 한다.</summary>
        private static void WriteHeader(Stream s, int width, int height)
        {
            s.Write(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, 0, 6);
            WriteShort(s, width);
            WriteShort(s, height);
            s.WriteByte(0x00);   // 전역 색표 없음
            s.WriteByte(0x00);   // 배경색 index
            s.WriteByte(0x00);   // 화면비 지정 안 함
        }

        /// <summary>NETSCAPE2.0 확장. 이게 없으면 한 번만 돌고 멈춘다.</summary>
        private static void WriteLoopForever(Stream s)
        {
            s.WriteByte(ExtensionIntroducer);
            s.WriteByte(ApplicationLabel);
            s.WriteByte(11);
            s.Write(new byte[]
            {
                (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A',
                (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0'
            }, 0, 11);
            s.WriteByte(3);
            s.WriteByte(1);      // 하위 블록 id
            WriteShort(s, 0);    // 0 = 끝없이 반복
            s.WriteByte(0);      // 블록 끝
        }

        /// <summary>프레임 간격. 앞 장을 지우지 않고 그 위에 덮는다(방식 1).</summary>
        private static void WriteGraphicControl(Stream s, int delayUnits)
        {
            s.WriteByte(ExtensionIntroducer);
            s.WriteByte(GraphicControlLabel);
            s.WriteByte(4);
            s.WriteByte(0x04);           // 처리 방식 1(그대로 두기), 투명색 없음
            WriteShort(s, delayUnits);
            s.WriteByte(0x00);           // 투명색 index (안 씀)
            s.WriteByte(0x00);           // 블록 끝
        }

        private static void WriteShort(Stream s, int value)
        {
            s.WriteByte((byte)(value & 0xFF));
            s.WriteByte((byte)((value >> 8) & 0xFF));
        }

        // ---------------------------------------------------------------- 한 장 인코딩

        /// <summary>한 장을 GIF 로 만든다. 색 줄이기와 LZW 압축은 WPF 가 해 준다.</summary>
        private static byte[] EncodeSingleGif(BitmapSource frame, int width, int height)
        {
            BitmapSource sized = frame.PixelWidth == width && frame.PixelHeight == height
                ? frame
                : Resize(frame, width, height);

            var encoder = new GifBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(sized));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static BitmapSource Resize(BitmapSource src, int width, int height)
        {
            var t = new TransformedBitmap(src,
                new ScaleTransform((double)width / src.PixelWidth, (double)height / src.PixelHeight));
            t.Freeze();
            return t;
        }

        private readonly record struct ImageBlock(byte[] Bytes, int Start, int Length);

        /// <summary>
        /// 한 장짜리 GIF 에서 <b>이미지 서술자부터 끝(0x3B) 직전까지</b>를 잘라 온다.
        /// 여기에 지역 색표와 압축된 픽셀이 다 들어 있다.
        /// </summary>
        private static ImageBlock ExtractImageBlock(byte[] gif)
        {
            int pos = 6;                       // 머리말 6바이트 건너뛰기

            int flags = gif[pos + 4];
            pos += 7;                          // 논리 화면 서술자

            if ((flags & 0x80) != 0)           // 전역 색표가 있으면 건너뛴다
                pos += 3 * (1 << ((flags & 0x07) + 1));

            while (pos < gif.Length)
            {
                byte marker = gif[pos];

                if (marker == ImageSeparator)
                {
                    int start = pos;
                    pos = SkipImageBlock(gif, pos);
                    return new ImageBlock(gif, start, pos - start);
                }

                if (marker == ExtensionIntroducer)
                {
                    pos += 2;                  // 도입부 + 라벨
                    pos = SkipSubBlocks(gif, pos);
                    continue;
                }

                break;                         // 0x3B(끝) 또는 모르는 바이트
            }

            throw new InvalidDataException("GIF 안에서 이미지 블록을 못 찾았습니다.");
        }

        private static int SkipImageBlock(byte[] gif, int pos)
        {
            int flags = gif[pos + 9];
            pos += 10;                         // 이미지 서술자

            if ((flags & 0x80) != 0)           // 지역 색표
                pos += 3 * (1 << ((flags & 0x07) + 1));

            pos += 1;                          // LZW 최소 코드 크기
            return SkipSubBlocks(gif, pos);
        }

        /// <summary>길이 바이트로 이어지는 하위 블록들을 0 이 나올 때까지 건너뛴다.</summary>
        private static int SkipSubBlocks(byte[] gif, int pos)
        {
            while (pos < gif.Length && gif[pos] != 0) pos += gif[pos] + 1;
            return pos + 1;
        }
    }
}
