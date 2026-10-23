using Unity.Burst;

namespace QuestX.Camera
{
    /// <summary>
    /// Converts the passthrough camera's RGBA readback to 8-bit luma. A Burst direct call, so
    /// unlike a job it can run on the detection worker thread.
    /// </summary>
    [BurstCompile]
    public static unsafe class GrayscaleConversion
    {
        /// <summary>
        /// Converts a tightly packed, bottom-up RGBA image into a top-down grayscale image with
        /// <paramref name="grayStride"/> bytes per row.
        /// </summary>
        [BurstCompile]
        public static void BottomUpRgbaToGray(
            byte* rgba,
            byte* gray,
            int width,
            int height,
            int grayStride
        )
        {
            for (int y = 0; y < height; y++)
            {
                byte* source = rgba + (long)(height - 1 - y) * width * 4;
                byte* destination = gray + (long)y * grayStride;
                for (int x = 0; x < width; x++)
                {
                    byte* pixel = source + x * 4;
                    destination[x] = (byte)((pixel[0] * 77 + pixel[1] * 150 + pixel[2] * 29) >> 8);
                }
            }
        }
    }
}
