using System;
using System.Diagnostics;
using System.Threading;
using QuestX.Camera;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>Compresses an 8-bit grayscale image to JPEG. Used on one thread at a time.</summary>
    public interface IGrayJpegEncoder : IDisposable
    {
        byte[] Encode(IntPtr pixels, int stride, int width, int height, int quality);
    }

    /// <summary>
    /// Encodes the synchronized AprilTag preview on its own thread, so detection never waits on
    /// JPEG work. The detector submits a copy of each frame it wants shown and moves on; only the
    /// newest submission is kept, so a slow encode or viewer drops preview frames rather than
    /// delaying detection. Frames are box-filtered down by a whole factor (see
    /// <see cref="ScaleFactor"/>; detection still uses full resolution), and tag corners are
    /// scaled to match.
    /// </summary>
    public sealed unsafe class AprilTagPreviewEncoder : IDisposable
    {
        /// <summary>Largest preview, in pixels, unless high-quality streams are enabled.</summary>
        public const int StandardMaxPixels = 640 * 480;

        private const int MaxScale = 16;

        /// <summary>Called with the encoded frame: width, height, capture time, tags, JPEG.</summary>
        public delegate void PublishFrame(
            int width,
            int height,
            double capturedAt,
            AprilTagOverlayTag[] tags,
            byte[] jpeg
        );

        private sealed class Submission
        {
            public byte[] Pixels = Array.Empty<byte>();
            public int Width;
            public int Height;
            public double CapturedAt;
            public AprilTagOverlayTag[] Tags;
            public CameraOrientation Orientation;
            public int Quality;
            public int Scale;
            public long Generation;
        }

        private readonly object sync = new object();
        private readonly Func<IGrayJpegEncoder> createEncoder;
        private readonly PublishFrame publish;
        private readonly Action<Exception> onError;
        private readonly Action<double> onEncoded;
        private readonly Thread thread;
        private Submission pending = new Submission();
        private Submission working = new Submission();
        private byte[] resampled = Array.Empty<byte>();
        private IGrayJpegEncoder encoder;
        private bool hasPending;
        private bool releaseRequested;
        private bool running = true;
        private long backoffUntilTicks;
        private long generation;
        private long droppedCount;

        /// <summary>Submissions replaced by a newer one before they were encoded.</summary>
        public long DroppedCount => Interlocked.Read(ref droppedCount);

        /// <summary>Capture with a frame; Submit rejects it if Reset occurred since capture.</summary>
        public long Generation
        {
            get
            {
                lock (sync)
                    return generation;
            }
        }

        /// <param name="onEncoded">Called with each published frame's scale-and-encode time, in ms.</param>
        public AprilTagPreviewEncoder(
            Func<IGrayJpegEncoder> createEncoder,
            PublishFrame publish,
            Action<Exception> onError,
            Action<double> onEncoded = null
        )
        {
            this.createEncoder = createEncoder;
            this.publish = publish;
            this.onError = onError;
            this.onEncoded = onEncoded;
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "QuestX AprilTag preview",
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
        }

        /// <summary>
        /// Copies a top-left-origin grayscale image to encode at 1/<paramref name="scale"/> of its
        /// size per side (from <see cref="ScaleFactor"/>). <paramref name="tags"/> are in the
        /// full-resolution image after <paramref name="orientation"/> and are not modified. The
        /// image is not read after this returns.
        /// </summary>
        public void Submit(
            IntPtr pixels,
            int width,
            int height,
            int stride,
            double capturedAt,
            AprilTagOverlayTag[] tags,
            CameraOrientation orientation,
            int quality,
            int scale,
            long? expectedGeneration = null
        )
        {
            if (pixels == IntPtr.Zero || width <= 0 || height <= 0 || stride < width)
            {
                return;
            }
            lock (sync)
            {
                if (
                    !running
                    || (expectedGeneration.HasValue && expectedGeneration.Value != generation)
                )
                {
                    return;
                }
                if (hasPending)
                {
                    Interlocked.Increment(ref droppedCount);
                }
                var s = pending;
                int size = width * height;
                if (s.Pixels.Length < size)
                {
                    s.Pixels = new byte[size];
                }
                fixed (byte* dst = s.Pixels)
                {
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            (byte*)pixels + (long)y * stride,
                            dst + (long)y * width,
                            width,
                            width
                        );
                    }
                }
                s.Generation = generation;
                s.Width = width;
                s.Height = height;
                s.CapturedAt = capturedAt;
                s.Tags = tags;
                s.Orientation = orientation;
                s.Quality = quality;
                s.Scale = scale >= 1 && width % scale == 0 && height % scale == 0 ? scale : 1;
                hasPending = true;
                Monitor.Pulse(sync);
            }
        }

        /// <summary>Invalidates queued and in-flight frames and releases the encoder on its worker.</summary>
        public void Reset()
        {
            lock (sync)
            {
                generation++;
                hasPending = false;
                backoffUntilTicks = 0;
                releaseRequested = true;
                Monitor.Pulse(sync);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                running = false;
                Monitor.Pulse(sync);
            }
            thread.Join();
        }

        private void Run()
        {
            while (true)
            {
                Submission job;
                lock (sync)
                {
                    while (running && !hasPending && !releaseRequested)
                    {
                        Monitor.Wait(sync);
                    }
                    if (!running)
                    {
                        break;
                    }
                    if (releaseRequested)
                    {
                        releaseRequested = false;
                        encoder?.Dispose();
                        encoder = null;
                        continue;
                    }
                    job = pending;
                    pending = working;
                    working = job;
                    hasPending = false;
                    if (DateTime.UtcNow.Ticks < backoffUntilTicks)
                    {
                        continue;
                    }
                }
                try
                {
                    Encode(job);
                }
                catch (Exception ex)
                {
                    // Preview failures must not stop detection; retry with a fresh encoder later.
                    encoder?.Dispose();
                    encoder = null;
                    lock (sync)
                    {
                        // A failed old job must not clear the preview or back off a new run.
                        if (running && job.Generation == generation)
                        {
                            backoffUntilTicks = DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond;
                            onError?.Invoke(ex);
                        }
                    }
                }
            }
            encoder?.Dispose();
            encoder = null;
        }

        private void Encode(Submission job)
        {
            long start = Stopwatch.GetTimestamp();
            int factor = job.Scale;
            CameraOrientationMath.RotateSize(
                job.Orientation,
                job.Width / factor,
                job.Height / factor,
                out int width,
                out int height
            );
            encoder ??= createEncoder();
            byte[] jpeg;
            if (factor == 1 && job.Orientation == CameraOrientation.NORMAL)
            {
                fixed (byte* pixels = job.Pixels)
                {
                    jpeg = encoder.Encode((IntPtr)pixels, width, width, height, job.Quality);
                }
            }
            else
            {
                if (resampled.Length < width * height)
                {
                    resampled = new byte[width * height];
                }
                Resample(job.Pixels, job.Width, job.Height, factor, job.Orientation, resampled);
                fixed (byte* pixels = resampled)
                {
                    jpeg = encoder.Encode((IntPtr)pixels, width, width, height, job.Quality);
                }
            }
            lock (sync)
            {
                if (!running || job.Generation != generation)
                {
                    return;
                }
                // Reset and publication are serialized: once Reset returns no old job can publish.
                publish(width, height, job.CapturedAt, ScaleTags(job.Tags, factor), jpeg);
            }
            onEncoded?.Invoke((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        }

        /// <summary>
        /// The per-side scale-down for a <paramref name="width"/> x <paramref name="height"/>
        /// frame: <paramref name="requested"/> (1, 2, 4 or 8), halved until it divides both sides
        /// evenly (so scaling and rotation commute exactly), then doubled while the preview has
        /// more than <paramref name="maxPixels"/> pixels (0 for no limit). The dashboard mirrors
        /// this in <c>previewSize</c> to label its choices.
        /// </summary>
        public static int ScaleFactor(int width, int height, int requested, int maxPixels)
        {
            bool Divides(int f) => width % f == 0 && height % f == 0;
            int factor = Math.Clamp(requested, 1, MaxScale);
            while (factor > 1 && !Divides(factor))
            {
                factor /= 2;
            }
            while (
                maxPixels > 0
                && (long)(width / factor) * (height / factor) > maxPixels
                && factor * 2 <= MaxScale
                && Divides(factor * 2)
            )
            {
                factor *= 2;
            }
            return factor;
        }

        /// <summary>
        /// Averages <paramref name="factor"/> x <paramref name="factor"/> blocks of a tightly packed
        /// <paramref name="width"/> x <paramref name="height"/> image and rotates the result for
        /// <paramref name="orientation"/> into <paramref name="destination"/>, tightly packed.
        /// </summary>
        public static void Resample(
            byte[] source,
            int width,
            int height,
            int factor,
            CameraOrientation orientation,
            byte[] destination
        )
        {
            int scaledWidth = width / factor;
            int scaledHeight = height / factor;
            int start,
                stepX,
                stepY;
            // Walk source blocks in row order; orientation only changes destination increments.
            switch (orientation)
            {
                case CameraOrientation.UPSIDE_DOWN:
                    start = scaledWidth * scaledHeight - 1;
                    stepX = -1;
                    stepY = -scaledWidth;
                    break;
                case CameraOrientation.LEFT:
                    start = (scaledWidth - 1) * scaledHeight;
                    stepX = -scaledHeight;
                    stepY = 1;
                    break;
                case CameraOrientation.RIGHT:
                    start = scaledHeight - 1;
                    stepX = scaledHeight;
                    stepY = -1;
                    break;
                default:
                    start = 0;
                    stepX = 1;
                    stepY = scaledWidth;
                    break;
            }
            int area = factor * factor;
            fixed (
                byte* src = source,
                    dst = destination
            )
            {
                for (int y = 0; y < scaledHeight; y++)
                {
                    int destinationIndex = start + y * stepY;
                    for (int x = 0; x < scaledWidth; x++, destinationIndex += stepX)
                    {
                        byte* block = src + (long)y * factor * width + x * factor;
                        int sum = 0;
                        for (int by = 0; by < factor; by++)
                        {
                            byte* line = block + (long)by * width;
                            for (int bx = 0; bx < factor; bx++)
                            {
                                sum += line[bx];
                            }
                        }
                        dst[destinationIndex] = (byte)((sum + area / 2) / area);
                    }
                }
            }
        }

        /// <summary>Maps pixel-centre corner coordinates onto the scaled image.</summary>
        public static AprilTagOverlayTag[] ScaleTags(AprilTagOverlayTag[] tags, int factor)
        {
            if (tags == null || factor == 1)
            {
                return tags ?? Array.Empty<AprilTagOverlayTag>();
            }
            var scaled = new AprilTagOverlayTag[tags.Length];
            for (int i = 0; i < tags.Length; i++)
            {
                var corners = new AprilTagOverlayPoint[tags[i].Corners.Count];
                for (int c = 0; c < corners.Length; c++)
                {
                    var p = tags[i].Corners[c];
                    corners[c] = new AprilTagOverlayPoint(
                        (p.X + 0.5) / factor - 0.5,
                        (p.Y + 0.5) / factor - 0.5
                    );
                }
                scaled[i] = new AprilTagOverlayTag(tags[i].Id, corners);
            }
            return scaled;
        }
    }
}
