using System;
using QuestX.QuestX.Geometry;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>
    /// Latest-frame handoff from the detector to the web UI's 3D views. Unlike the camera
    /// preview it carries no image and does not need the passthrough stream. The detector only
    /// builds frames while a viewer has polled within the last few seconds.
    /// </summary>
    public sealed class AprilTagScene
    {
        private const double ViewerLeaseMs = 3000;
        private readonly object sync = new object();
        private readonly Func<double> clock;
        private double requestedUntil = double.NegativeInfinity;
        private AprilTagSceneFrame frame;

        public AprilTagScene(Func<double> clock = null)
        {
            this.clock = clock ?? AprilTagOverlay.MonotonicMilliseconds;
        }

        /// <summary>Current time on the clock that frame capture times use, in milliseconds.</summary>
        public double Now => clock();

        /// <summary>Whether a viewer polled recently enough that frames are worth building.</summary>
        public bool IsRequested
        {
            get
            {
                lock (sync)
                {
                    return clock() < requestedUntil;
                }
            }
        }

        // Ownership transfers here: the frame and its tags must not be mutated afterwards.
        public void Publish(AprilTagSceneFrame next)
        {
            lock (sync)
            {
                frame = next;
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                frame = null;
            }
        }

        /// <summary>Returns the latest frame (or null) and extends the viewer lease.</summary>
        public AprilTagSceneFrame Read()
        {
            lock (sync)
            {
                requestedUntil = clock() + ViewerLeaseMs;
                return frame;
            }
        }
    }

    /// <summary>One detector frame: every raw detection with its single-tag camera-frame pose.</summary>
    public sealed class AprilTagSceneFrame
    {
        /// <summary>Exposure time on <see cref="AprilTagOverlay.MonotonicMilliseconds"/>.</summary>
        public double CapturedAtMs { get; }

        public int Width { get; }
        public int Height { get; }

        /// <summary>Pinhole intrinsics [fx, fy, cx, cy] in pixels.</summary>
        public double[] Intrinsics { get; }

        /// <summary>Passthrough camera pose relative to the headset center eye (WPILib axes).</summary>
        public Pose3d HeadsetToCamera { get; }

        public AprilTagSceneTag[] Tags { get; }

        public AprilTagSceneFrame(
            double capturedAtMs,
            int width,
            int height,
            double[] intrinsics,
            Pose3d headsetToCamera,
            AprilTagSceneTag[] tags
        )
        {
            CapturedAtMs = capturedAtMs;
            Width = width;
            Height = height;
            Intrinsics = intrinsics;
            HeadsetToCamera = headsetToCamera;
            Tags = tags;
        }
    }

    /// <summary>How the field solve treated a detection.</summary>
    public enum AprilTagSceneTagStatus
    {
        /// <summary>Passed the filters and was given to the field solve.</summary>
        Used,

        /// <summary>Dropped by the user's ignored-ID list.</summary>
        Ignored,

        /// <summary>Not in the loaded field layout.</summary>
        Unknown,
    }

    public sealed class AprilTagSceneTag
    {
        public int Id { get; }
        public AprilTagSceneTagStatus Status { get; }

        /// <summary>Single-tag pose in the camera frame, or null if the planar solve failed.</summary>
        public Pose3d CameraToTag { get; }

        public double Ambiguity { get; }
        public double ReprojectionErrorPixels { get; }

        public AprilTagSceneTag(
            int id,
            AprilTagSceneTagStatus status,
            Pose3d cameraToTag,
            double ambiguity,
            double reprojectionErrorPixels
        )
        {
            Id = id;
            Status = status;
            CameraToTag = cameraToTag;
            Ambiguity = ambiguity;
            ReprojectionErrorPixels = reprojectionErrorPixels;
        }
    }
}
