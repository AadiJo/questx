using System;
using System.Runtime.InteropServices;

namespace QuestX.Native.Camera
{
    /// <summary>Mirrors <c>qx_camera_info</c> in <c>native/questx-camera/src/qx_camera.h</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct QxCameraInfo
    {
        public fixed float intrinsics[5];
        public fixed float pose_translation[3];
        public fixed float pose_rotation[4];
        public int active_width;
        public int active_height;
        public int position;
        public int timestamp_source;
    }

    /// <summary>Mirrors <c>qx_frame</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QxFrame
    {
        public long frame_number;
        public long sensor_timestamp_ns;
        public long exposure_unix_ns;
        public long arrival_unix_ns;
        public long exposure_time_ns;
        public IntPtr data;
        public int width;
        public int height;
        public int stride;
        public int sensor_clock;
    }

    /// <summary>Mirrors <c>qx_camera_stats</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QxCameraStats
    {
        public long delivered;
        public long consumed;
        public long overwritten;
        public long errors;
        public int running;
        public int error;
    }

    /// <summary>
    /// P/Invoke bindings for <c>libquestxcamera.so</c>, the NDK Camera2 passthrough capture.
    /// Android only.
    /// </summary>
    public static unsafe class QuestXCameraNatives
    {
        private const string Library = "questxcamera";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int qx_camera_find(
            int position,
            byte* idOut,
            int idCapacity,
            QxCameraInfo* info
        );

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int qx_camera_start(byte* cameraId, int width, int height, int fps);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int qx_camera_acquire(QxFrame* frame, int timeoutMs);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void qx_camera_release();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void qx_camera_stop();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void qx_camera_get_stats(QxCameraStats* stats);
    }
}
