using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MFAAvalonia.Helper.Licensing;

namespace MFAAvalonia.Licensing.Tests;

internal static class Program
{
    private const string Machine = "9DCDFDCB9D96EEAB";
    private static int Main()
    {
        // Test keys are ephemeral. Never use or load an author's production private key here.
        using var issuer = RSA.Create(2048);
        var publicKey = issuer.ExportSubjectPublicKeyInfoPem();
        var passed = 0;
        var failed = 0;
        LicenseService Service(ILicenseStore store, FakeClock clock) => new(Machine, publicKey, store, clock);
        void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
        }

        Test("Trial UUID matches independent Python SHA256/network-order vector", () =>
            Check(LicenseToken.CreateTrialCode(Machine) == "a602c2bd-ee03-c463-299b-45a93aab53f7"));

        Test("Opening and refreshing never starts a trial", () =>
        {
            var store = new MemoryStore(); var clock = new FakeClock(); var service = Service(store, clock);
            Check(service.Refresh().Status == LicenseStatus.Unlicensed);
            clock.Advance(TimeSpan.FromDays(1));
            Check(service.Refresh().Status == LicenseStatus.Unlicensed && store.Record is null);
            Check(store.Saves == 0);
        });

        Test("Empty and malformed activation attempts do not create state", () =>
        {
            var store = new MemoryStore(); var service = Service(store, new FakeClock());
            foreach (var key in new string?[] { null, "", "  ", "wrong", "MFA1.?.?", new('x', 4097) })
                Check(!service.Activate(key).Success);
            Check(store.Record is null && store.Saves == 0);
        });

        Test("Two-hour trial begins on successful explicit activation", () =>
        {
            var store = new MemoryStore(); var clock = new FakeClock(); var service = Service(store, clock);
            clock.Advance(TimeSpan.FromHours(3));
            var activated = service.Activate("  " + service.TrialCode + "  ");
            Check(activated.Success && activated.Snapshot.Status == LicenseStatus.Trial);
            Check(activated.Snapshot.ExpiresAtUtc == clock.GetUtcNow() + TimeSpan.FromHours(2));
        });

        Test("Restart and reactivation preserve original trial start", () =>
        {
            var store = new MemoryStore(); var clock = new FakeClock(); var first = Service(store, clock);
            var original = first.Activate(first.TrialCode).Snapshot.ExpiresAtUtc;
            clock.Advance(TimeSpan.FromMinutes(100));
            var restarted = Service(store, clock);
            Check(restarted.Refresh().Remaining == TimeSpan.FromMinutes(20));
            Check(restarted.Activate(restarted.TrialCode).Snapshot.ExpiresAtUtc == original);
        });

        Test("Trial is expired at the exact two-hour boundary", () =>
        {
            var store = new MemoryStore(); var clock = new FakeClock(); var service = Service(store, clock);
            service.Activate(service.TrialCode); var start = store.Record!.TrialStartedAtUtc;
            clock.Advance(TimeSpan.FromHours(2));
            Check(service.Refresh().Status == LicenseStatus.Expired);
            Check(!service.Activate(service.TrialCode).Success && store.Record.TrialStartedAtUtc == start);
        });

        Test("Valid formal license survives service restart", () =>
        {
            var store = new MemoryStore(); var clock = new FakeClock(); var service = Service(store, clock);
            var expiry = clock.GetUtcNow().AddDays(30);
            Check(service.Activate(Sign(issuer, Machine, expiry)).Success);
            var restarted = Service(store, clock);
            Check(restarted.Refresh().Status == LicenseStatus.Formal);
            Check(restarted.Snapshot.ExpiresAtUtc == expiry);
        });

        Test("Formal license cannot activate a different machine", () =>
        {
            var clock = new FakeClock(); var service = Service(new MemoryStore(), clock);
            Check(!service.Activate(Sign(issuer, "AAAAAAAAAAAAAAAA", clock.GetUtcNow().AddDays(1))).Success);
        });

        Test("Expired and exact-boundary formal licenses are rejected", () =>
        {
            var clock = new FakeClock(); var service = Service(new MemoryStore(), clock);
            Check(!service.Activate(Sign(issuer, Machine, clock.GetUtcNow())).Success);
            Check(!service.Activate(Sign(issuer, Machine, clock.GetUtcNow().AddSeconds(-1))).Success);
        });

        Test("Runtime refresh expires a formal license", () =>
        {
            var clock = new FakeClock(); var service = Service(new MemoryStore(), clock);
            service.Activate(Sign(issuer, Machine, clock.GetUtcNow().AddSeconds(5)));
            clock.Advance(TimeSpan.FromSeconds(5));
            Check(!service.Refresh().IsAuthorized);
        });

