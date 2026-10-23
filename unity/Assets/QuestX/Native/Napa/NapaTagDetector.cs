using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using QuestX.Camera;
using QuestX.Native.AprilTag;

namespace QuestX.Native.Napa
{
    /// <summary>
    /// Napa backend for the passthrough camera. Converts the bottom-up RGBA readback into its
    /// own top-left-origin grayscale buffer (the same flip libapriltag's ImageU8 applies) and
    /// runs <see cref="NapaCore"/> on it.
    /// </summary>
    public sealed unsafe class NapaTagDetector : ITagDetector
    {
        private NapaCore core;
        private byte* gray;
        private int grayLength;

        public NapaTagDetector(int minTagPixels, int maxHamming)
        {
            core = new NapaCore(minTagPixels, maxHamming);
        }

        public string Name => "Napa";

        public GrayImage Image { get; private set; }

        public bool Detect(
            IntPtr rgba,
            long byteCount,
            int width,
            int height,
            List<TagDetection> detections
        )
        {
            if (core == null)
                throw new ObjectDisposedException(nameof(NapaTagDetector));
            detections.Clear();
            Image = default;
            if (!LibAprilTagDetector.IsReadable(rgba, byteCount, width, height))
                return false;

            int pixels = width * height;
            if (gray == null || grayLength != pixels)
            {
                FreeGray();
                gray = (byte*)Marshal.AllocHGlobal(pixels);
                grayLength = pixels;
            }

            GrayscaleConversion.BottomUpRgbaToGray((byte*)rgba, gray, width, height, width);
            core.Detect(gray, width, height, width, detections);
            Image = new GrayImage((IntPtr)gray, width, height, width);
            return true;
        }

        private void FreeGray()
        {
            if (gray != null)
            {
                Marshal.FreeHGlobal((IntPtr)gray);
                gray = null;
                grayLength = 0;
            }
        }

        public void Dispose()
        {
            core?.Dispose();
            core = null;
            Image = default;
            FreeGray();
        }
    }
}
