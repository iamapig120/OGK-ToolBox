using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OGKToolBox.Application.Abstractions;

namespace OGKToolBox.Application.Services;

public sealed class OpaqueIdService(byte[] secret) : IOpaqueIdService
{
    public string Create(string category, params string[] values)
    {
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { category }.Concat(values))));
        using var hmac = new HMACSHA256(secret);
        var signature = Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(payload)));
        return payload + "." + signature;
    }

    public IReadOnlyList<string> Read(string id, string expectedCategory)
    {
        var parts = id.Split('.', 2);
        if (parts.Length != 2) throw new KeyNotFoundException("The requested item id is invalid.");
        using var hmac = new HMACSHA256(secret);
        var expected = hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0]));
        byte[] supplied;
        try { supplied = FromBase64Url(parts[1]); }
        catch (FormatException) { throw new KeyNotFoundException("The requested item id is invalid."); }
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
            throw new KeyNotFoundException("The requested item id is invalid.");
        string[] values;
        try { values = JsonSerializer.Deserialize<string[]>(FromBase64Url(parts[0])) ?? []; }
        catch (JsonException) { throw new KeyNotFoundException("The requested item id is invalid."); }
        if (values.Length == 0 || !values[0].Equals(expectedCategory, StringComparison.Ordinal))
            throw new KeyNotFoundException("The requested item id has the wrong type.");
        return values[1..];
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        var decoded = Convert.FromBase64String(normalized);
        if (!Base64Url(decoded).Equals(value, StringComparison.Ordinal)) throw new FormatException();
        return decoded;
    }
}
