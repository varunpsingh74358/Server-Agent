using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace CloudOrc.ControlAgent.ResourceGovernor;

/// <summary>Aggregate usage of every process in a job.</summary>
public sealed record JobUsage(long TotalCpuTime100Ns, long WorkingSetBytes, long PeakJobMemoryBytes, int ActiveProcesses);

/// <summary>OS-level budget enforcement for the worker process tree (abstracted for tests).</summary>
public interface IJobObject : IDisposable
{
    void Configure(ResourceBudget budget);
    void SetCpuRate(double percentOfMachine);

    /// <summary>Applies a per-volume disk bandwidth cap; returns false if the OS refused (logged by caller).</summary>
    bool SetDiskBandwidth(IReadOnlyCollection<string> volumeRoots, long? bytesPerSecond);
    void Assign(Process process);
    JobUsage QueryUsage();
}

/// <summary>
/// Windows Job Object around the backup worker. Every process the worker starts (kopia/restic,
/// rclone) is automatically in the same job, so the caps apply to the whole tree (§7 "Enforce
/// both parent Agent Core and child Kopia/Restic process budgets"). KILL_ON_JOB_CLOSE ensures
/// that if Agent Core itself dies, no unsupervised engine keeps running against a grant
/// nobody is renewing; the job is reconciled on restart (§15).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsJobObject : IJobObject
{
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectCpuRateControlInformation = 15;

    private const uint LimitPriorityClass = 0x00000020;
    private const uint LimitJobMemory = 0x00000200;
    private const uint LimitDieOnUnhandledException = 0x00000400;
    private const uint LimitKillOnJobClose = 0x00002000;

    private const uint CpuRateControlEnable = 0x1;
    private const uint CpuRateControlHardCap = 0x4;
    private const uint IoRateControlEnable = 0x1;

    private const uint IdlePriorityClass = 0x40;
    private const uint BelowNormalPriorityClass = 0x4000;
    private const uint NormalPriorityClass = 0x20;

    private readonly SafeFileHandle _handle;

    public WindowsJobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
        }
    }

    public unsafe void Configure(ResourceBudget budget)
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = LimitKillOnJobClose | LimitDieOnUnhandledException | LimitPriorityClass | LimitJobMemory;
        info.BasicLimitInformation.PriorityClass = budget.Priority switch
        {
            JobPriority.Idle => IdlePriorityClass,
            JobPriority.BelowNormal => BelowNormalPriorityClass,
            _ => NormalPriorityClass
        };
        info.JobMemoryLimit = (UIntPtr)(ulong)Math.Max(64L * 1024 * 1024, budget.MaxMemoryBytes);

        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Setting job memory/priority limits failed.");
        }

        SetCpuRate(budget.MaxCpuPercent);
    }

    public unsafe void SetCpuRate(double percentOfMachine)
    {
        var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = CpuRateControlEnable | CpuRateControlHardCap,
            // CpuRate is "the portion of processor cycles ... times 100" (1..10000).
            CpuRate = (uint)Math.Clamp(Math.Round(percentOfMachine * 100), 1, 10000)
        };

        if (!SetInformationJobObject(_handle, JobObjectCpuRateControlInformation, &info, (uint)sizeof(JOBOBJECT_CPU_RATE_CONTROL_INFORMATION)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Setting job CPU rate control failed.");
        }
    }

    /// <summary>Diagnostics for the last per-volume IO rate call (result 0 = failed, see error).</summary>
    internal int LastDiskControlError { get; private set; }

    internal uint LastDiskControlResult { get; private set; }

    public bool SetDiskBandwidth(IReadOnlyCollection<string> volumeRoots, long? bytesPerSecond)
    {
        var allApplied = true;
        foreach (var root in volumeRoots)
        {
            var deviceName = TryGetNtVolumeDeviceName(root);
            if (deviceName is null)
            {
                allApplied = false;
                continue;
            }

            var namePtr = Marshal.StringToHGlobalUni(deviceName);
            try
            {
                var info = new JOBOBJECT_IO_RATE_CONTROL_INFORMATION
                {
                    MaxBandwidth = bytesPerSecond ?? 0,
                    VolumeName = namePtr,
                    // Clearing ControlFlags (uncapped) removes the policy for this volume.
                    ControlFlags = bytesPerSecond is > 0 ? IoRateControlEnable : 0
                };

                // Returns non-zero on success, 0 on failure (+ GetLastError). Verified on a real
                // host: Windows Server 2019 (17763) fails every call with ERROR_INVALID_FUNCTION,
                // i.e. per-job IO rate control is not available there - callers must fall back.
                var rc = SetIoRateControlInformationJobObject(_handle, ref info);
                LastDiskControlResult = rc;
                LastDiskControlError = rc == 0 ? Marshal.GetLastWin32Error() : 0;
                if (rc == 0)
                {
                    allApplied = false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(namePtr);
            }
        }

        return allApplied;
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
        }
    }

    public unsafe JobUsage QueryUsage()
    {
        var accounting = new JOBOBJECT_BASIC_ACCOUNTING_INFORMATION();
        QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, &accounting, (uint)sizeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION), out _);

        var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        QueryInformationJobObject(_handle, JobObjectExtendedLimitInformation, &extended, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION), out _);

        long workingSet = 0;
        const int maxIds = 256;
        var bufferSize = 8 + (IntPtr.Size * maxIds);
        var buffer = stackalloc byte[bufferSize];
        if (QueryInformationJobObject(_handle, JobObjectBasicProcessIdList, buffer, (uint)bufferSize, out _))
        {
            var count = *(uint*)(buffer + 4);
            var ids = (UIntPtr*)(buffer + 8);
            for (var i = 0; i < Math.Min(count, (uint)maxIds); i++)
            {
                try
                {
                    using var process = Process.GetProcessById((int)ids[i]);
                    workingSet += process.WorkingSet64;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // Process exited between enumeration and inspection.
                }
            }
        }

        return new JobUsage(
            accounting.TotalUserTime + accounting.TotalKernelTime,
            workingSet,
            (long)(ulong)extended.PeakJobMemoryUsed,
            (int)accounting.ActiveProcesses);
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>"C:\" → "\Device\HarddiskVolume3" (the NT name the IO rate API expects).</summary>
    private static string? TryGetNtVolumeDeviceName(string root)
    {
        var drive = Path.GetPathRoot(root)?.TrimEnd('\\');
        if (string.IsNullOrEmpty(drive) || drive.Length != 2 || drive[1] != ':')
        {
            return null;
        }

        var buffer = new char[1024];
        var length = QueryDosDeviceW(drive, buffer, buffer.Length);
        if (length == 0)
        {
            return null;
        }

        var first = new string(buffer, 0, (int)length);
        var nul = first.IndexOf('\0');
        return nul >= 0 ? first[..nul] : first;
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_IO_RATE_CONTROL_INFORMATION
    {
        public long MaxIops;
        public long MaxBandwidth;
        public long ReservationIops;
        public IntPtr VolumeName;
        public uint BaseIoSize;
        public uint ControlFlags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool SetInformationJobObject(SafeFileHandle job, int infoClass, void* info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool QueryInformationJobObject(SafeFileHandle job, int infoClass, void* info, uint length, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetIoRateControlInformationJobObject(SafeFileHandle job, ref JOBOBJECT_IO_RATE_CONTROL_INFORMATION info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string deviceName, char[] targetPath, int maxLength);
}
