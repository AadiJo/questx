using System;
using QuestX.QuestX.Geometry;

namespace QuestX.Network
{
    /// <summary>Limelight-compatible pose ordering, with documented unsupported metrics.</summary>
    public static class LimelightPoseFormat
    {
        public const double RadiansToDegrees = 180.0 / Math.PI;
        public const long ValidAgeMicros = 250_000;

        public static double[] Encode(Pose3d pose, double latencyMs, int tags, double distance)
        {
            return new[] {
                pose.X, pose.Y, pose.Z,
                pose.Rotation.X * RadiansToDegrees,
                pose.Rotation.Y * RadiansToDegrees,
                pose.Rotation.Z * RadiansToDegrees,
                latencyMs, (double)tags,
                0.0, distance, 0.0
            };
        }

        public static bool IsFresh(long now, long capture) =>
            capture > 0 && now >= capture && now - capture <= ValidAgeMicros;
    }
}
