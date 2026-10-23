using System;
using System.Text;
using Meta.XR;
using QuestX.Native.Camera;
using UnityEngine;

namespace QuestX.Camera
{
    /// <summary>
    /// The passthrough camera through our own NDK Camera2 client (<c>libquestxcamera.so</c>)
    /// instead of Meta's <see cref="PassthroughCameraAccess"/>. Frames arrive as luma on the
    /// camera's own thread, so a detector thread can start on each frame as soon as it exists,
    /// with no GPU readback and no wait for the next Unity frame. Only one instance may run, and
    /// <see cref="PassthroughCameraAccess"/> must not hold the same camera meanwhile: Camera2
    /// gives a camera to one client at a time.
    /// </summary>
    public sealed unsafe class NativePassthroughCamera : IDisposable
    {
        /// <summary>Fixed auto-exposure frame rates the Quest 3 cameras accept.</summary>
        private static readonly int[] SupportedFps = { 15, 30, 60 };

        private bool running;

        public string CameraId { get; }
        public Vector2Int Resolution { get; }
        public int Fps { get; }

        /// <summary>
        /// Intrinsics for <see cref="Resolution"/> and the lens pose, in the same conventions as
        /// <see cref="PassthroughCameraAccess.Intrinsics"/>.
        /// </summary>
        public PassthroughCameraAccess.CameraIntrinsics Intrinsics { get; }

        private NativePassthroughCamera(
            string cameraId,
            Vector2Int resolution,
            int fps,
            PassthroughCameraAccess.CameraIntrinsics intrinsics
        )
        {
            CameraId = cameraId;
            Resolution = resolution;
            Fps = fps;
            Intrinsics = intrinsics;
        }

        /// <summary>
        /// Opens the camera at <paramref name="position"/> and starts streaming. Throws
        /// <see cref="InvalidOperationException"/> with the native status if it cannot.
        /// </summary>
        public static NativePassthroughCamera Start(
            PassthroughCameraAccess.CameraPositionType position,
            Vector2Int resolution,
            int requestedFps
        )
        {
            var idBytes = new byte[64];
            QxCameraInfo info;
            int found;
            fixed (byte* id = idBytes)
            {
                found = QuestXCameraNatives.qx_camera_find(
                    position == PassthroughCameraAccess.CameraPositionType.Left ? 0 : 1,
                    id,
                    idBytes.Length,
                    &info
                );
            }
            if (found != 0)
            {
                throw new InvalidOperationException(
                    $"No {position} passthrough camera found (status {found})"
                );
            }
            int length = Array.IndexOf(idBytes, (byte)0);
            string cameraId = Encoding.ASCII.GetString(idBytes, 0, length < 0 ? 0 : length);

            int fps = SupportedFps[SupportedFps.Length - 1];
            foreach (int candidate in SupportedFps)
            {
                if (candidate >= requestedFps)
                {
                    fps = candidate;
                    break;
                }
            }

            int started;
            fixed (byte* id = idBytes)
            {
                started = QuestXCameraNatives.qx_camera_start(id, resolution.x, resolution.y, fps);
            }
            if (started != 0)
            {
                throw new InvalidOperationException(
                    $"Camera {cameraId} failed to start at {resolution.x}x{resolution.y} {fps} fps (status {started})"
                );
            }
            return new NativePassthroughCamera(
                cameraId,
                resolution,
                fps,
                ComputeIntrinsics(info, resolution)
            )
            {
                running = true,
            };
        }

        /// <summary>
        /// Scales the sensor calibration to an output resolution the way the camera produces it:
        /// a centered crop of the active array to the output's aspect ratio, then a uniform scale.
        /// The lens pose uses MRUK's conversion from Camera2's to Unity's axes.
        /// </summary>
        private static PassthroughCameraAccess.CameraIntrinsics ComputeIntrinsics(
            QxCameraInfo info,
            Vector2Int resolution
        )
        {
            double sensorW = info.active_width > 0 ? info.active_width : resolution.x;
            double sensorH = info.active_height > 0 ? info.active_height : resolution.y;
            double scaleX = resolution.x / sensorW;
            double scaleY = resolution.y / sensorH;
            double max = Math.Max(scaleX, scaleY);
            scaleX /= max;
            scaleY /= max;
            double cropX = sensorW * (1 - scaleX) * 0.5;
            double cropY = sensorH * (1 - scaleY) * 0.5;
            double toOutputX = resolution.x / (sensorW * scaleX);
            double toOutputY = resolution.y / (sensorH * scaleY);

            var t = new Vector3(
                info.pose_translation[0],
                info.pose_translation[1],
                info.pose_translation[2]
            );
            var r = info.pose_rotation;
            return new PassthroughCameraAccess.CameraIntrinsics
            {
                FocalLength = new Vector2(
                    (float)(info.intrinsics[0] * toOutputX),
                    (float)(info.intrinsics[1] * toOutputY)
                ),
                PrincipalPoint = new Vector2(
                    (float)((info.intrinsics[2] - cropX) * toOutputX),
                    (float)((info.intrinsics[3] - cropY) * toOutputY)
                ),
                SensorResolution = new Vector2Int((int)sensorW, (int)sensorH),
                LensOffset = new Pose(
                    new Vector3(t.x, t.y, -t.z),
                    Quaternion.Inverse(new Quaternion(-r[0], -r[1], r[2], r[3]))
                        * Quaternion.Euler(180, 0, 0)
                ),
            };
        }

        /// <summary>
        /// Waits for a frame newer than the last one. Returns 1 with a frame, 0 on timeout, or a
        /// negative native status when the camera stopped or failed. The frame's pixels stay
        /// valid until <see cref="Release"/> or the next call.
        /// </summary>
        public int Acquire(int timeoutMs, out QxFrame frame)
        {
            QxFrame result = default;
            int status = running ? QuestXCameraNatives.qx_camera_acquire(&result, timeoutMs) : -1;
            frame = status > 0 ? result : default;
            return status;
        }

        public void Release()
        {
            if (running)
                QuestXCameraNatives.qx_camera_release();
        }

        public QxCameraStats Stats
        {
            get
            {
                QxCameraStats stats;
                QuestXCameraNatives.qx_camera_get_stats(&stats);
                return stats;
            }
        }

        public void Dispose()
        {
            if (!running)
                return;
            running = false;
            QuestXCameraNatives.qx_camera_stop();
        }
    }
}
