using System.Numerics;
using System.Security.Cryptography;

namespace LuckyCat.Services;

/// <summary>字符串主键生成：ULID（26 字符 Crockford Base32，按时间可排序）</summary>
public static class Ids
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewUlid()
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        RandomNumberGenerator.Fill(bytes[6..]);
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var chars = new char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value % 32)];
            value /= 32;
        }
        return new string(chars);
    }
}
