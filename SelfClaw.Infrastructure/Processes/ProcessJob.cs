using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SelfClaw.Infrastructure.Processes;

/// <summary>
/// A Windows job object that owns one launched process together with every descendant it creates - including
/// the ones the command deliberately detaches, such as <c>(nohup npm run dev &amp;)</c>, <c>start /b</c> or any
/// child whose intermediate parent has already exited.
/// <para>
/// Killing by walking the parent ids (what <see cref="Process.Kill(bool)"/> and taskkill /T do) cannot see
/// those: the chain that leads to them is broken the moment the intermediate process exits, so the service
/// keeps running with nothing left pointing at it. Job membership is inherited at creation time and survives
/// that reparenting, which makes the job the only reliable place to find the survivors.
/// </para>
/// </summary>
internal sealed class ProcessJob : IDisposable
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;
    private bool _killsOnClose;

    private ProcessJob(IntPtr handle)
    {
        _handle = handle;
        _killsOnClose = true;
    }

    /// <summary>
    /// Creates a job that kills its members if this process dies without releasing it, so a crash cannot
    /// strand a service either. Returns null when the platform refuses (e.g. the parent process sits in a
    /// job that cannot be nested); the caller then falls back to killing the process tree.
    /// </summary>
    internal static ProcessJob? TryCreate()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var job = new ProcessJob(handle);
        if (!job.TrySetLimit(JobObjectLimitKillOnJobClose))
        {
            job.Dispose();
            return null;
        }

        return job;
    }

    internal bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (InvalidOperationException)
        {
            // The process exited before it could be enrolled; there is nothing left to own.
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Ends the caller's ownership by killing every member, detached ones included.</summary>
    internal void Terminate()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        // TerminateJobObject also succeeds for an empty job, so an already finished command is not an error.
        TerminateJobObject(_handle, 1);
    }

    /// <summary>
    /// Ends the caller's ownership without killing the members. A command that completes normally may have
    /// launched something the user keeps on purpose (<c>start chrome</c>, <c>code .</c>); detaching keeps that
    /// working while still leaving every abnormal end - cancel, timeout, failure - able to kill the job.
    /// </summary>
    internal void Detach()
    {
        if (_handle == IntPtr.Zero || !_killsOnClose)
        {
            return;
        }

        _killsOnClose = false;
        TrySetLimit(limitFlags: 0);
    }

    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        CloseHandle(handle);
    }

    private bool TrySetLimit(uint limitFlags)
    {
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = limitFlags }
        };
        return SetInformationJobObject(_handle, ExtendedLimitInformationClass, ref information,
            (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>());
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass,
        ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
