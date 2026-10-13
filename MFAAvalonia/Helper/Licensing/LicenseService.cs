using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MFAAvalonia.Helper.Licensing;

/// <summary>UI-independent, fail-closed authorization. Opening the UI never starts a trial.</summary>
public sealed class LicenseService : ILicenseService
{
    public static readonly TimeSpan TrialDuration = TimeSpan.FromHours(2);
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(30);
    private readonly object _sync = new();
    private readonly ILicenseStore _store;
    private readonly string _publicKey;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _bootUtc;
    private readonly long _bootTimestamp;
    private DateTimeOffset _greatestUtc;
    private bool _hasSeenRecord;
    private LicenseSnapshot _snapshot;

    public LicenseService(string machineCode, string publicKeyPem, ILicenseStore store,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        MachineCode = machineCode;
        TrialCode = LicenseToken.CreateTrialCode(machineCode);
        _publicKey = publicKeyPem;
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
        _bootUtc = _greatestUtc = _time.GetUtcNow();
        _bootTimestamp = _time.GetTimestamp();
        _snapshot = new(LicenseStatus.Unlicensed, null, _bootUtc);
    }

    public string MachineCode { get; }
    public string TrialCode { get; }
    public LicenseSnapshot Snapshot { get { lock (_sync) return _snapshot; } }

    public LicenseSnapshot Refresh()
    {
        lock (_sync)
        {
            try
            {
                using var lease = _store.AcquireLock();
                var record = ReadRecord();
                if (!TryGetNow(record, out var now))
                    return SetSnapshot(LicenseStatus.ClockRollback, null, now);
                if (record is null)
                    return SetSnapshot(LicenseStatus.Unlicensed, null, now);
                var status = Evaluate(record, now, out var expires);
                if (status is LicenseStatus.Trial or LicenseStatus.Formal)
                {
                    if (now - record.LastSeenUtc >= CheckpointInterval)
                        _store.Save(record with { LastSeenUtc = now });
                }
                return SetSnapshot(status, expires, now);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                return SetSnapshot(LicenseStatus.StorageError, null, _time.GetUtcNow());
            }
        }
    }

    public LicenseActivationResult Activate(string? activationKey)
    {
        lock (_sync)
        {
            var key = activationKey?.Trim() ?? string.Empty;
            // An invalid renewal must not overwrite an existing valid activation.
            Refresh();
            if (key.Length == 0 || key.Length > LicenseToken.MaximumKeyLength)
                return Failure(LicenseStatus.Invalid);
            try
            {
                using var lease = _store.AcquireLock();
                var previous = ReadRecord();
                if (!TryGetNow(previous, out var now))
                {
                    SetSnapshot(LicenseStatus.ClockRollback, null, now);
                    return Failure(LicenseStatus.ClockRollback);
                }
                DateTimeOffset expires;
                DateTimeOffset? trialStart = previous?.TrialStartedAtUtc;
                LicenseStatus status;
                if (string.Equals(key, TrialCode, StringComparison.OrdinalIgnoreCase))
                {
                    // Do not accidentally downgrade a valid formal license to a trial.
                    if (previous is not null && Evaluate(previous, now, out _) == LicenseStatus.Formal)
                        return Failure(LicenseStatus.Invalid);
                    trialStart ??= now;
                    expires = trialStart.Value + TrialDuration;
                    status = LicenseStatus.Trial;
                    key = TrialCode;
                }
                else if (LicenseToken.TryVerify(key, MachineCode, _publicKey, out expires))
                {
                    status = LicenseStatus.Formal;
                }
                else
                {
                    return Failure(LicenseStatus.Invalid);
                }
                if (expires <= now)
                    return Failure(LicenseStatus.Expired);
                var record = new LicenseRecord
                {
                    MachineCode = MachineCode,
                    ActivationKey = key,
                    TrialStartedAtUtc = trialStart,
                    LastSeenUtc = now
                };
                // Persistence is part of activation: a failed save must never unlock the app.
                _store.Save(record);
                _hasSeenRecord = true;
                SetSnapshot(status, expires, now);
                return new(true, LicenseStatus.Unlicensed, _snapshot);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                SetSnapshot(LicenseStatus.StorageError, null, _time.GetUtcNow());
                return Failure(LicenseStatus.StorageError);
            }
        }
    }

    private LicenseRecord? ReadRecord()
    {
        var record = _store.Load();
        if (record is null)
        {
            if (_hasSeenRecord)
                throw new InvalidDataException("Previously loaded license state is missing.");
            return null;
        }
        _hasSeenRecord = true;
        if (record.Version != 1 || record.MachineCode != MachineCode ||
            string.IsNullOrWhiteSpace(record.ActivationKey) ||
            record.ActivationKey.Length > LicenseToken.MaximumKeyLength ||
            record.LastSeenUtc == default ||
            record.TrialStartedAtUtc > record.LastSeenUtc ||
            (record.ActivationKey == TrialCode && record.TrialStartedAtUtc is null))
            throw new InvalidDataException("Invalid license state.");
        return record;
    }

    private LicenseStatus Evaluate(LicenseRecord record, DateTimeOffset now,
        out DateTimeOffset? expires)
    {
        expires = null;
        LicenseStatus kind;
        if (record.ActivationKey == TrialCode)
        {
            if (record.TrialStartedAtUtc is null)
                return LicenseStatus.Invalid;
            expires = record.TrialStartedAtUtc.Value + TrialDuration;
            kind = LicenseStatus.Trial;
        }
        else if (LicenseToken.TryVerify(record.ActivationKey, MachineCode, _publicKey, out var formalExpiry))
        {
            expires = formalExpiry;
            kind = LicenseStatus.Formal;
        }
        else
        {
            return LicenseStatus.Invalid;
        }
        return expires <= now ? LicenseStatus.Expired : kind;
    }

    private bool TryGetNow(LicenseRecord? record, out DateTimeOffset now)
    {
        var wall = _time.GetUtcNow();
        var monotonic = _bootUtc + _time.GetElapsedTime(_bootTimestamp, _time.GetTimestamp());
        var minimum = _greatestUtc;
        if (record is not null && record.LastSeenUtc > minimum)
            minimum = record.LastSeenUtc;
        now = wall > monotonic ? wall : monotonic;
        if (now < minimum) now = minimum;
        if (wall + ClockTolerance < minimum)
            return false;
        _greatestUtc = now;
        return true;
    }

    private LicenseSnapshot SetSnapshot(LicenseStatus status, DateTimeOffset? expires, DateTimeOffset now) =>
        _snapshot = new(status, expires, now);

    private LicenseActivationResult Failure(LicenseStatus status) => new(false, status, _snapshot);

    private static bool IsStorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        CryptographicException or JsonException or ArgumentException or FormatException or
        InvalidDataException or NotSupportedException;
}
