using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MFAAvalonia.Helper.Licensing;

/// <summary>Versioned offline licenses. The application contains only an issuer public key.</summary>
public static class LicenseToken
{
    public const string Prefix = "MFA1";
    public const string Product = "MFAAvalonia";
    public const int MaximumKeyLength = 4096;

    public static bool IsMachineCode(string? value) => value is not null &&
        Regex.IsMatch(value, "\\A[0-9A-F]{16}\\z", RegexOptions.CultureInvariant);

    public static string CreateTrialCode(string machineCode)
    {
        if (!IsMachineCode(machineCode))
            throw new ArgumentException("Expected a 16-character uppercase machine code.", nameof(machineCode));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(machineCode + "#MFAAvalonia.trial.v1"));
        // Python uuid.UUID(bytes=...) uses network byte order, unlike Guid(byte[]).
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true).ToString("D");
    }

    public static byte[] CreatePayload(string machineCode, DateTimeOffset expiresAtUtc)
    {
        if (!IsMachineCode(machineCode))
            throw new ArgumentException("Invalid machine code.", nameof(machineCode));
        return Encoding.UTF8.GetBytes(Product + "|" + machineCode + "|" +
            expiresAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryVerify(string? key, string machineCode, string publicKeyPem,
        out DateTimeOffset expiresAtUtc)
    {
        expiresAtUtc = default;
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumKeyLength ||
            string.IsNullOrWhiteSpace(publicKeyPem) || !IsMachineCode(machineCode))
            return false;
        try
        {
            var parts = key.Split('.');
            if (parts.Length != 3 || parts[0] != Prefix)
                return false;
            var payload = Decode(parts[1]);
            var signature = Decode(parts[2]);
            if (payload.Length > 256 || signature.Length > 1024)
                return false;
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            if (rsa.KeySize < 2048 || !rsa.VerifyData(payload, signature,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                return false;
            var fields = new UTF8Encoding(false, true).GetString(payload).Split('|');
            if (fields.Length != 3 || fields[0] != Product || fields[1] != machineCode ||
                !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
                return false;
            expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static byte[] Decode(string text)
    {
        if (text.Length == 0 || !Regex.IsMatch(text, "\\A[A-Za-z0-9_-]+\\z", RegexOptions.CultureInvariant))
            throw new FormatException("Invalid base64url data.");
        var standard = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(standard.PadRight((standard.Length + 3) / 4 * 4, '='));
    }
}
