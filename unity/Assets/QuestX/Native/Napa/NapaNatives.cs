using System;
using System.Runtime.InteropServices;

namespace QuestX.Native.Napa
{
    /// <summary>
    /// Mirrors <c>napa_params</c> in <c>native/napa/src/napa_core.h</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NapaParamsNative
    {
        /// <summary>Smallest black-square side to guarantee, in pixels.</summary>
        public int min_tag_px;

        /// <summary>Corrected payload bits, at most 3 for tag36h11.</summary>
        public int max_hamming;

        /// <summary>Grey levels between tag black and the white quiet zone.</summary>
        public int min_contrast;

        /// <summary>Dark border cells required out of 28.</summary>
        public int min_border_cells;

        /// <summary>Cell sharpening strength x100; upstream AprilTag uses 25.</summary>
        public int sharpening_x100;
    }

    /// <summary>
    /// Mirrors <c>napa_detection</c>. Corners are x0, y0, ... in OpenCV corner order with
    /// pixel-centre coordinates (pixel (0, 0)'s center is (0, 0)).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NapaDetectionNative
    {
        public int id;
        public int hamming;
        public float decision_margin;
        public fixed double corners[8];
    }

    /// <summary>
    /// P/Invoke bindings for the Napa sparse-sampling tag36h11 detector
    /// (<c>libnapa.so</c> on Quest, <c>napa.dll</c> in the Editor).
    /// </summary>
    public static unsafe class NapaNatives
    {
        private const string Library = "napa";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr napa_create();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void napa_destroy(IntPtr detector);

        /// <summary>
        /// Detects tags in an 8-bit grayscale image. Returns the number of detections written
        /// (at most <paramref name="maxOut"/>), or a negative value on invalid input or
        /// allocation failure. A detector handle is not thread-safe.
        /// </summary>
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int napa_detect(
            IntPtr detector,
            byte* image,
            int width,
            int height,
            int stride,
            NapaParamsNative* parameters,
            NapaDetectionNative* output,
            int maxOut
        );
    }
}
