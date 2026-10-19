using System;
using System.Collections.Generic;
using QuestX.Camera;
using QuestX.Native.AprilTag;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

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
        private NativeArray<byte> gray;

        public NapaTagDetector(int minTagPixels, int maxHamming)
        {
            core = new NapaCore(minTagPixels, maxHamming);
        }

        public string Name => "Napa";

        public GrayImage Image { get; private set; }

        public bool Detect(
            NativeArray<Color32> colors,
            int width,
            int height,
            List<TagDetection> detections
        )
        {
            if (core == null)
                throw new ObjectDisposedException(nameof(NapaTagDetector));
            detections.Clear();
            Image = default;
            if (!colors.IsCreated || width <= 0 || height <= 0 || colors.Length < width * height)
                return false;

            int pixels = width * height;
            if (!gray.IsCreated || gray.Length != pixels)
            {
                if (gray.IsCreated)
                    gray.Dispose();
                gray = new NativeArray<byte>(
                    pixels,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
            }

            new GrayscaleConversionJob
            {
                Source = colors.Reinterpret<byte>(4),
                Destination = gray,
                Width = width,
                Height = height,
                Stride = width,
                FlipVertically = true,
            }
                .Schedule(height, 32)
                .Complete();

            var buffer = (byte*)gray.GetUnsafeReadOnlyPtr();
            core.Detect(buffer, width, height, width, detections);
            Image = new GrayImage((IntPtr)buffer, width, height, width);
            return true;
        }

        public void Dispose()
        {
            core?.Dispose();
            core = null;
            Image = default;
            if (gray.IsCreated)
                gray.Dispose();
        }
    }
}
