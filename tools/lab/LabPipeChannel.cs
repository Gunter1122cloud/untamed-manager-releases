using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace UntamedLab
{
    // Local diagnostics channel only. It cannot enumerate or attach processes;
    // the pipe's kernel-reported owner must equal the newly spawned QEMU PID.
    public sealed class LabPipeChannel : IDisposable
    {
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
        readonly NamedPipeClientStream pipe;
        readonly Encoding encoding=new UTF8Encoding(false,true);
        LabPipeChannel(string name,uint expectedPid)
        {
            if(expectedPid==0||!Regex.IsMatch(name??"",@"\AUntamedLab-Qmp-[a-f0-9]{32}\z"))
                throw new ArgumentException("Invalid owned lab pipe identity");
            pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(5000);uint owner;
                if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out owner)||owner!=expectedPid)
                    throw new UnauthorizedAccessException("Lab pipe is not owned by the expected process");
            }
            catch{pipe.Dispose();throw;}
        }
        public static LabPipeChannel Connect(string name,uint expectedPid){return new LabPipeChannel(name,expectedPid);}
        public void WriteLine(string json)
        {
            if(json==null||json.Length>16384||json.IndexOfAny(new[]{'\r','\n','\0'})>=0)
                throw new ArgumentException("Invalid single-line diagnostic message");
            byte[] bytes=encoding.GetBytes(json+"\r\n");
            if(!pipe.WriteAsync(bytes,0,bytes.Length).Wait(5000))throw new TimeoutException("Lab pipe write deadline");
        }
        public string ReadLine()
        {
            var watch=Stopwatch.StartNew();byte[] line=new byte[16384],one=new byte[1];int count=0;
            for(;;)
            {
                int remaining=5000-(int)watch.ElapsedMilliseconds;
                if(remaining<=0)throw new TimeoutException("Lab pipe read deadline");
                var pending=pipe.ReadAsync(one,0,1);
                if(!pending.Wait(remaining))throw new TimeoutException("Lab pipe read deadline");
                if(pending.Result!=1)throw new EndOfStreamException("Lab pipe closed");
                if(one[0]==10)return encoding.GetString(line,0,count).TrimEnd('\r');
                if(count==line.Length)throw new InvalidDataException("Lab pipe line limit");
                line[count++]=one[0];
            }
        }
        public void Dispose(){pipe.Dispose();}
    }
}
