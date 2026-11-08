using System;
using System.Security.Cryptography;
using System.Text;

namespace UntamedManager.Core;

/// <summary>Current-user DPAPI payload protection shared by the Manager and its standalone updater.</summary>
internal static class LocalDataProtection
{
    private sealed record TestProtector(Func<string,string> Protect,Func<string,string> Unprotect);
    private static readonly AsyncLocal<TestProtector?> IsolatedTestProtector=new();

    internal static IDisposable UseTestProtectorForTests(Func<string,string> protect,
        Func<string,string> unprotect)
    {
        ArgumentNullException.ThrowIfNull(protect);ArgumentNullException.ThrowIfNull(unprotect);
        var prior=IsolatedTestProtector.Value;
        IsolatedTestProtector.Value=new TestProtector(protect,unprotect);
        return new TestProtectorScope(prior);
    }

    private sealed class TestProtectorScope(TestProtector? prior):IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)==0)IsolatedTestProtector.Value=prior;
        }
    }

    internal static string Protect(string plain)
    {
        if(string.IsNullOrEmpty(plain))return "";
        if(IsolatedTestProtector.Value is { } test)return test.Protect(plain);
        return Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(plain)));
    }

    internal static string Unprotect(string encrypted)
    {
        if(string.IsNullOrEmpty(encrypted))return "";
        if(IsolatedTestProtector.Value is { } test)return test.Unprotect(encrypted);
        return Encoding.UTF8.GetString(UnprotectBytes(Convert.FromBase64String(encrypted)));
    }

    internal static byte[] ProtectBytes(byte[] plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        if(IsolatedTestProtector.Value is { } test)
            return Encoding.UTF8.GetBytes(test.Protect(Convert.ToBase64String(plain)));
        return ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);
    }

    internal static byte[] UnprotectBytes(byte[] encrypted)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        if(IsolatedTestProtector.Value is { } test)
        {
            try{return Convert.FromBase64String(test.Unprotect(Encoding.UTF8.GetString(encrypted)));}
            catch(Exception ex) when(ex is not OutOfMemoryException)
            {throw new CryptographicException("Protected test binding is invalid",ex);}
        }
        return ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser);
    }
}
