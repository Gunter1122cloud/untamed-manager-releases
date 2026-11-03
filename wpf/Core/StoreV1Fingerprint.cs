using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UntamedManager.Core;

// Untamed Cluster Vault: compatibility with an existing on-disk JSON hash.
// This is independent C# code, not a copy of the original manager implementation.
// Object insertion order is preserved; re-sorting members or properties changes
// the hash. Only the documented string/array/object schema is accepted.
public static class StoreV1Fingerprint
{
    public static string Compute(JsonElement config)
    {
        if(config.ValueKind!=JsonValueKind.Object)
            throw new InvalidDataException("Expected historical Store configuration");
        var sb=new StringBuilder(2048);
        Append(config,sb,0);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static bool Matches(JsonElement config,string expected)
    {
        if(expected==null || expected.Length!=64 ||
           expected.Any(c=>!Uri.IsHexDigit(c)))return false;
        var found=Convert.FromHexString(Compute(config));
        return CryptographicOperations.FixedTimeEquals(found,Convert.FromHexString(expected));
    }

    private static void Append(JsonElement value,StringBuilder sb,int depth)
    {
        if(depth>32)throw new InvalidDataException("Store configuration depth exceeded");
        switch(value.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                bool first=true;
                var seen=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach(var property in value.EnumerateObject())
                {
                    if(!seen.Add(property.Name))
                        throw new InvalidDataException("Duplicate Store config property");
                    if(!first)sb.Append(',');
                    first=false;
                    Quote(property.Name,sb);
                    sb.Append(':');
                    Append(property.Value,sb,depth+1);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                bool opening=true;
                foreach(var element in value.EnumerateArray())
                {
                    if(!opening)sb.Append(',');
                    opening=false;
                    Append(element,sb,depth+1);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                Quote(value.GetString()??"",sb);
                break;
            case JsonValueKind.True: sb.Append("true");break;
            case JsonValueKind.False: sb.Append("false");break;
            case JsonValueKind.Null: sb.Append("null");break;
            default:
                // Numbers require JS ToString/exponent canonicalization, not
                // .NET's own formatter. The known v1 config uses string members.
                // Refuse unknown future shapes instead of accepting wrong hashes.
                throw new InvalidDataException("Unsupported Store configuration value");
        }
    }

    private static void Quote(string text,StringBuilder sb)
    {
        sb.Append('"');
        foreach(char c in text)
        {
            switch(c)
            {
                case '"': sb.Append("\\\"");break;
                case '\\':sb.Append("\\\\");break;
                case '\b':sb.Append("\\b");break;
                case '\f':sb.Append("\\f");break;
                case '\n':sb.Append("\\n");break;
                case '\r':sb.Append("\\r");break;
                case '\t':sb.Append("\\t");break;
                default:
                    if(c<0x20)sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
