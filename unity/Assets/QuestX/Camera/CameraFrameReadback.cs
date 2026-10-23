using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace QuestX.Camera
{
    /// <summary>
    /// One CPU copy of the passthrough camera texture, filled by a non-blocking GPU readback.
    /// <see cref="Meta.XR.PassthroughCameraAccess.GetColors"/> performs the same readback but
    /// waits for it, stalling the Unity main thread on the GPU every frame; this returns at
    /// once and reports completion a frame or two later.
    ///
    /// The data is the texture's raw RGBA bytes, tightly packed with the bottom row first,
    /// exactly as <c>GetColors</c> returns them. Requests and completion callbacks run on the
    /// main thread; once a request has completed, any thread may read <see cref="Data"/> until
    /// the owner issues the next request.
    /// </summary>
    public sealed unsafe class CameraFrameReadback : IDisposable
    {
        private NativeArray<byte> buffer;
        private IntPtr data;
        private AsyncGPUReadbackRequest request;

        /// <summary>Incremented to orphan the callback of an abandoned request.</summary>
        private int requestToken;

        /// <summary>A GPU copy into this buffer has been requested and has not completed.</summary>
        public bool InFlight { get; private set; }

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>Start of the RGBA bytes; valid after a successful completion.</summary>
        public IntPtr Data => data;

        public long ByteCount => (long)Width * Height * 4;

        /// <summary>
        /// Starts copying <paramref name="texture"/>. Main thread only. On success,
        /// <paramref name="completed"/> later runs on the main thread with whether the copy
        /// produced a whole frame of the requested size.
        /// </summary>
        public bool TryRequest(Texture texture, Action<bool> completed, out string failure)
        {
            if (InFlight)
            {
                failure = "A readback is already in flight";
                return false;
            }
            if (texture == null)
            {
                failure = "The camera returned no texture";
                return false;
            }
            var format = texture.graphicsFormat;
            if (format != GraphicsFormat.R8G8B8A8_SRGB && format != GraphicsFormat.R8G8B8A8_UNorm)
            {
                failure = $"Unsupported camera texture format {format}";
                return false;
            }

            int width = texture.width;
            int height = texture.height;
            int bytes = checked(width * height * 4);
            if (!buffer.IsCreated || buffer.Length != bytes)
            {
                if (buffer.IsCreated)
                {
                    buffer.Dispose();
                }
                buffer = new NativeArray<byte>(
                    bytes,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                data = (IntPtr)buffer.GetUnsafePtr();
            }

            Width = width;
            Height = height;
            InFlight = true;
            int token = ++requestToken;
            try
            {
                request = AsyncGPUReadback.RequestIntoNativeArray(
                    ref buffer,
                    texture,
                    0,
                    result =>
                    {
                        if (token != requestToken)
                        {
                            return;
                        }
                        InFlight = false;
                        // The SDK destroys and recreates its texture on every resolution
                        // change, which fails a pending copy; a size check catches anything
                        // else stale.
                        completed(
                            !result.hasError && result.width == width && result.height == height
                        );
                    }
                );
            }
            catch (Exception ex)
            {
                // Unity's safety checks throw instead of returning an errored request.
                requestToken++;
                InFlight = false;
                failure = $"The GPU readback request failed: {ex.Message}";
                return false;
            }
            if (request.hasError)
            {
                requestToken++;
                InFlight = false;
                failure = "The GPU readback request failed";
                return false;
            }
            failure = null;
            return true;
        }

        /// <summary>
        /// Frees the buffer. A pending copy is waited for first, since the GPU writes into it;
        /// its callback is suppressed. Main thread only, and only once no thread reads
        /// <see cref="Data"/>.
        /// </summary>
        public void Dispose()
        {
            requestToken++;
            if (InFlight)
            {
                request.WaitForCompletion();
                InFlight = false;
            }
            if (buffer.IsCreated)
            {
                buffer.Dispose();
            }
            data = IntPtr.Zero;
            Width = 0;
            Height = 0;
        }
    }
}
