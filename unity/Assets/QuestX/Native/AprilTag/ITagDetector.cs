using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace QuestX.Native.AprilTag
{
    /// <summary>
    /// An 8-bit grayscale image with a top-left origin, borrowed from the detector that owns it.
    /// Valid only until that detector's next <see cref="ITagDetector.Detect"/> or Dispose.
    /// </summary>
    public readonly struct GrayImage
    {
        public IntPtr Buffer { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>Bytes per row (at least <see cref="Width"/>).</summary>
        public int Stride { get; }

        public bool IsValid => Buffer != IntPtr.Zero && Width > 0 && Height > 0;

        public GrayImage(IntPtr buffer, int width, int height, int stride)
        {
            Buffer = buffer;
            Width = width;
            Height = height;
            Stride = stride;
        }
    }

    /// <summary>
    /// A tag36h11 detector that consumes the passthrough camera's RGBA readback directly, so
    /// each backend can convert the frame into whatever buffer its native library wants.
    /// Instances hold native state: use one per camera stream, on one thread, and dispose them.
    /// </summary>
    public interface ITagDetector : IDisposable
    {
        /// <summary>Short backend name for diagnostics and logs.</summary>
        string Name { get; }

        /// <summary>
        /// The grayscale image the last successful <see cref="Detect"/> read, so a preview can
        /// show exactly what was detected. Invalid after a skipped frame or Dispose.
        /// </summary>
        GrayImage Image { get; }

        /// <summary>
        /// Detects tags in the first <paramref name="width"/> x <paramref name="height"/> pixels
        /// of a bottom-up RGBA readback and replaces the contents of <paramref name="detections"/>.
        /// Returns false when the frame could not be converted (skip it); throws when the native
        /// detector fails.
        /// </summary>
        bool Detect(
            NativeArray<Color32> colors,
            int width,
            int height,
            List<TagDetection> detections
        );
    }

    /// <summary>The University of Michigan libapriltag backend.</summary>
    public sealed unsafe class LibAprilTagDetector : ITagDetector
    {
        private AprilTagDetector detector;

        public LibAprilTagDetector()
        {
            // The detector takes ownership of the family and disposes it with itself.
            var family = new Tag36h11();
            try
            {
                detector = new AprilTagDetector();
                detector.AddFamily(family);
            }
            catch
            {
                detector?.Dispose();
                family.Dispose();
                throw;
            }
        }

        public string Name => "AprilTag";

        public GrayImage Image { get; private set; }

        public bool Detect(
            NativeArray<Color32> colors,
            int width,
            int height,
            List<TagDetection> detections
        )
        {
            if (detector == null)
                throw new ObjectDisposedException(nameof(LibAprilTagDetector));
            detections.Clear();
            Image = default;

            // ImageU8 returns null when the readback is uninitialized or empty (e.g. the camera
            // was just enabled and hasn't produced a frame yet).
            var image = ImageU8.FromPassthroughCamera(colors, width, height);
            if (image == null)
                return false;
            var native = image.Handle;
            Image = new GrayImage(native->buf, native->width, native->height, native->stride);

            // Every detector call owns a native result array, even with zero tags.
            using var results = detector.Detect(image);
            foreach (var detection in results)
            {
                detections.Add(TagDetection.From(detection));
            }
            return true;
        }

        public void Dispose()
        {
            detector?.Dispose();
            detector = null;
            Image = default;
        }
    }
}
