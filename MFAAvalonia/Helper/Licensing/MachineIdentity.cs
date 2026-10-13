using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MFAAvalonia.Helper.Licensing;

internal static class MachineIdentity
{
    public static string GetMachineCode(string stateDirectory)
    {
        var identity = ReadPlatformIdentity();
        if (string.IsNullOrWhiteSpace(identity))
        {
            // Stable ordering, independent of adapter enumeration order or connection status.
            try
            {
                identity = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                    .Select(n => n.GetPhysicalAddress().GetAddressBytes())
                    .Where(b => b.Length == 6 && b.Any(v => v != 0))
                    .Select(Convert.ToHexString)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (identity is not null) identity = "mac:" + identity;
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                identity = null;
            }
        }
        // Sandboxed platforms may expose neither OS nor hardware IDs. Persist an installation
        // identity instead of returning a different random machine code on every launch.
        identity ??= ReadInstallationIdentity(stateDirectory);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(identity.Trim() + "#MFAAvalonia.machine.v1")))[..16];
    }

    private static string? ReadPlatformIdentity()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var id = key?.GetValue("MachineGuid") as string;
                return string.IsNullOrWhiteSpace(id) ? null : "windows:" + id.Trim().ToLowerInvariant();
            }
            if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
            {
                foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
                {
                    if (!File.Exists(path)) continue;
                    var id = File.ReadAllText(path).Trim();
                    if (Regex.IsMatch(id, "\\A[0-9a-fA-F]{32}\\z"))
                        return "linux:" + id.ToLowerInvariant();
                }
            }
            if (OperatingSystem.IsMacOS())
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/sbin/ioreg",
                    Arguments = "-rd1 -c IOPlatformExpertDevice",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (process is null) return null;
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                    return null;
                }
                var match = Regex.Match(output.GetAwaiter().GetResult(),
                    "\"IOPlatformUUID\"\\s*=\\s*\"([0-9A-Fa-f-]+)\"");
                if (match.Success) return "macos:" + match.Groups[1].Value.ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The deterministic MAC fallback is used when platform identity is inaccessible.
        }
        return null;
    }

    private static string ReadInstallationIdentity(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "installation-id");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = new FileStream(path, options);
            var bytes = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
            stream.Write(bytes);
            stream.Flush(true);
        }
        catch (IOException) when (File.Exists(path)) { }
        var id = File.ReadAllText(path).Trim();
        if (!Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("Invalid installation identity.");
        return "installation:" + id;
    }
}
