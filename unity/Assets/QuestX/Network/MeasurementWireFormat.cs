using System;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;
using QuestX.VioQuality;

namespace QuestX.Network
{
    /// <summary>
    /// Atomic double[] measurements. See protos/measurement-v2.md, protos/measurement-v3.md, and
    /// protos/measurement-v4.md.
    /// A topic's version fixes its layout; incompatible changes get a new topic version.
    /// </summary>
    public static class MeasurementWireFormat
    {
        public const int VioLength = 14;
        public const int AprilTagHeaderLength = 23;
        public const int VioQualityLength = 25;

        /// <summary>
        /// v3 AprilTag arrays repeat the v2 fields at indices 0-22, then add ambiguity (23),
        /// reprojection error in pixels (24), and processing latency in milliseconds (25). Tag IDs
        /// follow, then one ambiguity per tag: 26 + 2 * tagCount doubles in total.
        /// </summary>
        public const int AprilTagV3HeaderLength = 26;

        /// <summary>
        /// v4 AprilTag arrays repeat the v3 fields at indices 0-25, then the 21 upper-triangle
        /// entries of the published pose's 6x6 covariance, row by row (26-46), and the six
        /// upper-triangle entries of its field x/y/heading covariance (47-52). An all-zero planar
        /// block means heading is undefined. Tag IDs follow, then one ambiguity per tag.
        /// </summary>
        public const int AprilTagV4HeaderLength = 53;

        /// <summary>Upper-triangle entries of a 6x6 covariance.</summary>
        public const int CovarianceLength = 21;

        public static void WriteCommon(
            double[] target,
            ulong sequence,
            long captureMicros,
            double appTimestamp,
            Pose3d pose
        )
        {
            // Split 64-bit fields into unsigned 32-bit words; doubles represent each word exactly.
            target[0] = (uint)sequence;
            target[1] = (uint)(sequence >> 32);
            target[2] = (uint)captureMicros;
            target[3] = (uint)((ulong)captureMicros >> 32);
            target[4] = appTimestamp;
            target[5] = pose.X;
            target[6] = pose.Y;
            target[7] = pose.Z;
            var q = pose.Rotation.Quaternion;
            target[8] = q.W;
            target[9] = q.X;
            target[10] = q.Y;
            target[11] = q.Z;
        }

        public static void WriteVio(
            double[] target,
            ulong sequence,
            long captureMicros,
            double appTimestamp,
            Pose3d pose,
            bool tracking,
            uint epoch
        )
        {
            WriteCommon(target, sequence, captureMicros, appTimestamp, pose);
            target[12] = tracking ? 1 : 0;
            target[13] = epoch;
        }

        public static void WriteAprilTag(
            double[] target,
            ulong sequence,
            long captureMicros,
            double appTimestamp,
            Pose3d pose,
            int[] tagIds,
            uint inliers,
            uint corners,
            double inlierRatio,
            double averageDistance,
            double xStdDev,
            double yStdDev,
            double zStdDev,
            double rollStdDev,
            double pitchStdDev,
            double yawStdDev
        )
        {
            WriteAprilTagHeader(
                target,
                sequence,
                captureMicros,
                appTimestamp,
                pose,
                tagIds,
                inliers,
                corners,
                inlierRatio,
                averageDistance,
                xStdDev,
                yStdDev,
                zStdDev,
                rollStdDev,
                pitchStdDev,
                yawStdDev
            );
            int count = tagIds?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                target[AprilTagHeaderLength + i] = tagIds[i];
            }
        }

        public static void WriteAprilTagV3(
            double[] target,
            ulong sequence,
            long captureMicros,
            double appTimestamp,
            Pose3d pose,
            int[] tagIds,
            uint inliers,
            uint corners,
            double inlierRatio,
            double averageDistance,
            double xStdDev,
            double yStdDev,
            double zStdDev,
            double rollStdDev,
            double pitchStdDev,
            double yawStdDev,
            double ambiguity,
            double reprojectionErrorPixels,
            double processingLatencyMilliseconds,
            double[] tagAmbiguities
        )
        {
            WriteAprilTagHeader(
                target,
                sequence,
                captureMicros,
                appTimestamp,
                pose,
                tagIds,
                inliers,
                corners,
                inlierRatio,
                averageDistance,
                xStdDev,
                yStdDev,
                zStdDev,
                rollStdDev,
                pitchStdDev,
                yawStdDev
            );
            target[23] = ambiguity;
            target[24] = reprojectionErrorPixels;
            target[25] = processingLatencyMilliseconds;
            WriteTags(target, AprilTagV3HeaderLength, tagIds, tagAmbiguities);
        }

