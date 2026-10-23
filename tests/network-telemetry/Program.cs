using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestX.Config;
using QuestX.Core;
using QuestX.Native.NTCore;
using QuestX.Network;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;
using QuestX.Utils;
using QuestX.VioQuality;

internal static class Program
{
    const string Tag = QuestXConstants.Topics.APRIL_TAG_TELEMETRY;
    const string Lime = "/QuestX/limelight/";
    const string V4 = QuestXConstants.Topics.APRIL_TAG_DATA_V4;
    static int assertions;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Interlocked.Increment(ref assertions);
    }
    static (NetworkTableConnection Connection, NtInstance Nt) Create()
    {
        var c = new NetworkTableConnection(new ConfigStub());
        var nt = NtInstance.LastCreated;
        nt.Writes.Clear();
        QueuedLogger.Warnings.Clear();
        return (c, nt);
    }
    static AprilTagMeasurement Sample(int seed, int count = 2, long capture = 900_000)
    {
        var pose = new Pose3d(seed + .1, seed + .2, seed + .3, FixtureRotation(seed));
        var ids = Enumerable.Range(seed * 10, count).ToArray();
        var observations = ids.Select((id, i) => new AprilTagObservation(id, .01 * (seed + i), seed + .4, seed + .5, seed + .6, seed + .7, seed + .8)).ToArray();
        return new AprilTagMeasurement(capture, seed + .125, pose,
            Covariance(pose),
            ids, count * 3, count * 4, .75, seed + .9, .02 * seed, .3 * seed, observations);
    }
    // Exact unit quaternions keep the checked-in bitwise traces independent of platform
    // libm rounding for arbitrary-angle sin/cos, while exercising distinct Euler angles.
    static Rotation3d FixtureRotation(int seed) => new((seed % 5) switch
    {
        0 => new Quaternion(1, 0, 0, 0),
        1 => new Quaternion(0, 1, 0, 0),
        2 => new Quaternion(0, 0, 1, 0),
        3 => new Quaternion(0, 0, 0, 1),
        _ => new Quaternion(.5, .5, .5, .5),
    });
    static PoseCovariance Covariance(Pose3d pose)
    {
        var matrix = pose.Rotation.ToMatrix();
        var rotation = new double[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++) rotation[r * 3 + c] = matrix[r, c];
        var covariance = new double[36];
        for (int i = 0; i < 6; i++) covariance[i * 7] = .01 * (i + 1);
        // Fixture construction only: the domain project's linear covariance factory is internal.
        return (PoseCovariance)typeof(PoseCovariance).GetMethod("Linear", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { rotation, new[] { pose.X, pose.Y, pose.Z }, covariance })!;
    }
    static AprilTagDiagnosticsSnapshot Diagnostics() => new(true, 10, 1, 9, 8, .9, new[] { 10, 11 }, 6, 8, .75, 2, .1, "solved", "ok")
    {
        FieldLayoutFile = "field.json", LayoutLoaded = true, LayoutTagCount = 22, DetectedIds = new[] { 10, 11, 99 }
    };
    static void Vio(NetworkTableConnection c, long capture = 950_000) => c.PublishVioData(capture, .95, new Pose3d(7, 8, 9, FixtureRotation(4)), true, 3);
    static string Serialize(IEnumerable<Write> writes) => JsonSerializer.Serialize(writes, Json);
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    static Dictionary<string, Write[]> SequentialTraces()
    {
        var traces = new Dictionary<string, Write[]>();
        void Run(string name, Action<NetworkTableConnection, NtInstance> action)
        {
            var (c, nt) = Create();
            action(c, nt);
            traces[name] = nt.Writes.ToArray();
        }
        Run("no-sample-and-10hz-gate", (c, nt) =>
        {
            c.Periodic();
            Vio(c);
            nt.Clock += 99_999; Vio(c);
            nt.Clock++; Vio(c);
            Check(c.VioPublishCount == 3 && c.AprilTagPublishCount == 0, "no-sample counts");
        });
        Run("all-protocols-fresh-stale-and-diagnostics", (c, nt) =>
        {
            c.PublishAprilTagData(Sample(1)); Vio(c);
            c.PublishAprilTagDiagnostics(Diagnostics());
            nt.Clock = 1_150_000; c.Periodic(); // Exactly 250ms old: still fresh.
            nt.Clock = 1_250_000; c.Periodic(); // Stale; heartbeat continues.
            c.PublishAprilTagDiagnostics(Diagnostics());
            c.PublishVioQuality(1_240_000, 1.24, default(VioQualitySample));
        });
        Run("v4-failure-success-count-and-sequence", (c, nt) =>
        {
            c.PublishAprilTagData(Sample(1)); Vio(c);
            nt.Accept = p => p != V4;
            for (int i = 0; i < 5; i++)
            {
                nt.Clock += 100_000;
                c.PublishAprilTagData(Sample(i + 2, i % 3, nt.Clock - 50_000)); Vio(c);
            }
            Check(c.AprilTagPublishCount == 1 && c.AprilTagV4PublishFailureCount == 5, "failed v4 counts");
            Check(QueuedLogger.Warnings.Count(w => w.StartsWith("AprilTag v4")) == 3, "failure warning powers of two");
            nt.Accept = _ => true;
            nt.Clock += 100_000; c.PublishAprilTagData(Sample(8, 3, nt.Clock - 50_000)); Vio(c);
            Check(c.AprilTagPublishCount == 2 && c.AprilTagV4PublishFailureCount == 5, "recovery counts");
        });
        Run("reused-resized-arrays-empty-and-future-capture", (c, nt) =>
        {
            foreach (int count in new[] { 2, 2, 5, 0, 1 })
            {
                c.PublishAprilTagData(Sample(count + 1, count, nt.Clock + 10_000));
                c.Periodic(); nt.Clock += 100_000;
            }
        });
        Run("wide-capture-sequence-and-vio-failure", (c, nt) =>
        {
            typeof(NetworkTableConnection).GetField("nextAprilTagSequenceId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(c, (1UL << 40) + 3);
            nt.Clock = (1L << 42) + 123_456;
            c.PublishAprilTagData(Sample(3, 2, nt.Clock - 20_000));
            nt.Accept = p => p != QuestXConstants.Topics.VIO_DATA_V2;
            Vio(c, nt.Clock - 1_000);
            Check(c.VioPublishCount == 0, "failed VIO count");
        });
        return traces;
    }

    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--allocation")
        {
            var (c, nt) = Create();
            nt.RecordWrites = false;
            var sample = Sample(1);
            for (int i = 0; i < 1_000; i++) c.PublishAprilTagData(sample);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000; i++) c.PublishAprilTagData(sample);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"Managed bytes per two-tag publication (NT recorder disabled): {allocated / 1_000.0}");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--expect-worker-blocking")
        {
            var (c, nt) = Create();
            using var gate = new Gate(nt, V4);
            using var worker = new Job(() => c.PublishAprilTagData(Sample(1)));
            gate.Wait();
            using var main = new Job(() => Vio(c));
            try
            {
                Check(SpinWait.SpinUntil(() => !main.Thread.IsAlive || (main.Thread.ThreadState & ThreadState.WaitSleepJoin) != 0, 5_000), "main did not reach blocking point");
                Check(main.Thread.IsAlive && nt.Writes.All(w => w.Path != "<flush>"), "pre-edit VIO should be blocked before Flush");
            }
            finally { gate.Release(); }
            main.Finish(); worker.Finish();
            Console.WriteLine("PASS: pre-edit actual connection blocks VIO Flush on worker native publication.");
            return 0;
        }
        var traces = SequentialTraces();
        var hashes = traces.ToDictionary(pair => pair.Key, pair => Hash(Serialize(pair.Value)));
        if (args.Length == 2 && args[0] == "--baseline")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(hashes, Json) + "\n");
            File.WriteAllText(args[1] + ".traces.json", JsonSerializer.Serialize(traces, Json) + "\n");
            Console.WriteLine($"Recorded {traces.Count} baseline scenarios, {traces.Sum(p => p.Value.Length)} complete writes/flushes.");
            return 0;
        }
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Baseline.json")))!;
        Check(expected.Count == hashes.Count, "baseline scenario coverage");
        foreach (var pair in hashes) Check(expected[pair.Key] == pair.Value, "complete write trace changed: " + pair.Key);
        if (args.Length == 2 && args[0] == "--trace")
            File.WriteAllText(args[1], JsonSerializer.Serialize(traces, Json) + "\n");
        ConcurrencyChecks();
        Console.WriteLine($"PASS: {assertions} assertions; {traces.Count} before/after traces ({traces.Sum(p => p.Value.Length)} writes/flushes); gated worker/main/diagnostics and concurrent publishers.");
        return 0;
    }

    static Write[] SnapshotWrites(NtInstance nt) => nt.Writes.Where(w =>
        w.Path.StartsWith(Tag) || new[] { "tv", "tid", "ageMilliseconds", "hb" }.Any(p => w.Path == Lime + p)).ToArray();

    static void EqualWrites(IEnumerable<Write> expected, IEnumerable<Write> actual, string context) =>
        Check(Serialize(expected) == Serialize(actual), context);

    // Dedicated threads make the scheduling gates independent of thread-pool starvation.
    sealed class Job : IDisposable
    {
        public readonly Thread Thread;
        Exception error;
        public Job(Action action)
        {
            Thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } }) { IsBackground = true };
            Thread.Start();
        }
        public void Finish()
        {
            Check(Thread.Join(5_000), "publisher did not finish within the bounded gate timeout");
            if (error != null) throw new Exception("publisher failed", error);
        }
        public void Dispose() => Finish();
    }

    sealed class Gate : IDisposable
    {
        public readonly ManualResetEventSlim Entered = new();
        readonly ManualResetEventSlim release = new();
        int claimed;
        public Gate(NtInstance nt, string path)
        {
            nt.BeforeSet = p =>
            {
                if (p != path || Interlocked.CompareExchange(ref claimed, 1, 0) != 0) return;
                Entered.Set();
                if (!release.Wait(10_000)) throw new TimeoutException("native publisher gate was never released: " + path);
            };
        }
        public void Wait() => Check(Entered.Wait(5_000), "publisher never reached gate");
        public void Release() => release.Set();
        public void Dispose() { Release(); }
    }

    static void ConcurrencyChecks()
    {
        foreach (string path in new[]
        {
            QuestXConstants.Topics.APRIL_TAG_DATA,
            QuestXConstants.Topics.APRIL_TAG_DATA_V2,
            QuestXConstants.Topics.APRIL_TAG_DATA_V3,
            V4, Lime + "botpose_wpiblue", Lime + "stddevs"
        })
            WorkerGate(path, true);
        WorkerGate(V4, false);
        MainTelemetryGate();
        DiagnosticsGate();
        ConcurrentWorkers();
    }

    static void WorkerGate(string path, bool priorSample)
    {
        var (reference, expectedNt) = Create();
        if (priorSample) reference.PublishAprilTagData(Sample(1));
        Vio(reference);
        var oldSnapshot = SnapshotWrites(expectedNt);
        expectedNt.Writes.Clear();
        reference.PublishAprilTagDiagnostics(Diagnostics());
        var diagnostics = expectedNt.Writes.ToArray();
        expectedNt.Accept = p => p != V4;
        reference.PublishAprilTagData(Sample(2, 2, 950_000));
        expectedNt.Writes.Clear(); expectedNt.Clock += 100_000; Vio(reference);
        var newSnapshot = SnapshotWrites(expectedNt);

        var (c, nt) = Create();
        if (priorSample) c.PublishAprilTagData(Sample(1));
        nt.Writes.Clear(); nt.Accept = p => p != V4;
        using var gate = new Gate(nt, path);
        using var worker = new Job(() => c.PublishAprilTagData(Sample(2, 2, 950_000)));
        gate.Wait();
        try
        {
            using (var main = new Job(() => Vio(c))) main.Finish();
            Check(nt.Writes.Count(w => w.Path == "<flush>") == 1, "VIO must flush before worker gate release: " + path);
            EqualWrites(oldSnapshot, SnapshotWrites(nt), "held worker must expose the entire previous snapshot: " + path);
            int start = nt.Writes.Count;
            using (var main = new Job(() => c.PublishAprilTagDiagnostics(Diagnostics()))) main.Finish();
            EqualWrites(diagnostics, nt.Writes.Skip(start), "diagnostics must finish while worker native Set is held");
        }
        finally { gate.Release(); }
        worker.Finish();
        Check(c.AprilTagV4PublishFailureCount == 1, "held failed-v4 count");
        nt.Writes.Clear(); nt.Clock += 100_000; Vio(c);
        EqualWrites(newSnapshot, SnapshotWrites(nt), "failed-v4 sample must exchange coherent pose/sequence/tags/count/freshness: " + path);
    }

    static void MainTelemetryGate()
    {
        var (reference, expectedNt) = Create();
        reference.PublishAprilTagData(Sample(1)); Vio(reference);
        reference.PublishAprilTagData(Sample(2)); // Overwrite same-sized reusable v3 buffer.
        expectedNt.Accept = p => p != V4;
        reference.PublishAprilTagData(Sample(3, 4, 975_000)); // Resize and fail v4.
        expectedNt.Clock += 100_000; Vio(reference);
        var expected = SnapshotWrites(expectedNt);

        var (c, nt) = Create();
        c.PublishAprilTagData(Sample(1)); nt.Writes.Clear();
        using var gate = new Gate(nt, Tag + "hasMeasurement");
        using var main = new Job(() => Vio(c));
        gate.Wait();
        try
        {
            using var worker = new Job(() =>
            {
                c.PublishAprilTagData(Sample(2));
                nt.Accept = p => p != V4;
                c.PublishAprilTagData(Sample(3, 4, 975_000));
            });
            worker.Finish();
            Check(c.AprilTagPublishCount == 2 && c.AprilTagV4PublishFailureCount == 1, "workers complete while main native Set is held");
        }
        finally { gate.Release(); }
        main.Finish();
        nt.Clock += 100_000; Vio(c);
        EqualWrites(expected, SnapshotWrites(nt), "main tick must keep its owned snapshot through worker buffer overwrites/resizes and failed v4");
    }

    static void DiagnosticsGate()
    {
        var (c, nt) = Create();
        using var gate = new Gate(nt, Lime + "fieldLayout");
        using var diagnostics = new Job(() => c.PublishAprilTagDiagnostics(Diagnostics()));
        gate.Wait();
        try
        {
            using (var worker = new Job(() => c.PublishAprilTagData(Sample(1)))) worker.Finish();
            using (var main = new Job(() => Vio(c))) main.Finish();
            Check(nt.Writes.Count(w => w.Path == "<flush>") == 2, "held diagnostics must not delay worker or VIO flush");
        }
        finally { gate.Release(); }
        diagnostics.Finish();
    }

    static void ConcurrentWorkers()
    {
        var (reference, expectedNt) = Create();
        reference.PublishAprilTagData(Sample(1)); reference.PublishAprilTagData(Sample(2));
        var expected = expectedNt.Writes.Where(w => w.Path != "<flush>").ToArray();

        var (c, nt) = Create();
        using var gate = new Gate(nt, V4);
        using var first = new Job(() => c.PublishAprilTagData(Sample(1)));
        gate.Wait();
        using var secondEntered = new ManualResetEventSlim();
        using var second = new Job(() => { secondEntered.Set(); c.PublishAprilTagData(Sample(2)); });
        try
        {
            Check(secondEntered.Wait(5_000), "second worker did not start");
            Check(SpinWait.SpinUntil(() => !second.Thread.IsAlive || (second.Thread.ThreadState & ThreadState.WaitSleepJoin) != 0, 5_000), "second worker did not reach serialization lock");
            Check(second.Thread.IsAlive, "second worker must not bypass publication serialization");
            Check(nt.Writes.Count == 3, "second worker must not interleave with held first publication");
        }
        finally { gate.Release(); }
        first.Finish(); second.Finish();
        EqualWrites(expected, nt.Writes.Where(w => w.Path != "<flush>"), "concurrent worker calls must preserve full protocol batch order and sequence");
        Check(nt.Writes.Count(w => w.Path == "<flush>") == 2 && c.AprilTagPublishCount == 2, "each worker call flushes and counts");
    }
}
