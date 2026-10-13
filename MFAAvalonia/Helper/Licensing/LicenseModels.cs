using System;

namespace MFAAvalonia.Helper.Licensing;

public enum LicenseStatus
{
    Unlicensed,
    Trial,
    Formal,
    Expired,
    Invalid,
    ClockRollback,
    StorageError
}

public sealed record LicenseSnapshot(
    LicenseStatus Status,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset EvaluatedAtUtc)
{
    public bool IsAuthorized => Status is LicenseStatus.Trial or LicenseStatus.Formal;
    public TimeSpan Remaining => IsAuthorized && ExpiresAtUtc > EvaluatedAtUtc
        ? ExpiresAtUtc.Value - EvaluatedAtUtc : TimeSpan.Zero;
}

public sealed record LicenseActivationResult(bool Success, LicenseStatus Error, LicenseSnapshot Snapshot);

public sealed record LicenseRecord
{
    public int Version { get; init; } = 1;
    public string MachineCode { get; init; } = string.Empty;
    public string ActivationKey { get; init; } = string.Empty;
    public DateTimeOffset? TrialStartedAtUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
}

public interface ILicenseStore
{
    // Hold the lease across read/validate/write, including across application instances.
    IDisposable AcquireLock();
    LicenseRecord? Load();
    void Save(LicenseRecord record);
}

public interface ILicenseService
{
    string MachineCode { get; }
    string TrialCode { get; }
    LicenseSnapshot Snapshot { get; }
    LicenseSnapshot Refresh();
    LicenseActivationResult Activate(string? activationKey);
}
