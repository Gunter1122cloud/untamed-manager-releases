using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UntamedManager.Core;

// Verify the actual signature on the file with Windows Authenticode, then pin
// the signer selected by the trust provider. Merely extracting an embedded
// certificate does NOT establish a valid file signature.
internal static class AuthenticodePublisher
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct FileInfoNative
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData,SipClientData;
        public uint UiChoice,RevocationChecks,UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData,UrlReference;
        public uint ProviderFlags,UiContext;
        public IntPtr SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCert
    {
        public uint Size;
        public IntPtr CertificateContext;
    }
    [DllImport("wintrust.dll",ExactSpelling=true)]
    private static extern int WinVerifyTrust(IntPtr hwnd,ref Guid action,ref TrustData data);
    [DllImport("wintrust.dll",ExactSpelling=true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll",ExactSpelling=true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider,uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,uint counterIndex);
    [DllImport("wintrust.dll",ExactSpelling=true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer,uint certIndex);
    
    internal static void Verify(string path,string thumbprint)
    {
        // A missing pin is never treated as verified publisher identity.
        string expected=(thumbprint??"").Replace(" ","",StringComparison.Ordinal);
        if(!OperatingSystem.IsWindows()||expected.Length!=40||!expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("A valid configured Windows publisher certificate pin is required");
        var file=new FileInfoNative{Size=(uint)Marshal.SizeOf<FileInfoNative>(),Path=System.IO.Path.GetFullPath(path)};
        IntPtr ptr=Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoNative>());
        var action=new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var trust=new TrustData{Size=(uint)Marshal.SizeOf<TrustData>(),UiChoice=2,
            RevocationChecks=1,UnionChoice=1,FileInfo=ptr,StateAction=1,ProviderFlags=0x80};
        bool marshalled=false;
        try
        {
            Marshal.StructureToPtr(file,ptr,false);marshalled=true;
            // WTD_UI_NONE: no trust dialogs; verify chain including revocation.
            if(WinVerifyTrust(new IntPtr(-1),ref action,ref trust)!=0)
                throw new InvalidDataException("Installer Authenticode signature is not trusted");
            IntPtr provider=WTHelperProvDataFromStateData(trust.StateData);
            IntPtr signer=provider==IntPtr.Zero?IntPtr.Zero:WTHelperGetProvSignerFromChain(provider,0,false,0);
            IntPtr cert=signer==IntPtr.Zero?IntPtr.Zero:WTHelperGetProvCertFromChain(signer,0);
            if(cert==IntPtr.Zero)throw new InvalidDataException("Verified installer signer unavailable");
            var info=Marshal.PtrToStructure<ProviderCert>(cert);
            if(info.CertificateContext==IntPtr.Zero)throw new InvalidDataException("Verified installer certificate unavailable");
            using var certificate=new X509Certificate2(info.CertificateContext);
            if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(certificate.Thumbprint),Convert.FromHexString(expected)))
                throw new InvalidDataException("Verified installer signer does not match configured pin");
        }
        finally
        {
            if(trust.StateData!=IntPtr.Zero)
            {
                trust.StateAction=2;
                _=WinVerifyTrust(new IntPtr(-1),ref action,ref trust);
            }
            if(marshalled)Marshal.DestroyStructure<FileInfoNative>(ptr);
            Marshal.FreeHGlobal(ptr);
        }
    }
}
