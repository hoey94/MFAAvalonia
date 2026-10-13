using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MFAAvalonia.Helper.Licensing;

/// <summary>
/// Atomic local state with an inter-process lease. Local authentication detects accidental edits;
/// offline state cannot defeat a privileged local attacker or a complete filesystem rollback.
/// </summary>
public sealed class FileLicenseStore : ILicenseStore
{
    private const int MaximumFileSize = 65536;
    private readonly string _directory;
    private readonly string _statePath;
    private readonly string _keyPath;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MFAAvalonia.Licensing.v1");

    public FileLicenseStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        _statePath = Path.Combine(_directory, "license-state.json");
        _keyPath = Path.Combine(_directory, "state-auth.key");
    }

    public IDisposable AcquireLock()
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(Path.Combine(_directory, "state.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(25);
            }
        }
    }

    public LicenseRecord? Load()
    {
        var stateExists = File.Exists(_statePath);
        var keyExists = File.Exists(_keyPath);
        if (!stateExists && !keyExists)
            return null;
        if (!stateExists || !keyExists)
            throw new InvalidDataException("Incomplete license state; restore the original state files.");
        var envelope = JsonSerializer.Deserialize<Envelope>(ReadBounded(_statePath))
            ?? throw new InvalidDataException("Missing license envelope.");
        var payload = Convert.FromBase64String(envelope.Payload);
        var signature = Convert.FromBase64String(envelope.Signature);
        var key = ReadKey();
        try
        {
            var expected = HMACSHA256.HashData(key, payload);
            if (!CryptographicOperations.FixedTimeEquals(signature, expected))
                throw new InvalidDataException("License state authentication failed.");
            return JsonSerializer.Deserialize<LicenseRecord>(payload)
                ?? throw new InvalidDataException("Missing license record.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Save(LicenseRecord record)
    {
        var key = File.Exists(_keyPath) ? ReadKey() : RandomNumberGenerator.GetBytes(32);
        try
        {
            if (!File.Exists(_keyPath))
            {
                var stored = OperatingSystem.IsWindows()
                    ? ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser) : key;
                WriteAtomic(_keyPath, stored);
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(record);
            var envelope = new Envelope(Convert.ToBase64String(payload),
                Convert.ToBase64String(HMACSHA256.HashData(key, payload)));
            WriteAtomic(_statePath, JsonSerializer.SerializeToUtf8Bytes(envelope));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] ReadKey()
    {
        var bytes = ReadBounded(_keyPath);
        var key = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser) : bytes;
        if (key.Length != 32)
            throw new InvalidDataException("Invalid local state key.");
        return key;
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileSize)
            throw new InvalidDataException("License state is too large.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public sealed record Envelope(string Payload, string Signature);
}
