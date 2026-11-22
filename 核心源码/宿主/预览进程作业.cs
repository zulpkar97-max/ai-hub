using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexLauncher
{
    internal sealed class PreviewJob : IDisposable
    {
        private readonly SafeFileHandle _handle;

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            internal long ProcessUserTimeLimit, JobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal ulong ReadOperations, WriteOperations, OtherOperations;
            internal ulong ReadBytes, WriteBytes, OtherBytes;
            internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits limits, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

        internal PreviewJob()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!_handle.IsInvalid && SetInformationJobObject(_handle, 9, ref limits, Marshal.SizeOf(typeof(ExtendedLimits)))) return;
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "无法创建项目预览的进程组。");
        }

        internal void Attach(Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法接管刚启动的预览进程。");
        }

        internal static bool OwnsListener(int port, int pid)
        {
            const int ipv4 = 2, ownerPidListener = 3, rowSize = 24;
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, ipv4, ownerPidListener, 0);
            if (size < sizeof(int)) return false;
            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(table, ref size, false, ipv4, ownerPidListener, 0) != 0) return false;
                int rows = Math.Min(Marshal.ReadInt32(table), (size - sizeof(int)) / rowSize);
                for (int i = 0; i < rows; i++)
                {
                    int row = sizeof(int) + i * rowSize;
                    int rowPort = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(table, row + 8));
                    if (rowPort == port && Marshal.ReadInt32(table, row + 20) == pid) return true;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(table); }
        }

        public void Dispose()
        {
            _handle.Dispose();
        }
    }
}
