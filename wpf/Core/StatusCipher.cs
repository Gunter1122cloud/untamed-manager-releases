using System;
using System.Security.Cryptography;
using System.Text;

namespace UntamedManager.Core;

// Versioned authenticated encryption. No plaintext fallback: peers must both be upgraded.
public static class StatusCipher
{
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("UntamedManager/status/v2");
    public static byte[] Encrypt(byte[] pairingKey, byte[] plaintext)
    {
        byte[] key=HMACSHA256.HashData(pairingKey,Context);
        try
        {
            byte[] frame=new byte[29+plaintext.Length];
            frame[0]=2;
            RandomNumberGenerator.Fill(frame.AsSpan(1,12));
            using var cipher=new AesGcm(key,16);
            cipher.Encrypt(frame.AsSpan(1,12),plaintext,frame.AsSpan(29),frame.AsSpan(13,16),Context);
            return frame;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static byte[] Decrypt(byte[] pairingKey, byte[] frame)
    {
        if(frame.Length<29 || frame.Length>180000 || frame[0]!=2)
            throw new CryptographicException("Unsupported encrypted status frame");
        byte[] key=HMACSHA256.HashData(pairingKey,Context);
        try
        {
            byte[] plaintext=new byte[frame.Length-29];
            using var cipher=new AesGcm(key,16);
            cipher.Decrypt(frame.AsSpan(1,12),frame.AsSpan(29),frame.AsSpan(13,16),plaintext,Context);
            return plaintext;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
