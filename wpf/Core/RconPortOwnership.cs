using System;
using System.IO;
using System.Runtime.InteropServices;

namespace UntamedManager.Core;

internal static class RconPortOwnership
{
    [DllImport("iphlpapi.dll",SetLastError=true)]
    private static extern uint GetExtendedTcpTable(IntPtr table,ref int size,
        [MarshalAs(UnmanagedType.Bool)]bool sorted,uint family,int tableClass,uint reserved);

    // IPv4 loopback is intentional: no DNS and no remote peer endpoint can
    // enter the isolated mutating queue. Kernel PID ownership supplements RCON auth.
    internal static bool IsOwnedListener(int port,int pid)
    {
        if(!OperatingSystem.IsWindows()||port is <1 or >65535||pid<=0)return false;
        int size=0;uint code=GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,3,0);
        if(code!=122||size is <4 or >16_777_216)return false;
        IntPtr pointer=Marshal.AllocHGlobal(size);
        try
        {
            if(GetExtendedTcpTable(pointer,ref size,false,2,3,0)!=0)return false;
            byte[] bytes=new byte[size];Marshal.Copy(pointer,bytes,0,size);
            int count=BitConverter.ToInt32(bytes,0);
            if(count<0||count>(size-4)/24)throw new InvalidDataException("Invalid Windows TCP ownership table");
            bool matched=false;
            for(int i=0;i<count;i++)
            {
                int at=4+24*i;
                int localPort=(bytes[at+8]<<8)|bytes[at+9];
                bool local=(bytes[at+4]==127&&bytes[at+5]==0&&bytes[at+6]==0&&bytes[at+7]==1)||
                    (bytes[at+4]==0&&bytes[at+5]==0&&bytes[at+6]==0&&bytes[at+7]==0);
                if(localPort!=port||!local)continue;
                if(BitConverter.ToInt32(bytes,at+20)!=pid)return false;
                matched=true;
            }
            return matched;
        }
        finally{Marshal.FreeHGlobal(pointer);}
    }
}