        Test("Payload tampering is detected", () =>
        {
            var clock = new FakeClock(); var service = Service(new MemoryStore(), clock);
            var parts = Sign(issuer, Machine, clock.GetUtcNow().AddDays(1)).Split('.');
            parts[1] = LicenseToken.Encode(LicenseToken.CreatePayload(Machine, clock.GetUtcNow().AddYears(10)));
            Check(!service.Activate(string.Join('.', parts)).Success);
        });

        Test("Wrong issuer, wrong product and invalid timestamps are rejected", () =>
        {
            var clock = new FakeClock(); var service = Service(new MemoryStore(), clock);
            using var other = RSA.Create(2048);
            Check(!service.Activate(Sign(other, Machine, clock.GetUtcNow().AddDays(1))).Success);
            Check(!service.Activate(SignPayload(issuer, Encoding.UTF8.GetBytes($"AnotherProduct|{Machine}|2000000000"))).Success);
            Check(!service.Activate(SignPayload(issuer, Encoding.UTF8.GetBytes($"MFAAvalonia|{Machine}|{long.MaxValue}"))).Success);
        });

        Test("Invalid renewal keeps the existing valid activation", () =>
        {
            var store = new MemoryStore(); var clock = new FakeClock(); var service = Service(store, clock);
            var key = Sign(issuer, Machine, clock.GetUtcNow().AddDays(1));
            service.Activate(key);
            Check(!service.Activate("wrong").Success);
            Check(store.Record!.ActivationKey == key && service.Snapshot.IsAuthorized);
            Check(!service.Activate(service.TrialCode).Success);
            Check(store.Record.ActivationKey == key);
        });

        Test("Expired formal license can be renewed", () =>
        {
            var clock = new FakeClock(); var service = Service(new MemoryStore(), clock);
            service.Activate(Sign(issuer, Machine, clock.GetUtcNow().AddHours(1)));
            clock.Advance(TimeSpan.FromHours(2));
            Check(!service.Refresh().IsAuthorized);
            Check(service.Activate(Sign(issuer, Machine, clock.GetUtcNow().AddDays(1))).Success);
        });

        Test("Formal upgrade never resets consumed trial history", () =>
        {
            var store = new MemoryStore(); var clock = new FakeClock(); var service = Service(store, clock);
            service.Activate(service.TrialCode); var start = store.Record!.TrialStartedAtUtc;
            clock.Advance(TimeSpan.FromHours(1));
            Check(service.Activate(Sign(issuer, Machine, clock.GetUtcNow().AddHours(1))).Success);
            Check(store.Record!.TrialStartedAtUtc == start);
            clock.Advance(TimeSpan.FromHours(2));
            Check(!service.Activate(service.TrialCode).Success);
        });

        Test("Failed persistence never unlocks the application", () =>
        {
            var store = new MemoryStore { FailSave = true }; var service = Service(store, new FakeClock());
            Check(!service.Activate(service.TrialCode).Success);
            Check(!service.Snapshot.IsAuthorized && store.Record is null);
        });

        Test("Corrupt storage does not silently start a new trial", () =>
        {
            var store = new MemoryStore { FailLoad = true }; var service = Service(store, new FakeClock());
            Check(service.Refresh().Status == LicenseStatus.StorageError);
            Check(!service.Activate(service.TrialCode).Success && store.Saves == 0);
        });

        Test("A stored trial without its original start cannot be reset", () =>
        {
            var clock = new FakeClock(); var store = new MemoryStore(); var service = Service(store, clock);
            store.Record = new LicenseRecord { MachineCode = Machine, ActivationKey = service.TrialCode, LastSeenUtc = clock.GetUtcNow() };
            Check(!service.Refresh().IsAuthorized);
            Check(!service.Activate(service.TrialCode).Success && store.Record.TrialStartedAtUtc is null);
        });

        Test("Deleting already-observed state is fail-closed", () =>
        {
            var store = new MemoryStore(); var service = Service(store, new FakeClock());
            service.Activate(service.TrialCode); store.Record = null;
            Check(service.Refresh().Status == LicenseStatus.StorageError);
            Check(!service.Activate(service.TrialCode).Success);
        });

        Test("Large clock rollback is detected and correct time recovers service", () =>
        {
            var clock = new FakeClock(); var service = Service(new MemoryStore(), clock);
            service.Activate(service.TrialCode); clock.Advance(TimeSpan.FromMinutes(10)); service.Refresh();
            clock.WallUtc = clock.WallUtc.AddMinutes(-5);
            Check(service.Refresh().Status == LicenseStatus.ClockRollback);
            clock.WallUtc = clock.WallUtc.AddMinutes(5);
            Check(service.Refresh().IsAuthorized);
        });

