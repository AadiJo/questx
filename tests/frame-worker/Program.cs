using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using QuestX.Utils;
using QuestX.Camera;

namespace QuestX.Utils
{
    public static class QueuedLogger
    {
        public static readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();
        public static void LogError(string message) => Errors.Enqueue(message);
    }
}

static class Program
{
    static int checks;
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); checks++; }

    sealed class Item { public int Id; public Item(int id) { Id = id; } }

    static void Main()
    {
        // Newest wins: while item 1 blocks the worker, 2 and 3 are posted; 2 is discarded.
        var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var processed = new ConcurrentQueue<int>();
        var discarded = new ConcurrentQueue<int>();
        var worker = new LatestOnlyWorker<Item>("check", item =>
        {
            if (item.Id == 1) { started.Set(); gate.Wait(); }
            if (item.Id == 4) throw new InvalidOperationException("boom");
            processed.Enqueue(item.Id);
        }, item => discarded.Enqueue(item.Id));
        worker.Post(new Item(1));
        Check(started.Wait(2000), "worker picks up the first item");
        worker.Post(new Item(2));
        worker.Post(new Item(3));
        Check(discarded.TryDequeue(out int d) && d == 2 && discarded.IsEmpty, "replaced pending item is discarded");
        Check(worker.ReplacedCount == 1, "replacement is counted exactly once");
        gate.Set();
        SpinWait.SpinUntil(() => processed.Count == 2, 2000);
        Check(string.Join(",", processed) == "1,3", "processes the running item, then only the newest");

        // A throwing item is logged and the thread keeps working.
        worker.Post(new Item(4));
        SpinWait.SpinUntil(() => !QueuedLogger.Errors.IsEmpty, 2000);
        Check(QueuedLogger.Errors.TryDequeue(out var error) && error.Contains("boom"), "exception is logged");
        worker.Post(new Item(5));
        SpinWait.SpinUntil(() => processed.Count == 3, 2000);
        Check(processed.ToArray()[2] == 5, "thread survives an exception");

        // Stop returns once idle; later posts are discarded, never processed.
        Check(worker.Stop(TimeSpan.FromSeconds(2)), "idle worker stops");
        worker.Post(new Item(6));
        Check(discarded.TryDequeue(out d) && d == 6, "posts after stop are discarded");
        Check(processed.Count == 3, "nothing runs after stop");
        Check(worker.ReplacedCount == 1, "post-stop rejection is not an overload replacement");
        Check(worker.Stop(TimeSpan.Zero), "stopping twice is harmless");

        // Stop times out while an item is stuck, discards the pending one, then succeeds later.
        var stuck = new ManualResetEventSlim(false);
        var running = new ManualResetEventSlim(false);
        var slow = new LatestOnlyWorker<Item>("slow", item => { running.Set(); stuck.Wait(); }, item => discarded.Enqueue(item.Id));
        slow.Post(new Item(7));
        Check(running.Wait(2000), "slow worker starts");
        slow.Post(new Item(8));
        Check(!slow.Stop(TimeSpan.FromMilliseconds(100)), "stop reports a stuck worker");
        Check(discarded.TryDequeue(out d) && d == 8, "stop discards the pending item");
        Check(slow.ReplacedCount == 0, "shutdown discard is not an overload replacement");
        stuck.Set();
        Check(slow.Stop(TimeSpan.FromSeconds(2)), "a later stop succeeds once the item finishes");

        foreach (var rates in new[] { (30, 24), (60, 24), (60, 30), (120, 24), (15, 24) })
        {
            double nextAt = 0;
            int taken = 0;
            for (int frame = 0; frame < rates.Item1 * 10; frame++)
                if (FrameCadence.TryTake(1000 + frame * 1000.0 / rates.Item1, 1000.0 / rates.Item2, ref nextAt)) taken++;
            int expected = System.Math.Min(rates.Item1, rates.Item2) * 10;
            Check(System.Math.Abs(taken - expected) <= 1,
                $"{rates.Item1} Hz input at {rates.Item2} fps selects {expected} frames in ten seconds, got {taken}");
        }
        double deadline = 0;
        Check(FrameCadence.TryTake(1000, 40, ref deadline), "first frame is immediate");
        Check(!FrameCadence.TryTake(1000, 40, ref deadline), "same instant cannot produce a second frame");
        Check(FrameCadence.TryTake(9000, 40, ref deadline) && deadline == 9040, "long stall reanchors without catch-up burst");
        Check(!FrameCadence.TryTake(9001, 40, ref deadline), "no catch-up work after the stall");
        deadline = 0;
        Check(FrameCadence.TryTake(9001, 100, ref deadline) && deadline == 9101, "mode reset starts a fresh cadence");

        // The preview decision belongs to the consumer, after newest-only replacement.
        // At 24 fps, capture-time gating marks 1000 ms eligible and 1033 ms ineligible.
        // If the former is replaced, the surviving frame must still produce a preview.
        using var previewStarted = new ManualResetEventSlim();
        using var previewRelease = new ManualResetEventSlim();
        using var previewDone = new ManualResetEventSlim();
        double previewDeadline = 0;
        bool previewProduced = false;
        var previewWorker = new LatestOnlyWorker<Item>("preview", item =>
        {
            if (item.Id == 0) { previewStarted.Set(); previewRelease.Wait(); }
            else
            {
                previewProduced = FrameCadence.TryTake(item.Id, 1000.0 / 24, ref previewDeadline);
                previewDone.Set();
            }
        }, _ => { });
        try
        {
            previewWorker.Post(new Item(0));
            Check(previewStarted.Wait(2000), "preview consumer waits while newer frames arrive");
            previewWorker.Post(new Item(1000));
            previewWorker.Post(new Item(1033));
            previewRelease.Set();
            Check(previewDone.Wait(2000), "preview consumer handles surviving frame");
            Check(previewProduced && previewWorker.ReplacedCount == 1,
                "discarded capture does not consume the surviving frame's preview opportunity");
        }
        finally
        {
            previewRelease.Set();
            Check(previewWorker.Stop(TimeSpan.FromSeconds(2)), "preview worker shuts down");
        }
        Console.WriteLine($"worker and cadence checks passed ({checks} assertions)");
    }
}
