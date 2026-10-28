using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PulseWatch.Core
{
    /// <summary>
    /// Asks Windows whether a physical disk is spun down, without touching the disk itself
    /// (a handle opened with no access rights + GetDevicePowerState never issues a command to the drive).
    /// Used so SMART polling never wakes a sleeping HDD.
    /// </summary>
    public static class DiskPower
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetDevicePowerState(SafeFileHandle device, out bool on);

        /// <returns>true if Windows reports the disk as powered down; null if it cannot tell.</returns>
        public static bool? IsAsleep(int physicalDrive)
        {
            try
            {
                using var h = CreateFile($@"\\.\PhysicalDrive{physicalDrive}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid) return null;
                return GetDevicePowerState(h, out bool on) ? !on : null;
            }
            catch { return null; }
        }
    }
}
