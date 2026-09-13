using System;
using System.Runtime.InteropServices;

namespace LibProsperoPkg.Gui.Services;

/// <summary>
/// Process disk counters via <c>proc_pid_rusage</c>. Used for silent SI/digest
/// passes that <c>pread</c> without moving the file pointer (so size and fd
/// offset stay frozen). Not the old <c>fcntl(F_GETPATH)</c> probe.
/// </summary>
internal static class MacProcessIo
{
    private const int RusageInfoV4 = 4;

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pid_rusage")]
    private static extern int proc_pid_rusage(int pid, int flavor, byte[] buffer);

    public static (ulong Read, ulong Written)? Snapshot()
    {
        try
        {
            byte[] buffer = new byte[512];
            if (proc_pid_rusage(Environment.ProcessId, RusageInfoV4, buffer) != 0)
                return null;
            // rusage_info_v4: 16-byte uuid, then 16 ulongs, then bytesread/written.
            ulong read = BitConverter.ToUInt64(buffer, 16 + (16 * 8));
            ulong written = BitConverter.ToUInt64(buffer, 16 + (17 * 8));
            return (read, written);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
