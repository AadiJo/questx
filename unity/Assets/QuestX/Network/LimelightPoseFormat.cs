using System;
using System.Collections.Generic;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;

namespace QuestX.Network
{
    /// <summary>Limelight-compatible pose ordering, with documented unsupported metrics.</summary>
    public static class LimelightPoseFormat
    {
        public const double RadiansToDegrees = 180.0 / Math.PI;
        public const long ValidAgeMicros = 250_000;

        /// <summary>Limelight raw fiducial: id, txnc, tync, ta, distToCamera, distToRobot, ambiguity.</summary>
        public const int ValuesPerFiducial = 7;

        /// <summary>Pose, latency, tag count, tag span, distance, and area precede the fiducials.</summary>
        public const int BotposeHeaderLength = 11;

        /// <summary>
        /// Encodes a botpose array. Like Limelight, raw fiducials follow the 11 header values,
        /// which lets LimelightHelpers populate <c>PoseEstimate.rawFiducials</c> (and ambiguity).
        /// Tag span is not computed and is always 0.
        /// </summary>
        public static double[] Encode(
            Pose3d pose,
            double latencyMs,
            double distance,
            IReadOnlyList<AprilTagObservation> tags
        )
        {
            int count = tags.Count;
            double averageArea = 0;
            foreach (var tag in tags)
            {
                averageArea += tag.AreaPercent / count;
            }
            var value = new double[BotposeHeaderLength + ValuesPerFiducial * count];
            value[0] = pose.X;
            value[1] = pose.Y;
            value[2] = pose.Z;
            value[3] = pose.Rotation.X * RadiansToDegrees;
            value[4] = pose.Rotation.Y * RadiansToDegrees;
            value[5] = pose.Rotation.Z * RadiansToDegrees;
            value[6] = latencyMs;
            value[7] = count;
            value[8] = 0.0;
            value[9] = distance;
            value[10] = averageArea;
            WriteRawFiducials(value, BotposeHeaderLength, tags);
            return value;
        }

        /// <summary>Encodes Limelight's standalone <c>rawfiducials</c> array.</summary>
        public static double[] RawFiducials(IReadOnlyList<AprilTagObservation> tags)
        {
            var value = new double[ValuesPerFiducial * tags.Count];
            WriteRawFiducials(value, 0, tags);
            return value;
        }

        private static void WriteRawFiducials(
            double[] target,
            int offset,
            IReadOnlyList<AprilTagObservation> tags
        )
        {
            for (int i = 0; i < tags.Count; i++)
            {
                var tag = tags[i];
                int index = offset + i * ValuesPerFiducial;
                target[index] = tag.Id;
                target[index + 1] = tag.TxDegrees;
                target[index + 2] = tag.TyDegrees;
                target[index + 3] = tag.AreaPercent;
                target[index + 4] = tag.CameraDistanceMeters;
                // Botpose treats the headset as the robot center (zero mount offset).
                target[index + 5] = tag.HeadsetDistanceMeters;
                target[index + 6] = tag.Ambiguity;
            }
        }

        public static bool IsFresh(long now, long capture) =>
            capture > 0 && now >= capture && now - capture <= ValidAgeMicros;
    }
}
