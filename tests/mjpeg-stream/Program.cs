using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuestX.WebServer;

static class Program
{
    static int checks;
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); checks++; }

    static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    /// <summary>Fake monotonic time shared by the stream, source and delay.</summary>
    sealed class Clock
    {
        public TimeSpan Now;
        public readonly List<TimeSpan> Delays = new List<TimeSpan>();
        /// <summary>Extra time before the given wait wakes, modelling a late timer.</summary>
        public Func<int, TimeSpan> Oversleep = _ => TimeSpan.Zero;
        public Task Delay(TimeSpan duration, CancellationToken token)
        {
            Now += duration + Oversleep(Delays.Count);
            Delays.Add(duration);
            return Task.CompletedTask;
        }
    }

    /// <summary>Records each write and flush; each write advances the clock to model a slow client.</summary>
    sealed class ControlledStream : MemoryStream
    {
        readonly Clock clock;
        public Func<int, TimeSpan> WriteCost = _ => TimeSpan.Zero;
        public Action<int> BeforeWrite = _ => { };
        public readonly List<byte[]> WrittenArrays = new List<byte[]>();
        public int Writes;
        public int Flushes;
        public ControlledStream(Clock clock) { this.clock = clock; }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            BeforeWrite(Writes);
            WrittenArrays.Add(buffer);
            clock.Now += WriteCost(Writes++);
            if (token.IsCancellationRequested)
                return Task.FromCanceled(token);
            base.Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new InvalidOperationException("synchronous write");

        public override void Flush() => throw new InvalidOperationException("synchronous flush");

        public override Task FlushAsync(CancellationToken token)
        {
            Flushes++;
            return token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;
        }
    }

    /// <summary>A scripted source: each poll returns the next frame and records the poll time.</summary>
    sealed class Source
    {
        readonly Clock clock;
        readonly EncodedFrame[] frames;
        public readonly List<TimeSpan> Polls = new List<TimeSpan>();
        public int Rate = 10;
        public Source(Clock clock, params EncodedFrame[] frames) { this.clock = clock; this.frames = frames; }
        public bool Available => Polls.Count < frames.Length;
        public EncodedFrame Current()
        {
            Polls.Add(clock.Now);
            return frames[Polls.Count - 1];
        }
    }

    static Task Run(ControlledStream output, Source source, Clock clock, CancellationToken token = default) =>
        MjpegStream.StreamAsync(output, source.Current, () => source.Available, () => source.Rate,
            () => clock.Now, clock.Delay, token);

    static EncodedFrame Frame(int number, int length) =>
        new EncodedFrame(number, Enumerable.Range(0, length).Select(i => (byte)(i * 7 + number)).ToArray());

    static byte[] Part(byte[] jpeg) =>
        Encoding.ASCII.GetBytes($"\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n")
            .Concat(jpeg).ToArray();

    static void Main()
    {
        Framing();
        PacingIncludesWriteTime();
        SlowClientSkipsMissedPeriods();
        LateTimerDoesNotBurst();
        RateChange();
        CancellationAndFailures();
        RealClockCancellation().GetAwaiter().GetResult();
        Console.WriteLine($"MJPEG stream checks passed ({checks} checks)");
    }

    static void Framing()
    {
        // A long frame then a short one proves the reused header has no stale digits.
        var clock = new Clock();
        EncodedFrame big = Frame(1, 12345), small = Frame(3, 7), empty = Frame(4, 0);
        var source = new Source(clock, default, new EncodedFrame(1, null), big, big, Frame(2, 50), small, small, empty);
        var output = new ControlledStream(clock);
        Run(output, source, clock).GetAwaiter().GetResult();

        // Frame 0 and a frame without data are skipped; frame 1 still goes out once its bytes appear.
        byte[] expected = Part(big.frameData).Concat(Part(Frame(2, 50).frameData)).Concat(Part(small.frameData))
            .Concat(Part(empty.frameData)).ToArray();
        Check(output.ToArray().SequenceEqual(expected), "multipart bytes, boundary and Content-Length match exactly");
        Check(output.Writes == 8 && output.Flushes == 4, "each frame is one header write, one JPEG write and one flush");
        Check(ReferenceEquals(output.WrittenArrays[1], big.frameData), "the JPEG array is written without an intermediate copy");
        Check(source.Polls.Count == 8, "the source is polled until it becomes unavailable");

        // An older frame number after a newer one is never sent (latest-only, no rewind).
        clock = new Clock();
        source = new Source(clock, Frame(5, 3), Frame(4, 3), Frame(5, 3));
        output = new ControlledStream(clock);
        Run(output, source, clock).GetAwaiter().GetResult();
        Check(output.Flushes == 1, "stale or repeated frames are not re-sent");

        // An unavailable source writes nothing.
        clock = new Clock();
        source = new Source(clock);
        output = new ControlledStream(clock);
        Run(output, source, clock).GetAwaiter().GetResult();
        Check(output.Length == 0 && source.Polls.Count == 0, "an unavailable source writes nothing");
    }

    static void PacingIncludesWriteTime()
    {
        // 10 fps; each frame costs 10 ms header + 20 ms JPEG. Polls stay on the 100 ms grid.
        var clock = new Clock();
        var source = new Source(clock, Frame(1, 4), Frame(1, 4), Frame(2, 4), Frame(3, 4));
        var output = new ControlledStream(clock) { WriteCost = i => i % 2 == 0 ? 10 * Ms : 20 * Ms };
        Run(output, source, clock).GetAwaiter().GetResult();
        Check(source.Polls.SequenceEqual(new[] { 0, 100, 200, 300 }.Select(ms => ms * Ms)),
            "poll starts are one frame period apart despite write time");
        Check(clock.Delays.SequenceEqual(new[] { 70, 100, 70, 70 }.Select(ms => ms * Ms)),
            "the wait is the period minus write time, and a full period with no new frame");
    }

    static void SlowClientSkipsMissedPeriods()
    {
        // At 10 fps the second frame takes 350 ms to write (3.5 periods). The next poll starts
        // immediately, then pacing resumes from there: no back-to-back catch-up polls.
        var clock = new Clock();
        var source = new Source(clock, Enumerable.Range(1, 6).Select(n => Frame(n, 4)).ToArray());
        var output = new ControlledStream(clock) { WriteCost = i => i == 3 ? 350 * Ms : TimeSpan.Zero };
        Run(output, source, clock).GetAwaiter().GetResult();
        Check(source.Polls.SequenceEqual(new[] { 0, 100, 450, 550, 650, 750 }.Select(ms => ms * Ms)),
            "missed periods are skipped and later polls keep the configured spacing");
        Check(output.Flushes == 6, "every newer frame still reaches the slow client");
        Check(clock.Delays.Count(d => d <= TimeSpan.Zero) == 0, "no zero-length waits are requested");

        // A client that is always slower than the period is served back-to-back at its own rate,
        // never faster.
        clock = new Clock();
        source = new Source(clock, Enumerable.Range(1, 5).Select(n => Frame(n, 4)).ToArray());
        output = new ControlledStream(clock) { WriteCost = _ => 80 * Ms };
        Run(output, source, clock).GetAwaiter().GetResult();
        var gaps = source.Polls.Zip(source.Polls.Skip(1), (a, b) => b - a).ToArray();
        Check(gaps.All(g => g == 160 * Ms), "a persistently slow client is paced by its write time");
    }

    static void LateTimerDoesNotBurst()
    {
        // At 25 fps (40 ms) with 1 ms writes, the first 39 ms wait wakes at 200 ms, 160 ms late. The late
        // poll re-anchors pacing, so the following poll is a full period later, not immediate.
        var clock = new Clock { Oversleep = i => i == 0 ? 160 * Ms : TimeSpan.Zero };
        var source = new Source(clock, Enumerable.Range(1, 4).Select(n => Frame(n, 4)).ToArray()) { Rate = 25 };
        var output = new ControlledStream(clock) { WriteCost = i => i % 2 == 0 ? 1 * Ms : TimeSpan.Zero };
        Run(output, source, clock).GetAwaiter().GetResult();
        Check(source.Polls.SequenceEqual(new[] { 0, 200, 240, 280 }.Select(ms => ms * Ms)),
            "a late timer wakeup is not followed by a back-to-back frame");
        Check(output.Flushes == 4 && clock.Delays.All(d => d == 39 * Ms),
            "every wait after the late wakeup is the period minus write time");
    }

    static void RateChange()
    {
        // The configured rate is read every poll, so a mode change takes effect on the next wait.
        var clock = new Clock();
        var source = new Source(clock, Frame(1, 4), Frame(2, 4), Frame(3, 4), Frame(4, 4));
        var output = new ControlledStream(clock);
        output.BeforeWrite = i => { if (i == 2) source.Rate = 20; };
        Run(output, source, clock).GetAwaiter().GetResult();
        Check(source.Polls.SequenceEqual(new[] { 0, 100, 150, 200 }.Select(ms => ms * Ms)),
            "a rate change applies to the next period");

        // A non-positive rate is treated as 1 fps rather than dividing by zero.
        clock = new Clock();
        source = new Source(clock, Frame(1, 4), Frame(2, 4)) { Rate = 0 };
        Run(new ControlledStream(clock), source, clock).GetAwaiter().GetResult();
        Check(clock.Delays[0] == TimeSpan.FromSeconds(1), "a zero rate falls back to one frame per second");
    }

    static void CancellationAndFailures()
    {
        // Cancelled while writing: the write observes the token and the stream ends without throwing.
        var clock = new Clock();
        var cts = new CancellationTokenSource();
        var source = new Source(clock, Frame(1, 4), Frame(2, 4), Frame(3, 4));
        var output = new ControlledStream(clock) { BeforeWrite = i => { if (i == 2) cts.Cancel(); } };
        Task run = Run(output, source, clock, cts.Token);
        Check(run.IsCompletedSuccessfully, "cancellation during a write ends the stream cleanly");
        Check(output.Flushes == 1 && source.Polls.Count == 2, "nothing is written or polled after cancellation");

        // Cancelled while waiting.
        clock = new Clock();
        cts = new CancellationTokenSource();
        source = new Source(clock, Frame(1, 4), Frame(2, 4));
        Task run2 = MjpegStream.StreamAsync(new ControlledStream(clock), source.Current, () => source.Available,
            () => 10, () => clock.Now, (d, token) => { cts.Cancel(); return Task.Delay(d, token); }, cts.Token);
        Check(run2.IsCompletedSuccessfully && source.Polls.Count == 1, "cancellation during a wait ends the stream cleanly");

        // A disconnected client (IOException) ends the stream; unexpected exceptions still surface.
        clock = new Clock();
        source = new Source(clock, Frame(1, 4), Frame(2, 4));
        output = new ControlledStream(clock) { BeforeWrite = _ => throw new IOException("reset") };
        Check(Run(output, source, clock).IsCompletedSuccessfully && source.Polls.Count == 1,
            "a write failure ends the stream without throwing");

        clock = new Clock();
        source = new Source(clock, Frame(1, 4));
        output = new ControlledStream(clock) { BeforeWrite = _ => throw new InvalidOperationException("bug") };
        Check(Run(output, source, clock).IsFaulted, "unexpected write exceptions are not swallowed");

        // A cancellation exception from something other than the request token is not hidden.
        clock = new Clock();
        source = new Source(clock, Frame(1, 4));
        output = new ControlledStream(clock) { BeforeWrite = _ => throw new OperationCanceledException() };
        Check(Run(output, source, clock).IsCanceled, "foreign cancellation is not treated as a disconnect");
    }

    static async Task RealClockCancellation()
    {
        // The production overload waits on the request token, so a 1 fps stream stops promptly.
        var cts = new CancellationTokenSource();
        var output = new MemoryStream();
        int polls = 0;
        var watch = Stopwatch.StartNew();
        Task run = MjpegStream.StreamAsync(output, () => { polls++; return Frame(polls, 4); }, () => true,
            () => 1, cts.Token);
        cts.CancelAfter(50);
        Task finished = await Task.WhenAny(run, Task.Delay(5000));
        Check(finished == run && run.IsCompletedSuccessfully, "the real-time stream ends when the request is cancelled");
        Check(watch.ElapsedMilliseconds < 900 && polls == 1, "cancellation interrupts the frame wait");
        Check(output.ToArray().SequenceEqual(Part(Frame(1, 4).frameData)), "the real-time stream wrote one frame");
    }
}
