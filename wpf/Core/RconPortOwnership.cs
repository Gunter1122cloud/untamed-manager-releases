using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace UntamedManager.Core;

internal static class RconPortOwnership
{
    [DllImport("iphlpapi.dll",SetLastError=true)]
    private static extern uint GetExtendedTcpTable(IntPtr table,ref int size,
        [MarshalAs(UnmanagedType.Bool)]bool sorted,uint family,int tableClass,uint reserved);

    [DllImport("iphlpapi.dll",SetLastError=true)]
    private static extern uint GetExtendedUdpTable(IntPtr table,ref int size,
        [MarshalAs(UnmanagedType.Bool)]bool sorted,uint family,int tableClass,uint reserved);

    internal enum UdpOwnership { Owned, Missing, ForeignOrAmbiguous, Unverified }

    // Windows IP Helper exposes separate IPv4 and IPv6 owner PID tables.
    // An ASA Query listener bound only over IPv6 must not be incorrectly
    // reported missing; a port owned by another PID on either family fails.
    internal static UdpOwnership InspectUdpPort(int port,int pid)
    {
        if(!OperatingSystem.IsWindows()||port is <1 or >65535||pid<=0)
            return UdpOwnership.Unverified;
        if(!TryReadUdpRows(2,12,4,8,out var ipv4)||
           !TryReadUdpRows(23,28,20,24,out var ipv6))
            return UdpOwnership.Unverified;
        bool own=false;
        foreach(var row in ipv4.Concat(ipv6))
        {
            if(row.Port!=port)continue;
            if(row.Pid!=pid)return UdpOwnership.ForeignOrAmbiguous;
            own=true;
        }
        return own?UdpOwnership.Owned:UdpOwnership.Missing;
    }

    internal static bool OwnsUdpPort(int port,int pid)=>
        InspectUdpPort(port,pid)==UdpOwnership.Owned;

    private readonly record struct UdpRow(int Port,int Pid);

    // Pure byte-array decoder is also used by isolated Windows-native tests.
    // IPv4: MIB_UDPROW_OWNER_PID = 12 bytes, port at 4, PID at 8.
    // IPv6: MIB_UDP6ROW_OWNER_PID = 28 bytes, port at 20, PID at 24.
    private static bool TryParseUdpRows(byte[] data,int rowBytes,
        int portOffset,int pidOffset,out UdpRow[] rows)
    {
        rows=Array.Empty<UdpRow>();
        if(data.Length<4||rowBytes<12||pidOffset+4>rowBytes||
           portOffset+2>rowBytes)return false;
        int count=BitConverter.ToInt32(data,0);
        if(count<0||count>(data.Length-4)/rowBytes)return false;
        var result=new UdpRow[count];
        for(int i=0;i<count;i++)
        {
            int p=4+i*rowBytes;
            int port=(data[p+portOffset]<<8)|data[p+portOffset+1];
            result[i]=new(port,BitConverter.ToInt32(data,p+pidOffset));
        }
        rows=result;
        return true;
    }

    internal static string InspectSyntheticUdp(byte[] ipv4,byte[] ipv6,
        int port,int pid)
    {
        if(!TryParseUdpRows(ipv4,12,4,8,out var v4)||
           !TryParseUdpRows(ipv6,28,20,24,out var v6))
            return UdpOwnership.Unverified.ToString();
        bool owned=false;
        foreach(var row in v4.Concat(v6))
        {
            if(row.Port!=port)continue;
            if(row.Pid!=pid)return UdpOwnership.ForeignOrAmbiguous.ToString();
            owned=true;
        }
        return (owned?UdpOwnership.Owned:UdpOwnership.Missing).ToString();
    }

    private static bool TryReadUdpRows(uint family,int rowBytes,
        int portOffset,int pidOffset,out UdpRow[] rows)
    {
        rows=Array.Empty<UdpRow>();
        int size=0;
        uint code=GetExtendedUdpTable(IntPtr.Zero,ref size,false,family,1,0);
        if(code!=122||size is <4 or >16_777_216)return false;
        IntPtr pointer=Marshal.AllocHGlobal(size);
        try
        {
            if(GetExtendedUdpTable(pointer,ref size,false,family,1,0)!=0||
               size is <4 or >16_777_216)return false;
            byte[] bytes=new byte[size];
            Marshal.Copy(pointer,bytes,0,size);
            return TryParseUdpRows(bytes,rowBytes,portOffset,pidOffset,out rows);
        }
        finally{Marshal.FreeHGlobal(pointer);}
    }

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
