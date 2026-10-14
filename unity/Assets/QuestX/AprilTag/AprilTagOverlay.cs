using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>
    /// Latest-frame-only handoff from the detector to HTTP viewers. Each frame owns
    /// both its JPEG and detections. Viewers request another frame after consuming
    /// the previous one; multiple viewers share the same encoded frame.
    /// </summary>
    public sealed class AprilTagOverlay
    {
        private const double ViewerLeaseMs = 3000;
        public const double MaxAgeMs = 2000;
        private readonly object sync = new object();
        private readonly Func<double> clock;
        private bool enabled;
        private bool streamEnabled;
        private bool requested;
        private double requestedUntil;
        private long revision;
        private Frame frame;
        private TaskCompletionSource<bool> changed = NewSignal();

        public AprilTagOverlay(Func<double> clock = null)
        {
            this.clock = clock ?? MonotonicMilliseconds;
        }

        public static double MonotonicMilliseconds() =>
            Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsRequested
        {
            get
            {
                lock (sync)
                {
                    return enabled && streamEnabled && requested && clock() < requestedUntil;
                }
            }
        }

        public void SetEnabled(bool value)
        {
            lock (sync)
            {
                enabled = value;
                ClearLocked();
            }
        }

        public void SetStreamEnabled(bool value)
        {
            lock (sync)
            {
                streamEnabled = value;
                ClearLocked();
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                ClearLocked();
            }
        }

        private void ClearLocked()
        {
            frame = null;
            revision++;
            SignalLocked();
        }

        private void SignalLocked()
        {
            var previous = changed;
            changed = NewSignal();
            previous.TrySetResult(true);
        }

        // Ownership transfers here: neither byte array nor detections may be mutated.
        public void Publish(
            int width,
            int height,
            double capturedAt,
            AprilTagOverlayTag[] detections,
            byte[] jpeg
        )
        {
            if (width <= 0 || height <= 0 || jpeg == null || jpeg.Length == 0)
            {
                throw new ArgumentException(
                    "A preview frame must include an image and dimensions."
                );
            }
            lock (sync)
            {
                if (!enabled || !streamEnabled || !requested || clock() >= requestedUntil)
                {
                    return;
                }
                frame = new Frame(++revision, width, height, capturedAt, detections, jpeg);
                requested = false;
                SignalLocked();
            }
        }

        /// <summary>
        /// Waits at most one second for a new frame, then returns an image-free
        /// heartbeat. No per-client frame queues. Equality (not greater-than) also
        /// lets a viewer recover when the headset restarts its sequence.
        /// </summary>
        public async Task<AprilTagOverlayPacket> ReadAsync(
            long after,
            CancellationToken cancellation
        )
        {
            cancellation.ThrowIfCancellationRequested();
            double readStartedAt = clock();
            Task signal = null;
            lock (sync)
            {
                double now = clock();
                if (now >= requestedUntil && frame != null)
                {
                    ClearLocked();
                }
                requestedUntil = now + ViewerLeaseMs;
                if (enabled && streamEnabled && !HasNewFrame(after, now))
                {
                    requested = true;
                    signal = changed.Task;
                }
            }

            if (signal != null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                await Task.WhenAny(signal, Task.Delay(1000, timeout.Token)).ConfigureAwait(false);
                timeout.Cancel();
                cancellation.ThrowIfCancellationRequested();
            }

            lock (sync)
            {
                double now = clock();
                bool includeFrame = enabled && streamEnabled && HasNewFrame(after, now);
                return new AprilTagOverlayPacket(
                    new AprilTagOverlayMetadata(
                        enabled,
                        streamEnabled,
                        revision,
                        includeFrame ? frame.Width : 0,
                        includeFrame ? frame.Height : 0,
                        includeFrame ? Math.Max(0, now - frame.CapturedAt) : -1,
                        Math.Max(0, now - readStartedAt),
                        includeFrame ? frame.Tags : Array.Empty<AprilTagOverlayTag>()
                    ),
                    includeFrame ? frame.Jpeg : Array.Empty<byte>()
                );
            }
        }

        private bool HasNewFrame(long after, double now) =>
            frame != null && frame.Id != after && now - frame.CapturedAt < MaxAgeMs;

        private sealed class Frame
        {
            public readonly long Id;
            public readonly int Width;
            public readonly int Height;
            public readonly double CapturedAt;
            public readonly IReadOnlyList<AprilTagOverlayTag> Tags;
            public readonly byte[] Jpeg;

            public Frame(
                long id,
                int width,
                int height,
                double capturedAt,
                AprilTagOverlayTag[] tags,
                byte[] jpeg
            )
            {
                Id = id;
                Width = width;
                Height = height;
                CapturedAt = capturedAt;
                Tags = Array.AsReadOnly(tags);
                Jpeg = jpeg;
            }
        }
    }

    /// <summary>
    /// Wire format: four-byte little-endian JSON length, UTF-8 metadata, JPEG bytes.
    /// JPEG data is shared with the immutable frame, never base64 encoded or copied
    /// per viewer. A heartbeat has no JPEG and width/height zero.
    /// </summary>
    public sealed class AprilTagOverlayPacket
    {
        private readonly byte[] prefix = new byte[4];
        private readonly byte[] metadata;
        private readonly byte[] jpeg;
        public long ContentLength => prefix.Length + metadata.Length + (long)jpeg.Length;

        public AprilTagOverlayPacket(AprilTagOverlayMetadata metadata, byte[] jpeg)
        {
            this.metadata = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(metadata));
            this.jpeg = jpeg;
            BinaryPrimitives.WriteInt32LittleEndian(prefix, this.metadata.Length);
        }

        public async Task WriteAsync(Stream output, CancellationToken cancellation)
        {
            await output.WriteAsync(prefix, 0, prefix.Length, cancellation).ConfigureAwait(false);
            await output
                .WriteAsync(metadata, 0, metadata.Length, cancellation)
                .ConfigureAwait(false);
            await output.WriteAsync(jpeg, 0, jpeg.Length, cancellation).ConfigureAwait(false);
        }
    }

    public sealed class AprilTagOverlayMetadata
    {
        [JsonProperty("version")]
        public int Version => 1;

        [JsonProperty("enabled")]
        public bool Enabled { get; }

        [JsonProperty("streamEnabled")]
        public bool StreamEnabled { get; }

        [JsonProperty("frameId")]
        public long FrameId { get; }

        [JsonProperty("width")]
        public int Width { get; }

        [JsonProperty("height")]
        public int Height { get; }

        [JsonProperty("ageMs")]
        public double AgeMs { get; }

        [JsonProperty("waitMs")]
        public double WaitMs { get; }

        [JsonProperty("maxAgeMs")]
        public double MaxAgeMs => AprilTagOverlay.MaxAgeMs;

        [JsonProperty("tags")]
        public IReadOnlyList<AprilTagOverlayTag> Tags { get; }

        public AprilTagOverlayMetadata(
            bool enabled,
            bool streamEnabled,
            long frameId,
            int width,
            int height,
            double ageMs,
            double waitMs,
            IReadOnlyList<AprilTagOverlayTag> tags
        )
        {
            Enabled = enabled;
            StreamEnabled = streamEnabled;
            FrameId = frameId;
            Width = width;
            Height = height;
            AgeMs = ageMs;
            WaitMs = waitMs;
            Tags = tags;
        }
    }

    public sealed class AprilTagOverlayTag
    {
        [JsonProperty("id")]
        public int Id { get; }

        [JsonProperty("corners")]
        public IReadOnlyList<AprilTagOverlayPoint> Corners { get; }

        public AprilTagOverlayTag(int id, params AprilTagOverlayPoint[] corners)
        {
            Id = id;
            Corners = Array.AsReadOnly(corners);
        }
    }

    public readonly struct AprilTagOverlayPoint
    {
        [JsonProperty("x")]
        public double X { get; }

        [JsonProperty("y")]
        public double Y { get; }

        public AprilTagOverlayPoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }
}
