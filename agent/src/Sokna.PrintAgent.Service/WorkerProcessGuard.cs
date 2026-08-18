using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Sokna.PrintAgent.Service;

/// <summary>Keeps the isolated worker inside a Windows Job Object so a service crash closes the job and kills the worker.</summary>
internal sealed class WorkerProcessGuard:IDisposable
{
    private readonly SafeFileHandle _job;
    private WorkerProcessGuard(SafeFileHandle job)=>_job=job;
    public static WorkerProcessGuard Attach(Process process)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("WorkerProcessGuard فقط روی Windows اجرا می‌شود.");
        var raw=CreateJobObject(IntPtr.Zero,null);if(raw==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"CreateJobObject failed.");
        var safe=new SafeFileHandle(raw,true);
        try
        {
            var info=new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();info.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            var size=Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();var ptr=Marshal.AllocHGlobal(size);
            try{Marshal.StructureToPtr(info,ptr,false);if(!SetInformationJobObject(safe.DangerousGetHandle(),JobObjectExtendedLimitInformation,ptr,(uint)size))throw new Win32Exception(Marshal.GetLastWin32Error(),"SetInformationJobObject failed.");}
            finally{Marshal.FreeHGlobal(ptr);}
            if(!AssignProcessToJobObject(safe.DangerousGetHandle(),process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error(),"AssignProcessToJobObject failed.");
            return new WorkerProcessGuard(safe);
        }
        catch{safe.Dispose();throw;}
    }
    public void Dispose()=>_job.Dispose();

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE=0x00002000;
    private const int JobObjectExtendedLimitInformation=9;
    [StructLayout(LayoutKind.Sequential)]private struct JOBOBJECT_BASIC_LIMIT_INFORMATION{public long PerProcessUserTimeLimit;public long PerJobUserTimeLimit;public uint LimitFlags;public UIntPtr MinimumWorkingSetSize;public UIntPtr MaximumWorkingSetSize;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint PriorityClass;public uint SchedulingClass;}
    [StructLayout(LayoutKind.Sequential)]private struct IO_COUNTERS{public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount;}
    [StructLayout(LayoutKind.Sequential)]private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION{public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;public IO_COUNTERS IoInfo;public UIntPtr ProcessMemoryLimit;public UIntPtr JobMemoryLimit;public UIntPtr PeakProcessMemoryUsed;public UIntPtr PeakJobMemoryUsed;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes,string? lpName);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetInformationJobObject(IntPtr hJob,int infoClass,IntPtr lpJobObjectInfo,uint cbJobObjectInfoLength);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(IntPtr hJob,IntPtr hProcess);
}
