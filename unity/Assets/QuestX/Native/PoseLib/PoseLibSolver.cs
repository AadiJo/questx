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

            var corners2d = new List<double>(count * 8);
            var corners3d = new List<double>(count * 12);

            foreach (var detection in detections)
            {
                corners2d.Add(detection.CornerBottomRight0.x);
                corners2d.Add(detection.CornerBottomRight0.y);

                corners2d.Add(detection.CornerBottomLeft1.x);
                corners2d.Add(detection.CornerBottomLeft1.y);

                corners2d.Add(detection.CornerUpperLeft2.x);
                corners2d.Add(detection.CornerUpperLeft2.y);

                corners2d.Add(detection.CornerUpperRight3.x);
                corners2d.Add(detection.CornerUpperRight3.y);

                var corner3dTranslations = fieldLayout.GetTagCorners(detection.Id);
                foreach (var corner3d in corner3dTranslations)
                {
                    corners3d.Add(corner3d.X);
                    corners3d.Add(corner3d.Y);
                    corners3d.Add(corner3d.Z);
                }
            }

            double[] points2d = corners2d.ToArray();
            double[] points3d = corners3d.ToArray();

            // 12 px RANSAC reprojection-error threshold was tuned for the Quest 3 / 3S
            // 1280-wide passthrough frames. (An earlier attempt to scale this with
            // colors.Length was based on a misread of the Meta SDK's GetColors over-
            // allocation; see AprilTagManager comment in AprilTagFrameCaptureCoroutine.)
            int status = PoseLibNatives.poselib_estimate_absolute_pose_simple(
                points2d,
                points3d,
                (ulong)(count * 4),
                (int)PoseLibNatives.PoseLibCameraModelIdNative.POSELIB_CAMERA_PINHOLE,
                resolutionX,
                resolutionY,
                intrinsicsArray,
                4,
                12,
                out var resultPose,
                out ulong resultInliers
            );

            if (status == 0)
            {
                // PoseLib returns one pose. Re-solve each planar tag (and a coplanar group)
                // with IPPE so robot code receives the ambiguity it uses for rejection.
                AprilTagAmbiguity.Result quality;
                unsafe
                {
                    quality = AprilTagAmbiguity.Evaluate(
                        points2d,
                        points3d,
                        count,
                        intrinsicsArray,
                        new[] { resultPose.Q[0], resultPose.Q[1], resultPose.Q[2], resultPose.Q[3] },
                        new[] { resultPose.T[0], resultPose.T[1], resultPose.T[2] }
                    );
                }
                return new PoseLibResult(resultPose, resultInliers, count * 4, quality);
            }

            QueuedLogger.LogError($"PoseLib solve failed! Error code: {status}");
            return null;
        }
    }
}
