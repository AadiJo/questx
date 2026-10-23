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
            image = Resize(image, width, height);
            if (image == null)
            {
                return null;
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

        /// <summary>
        /// Returns <paramref name="image"/> refilled from a top-left-origin grayscale image with
        /// rows <paramref name="stride"/> bytes apart. Reallocates when the size changes. Returns
        /// null for an invalid source or if the native image cannot be created.
        /// </summary>
        public static ImageU8 FromGray(
            ImageU8 image,
            IntPtr gray,
            int width,
            int height,
            int stride
        )
        {
            if (gray == IntPtr.Zero || width <= 0 || height <= 0 || stride < width)
            {
                return null;
            }
            image = Resize(image, width, height);
            if (image == null)
            {
                return null;
            }
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    (byte*)gray + (long)y * stride,
                    (byte*)image.Handle->buf + (long)y * image.Handle->stride,
                    width,
                    width
                );
            }
            return image;
        }

        private static ImageU8 Resize(ImageU8 image, int width, int height)
        {
            if (image != null && image.Width == width && image.Height == height)
            {
                return image;
            }
            image?.Dispose();
            var native = AprilTagNatives.image_u8_create(width, height);
            if (native == null)
            {
                QueuedLogger.LogError("Failed to create native ImageU8");
                return null;
            }
            return new ImageU8(native);
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
