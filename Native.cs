using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TawmaeUIUpdater
{
    // ==============================================================
    static class Native
    {
        const uint QueryLimitedInformation = 0x1000;
        const uint Synchronize = 0x00100000;
        const int InvalidParameter = 87;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("ntdll.dll")]
        static extern int RtlGetVersion(ref OsVersion version);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OsVersion
        {
            public int Size;
            public int Major;
            public int Minor;
            public int Build;
            public int Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string ServicePack;
        }

        // ==============================================================
        public static string ImagePath(int processId, out bool gone)
        {
            gone = false;
            IntPtr handle = OpenProcess(QueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
            {
                gone = Marshal.GetLastWin32Error() == InvalidParameter;
                return null;
            }
            try
            {
                var name = new StringBuilder(4096);
                int size = name.Capacity;
                return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        // ==============================================================
        public static IDisposable WatchExit(int processId, Action exited)
        {
            IntPtr handle = OpenProcess(Synchronize, false, processId);
            if (handle == IntPtr.Zero)
                return null;
            var process = new ProcessHandle(handle);
            var wait = ThreadPool.RegisterWaitForSingleObject(process, (state, timedOut) => exited(), null, Timeout.Infinite, true);
            return new ExitWatch(process, wait);
        }

        sealed class ProcessHandle : WaitHandle
        {
            public ProcessHandle(IntPtr handle)
            {
                SafeWaitHandle = new SafeWaitHandle(handle, true);
            }
        }

        sealed class ExitWatch : IDisposable
        {
            readonly WaitHandle _process;
            readonly RegisteredWaitHandle _wait;

            public ExitWatch(WaitHandle process, RegisteredWaitHandle wait)
            {
                _process = process;
                _wait = wait;
            }

            public void Dispose()
            {
                _wait.Unregister(null);
                _process.Dispose();
            }
        }

        // ==============================================================
        public static bool IsWindows11()
        {
            try
            {
                var version = new OsVersion { Size = Marshal.SizeOf(typeof(OsVersion)) };
                return RtlGetVersion(ref version) == 0 && version.Major >= 10 && version.Build >= 22000;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ==============================================================
        public static void DarkFrame(IntPtr window, int border)
        {
            try
            {
                int on = 1;
                int round = 2;
                DwmSetWindowAttribute(window, 20, ref on, sizeof(int));
                DwmSetWindowAttribute(window, 33, ref round, sizeof(int));
                DwmSetWindowAttribute(window, 34, ref border, sizeof(int));
            }
            catch (Exception)
            {
            }
        }
    }
}
