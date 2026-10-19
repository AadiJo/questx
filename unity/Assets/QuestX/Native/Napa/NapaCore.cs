using System;
using System.Collections.Generic;
using QuestX.Native.AprilTag;

namespace QuestX.Native.Napa
{
    /// <summary>
    /// Managed owner of one native Napa detector and its reusable result buffer. Napa samples
    /// one pixel every 3/4 of the smallest tag cell, skips contrast-free tiles, and fits
    /// sub-pixel edges at full resolution; see <c>native/napa/README.md</c>.
    /// Not thread-safe: use one instance per camera stream.
    /// </summary>
    public sealed unsafe class NapaCore : IDisposable
    {
        public const int MaxDetections = 64;

        // libapriltag corner i is Napa corner AprilTagCornerOrder[i] (verified against the
        // bundled apriltag.dll on warped renders; see native/napa/README.md).
        private static readonly int[] AprilTagCornerOrder = { 1, 0, 3, 2 };

        // Napa puts pixel (0, 0)'s center at (0, 0); libapriltag puts it at (0.5, 0.5).
        private const double AprilTagPixelOffset = 0.5;

        private IntPtr handle;
        private NapaParamsNative parameters;
        private readonly NapaDetectionNative[] results = new NapaDetectionNative[MaxDetections];

        /// <param name="minTagPixels">Smallest black-square side guaranteed to be found. Napa's
        /// sampling stride is <c>minTagPixels * 3 / 32</c>, so cost falls steeply as it grows.</param>
        /// <param name="maxHamming">Corrected payload bits, 0 to 3.</param>
        /// <param name="minContrast">Grey levels between tag black and the white quiet zone.</param>
        /// <param name="minBorderCells">Dark border cells required out of 28.</param>
        /// <param name="sharpening">Cell sharpening strength; upstream AprilTag uses 0.25.</param>
        public NapaCore(
            int minTagPixels = 24,
            int maxHamming = 3,
            int minContrast = 20,
            int minBorderCells = 22,
            double sharpening = 0.25
        )
        {
            if (minTagPixels < 8)
                throw new ArgumentOutOfRangeException(nameof(minTagPixels), "must be at least 8");
            if (maxHamming < 0 || maxHamming > 3)
                throw new ArgumentOutOfRangeException(nameof(maxHamming), "must be 0 to 3");
            if (minContrast < 1 || minContrast > 255)
                throw new ArgumentOutOfRangeException(nameof(minContrast), "must be 1 to 255");
            if (minBorderCells < 0 || minBorderCells > 28)
                throw new ArgumentOutOfRangeException(nameof(minBorderCells), "must be 0 to 28");
            if (!(sharpening >= 0.0 && sharpening <= 1.0))
                throw new ArgumentOutOfRangeException(nameof(sharpening), "must be 0 to 1");

            parameters = new NapaParamsNative
            {
                min_tag_px = minTagPixels,
                max_hamming = maxHamming,
                min_contrast = minContrast,
                min_border_cells = minBorderCells,
                sharpening_x100 = (int)Math.Round(sharpening * 100.0),
            };
            handle = NapaNatives.napa_create();
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("napa_create failed");
        }

        public int MinTagPixels => parameters.min_tag_px;

        /// <summary>
        /// Detects tags in an 8-bit grayscale image with a top-left origin and replaces the
        /// contents of <paramref name="detections"/>, converted to libapriltag conventions.
        /// </summary>
        public void Detect(
            byte* gray,
            int width,
            int height,
            int stride,
            List<TagDetection> detections
        )
        {
            if (handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(NapaCore));
            if (gray == null)
                throw new ArgumentNullException(nameof(gray));
            detections.Clear();

            int count;
            fixed (NapaDetectionNative* output = results)
            fixed (NapaParamsNative* p = &parameters)
            {
                count = NapaNatives.napa_detect(
                    handle,
                    gray,
                    width,
                    height,
                    stride,
                    p,
                    output,
                    MaxDetections
                );
                if (count < 0)
                    throw new InvalidOperationException($"napa_detect failed with status {count}");
                for (int i = 0; i < count; i++)
                {
                    detections.Add(ToTagDetection(output + i));
                }
            }
        }

        private static TagDetection ToTagDetection(NapaDetectionNative* detection)
        {
            var corners = new Point2D[4];
            for (int i = 0; i < 4; i++)
            {
                int k = AprilTagCornerOrder[i];
                corners[i] = new Point2D
                {
                    x = detection->corners[2 * k] + AprilTagPixelOffset,
                    y = detection->corners[2 * k + 1] + AprilTagPixelOffset,
                };
            }
            return new TagDetection(
                detection->id,
                detection->hamming,
                detection->decision_margin,
                ProjectiveCenter(corners),
                corners[0],
                corners[1],
                corners[2],
                corners[3]
            );
        }

        /// <summary>
        /// libapriltag's center is the homography image of the tag center, which is the
        /// intersection of the quad's diagonals. Falls back to the corner mean if they are parallel.
        /// </summary>
        private static Point2D ProjectiveCenter(Point2D[] c)
        {
            double d1x = c[2].x - c[0].x,
                d1y = c[2].y - c[0].y;
            double d2x = c[3].x - c[1].x,
                d2y = c[3].y - c[1].y;
            double denominator = d1x * d2y - d1y * d2x;
            if (Math.Abs(denominator) > 1e-9)
            {
                double t = ((c[1].x - c[0].x) * d2y - (c[1].y - c[0].y) * d2x) / denominator;
                return new Point2D { x = c[0].x + t * d1x, y = c[0].y + t * d1y };
            }
            return new Point2D
            {
                x = (c[0].x + c[1].x + c[2].x + c[3].x) / 4.0,
                y = (c[0].y + c[1].y + c[2].y + c[3].y) / 4.0,
            };
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                NapaNatives.napa_destroy(handle);
                handle = IntPtr.Zero;
            }
        }
    }
}
