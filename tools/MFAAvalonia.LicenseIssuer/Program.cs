using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MFAAvalonia.Helper.Licensing;

namespace MFAAvalonia.LicenseIssuer;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--keygen", var privatePath, var publicPath])
            {
                GenerateKeys(privatePath, publicPath);
                return 0;
            }
            if (args is ["--trial-code", var trialMachine])
            {
                Console.WriteLine(LicenseToken.CreateTrialCode(trialMachine.Trim().ToUpperInvariant()));
                return 0;
            }
            if (args is ["--gen-key", var keyPath, var machine, var expiry])
            {
                Issue(keyPath, machine, expiry);
                return 0;
            }
            Console.Error.WriteLine("MFAAvalonia 授权签发工具（仅作者使用，不随客户端分发）");
            Console.Error.WriteLine("--gen-key <私钥.pem> <16位机器码> <带时区的到期时间>");
            Console.Error.WriteLine("示例：--gen-key /secure/author-private.pem 9DCDFDCB9D96EEAB 2027-12-31T23:59:59+08:00");
            Console.Error.WriteLine("--keygen <新私钥.pem> <新公钥.pem>  （不会覆盖已有文件）");
            Console.Error.WriteLine("--trial-code <16位机器码>");
            return args.Length == 0 || args is ["--help"] ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            CryptographicException or ArgumentException or FormatException or InvalidOperationException)
        {
            Console.Error.WriteLine($"签发失败：{ex.Message}");
            return 2;
        }
    }

    private static void Issue(string keyPath, string machine, string expiry)
    {
        machine = machine.Trim().ToUpperInvariant();
        if (!LicenseToken.IsMachineCode(machine))
            throw new ArgumentException("机器码必须是授权窗口显示的 16 位十六进制机器码。");
        if (!Regex.IsMatch(expiry, "(Z|[+-][0-9]{2}:[0-9]{2})\\z", RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParseExact(expiry,
                ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expiresAt))
            throw new ArgumentException("到期时间必须显式包含时区，例如 2027-12-31T23:59:59+08:00 或 2027-12-31T15:59:59Z。");
        if (expiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentException("到期时间必须在将来。");
        if (new FileInfo(keyPath).Length > 16384)
            throw new InvalidDataException("私钥文件过大。");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(keyPath));
        if (rsa.KeySize < 2048)
            throw new CryptographicException("RSA 私钥至少需要 2048 位。");
        var payload = LicenseToken.CreatePayload(machine, expiresAt);
        var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var token = $"{LicenseToken.Prefix}.{LicenseToken.Encode(payload)}.{LicenseToken.Encode(signature)}";
        Console.Error.WriteLine($"机器码：{machine}；到期时间（UTC）：{expiresAt:O}");
        // Standard output contains only the public activation token, never the private key.
        Console.WriteLine(token);
    }

    private static void GenerateKeys(string privatePath, string publicPath)
    {
        privatePath = Path.GetFullPath(privatePath);
        publicPath = Path.GetFullPath(publicPath);
        if (privatePath == publicPath || File.Exists(privatePath) || File.Exists(publicPath))
            throw new IOException("输出路径不能相同，且不能覆盖现有密钥。");
        using var rsa = RSA.Create(3072);
        WriteNew(privatePath, rsa.ExportPkcs8PrivateKeyPem(), secret: true);
        WriteNew(publicPath, rsa.ExportSubjectPublicKeyInfoPem(), secret: false);
        Console.Error.WriteLine("密钥已生成。私钥仅由作者离线保管；将公钥写入 LicensePublicKey.Pem 后重新构建客户端。");
        Console.Error.WriteLine("更换公钥会使旧公钥签发的正式授权失效，需要重新签发。");
    }

    private static void WriteNew(string path, string text, bool secret)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = secret
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        using var stream = new FileStream(path, options);
        var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
        try { stream.Write(bytes); stream.Flush(true); }
        finally { if (secret) CryptographicOperations.ZeroMemory(bytes); }
    }
}
