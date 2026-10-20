using System;
using System.Collections.Generic;
using QuestX.QuestX.Geometry;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>
    /// Pose of one tag in the camera frame, solved from that tag's four corners alone. It needs
    /// no field layout, so tags that are ignored, unknown, or rejected by the field solve still
    /// get a pose for the web UI's headset view.
    /// </summary>
    /// <remarks>
    /// This class has no Unity dependencies so it can be checked outside the editor.
    /// </remarks>
    public static class AprilTagCameraPose
    {
        /// <summary>A tag pose in the camera frame and the quality of its planar solve.</summary>
        public readonly struct Result
        {
            /// <summary>
            /// Tag pose in WPILib camera axes (X forward along the optical axis, Y left, Z up),
            /// with the tag in its WPILib frame (+X out of the printed face).
            /// </summary>
            public Pose3d CameraToTag { get; }

            /// <summary>Lower IPPE reprojection error over the higher one, from 0 to 1.</summary>
            public double Ambiguity { get; }

            /// <summary>RMS pixel error of the chosen solution.</summary>
            public double ReprojectionErrorPixels { get; }

            public Result(Pose3d cameraToTag, double ambiguity, double reprojectionErrorPixels)
            {
                CameraToTag = cameraToTag;
                Ambiguity = ambiguity;
                ReprojectionErrorPixels = reprojectionErrorPixels;
            }
        }

        /// <summary>
        /// Corner offsets in the WPILib tag frame (+X out of the printed face, +Y left, +Z up),
        /// in the order the detector reports corners and the solvers consume them. The Quest
        /// passthrough image is mirrored, so that order is clockwise: bottom-right,
        /// bottom-left, upper-left, upper-right.
        /// </summary>
        public static Translation3d[] TagLocalCorners(double tagSize)
        {
            double h = tagSize / 2.0;
            return new[]
            {
                new Translation3d(0, -h, -h),
                new Translation3d(0, h, -h),
                new Translation3d(0, h, h),
                new Translation3d(0, -h, h),
            };
        }

        /// <summary>
        /// Solves the tag pose from pixel corners [x0, y0, ... x3, y3] in
        /// <see cref="TagLocalCorners"/> order.
        /// </summary>
        /// <param name="corners2d">Eight pixel coordinates, top-left image origin</param>
        /// <param name="tagSize">Edge length of the tag's black square in meters</param>
        /// <param name="intrinsics">Pinhole intrinsics [fx, fy, cx, cy] in pixels</param>
        public static bool TrySolve(
            double[] corners2d,
            double tagSize,
            IReadOnlyList<double> intrinsics,
            out Result result
        )
        {
            result = default;
            if (
                corners2d == null
                || corners2d.Length != 8
                || intrinsics == null
                || intrinsics.Count < 4
            )
            {
                return false;
            }

            var points3d = new double[12];
            var corners = TagLocalCorners(tagSize);
            for (int i = 0; i < corners.Length; i++)
            {
                points3d[i * 3] = corners[i].X;
                points3d[i * 3 + 1] = corners[i].Y;
                points3d[i * 3 + 2] = corners[i].Z;
            }
            var pinhole = new[] { intrinsics[0], intrinsics[1], intrinsics[2], intrinsics[3] };

            if (
                !AprilTagAmbiguity.TrySolvePlanar(
                    corners2d,
                    points3d,
                    0,
                    4,
                    pinhole,
                    out var solutions
                )
            )
            {
                return false;
            }
            double errorA = AprilTagAmbiguity.RmsError(
                solutions[0].Rotation,
                solutions[0].Translation,
                corners2d,
                points3d,
                0,
                4,
                pinhole
            );
            double errorB = AprilTagAmbiguity.RmsError(
                solutions[1].Rotation,
                solutions[1].Translation,
                corners2d,
                points3d,
                0,
                4,
                pinhole
            );
            var best = errorA <= errorB ? solutions[0] : solutions[1];
            double bestError = Math.Min(errorA, errorB);
            if (!double.IsFinite(bestError))
            {
                return false;
            }

            // The solution maps tag-frame points into OpenCV camera axes (x right, y down,
            // z forward). WPILib camera axes are X = z, Y = -x, Z = -y.
            double[] r = best.Rotation;
            double[] t = best.Translation;
            double[,] rotation =
            {
                { r[6], r[7], r[8] },
                { -r[0], -r[1], -r[2] },
                { -r[3], -r[4], -r[5] },
            };
            result = new Result(
                new Pose3d(new Translation3d(t[2], -t[0], -t[1]), FromMatrix(rotation)),
                AprilTagAmbiguity.Ratio(bestError, Math.Max(errorA, errorB)),
                bestError
            );
            return true;
        }

        /// <summary>
        /// Rotation of a numerically orthonormal matrix. Converting through a normalized
        /// quaternion tolerates the rounding that the strict matrix constructor rejects.
        /// </summary>
        private static Rotation3d FromMatrix(double[,] m)
        {
            double w,
                x,
                y,
                z;
            double trace = m[0, 0] + m[1, 1] + m[2, 2];
            if (trace > 0)
            {
                double s = 0.5 / Math.Sqrt(trace + 1.0);
                w = 0.25 / s;
                x = (m[2, 1] - m[1, 2]) * s;
                y = (m[0, 2] - m[2, 0]) * s;
                z = (m[1, 0] - m[0, 1]) * s;
            }
            else if (m[0, 0] > m[1, 1] && m[0, 0] > m[2, 2])
            {
                double s = 2.0 * Math.Sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]);
                w = (m[2, 1] - m[1, 2]) / s;
                x = 0.25 * s;
                y = (m[0, 1] + m[1, 0]) / s;
                z = (m[0, 2] + m[2, 0]) / s;
            }
            else if (m[1, 1] > m[2, 2])
            {
                double s = 2.0 * Math.Sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]);
                w = (m[0, 2] - m[2, 0]) / s;
                x = (m[0, 1] + m[1, 0]) / s;
                y = 0.25 * s;
                z = (m[1, 2] + m[2, 1]) / s;
            }
            else
            {
                double s = 2.0 * Math.Sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]);
                w = (m[1, 0] - m[0, 1]) / s;
                x = (m[0, 2] + m[2, 0]) / s;
                y = (m[1, 2] + m[2, 1]) / s;
                z = 0.25 * s;
            }
            double norm = Math.Sqrt(w * w + x * x + y * y + z * z);
            return new Rotation3d(new Quaternion(w / norm, x / norm, y / norm, z / norm));
        }
    }
}
