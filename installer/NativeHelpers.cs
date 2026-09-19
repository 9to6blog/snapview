using System;
using System.Runtime.InteropServices;

namespace SnapViewSetup
{
    internal static class NativeHelpers
    {
        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        /// <summary>MOVEFILE_DELAY_UNTIL_REBOOT</summary>
        private const uint MoveFileDelayUntilReboot = 0x00000004;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existing, string? newName, uint flags);

        internal static void NotifyAssociationsChanged()
        {
            try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        /// <summary>
        /// 지금은 못 지우는 것(자기 자신 등)을 다음 부팅 때 지우도록 예약한다.
        /// 두 번째 인자를 null 로 주면 "옮기지 말고 지워라" 라는 뜻이다.
        /// </summary>
        internal static void DeleteOnReboot(string path)
        {
            try { MoveFileEx(path, null, MoveFileDelayUntilReboot); }
            catch { }
        }
    }
}
