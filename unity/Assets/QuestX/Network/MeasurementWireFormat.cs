using QuestX.QuestX.Geometry;

namespace QuestX.Network
{
    /// <summary>Version 2 atomic double[] measurements. See protos/measurement-v2.md.</summary>
    public static class MeasurementWireFormat
    {
        public const int VioLength = 14;
        public const int AprilTagHeaderLength = 23;

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
            WriteCommon(target, sequence, captureMicros, appTimestamp, pose);
            int count = tagIds?.Length ?? 0;
            target[12] = count;
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
            for (int i = 0; i < count; i++)
            {
                target[AprilTagHeaderLength + i] = tagIds[i];
            }
        }
    }
}
