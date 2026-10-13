using System;
using System.IO;
using System.Threading;

namespace MFAAvalonia.Helper.Licensing;

/// <summary>One authority for UI, background jobs, timers and all application instances.</summary>
public static class LicenseRuntime
{
    private static readonly Lazy<ILicenseService> Authority = new(CreateService);
    private static readonly object MonitorLock = new();
    private static Timer? _monitor;
    private static Timer? _exitWatchdog;
    private static int _checking;
    private static int _revoked;
    public static ILicenseService Service => Authority.Value;
    public static bool IsSessionRevoked => Volatile.Read(ref _revoked) != 0;
    public static event Action<LicenseSnapshot>? AuthorizationLost;

    public static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MFAAvalonia", "Licensing");

    public static void RequireAuthorized()
    {
        if (IsSessionRevoked)
            throw new LicenseRequiredException();
        var snapshot = Service.Refresh();
        if (!snapshot.IsAuthorized)
        {
            lock (MonitorLock)
            {
                if (_monitor is not null) Revoke(snapshot);
            }
            throw new LicenseRequiredException();
        }
        StartMonitor();
        // A concurrent monitor tick may have revoked the session during verification.
        if (IsSessionRevoked) throw new LicenseRequiredException();
    }

    private static ILicenseService CreateService()
    {
        var directory = StateDirectory;
        if (!Path.IsPathRooted(directory))
            throw new InvalidOperationException("A writable application-data directory is required for licensing.");
        var store = new FileLicenseStore(directory);
        // Coordinate installation-ID creation with other processes using the same state.
        using var lease = store.AcquireLock();
        return new LicenseService(MachineIdentity.GetMachineCode(directory), LicensePublicKey.Pem, store);
    }

    private static void StartMonitor()
    {
        lock (MonitorLock)
        {
            _monitor ??= new Timer(CheckAuthorization, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    private static void CheckAuthorization(object? state)
    {
        if (IsSessionRevoked || Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            var snapshot = Service.Refresh();
            if (!snapshot.IsAuthorized) Revoke(snapshot);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"License verification failed: {ex.GetType().Name}");
            Revoke(new LicenseSnapshot(LicenseStatus.StorageError, null, DateTimeOffset.UtcNow));
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private static void Revoke(LicenseSnapshot snapshot)
    {
        if (Interlocked.Exchange(ref _revoked, 1) != 0) return;
        // A stalled UI/native call must not keep the application alive indefinitely.
        _exitWatchdog = new Timer(_ => Environment.Exit(3), null,
            TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        LoggerHelper.Warning($"License session locked: {snapshot.Status}");
        try { AuthorizationLost?.Invoke(snapshot); }
        catch (Exception ex) { LoggerHelper.Warning($"License shutdown failed: {ex.GetType().Name}"); }
    }

    public static void StopMonitoring()
    {
        lock (MonitorLock)
        {
            _monitor?.Dispose();
            _monitor = null;
        }
        // Do not cancel the fail-closed watchdog during a revoked session's shutdown.
    }
}

public sealed class LicenseRequiredException : InvalidOperationException
{
    public LicenseRequiredException() : base("Software authorization is required. Open the activation window.") { }
}
