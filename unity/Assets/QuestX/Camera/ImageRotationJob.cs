using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace QuestX.Camera
{
    /// <summary>
    /// Copies an image into a tightly packed, top-down upright image for a
    /// <see cref="CameraOrientation"/>. Schedule one iteration per destination row.
    /// </summary>
    [BurstCompile]
    public struct ImageRotationJob : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<byte> Source;

        [WriteOnly, NativeDisableParallelForRestriction]
        public NativeArray<byte> Destination;
        public int Width;
        public int Height;
        public int SourceStride;
        public int BytesPerPixel;
        public bool SourceBottomUp;
        public CameraOrientation Orientation;

        public void Execute(int rotatedY)
        {
            CameraOrientationMath.RotateSize(
                Orientation,
                Width,
                Height,
                out int rotatedWidth,
                out _
            );
            int dstRow = rotatedY * rotatedWidth * BytesPerPixel;

            for (int rotatedX = 0; rotatedX < rotatedWidth; rotatedX++)
            {
                CameraOrientationMath.SourcePixel(
                    Orientation,
                    rotatedX,
                    rotatedY,
                    Width,
                    Height,
                    out int x,
                    out int y
                );
                int srcRow = SourceBottomUp ? Height - 1 - y : y;
                int srcIdx = srcRow * SourceStride + x * BytesPerPixel;
                int dstIdx = dstRow + rotatedX * BytesPerPixel;
                for (int b = 0; b < BytesPerPixel; b++)
                {
                    Destination[dstIdx + b] = Source[srcIdx + b];
                }
            }
        }

        /// <summary>
        /// Rotates <paramref name="source"/> into a new array the caller must dispose.
        /// The result is top-down with a stride of rotated width × bytes per pixel.
        /// </summary>
        public static NativeArray<byte> Rotate(
            NativeArray<byte> source,
            int width,
            int height,
            int sourceStride,
            int bytesPerPixel,
            bool sourceBottomUp,
            CameraOrientation orientation,
            Allocator allocator
        )
        {
            CameraOrientationMath.RotateSize(
                orientation,
                width,
                height,
                out _,
                out int rotatedHeight
            );
            var destination = new NativeArray<byte>(
                width * height * bytesPerPixel,
                allocator,
                NativeArrayOptions.UninitializedMemory
            );
            new ImageRotationJob
            {
                Source = source,
                Destination = destination,
                Width = width,
                Height = height,
                SourceStride = sourceStride,
                BytesPerPixel = bytesPerPixel,
                SourceBottomUp = sourceBottomUp,
                Orientation = orientation,
            }
                .Schedule(rotatedHeight, 32)
                .Complete();
            return destination;
        }
    }
}