        Test("Small clock adjustments cannot extend a running trial", () =>
        {
            var clock = new FakeClock(); var service = Service(new MemoryStore(), clock);
            service.Activate(service.TrialCode); clock.Advance(TimeSpan.FromMinutes(1));
            clock.WallUtc = clock.WallUtc.AddSeconds(-30);
            Check(service.Refresh().Remaining == TimeSpan.FromMinutes(119));
        });

        Test("Persistent high-water mark detects rollback after restart", () =>
        {
            var store = new MemoryStore(); var clock = new FakeClock(); var service = Service(store, clock);
            service.Activate(service.TrialCode); clock.Advance(TimeSpan.FromMinutes(10)); service.Refresh();
            var restartedClock = new FakeClock { WallUtc = clock.WallUtc.AddMinutes(-5) };
            Check(Service(store, restartedClock).Refresh().Status == LicenseStatus.ClockRollback);
        });

        Test("File store round-trip and restart retain trial history", () =>
        {
            using var directory = new TestDirectory(); var clock = new FakeClock();
            var service = Service(new FileLicenseStore(directory.Path), clock);
            Check(service.Activate(service.TrialCode).Success);
            clock.Advance(TimeSpan.FromHours(1));
            Check(Service(new FileLicenseStore(directory.Path), clock).Refresh().Remaining == TimeSpan.FromHours(1));
        });

        Test("File store rejects authenticated-state tampering", () =>
        {
            using var directory = new TestDirectory(); var service = Service(new FileLicenseStore(directory.Path), new FakeClock());
            service.Activate(service.TrialCode);
            var path = System.IO.Path.Combine(directory.Path, "license-state.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["Signature"] = Convert.ToBase64String(new byte[32]);
            File.WriteAllText(path, json.ToJsonString());
            Check(service.Refresh().Status == LicenseStatus.StorageError);
            Check(!service.Activate(service.TrialCode).Success);
        });

        Test("Missing state file cannot reset trial while its key remains", () =>
        {
            using var directory = new TestDirectory(); var clock = new FakeClock();
            var service = Service(new FileLicenseStore(directory.Path), clock); service.Activate(service.TrialCode);
            File.Delete(System.IO.Path.Combine(directory.Path, "license-state.json"));
            var restarted = Service(new FileLicenseStore(directory.Path), clock);
            Check(!restarted.Activate(restarted.TrialCode).Success);
        });

        Test("Malformed base64 and oversized state fail closed", () =>
        {
            using var directory = new TestDirectory(); var service = Service(new FileLicenseStore(directory.Path), new FakeClock());
            service.Activate(service.TrialCode);
            var path = System.IO.Path.Combine(directory.Path, "license-state.json");
            File.WriteAllText(path, "{\"Payload\":\"?!\",\"Signature\":\"AA==\"}");
            Check(service.Refresh().Status == LicenseStatus.StorageError);
            File.WriteAllText(path, new string('x', 65537));
            Check(service.Refresh().Status == LicenseStatus.StorageError);
        });

        Test("Concurrent activations share one original trial start", () =>
        {
            using var directory = new TestDirectory(); var aClock = new FakeClock(); var bClock = new FakeClock();
            bClock.Advance(TimeSpan.FromMinutes(1));
            var a = Service(new FileLicenseStore(directory.Path), aClock);
            var b = Service(new FileLicenseStore(directory.Path), bClock);
            var results = Task.WhenAll(Task.Run(() => a.Activate(a.TrialCode)), Task.Run(() => b.Activate(b.TrialCode)))
                .GetAwaiter().GetResult();
            Check(results.All(result => result.Success));
            Check(results[0].Snapshot.ExpiresAtUtc == results[1].Snapshot.ExpiresAtUtc);
        });

        Console.WriteLine($"{passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static string Sign(RSA key, string machine, DateTimeOffset expiry) =>
        SignPayload(key, LicenseToken.CreatePayload(machine, expiry));

    private static string SignPayload(RSA key, byte[] payload) =>
        $"{LicenseToken.Prefix}.{LicenseToken.Encode(payload)}.{LicenseToken.Encode(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))}";

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed.");
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset WallUtc { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => WallUtc;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration) { WallUtc += duration; _timestamp += duration.Ticks; }
    }

    private sealed class MemoryStore : ILicenseStore
    {
        private readonly object _lock = new();
        public LicenseRecord? Record { get; set; }
        public bool FailSave { get; init; }
        public bool FailLoad { get; init; }
        public int Saves { get; private set; }
        public IDisposable AcquireLock() { Monitor.Enter(_lock); return new Lease(() => Monitor.Exit(_lock)); }
        public LicenseRecord? Load() => FailLoad ? throw new JsonException("Corrupt test record.") : Record;
        public void Save(LicenseRecord record)
        {
            if (FailSave) throw new IOException("Simulated persistence failure.");
            Record = record; Saves++;
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("mfa-license-tests-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
