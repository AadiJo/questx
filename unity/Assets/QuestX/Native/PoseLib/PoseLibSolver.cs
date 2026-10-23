using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Meta.XR;
using QuestX.Camera;
using QuestX.Native.AprilTag;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Native.PoseLib;
using QuestX.Utils;
using UnityEngine;

namespace QuestX.Native.PoseLib
{
    public class PoseLibSolver
    {
        /// <summary>
        /// RANSAC reprojection-error threshold, tuned for the Quest 3 / 3S 1280-wide passthrough
        /// frames. Quality, covariance, and published observations share a complete-tag selection
        /// using this threshold at the native solved pose.
        /// </summary>
        private const double RansacThresholdPixels = 12;

        // Cached camera intrinsics. These MUST be refreshed via RefreshIntrinsics()
        // whenever the camera resolution changes (the Meta SDK exposes intrinsics
        // per-resolution: focal length, principal point, and sensor resolution all
        // change when RequestedResolution changes). Solving with stale intrinsics
        // produces a pose that's silently wrong by 5-50 cm.
        private double[] intrinsicsArray;
        private int resolutionX;
        private int resolutionY;
        private readonly AprilTagFieldLayout fieldLayout;

        public PoseLibSolver(
            AprilTagFieldLayout fieldLayout,
            PassthroughCameraAccess.CameraIntrinsics intrinsics
        )
        {
            this.fieldLayout = fieldLayout;
            RefreshIntrinsics(intrinsics);
        }

        /// <summary>
        /// Pinhole intrinsics used by the most recent solve: [fx, fy, cx, cy] in pixels.
        /// </summary>
        public IReadOnlyList<double> Intrinsics => intrinsicsArray;

        /// <summary>
        /// Updates the cached camera intrinsics. Called from
        /// <c>AprilTagManager.OnAprilTagDetectorModeChanged</c> after the camera arbiter
        /// has applied a new resolution. The next solve uses the refreshed values.
        /// </summary>
        public void RefreshIntrinsics(PassthroughCameraAccess.CameraIntrinsics intrinsics)
        {
            intrinsicsArray = new double[]
            {
                intrinsics.FocalLength.x,
                intrinsics.FocalLength.y,
                intrinsics.PrincipalPoint.x,
                intrinsics.PrincipalPoint.y,
            };
            resolutionX = intrinsics.SensorResolution.x;
            resolutionY = intrinsics.SensorResolution.y;
        }

        public PoseLibResult PoseLibSolve(AprilTagDetectionResults detections)
        {
            // Materialize the collection so the IReadOnlyList overload can iterate twice
            // (once to count and once to fill the buffers).
            var list = new List<TagDetection>(detections.NumberOfDetections);
            foreach (var detection in detections)
            {
                list.Add(TagDetection.From(detection));
            }
            return PoseLibSolve(list);
        }

        /// <summary>
        /// Overload that accepts a pre-filtered list of detections from either detector
        /// backend. Used by <c>AprilTagManager.AprilTagFrameCaptureCoroutine</c> after it
        /// drops ignored and unknown tag IDs.
        /// </summary>
        public PoseLibResult PoseLibSolve(IReadOnlyList<TagDetection> detections)
        {
            int count = detections?.Count ?? 0;
            if (count == 0)
            {
                return null;
            }

            var points2d = new double[count * 8];
            var points3d = new double[count * 12];

            for (int i = 0; i < count; i++)
            {
                var detection = detections[i];
                int offset = i * 8;
                points2d[offset] = detection.CornerBottomRight0.x;
                points2d[offset + 1] = detection.CornerBottomRight0.y;

                points2d[offset + 2] = detection.CornerBottomLeft1.x;
                points2d[offset + 3] = detection.CornerBottomLeft1.y;

                points2d[offset + 4] = detection.CornerUpperLeft2.x;
                points2d[offset + 5] = detection.CornerUpperLeft2.y;

                points2d[offset + 6] = detection.CornerUpperRight3.x;
                points2d[offset + 7] = detection.CornerUpperRight3.y;

                // The manager filters unknown IDs. Also guard direct callers: native code
                // reads exactly count * 4 correspondences, so every tag needs four 3D corners.
                if (!fieldLayout.TryCopyTagCorners(detection.Id, points3d, i * 12))
                    return null;
            }

            // An earlier attempt to scale the RANSAC threshold with the readback length was
            // based on a misread of the Meta SDK's GetColors buffer, which is allocated 4x the
            // image. Frames now come from CameraFrameReadback, sized exactly to the image.
            int status = PoseLibNatives.poselib_estimate_absolute_pose_simple(
                points2d,
                points3d,
                (ulong)(count * 4),
                (int)PoseLibNatives.PoseLibCameraModelIdNative.POSELIB_CAMERA_PINHOLE,
                resolutionX,
                resolutionY,
                intrinsicsArray,
                4,
                RansacThresholdPixels,
                out var resultPose,
                out ulong resultInliers
            );

            if (status == 0)
            {
                double[] rotation;
                double[] translation;
                unsafe
                {
                    rotation = new[]
                    {
                        resultPose.Q[0],
                        resultPose.Q[1],
                        resultPose.Q[2],
                        resultPose.Q[3],
                    };
                    translation = new[] { resultPose.T[0], resultPose.T[1], resultPose.T[2] };
                }
                // Select once at the native pose. Rejected tags must not lower the aggregate
                // ambiguity, shrink covariance, or appear among published observations.
                var selection = AprilTagCovariance.SelectInliers(
                    points2d,
                    points3d,
                    count,
                    intrinsicsArray,
                    rotation,
                    translation,
                    RansacThresholdPixels
                );
                var quality =
                    selection.TagCount == 0
                        ? new AprilTagAmbiguity.Result(
                            1,
                            double.PositiveInfinity,
                            Array.Empty<double>()
                        )
                        : AprilTagAmbiguity.Evaluate(
                            selection.Points2d,
                            selection.Points3d,
                            selection.TagCount,
                            intrinsicsArray,
                            rotation,
                            translation
                        );
                PoseCovariance covariance;
                try
                {
                    covariance = AprilTagCovariance.Estimate(
                        selection,
                        intrinsicsArray,
                        rotation,
                        translation,
                        AprilTagCovariance.DefaultCornerNoisePixels
                    );
                }
                catch (ArgumentException ex)
                {
                    // Runs inside the capture coroutine, which an exception would stop. The
                    // measurement is rejected instead.
                    QueuedLogger.LogError($"AprilTag covariance failed: {ex.Message}");
                    covariance = null;
                }
                return new PoseLibResult(
                    resultPose,
                    resultInliers,
                    count * 4,
                    quality,
                    covariance,
                    selection.SourceTagIndices
                );
            }

            QueuedLogger.LogError($"PoseLib solve failed! Error code: {status}");
            return null;
        }
    }
}
