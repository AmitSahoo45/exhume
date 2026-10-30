using System.Security.Cryptography;
using System.Text.Json;

namespace Exhume.Core;

internal static class IdentityHash
{
    internal static string Create(params string[] components)
    {
        foreach (string component in components)
        {
            ArgumentNullException.ThrowIfNull(component);
        }

        // Structured encoding preserves component boundaries even when values contain separators.
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(components);
        return Convert.ToHexStringLower(SHA256.HashData(encoded));
    }
}
