using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using QuestX.Camera;
using QuestX.QuestX.AprilTag;

// AprilTagPreviewEncoder: scaling, rotation, tag alignment, and the latest-only worker thread.
static unsafe class EncoderChecks
{
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
        assertions++;
    }

    private sealed class FakeEncoder : IGrayJpegEncoder
    {
        public static int Created;
        public static ManualResetEventSlim Entered = new ManualResetEventSlim();
        public static ManualResetEventSlim Gate = new ManualResetEventSlim(true);
        public static bool ThrowNext;

        public FakeEncoder() => Interlocked.Increment(ref Created);

        public byte[] Encode(IntPtr pixels, int stride, int width, int height, int quality)
        {
            Entered.Set();
            Gate.Wait();
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new InvalidOperationException("encoder failed");
            }
            // "JPEG": the first pixel, the dimensions and the quality, enough to check the input.
            return new[] { ((byte*)pixels)[0], (byte)(width / 10), (byte)(height / 10), (byte)quality };
        }

        public void Dispose() { }
    }

    // Independent output-order reference checks orientation, box boundaries and rounding.
    private static void CheckResampling()
    {
        var random = new Random(719);
        foreach (var (width, height) in new[] { (32, 48), (80, 60), (1280, 960) })
        foreach (int factor in new[] { 1, 2, 4, 8, 16 })
        {
            if (width % factor != 0 || height % factor != 0) continue;
            var input = new byte[width * height];
            foreach (int pattern in new[] { 0, 1, 2, 3 })
            {
                for (int i = 0; i < input.Length; i++)
                    input[i] = pattern == 0 ? (byte)0 : pattern == 1 ? (byte)255
                        : pattern == 2 ? (byte)(i % 2) : (byte)random.Next(256);
                foreach (CameraOrientation orientation in Enum.GetValues(typeof(CameraOrientation)))
                {
                    int sw = width / factor, sh = height / factor;
                    CameraOrientationMath.RotateSize(orientation, sw, sh, out int ow, out int oh);
                    var actual = new byte[ow * oh];
                    AprilTagPreviewEncoder.Resample(input, width, height, factor, orientation, actual);
                    bool equal = true;
                    for (int y = 0; y < oh; y++)
                    for (int x = 0; x < ow; x++)
                    {
                        CameraOrientationMath.SourcePixel(orientation, x, y, sw, sh, out int sx, out int sy);
                        int sum = 0;
                        for (int by = 0; by < factor; by++)
                        for (int bx = 0; bx < factor; bx++)
                            sum += input[(sy * factor + by) * width + sx * factor + bx];
                        equal &= actual[y * ow + x] == (sum + factor * factor / 2) / (factor * factor);
                    }
                    Check(equal, $"Exact resample {width}x{height}/{factor} {orientation} pattern {pattern}");
                }
            }
        }
    }

    public static int Run()
    {
        // Scale choices at the Quest's sizes, with and without the 640x480 standard cap.
        const int Cap = AprilTagPreviewEncoder.StandardMaxPixels;
        int F(int w, int h, int requested, int max = 0) => AprilTagPreviewEncoder.ScaleFactor(w, h, requested, max);
        Check(F(1280, 1280, 4) == 4, "Default quarter: 1280x1280 previews at 320x320");
        Check(F(1280, 960, 4) == 4, "Quarter of 1280x960 is 320x240, Limelight's classic size");
        Check(F(1280, 960, 1) == 1 && F(1280, 960, 2) == 2 && F(1280, 960, 8) == 8, "Full, half, eighth");
        Check(F(1280, 1080, 8) == 8, "1280x1080 eighth is 160x135");
        Check(F(800, 600, 8) == 8, "800x600 eighth is 100x75");
        Check(F(1000, 750, 8) == 2, "Falls back to the largest halving that divides both sides");
        Check(F(720, 480, 8) == 8, "720x480 eighth is 90x60");
        Check(F(1280, 960, 2, Cap) == 2, "640x480 fits the standard cap");
        Check(F(1280, 1280, 2, Cap) == 4, "640x640 exceeds the cap, so it drops to a quarter");
        Check(F(1280, 1280, 1, Cap) == 4, "Full 1280x1280 needs high-quality streams");
        Check(F(1280, 1280, 1, 0) == 1, "High-quality streams allow full resolution");
        Check(F(640, 480, 1, Cap) == 1, "640x480 full is within the cap");
        for (int w = 2; w <= 2000; w += 38)
        {
            for (int h = 2; h <= 2000; h += 42)
            {
                foreach (int requested in new[] { 1, 2, 4, 8 })
                {
                    int f = F(w, h, requested, Cap);
                    Check(f >= 1 && w % f == 0 && h % f == 0, $"Factor {f} divides {w}x{h}");
                }
            }
        }

        CheckResampling();

        // 2x2 box averages, rounded.
        var src = new byte[] { 0, 2, 10, 20, 4, 6, 30, 41, 100, 100, 7, 7, 100, 101, 7, 8 };
        var dst = new byte[4];
        AprilTagPreviewEncoder.Resample(src, 4, 4, 2, CameraOrientation.NORMAL, dst);
        Check(dst[0] == 3 && dst[1] == 25 && dst[2] == 100 && dst[3] == 7, "Block averages");

        // The outline drawn from the scaled corners lands on the scaled, rotated image's pixels.
        foreach (CameraOrientation orientation in Enum.GetValues(typeof(CameraOrientation)))
        {
            foreach (var (width, height) in new[] { (1280, 1280), (1280, 960) })
            {
                var image = new byte[width * height];
                int bx = 210, by = 377; // a bright 2x2 block at source pixels (420..421, 754..755)
                for (int y = 0; y < 2; y++)
                    for (int x = 0; x < 2; x++)
                        image[(by * 2 + y) * width + bx * 2 + x] = 255;
                CameraOrientationMath.RotateSize(orientation, width / 2, height / 2, out int ow, out int oh);
                var output = new byte[ow * oh];
                AprilTagPreviewEncoder.Resample(image, width, height, 2, orientation, output);
                int brightest = Array.IndexOf(output, (byte)255);
                Check(brightest >= 0, "Block survives scaling");
                // The detector's pixel-centre coordinates, rotated like ToOverlayPoint does.
                CameraOrientationMath.RotatePoint(
                    orientation, bx * 2 + 0.5, by * 2 + 0.5, width, height, out double rx, out double ry);
                var tag = new AprilTagOverlayTag(1, new AprilTagOverlayPoint(rx, ry));
                var scaled = AprilTagPreviewEncoder.ScaleTags(new[] { tag }, 2)[0].Corners[0];
                double dx = scaled.X - brightest % ow, dy = scaled.Y - brightest / ow;
                Check(Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1,
                    $"{orientation} {width}x{height}: outline within 1 px of the image (off {dx:F2}, {dy:F2})");
            }
        }
        var unscaled = new[] { new AprilTagOverlayTag(4, new AprilTagOverlayPoint(3, 5)) };
        Check(ReferenceEquals(AprilTagPreviewEncoder.ScaleTags(unscaled, 1), unscaled), "Factor 1 keeps tags");

        // The worker: only the newest submission is encoded, and the image is copied on Submit.
        var published = new List<(int W, int H, double At, AprilTagOverlayTag[] Tags, byte[] Jpeg)>();
        var publishedSignal = new SemaphoreSlim(0);
        var errors = new List<Exception>();
        int encodeTimes = 0;
        using var encoder = new AprilTagPreviewEncoder(
            () => new FakeEncoder(),
            (w, h, at, tags, jpeg) =>
            {
                lock (published) published.Add((w, h, at, tags, jpeg));
                publishedSignal.Release();
            },
            ex => { lock (errors) errors.Add(ex); },
            ms => { if (ms >= 0) Interlocked.Increment(ref encodeTimes); });

        var frame = new byte[1280 * 1280];
        var handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            IntPtr ptr = handle.AddrOfPinnedObject();
            var tags = new[] { new AprilTagOverlayTag(7, new AprilTagOverlayPoint(101.5, 200.5)) };
            void Submit(byte value, double at)
            {
                for (int i = 0; i < 4; i++) frame[i % 2 + (i / 2) * 1280] = value; // first 2x2 block
                encoder.Submit(ptr, 1280, 1280, 1280, at, tags, CameraOrientation.NORMAL, 60, 2);
            }

            FakeEncoder.Gate.Reset();
            FakeEncoder.Entered.Reset();
            Submit(10, 1);
            Check(FakeEncoder.Entered.Wait(2000), "Worker picks up the first frame");
            Submit(20, 2);
            Submit(30, 3); // replaces frame 2 before the worker is free
            Array.Clear(frame); // the encoder must already hold its own copy
            FakeEncoder.Gate.Set();
            Check(publishedSignal.Wait(2000) && publishedSignal.Wait(2000), "Two frames published");
            Check(!publishedSignal.Wait(200), "The replaced frame is never published");
            Check(encoder.DroppedCount == 1, "One frame dropped");
            Check(published[0].At == 1 && published[1].At == 3, "Newest frame wins");
            Check(published[0].Jpeg[0] == 10 && published[1].Jpeg[0] == 30, "Pixels copied at Submit");
            Check(published[1].W == 640 && published[1].H == 640, "Published at the scaled size");
            Check(published[1].Jpeg[1] == 64 && published[1].Jpeg[2] == 64 && published[1].Jpeg[3] == 60,
                "Encoder gets the scaled size and quality");
            var corner = published[1].Tags[0].Corners[0];
            Check(Math.Abs(corner.X - 50.5) < 1e-9 && Math.Abs(corner.Y - 100) < 1e-9, "Tags scaled");
            Check(tags[0].Corners[0].X == 101.5, "Submitted tags are not modified");

            // A failure reports once, skips frames for a second, then recovers with a new encoder.
            int createdBefore = FakeEncoder.Created;
            FakeEncoder.ThrowNext = true;
            Submit(40, 4);
            Thread.Sleep(300);
            Check(errors.Count == 1 && !publishedSignal.Wait(0), "Failure reported, nothing published");
            Submit(50, 5);
            Check(!publishedSignal.Wait(300), "Frames are skipped while backing off");
            Thread.Sleep(900);
            Submit(60, 6);
            Check(publishedSignal.Wait(2000), "Encodes again after backing off");
            Check(published[^1].At == 6 && FakeEncoder.Created == createdBefore + 1, "Fresh encoder after a failure");

            // Reset invalidates both queued and active jobs, plus captures awaiting Submit.
            FakeEncoder.Gate.Reset();
            FakeEncoder.Entered.Reset();
            Submit(70, 7);
            Check(FakeEncoder.Entered.Wait(2000), "Worker busy");
            Submit(80, 8);
            long oldGeneration = encoder.Generation;
            encoder.Reset();
            Check(encoder.Generation != oldGeneration, "Reset advances submission generation");
            long drops = encoder.DroppedCount;
            // A captured frame can reach Submit after Reset. It must not occupy the pending slot.
            encoder.Submit(ptr, 1280, 1280, 1280, 8.5, tags, CameraOrientation.NORMAL, 60, 2, oldGeneration);
            Submit(90, 9);
            Check(encoder.DroppedCount == drops, "Reset and stale submissions do not count as replacements");
            FakeEncoder.Gate.Set();
            Check(publishedSignal.Wait(2000) && published[^1].At == 9,
                "Only the new generation publishes after reset, not active or queued old frames");
            Check(!publishedSignal.Wait(100), "No extra publication after reset");

            // A failure from the old generation must not clear or back off the new run.
            FakeEncoder.Gate.Reset();
            FakeEncoder.Entered.Reset();
            FakeEncoder.ThrowNext = true;
            Submit(100, 10);
            Check(FakeEncoder.Entered.Wait(2000), "Failing old encode is running");
            int priorErrors;
            lock (errors) priorErrors = errors.Count;
            encoder.Reset();
            Submit(110, 11);
            FakeEncoder.Gate.Set();
            Check(publishedSignal.Wait(2000) && published[^1].At == 11,
                "A new frame publishes immediately after an old-generation failure");
            lock (errors) Check(errors.Count == priorErrors, "Old-generation error callback suppressed");
        }
        finally
        {
            FakeEncoder.Gate.Set();
            handle.Free();
        }
        encoder.Dispose();
        encoder.Submit((IntPtr)1, 4, 4, 4, 9, null, CameraOrientation.NORMAL, 60, 1);
        Check(published[^1].At == 11, "Submit after Dispose is ignored");
        Check(encodeTimes == published.Count, "Encode time is reported once per published frame");
        return assertions;
    }
}
