using System;
using QuestX.Camera;
using QuestX.Utils;

namespace QuestX.Native.AprilTag
{
    /// <summary>
    /// A libapriltag image_u8 that one detector owns and refills each frame.
    /// </summary>
    public unsafe class ImageU8 : IDisposable
    {
        internal ImageU8Native* Handle { get; private set; }

        private ImageU8(ImageU8Native* img)
        {
            Handle = img;
        }

        public int Width => Handle == null ? 0 : Handle->width;
        public int Height => Handle == null ? 0 : Handle->height;

        /// <summary>
        /// Returns <paramref name="image"/> refilled from a tightly packed, bottom-up RGBA
        /// readback (the passthrough camera's layout), flipped to libapriltag's top-left origin.
        /// Reallocates when the size changes. Returns null if the native image cannot be created.
        /// </summary>
        public static ImageU8 FromBottomUpRgba(ImageU8 image, IntPtr rgba, int width, int height)
        {
            if (image == null || image.Width != width || image.Height != height)
            {
                image?.Dispose();
                var native = AprilTagNatives.image_u8_create(width, height);
                if (native == null)
                {
                    QueuedLogger.LogError("Failed to create native ImageU8");
                    return null;
                }
                image = new ImageU8(native);
            }

            GrayscaleConversion.BottomUpRgbaToGray(
                (byte*)rgba,
                (byte*)image.Handle->buf,
                width,
                height,
                image.Handle->stride
            );
            return image;
        }

        public void Dispose()
        {
            if (Handle != null)
            {
                AprilTagNatives.image_u8_destroy(Handle);
                Handle = null;
            }
        }
    }
}
