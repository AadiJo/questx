using Unity.Burst;

namespace QuestX.Camera
{
    /// <summary>
    /// Copies an image into a tightly packed, top-down upright image for a
    /// <see cref="CameraOrientation"/>. A Burst direct call, so it can run on the worker threads
    /// that encode the stream and the tag preview.
    /// </summary>
    [BurstCompile]
    public static unsafe class ImageRotation
    {
        /// <summary>
        /// Writes the upright image to <paramref name="destination"/>, which must hold
        /// width × height × <paramref name="bytesPerPixel"/> bytes. Its stride is the rotated
        /// width × bytes per pixel (see <see cref="CameraOrientationMath.RotateSize"/>).
        /// </summary>
        /// <param name="sourceBottomUp">Nonzero when the source's first row is the bottom row.</param>
        [BurstCompile]
        public static void Rotate(
            byte* source,
            int width,
            int height,
            int sourceStride,
            int bytesPerPixel,
            int sourceBottomUp,
            CameraOrientation orientation,
            byte* destination
        )
        {
            CameraOrientationMath.RotateSize(
                orientation,
                width,
                height,
                out int rotatedWidth,
                out int rotatedHeight
            );
            for (int rotatedY = 0; rotatedY < rotatedHeight; rotatedY++)
            {
                byte* dstRow = destination + (long)rotatedY * rotatedWidth * bytesPerPixel;
                for (int rotatedX = 0; rotatedX < rotatedWidth; rotatedX++)
                {
                    CameraOrientationMath.SourcePixel(
                        orientation,
                        rotatedX,
                        rotatedY,
                        width,
                        height,
                        out int x,
                        out int y
                    );
                    int srcRow = sourceBottomUp != 0 ? height - 1 - y : y;
                    byte* src = source + (long)srcRow * sourceStride + x * bytesPerPixel;
                    byte* dst = dstRow + rotatedX * bytesPerPixel;
                    for (int b = 0; b < bytesPerPixel; b++)
                    {
                        dst[b] = src[b];
                    }
                }
            }
        }
    }
}
