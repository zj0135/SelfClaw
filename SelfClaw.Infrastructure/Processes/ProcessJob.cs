using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SelfClaw.Infrastructure.Processes;

/// <summary>
/// A Windows Job Object that owns every process a hook starts. It is created with
/// <c>KILL_ON_JOB_CLOSE</c> so a process that outlives its parent (or the host itself) cannot
/// survive: closing the handle - including by the kernel when the host crashes - terminates the
/// whole tree.
/// </summary>
internal sealed class ProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeJobHandle _handle;
    private int _terminated;

    private ProcessJob(SafeJobHandle handle)
    {
        _handle = handle;
    }

    internal static ProcessJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var rawHandle = CreateJobObject(nint.Zero, null);
        if (rawHandle == nint.Zero)
        {
            return null;
        }

        var job = new SafeJobHandle(rawHandle);
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, (uint)length))
            {
                job.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new ProcessJob(job);
    }

    internal bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return AssignProcessToJobObject(_handle, process.SafeHandle);
    }

    internal void Terminate()
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0)
        {
            return;
        }

        TerminateJobObject(_handle, 1);
    }

    public void Dispose() => _handle.Dispose();

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle(nint handle)
            : base(ownsHandle: true)
            => SetHandle(handle);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        nint information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
