using System.Text.Json;

namespace Plainstall.Core.Catalog;

/// <summary>
///     Turns the JSON form of registry data in the catalog into the typed value that is written:
///     <see cref="uint" /> for dword, <see cref="ulong" /> for qword, <see cref="string" /> for string and
///     expandString, <c>string[]</c> for multiString, <c>byte[]</c> for binary.
/// </summary>
public static class RegistryData
{
    /// <summary>Returns the typed value, or null with <paramref name="error" /> set when the data does not fit the type.</summary>
    public static object? TryConvert(RegistryValueType type, JsonElement data, out string? error)
    {
        error = null;
        switch (type)
        {
            case RegistryValueType.Dword:
                if (data.ValueKind == JsonValueKind.Number && data.TryGetUInt32(out var dword)) return dword;
                error = "dword data must be an integer from 0 to 4294967295";
                return null;

            case RegistryValueType.Qword:
                if (data.ValueKind == JsonValueKind.Number && data.TryGetUInt64(out var qword)) return qword;
                error = "qword data must be a non-negative integer";
                return null;

            case RegistryValueType.String:
            case RegistryValueType.ExpandString:
                if (data.ValueKind == JsonValueKind.String) return data.GetString()!;
                error = "string data must be a JSON string";
                return null;

            case RegistryValueType.MultiString:
                if (data.ValueKind == JsonValueKind.Array &&
                    data.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
                    return data.EnumerateArray().Select(e => e.GetString()!).ToArray();
                error = "multiString data must be an array of strings";
                return null;

            case RegistryValueType.Binary:
                if (data.ValueKind == JsonValueKind.String)
                {
                    var hex = data.GetString()!;
                    if (hex.Length % 2 == 0 && hex.All(Uri.IsHexDigit)) return Convert.FromHexString(hex);
                }

                error = "binary data must be a hex string with an even number of digits";
                return null;

            default:
                error = "unknown registry type";
                return null;
        }
    }

    public static object ToTypedValue(RegistryValueType type, JsonElement data)
    {
        return TryConvert(type, data, out var error) ?? throw new PlainstallException(ErrorKind.InvalidInput, error!);
    }

    /// <summary>Compares two typed registry values as produced by <see cref="TryConvert" />.</summary>
    public static bool ValuesEqual(object? a, object? b)
    {
        return (a, b) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (string[] x, string[] y) => x.SequenceEqual(y, StringComparer.Ordinal),
            (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
            _ => a.Equals(b)
        };
    }
}