        /// <summary>
        /// Writes a v4 array: the v3 header with <paramref name="covariance"/>'s upper triangle at
        /// 26-46 and its planar covariance at 47-52, then the tags. The standard deviations at
        /// 17-22 come from it too.
        /// </summary>
        public static void WriteAprilTagV4(
            double[] target,
            ulong sequence,
            long captureMicros,
            double appTimestamp,
            Pose3d pose,
            PoseCovariance covariance,
            int[] tagIds,
            uint inliers,
            uint corners,
            double inlierRatio,
            double averageDistance,
            double ambiguity,
            double reprojectionErrorPixels,
            double processingLatencyMilliseconds,
            double[] tagAmbiguities
        )
        {
            double[] stdDevs = covariance.EstimatorStdDevs(out double[] planar);
            WriteAprilTagHeader(
                target,
                sequence,
                captureMicros,
                appTimestamp,
                pose,
                tagIds,
                inliers,
                corners,
                inlierRatio,
                averageDistance,
                stdDevs[0],
                stdDevs[1],
                stdDevs[2],
                stdDevs[3],
                stdDevs[4],
                stdDevs[5]
            );
            target[23] = ambiguity;
            target[24] = reprojectionErrorPixels;
            target[25] = processingLatencyMilliseconds;
            Array.Copy(
                covariance.UpperTriangle(),
                0,
                target,
                AprilTagV3HeaderLength,
                CovarianceLength
            );
            if (planar != null)
            {
                int index = AprilTagV3HeaderLength + CovarianceLength;
                for (int r = 0; r < 3; r++)
                {
                    for (int c = r; c < 3; c++)
                    {
                        target[index++] = planar[r * 3 + c];
                    }
                }
            }
            else
            {
                // The publisher reuses its array: clear any previous valid projection.
                Array.Clear(target, AprilTagV3HeaderLength + CovarianceLength, 6);
            }
            WriteTags(target, AprilTagV4HeaderLength, tagIds, tagAmbiguities);
        }

        /// <summary>Tag IDs from <paramref name="offset"/>, then one ambiguity per tag.</summary>
        private static void WriteTags(
            double[] target,
            int offset,
            int[] tagIds,
            double[] tagAmbiguities
        )
        {
            int count = tagIds?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                target[offset + i] = tagIds[i];
                target[offset + count + i] = tagAmbiguities[i];
            }
        }

        private static void WriteAprilTagHeader(
            double[] target,
            ulong sequence,
            long captureMicros,
            double appTimestamp,
            Pose3d pose,
            int[] tagIds,
            uint inliers,
            uint corners,
            double inlierRatio,
            double averageDistance,
            double xStdDev,
            double yStdDev,
            double zStdDev,
            double rollStdDev,
            double pitchStdDev,
            double yawStdDev
        )
        {
            WriteCommon(target, sequence, captureMicros, appTimestamp, pose);
            target[12] = tagIds?.Length ?? 0;
            target[13] = inliers;
            target[14] = corners;
            target[15] = inlierRatio;
            target[16] = averageDistance;
            target[17] = xStdDev;
            target[18] = yStdDev;
            target[19] = zStdDev;
            target[20] = rollStdDev;
            target[21] = pitchStdDev;
            target[22] = yawStdDev;
        }

        /// <summary>
        /// /v2/vioQuality: [0-1] sequence words, [2-3] capture micros words, [4] app time s, [5] status
        /// (0 ok, 1 not supported, 2 not ready, 3 not tracking, 4 too few hits, 5 new keyframe, 6 too few matches),
        /// [6] tracking epoch, [7] position tracked, [8] position valid, [9] orientation tracked,
        /// [10] head angular speed rad/s, [11] head linear speed m/s, [12] keyframe baseline s, [13] rays, [14] hits,
        /// [15] near-occlusion fraction, [16] matched, [17] inlier ratio, [18-20] drift X/Y/Z m,
        /// [21-23] drift standard deviation X/Y/Z m, [24] residual RMS m.
        /// Drift is the VIO displacement error accumulated since the keyframe, in the published VIO frame (FRC axes,
        /// same frame as /v2/vio); fields 12 and 16-24 are meaningful only when status is 0.
        /// </summary>
        public static void WriteVioQuality(
            double[] target,
            ulong sequence,
            long captureMicros,
            double appTimestamp,
            VioQualitySample q
        )
        {
            target[0] = (uint)sequence;
            target[1] = (uint)(sequence >> 32);
            target[2] = (uint)captureMicros;
            target[3] = (uint)((ulong)captureMicros >> 32);
            target[4] = appTimestamp;
            var d = q.Drift;
            target[5] = (int)d.Status;
            target[6] = q.TrackingEpoch;
            target[7] = q.PositionTracked ? 1 : 0;
            target[8] = q.PositionValid ? 1 : 0;
            target[9] = q.OrientationTracked ? 1 : 0;
            target[10] = q.AngularSpeedRadPerSec;
            target[11] = q.LinearSpeedMetersPerSec;
            target[12] = d.BaselineSeconds;
            target[13] = q.Rays;
            target[14] = d.Hits;
            target[15] = q.NearFraction;
            target[16] = d.Matched;
            target[17] = d.InlierRatio;
            target[18] = d.Drift.X;
            target[19] = d.Drift.Y;
            target[20] = d.Drift.Z;
            target[21] = d.DriftStdDev.X;
            target[22] = d.DriftStdDev.Y;
            target[23] = d.DriftStdDev.Z;
            target[24] = d.ResidualRms;
        }
    }
}
