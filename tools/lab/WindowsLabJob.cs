using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UntamedLab
{
    // Test-lab helper only. Never attaches to an existing process, changes a
    // service, or changes host virtualization settings. Own job dies on close.
    public sealed class WindowsLabJob : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] struct IoCounters
        { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)] struct CpuLimits { public uint Flags, Rate; }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct StartupInfo
        {
            public uint Size;
            public string Reserved, Desktop, Title;
            public uint X,Y,XSize,YSize,XChars,YChars,Fill,Flags;
            public ushort ShowWindow, ReservedBytes;
            public IntPtr ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] struct ProcessInfo
        { public IntPtr Process, Thread; public uint Pid, Tid; }
        [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr security, string name);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int kind,IntPtr info,uint size);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int kind,IntPtr info,uint size,IntPtr returned);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(
            string app,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,
            IntPtr environment,string directory,ref StartupInfo startup,out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateProcess(IntPtr process,uint code);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(IntPtr job,uint code);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
        [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        IntPtr job, process;
        public uint ProcessId { get; private set; }
        public uint CpuRate { get; private set; }
        public ulong MemoryLimit { get; private set; }
        WindowsLabJob() {}
        static void Require(bool ok) { if(!ok)throw new Win32Exception(Marshal.GetLastWin32Error()); }
        static void Set<T>(IntPtr job,int kind,T data)
        {
            int bytes=Marshal.SizeOf(typeof(T)); IntPtr buffer=Marshal.AllocHGlobal(bytes);
            try { Marshal.StructureToPtr(data,buffer,false); Require(SetInformationJobObject(job,kind,buffer,(uint)bytes)); }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        static T Query<T>(IntPtr job,int kind)
        {
            int bytes=Marshal.SizeOf(typeof(T)); IntPtr buffer=Marshal.AllocHGlobal(bytes);
            try { Require(QueryInformationJobObject(job,kind,buffer,(uint)bytes,IntPtr.Zero)); return (T)Marshal.PtrToStructure(buffer,typeof(T)); }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        // Windows CommandLineToArgvW-compatible quoting; never goes via a shell.
        public static string Quote(string value)
        {
            if(value==null||value.IndexOf('\0')>=0)throw new ArgumentException("Invalid argument");
            var result=new StringBuilder("\""); int slashes=0;
            foreach(char c in value)
            {
                if(c=='\\'){slashes++;continue;}
                if(c=='\"'){result.Append('\\',slashes*2+1);result.Append(c);slashes=0;continue;}
                result.Append('\\',slashes);slashes=0;result.Append(c);
            }
            return result.Append('\\',slashes*2).Append('"').ToString();
        }
        public static WindowsLabJob Start(string executable,string[] arguments,string workingDirectory,
            uint cpuPercent,ulong memoryBytes)
        {
            if(!Environment.Is64BitProcess||cpuPercent<1||cpuPercent>10||memoryBytes<64UL*1024*1024||memoryBytes>10UL*1024*1024*1024)
                throw new ArgumentOutOfRangeException("Lab resource limits");
            if(!Path.IsPathRooted(executable)||!File.Exists(executable)||!Directory.Exists(workingDirectory))
                throw new ArgumentException("Explicit executable and working directory required");
            var owned=new WindowsLabJob(); IntPtr thread=IntPtr.Zero;
            try
            {
                owned.job=CreateJobObject(IntPtr.Zero,null); Require(owned.job!=IntPtr.Zero);
                var limits=new ExtendedLimits();
                // KILL_ON_JOB_CLOSE | JOB_MEMORY | ACTIVE_PROCESS | PRIORITY_CLASS.
                limits.Basic.Flags=0x2000|0x200|0x8|0x20;
                limits.Basic.ActiveProcesses=1;limits.Basic.Priority=0x40;
                limits.JobMemory=new UIntPtr(memoryBytes); Set(owned.job,9,limits);
                Set(owned.job,15,new CpuLimits{Flags=5,Rate=cpuPercent*100});
                var cpu=Query<CpuLimits>(owned.job,15);var verified=Query<ExtendedLimits>(owned.job,9);
                if(cpu.Flags!=5||cpu.Rate!=cpuPercent*100||verified.JobMemory.ToUInt64()!=memoryBytes||
                    verified.Basic.Flags!=limits.Basic.Flags||verified.Basic.ActiveProcesses!=1)
                    throw new InvalidOperationException("Lab limits could not be verified");
                owned.CpuRate=cpu.Rate;owned.MemoryLimit=memoryBytes;
                var command=new StringBuilder(Quote(executable));
                foreach(string arg in arguments)command.Append(' ').Append(Quote(arg));
                var startup=new StartupInfo{Size=(uint)Marshal.SizeOf(typeof(StartupInfo))}; ProcessInfo created;
                // No inherited handles. No thread executes before job assignment.
                Require(CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,false,
                    0x4|0x08000000|0x40,IntPtr.Zero,workingDirectory,ref startup,out created));
                owned.process=created.Process;thread=created.Thread;owned.ProcessId=created.Pid;
                Require(AssignProcessToJobObject(owned.job,owned.process));
                Require(ResumeThread(thread)!=uint.MaxValue);
                return owned;
            }
            catch
            {
                // This handle can only refer to the child created above.
                if(owned.process!=IntPtr.Zero)TerminateProcess(owned.process,125);
                owned.Dispose();throw;
            }
            finally { if(thread!=IntPtr.Zero)CloseHandle(thread); }
        }
        public bool Wait(uint milliseconds)
        {
            if(process==IntPtr.Zero)throw new ObjectDisposedException("WindowsLabJob");
            uint status=WaitForSingleObject(process,milliseconds);
            if(status==0)return true;if(status==258)return false;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public uint ExitCode
        {
            get { uint code;Require(GetExitCodeProcess(process,out code));return code; }
        }
        public void Stop()
        {
            if(job==IntPtr.Zero)throw new ObjectDisposedException("WindowsLabJob");
            Require(TerminateJobObject(job,124));
        }
        public void Dispose()
        {
            if(job!=IntPtr.Zero){CloseHandle(job);job=IntPtr.Zero;}
            if(process!=IntPtr.Zero){CloseHandle(process);process=IntPtr.Zero;}
        }
    }
}
