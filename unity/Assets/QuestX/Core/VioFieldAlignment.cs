using System;
using QuestX.QuestX.Geometry;

namespace QuestX.Core
{
    /// <summary>
    /// Display-only field pose for the web UI: the robot's VIO pose carried into the field frame
    /// by the most recent AprilTag measurement. Robot code fuses the streams itself and never
    /// sees this estimate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each measurement re-anchors VIO with a yaw rotation and a translation, as a pose reset
    /// does, so roll and pitch stay gravity-referenced VIO measurements. Before re-anchoring,
    /// the old anchor's prediction at the measurement's capture time is compared with the
    /// measurement. That difference is how far VIO drifted (plus measurement noise) since the
    /// previous tag solve.
    /// </para>
    /// <para>
    /// An anchor only applies to VIO samples from its own tracking epoch; after a reset or
    /// recenter there is no estimate until the next measurement. Recording and alignment run on
    /// Unity's main thread and the web server reads snapshots, so all access is locked.
    /// </para>
    /// </remarks>
    public sealed class VioFieldAlignment
    {
        /// <summary>Longest gap between VIO samples that may be interpolated across.</summary>
        public const double MaxSampleGapSeconds = 0.05;

        private readonly struct Sample
        {
            public readonly double Time;
            public readonly uint Epoch;
            public readonly bool Tracking;
            public readonly Pose3d Pose;

            public Sample(double time, uint epoch, bool tracking, Pose3d pose)
            {
                Time = time;
                Epoch = epoch;
                Tracking = tracking;
                Pose = pose;
            }
        }

        /// <summary>The latest anchored field pose and the most recent correction.</summary>
        public sealed class Snapshot
        {
            /// <summary>Field pose of the newest VIO sample, or null without a current anchor.</summary>
            public Pose3d Estimate { get; }

            /// <summary>App time of the measurement that set the anchor, or NaN if none.</summary>
            public double AnchorAppTimestamp { get; }

            /// <summary>Planar distance between prediction and measurement at the last alignment, or NaN.</summary>
            public double LastCorrectionMeters { get; }

            /// <summary>Signed yaw difference (measurement minus prediction) at the last alignment, or NaN.</summary>
            public double LastCorrectionYawRadians { get; }

            /// <summary>VIO time span the last correction accumulated over, or NaN.</summary>
            public double LastCorrectionIntervalSeconds { get; }

            public Snapshot(
                Pose3d estimate,
                double anchorAppTimestamp,
                double lastCorrectionMeters,
                double lastCorrectionYawRadians,
                double lastCorrectionIntervalSeconds
            )
            {
                Estimate = estimate;
                AnchorAppTimestamp = anchorAppTimestamp;
                LastCorrectionMeters = lastCorrectionMeters;
                LastCorrectionYawRadians = lastCorrectionYawRadians;
                LastCorrectionIntervalSeconds = lastCorrectionIntervalSeconds;
            }
        }

        private readonly object sync = new object();
        private readonly Sample[] history;
        private int count;
        private int next;

        private bool anchored;
        private uint anchorEpoch;
        private double anchorTime = double.NaN;
        private Rotation3d anchorYaw = Rotation3d.Zero;
        private Translation3d anchorOffset = Translation3d.Zero;
        private double correctionMeters = double.NaN;
        private double correctionYaw = double.NaN;
        private double correctionInterval = double.NaN;

        /// <param name="historyLength">VIO samples kept for looking up a measurement's capture time</param>
        public VioFieldAlignment(int historyLength = 256)
        {
            history = new Sample[Math.Max(2, historyLength)];
        }

        /// <summary>Records a published VIO sample (robot pose in the VIO frame).</summary>
        public void RecordVio(double appTime, uint epoch, bool tracking, Pose3d robotPose)
        {
            if (robotPose == null)
            {
                return;
            }
            lock (sync)
            {
                history[next] = new Sample(appTime, epoch, tracking, robotPose);
                next = (next + 1) % history.Length;
                count = Math.Min(count + 1, history.Length);
            }
        }

        /// <summary>
        /// Re-anchors VIO on a measured robot field pose captured at <paramref name="captureAppTime"/>.
        /// </summary>
        /// <returns>False, with a reason, when no tracking VIO sample brackets the capture time.</returns>
        public bool Align(double captureAppTime, Pose3d robotFieldPose, out string reason)
        {
            lock (sync)
            {
                if (!TryInterpolate(captureAppTime, out uint epoch, out Pose3d vio, out reason))
                {
                    return false;
                }

                if (anchored && anchorEpoch == epoch)
                {
                    var predicted = Apply(vio);
                    double dx = robotFieldPose.X - predicted.X;
                    double dy = robotFieldPose.Y - predicted.Y;
                    correctionMeters = Math.Sqrt(dx * dx + dy * dy);
                    correctionYaw = WrapAngle(robotFieldPose.Rotation.Z - predicted.Rotation.Z);
                    correctionInterval = captureAppTime - anchorTime;
                }
                else
                {
                    correctionMeters = double.NaN;
                    correctionYaw = double.NaN;
                    correctionInterval = double.NaN;
                }

                anchorYaw = new Rotation3d(0, 0, robotFieldPose.Rotation.Z - vio.Rotation.Z);
                anchorOffset = robotFieldPose.Translation.Minus(
                    vio.Translation.RotateBy(anchorYaw)
                );
                anchorEpoch = epoch;
                anchorTime = captureAppTime;
                anchored = true;
                reason = "";
                return true;
            }
        }

        /// <summary>Returns the current estimate and the last correction.</summary>
        public Snapshot GetSnapshot()
        {
            lock (sync)
            {
                Pose3d estimate = null;
                if (anchored && count > 0)
                {
                    var newest = history[(next - 1 + history.Length) % history.Length];
                    if (newest.Epoch == anchorEpoch)
                    {
                        estimate = Apply(newest.Pose);
                    }
                }
                return new Snapshot(
                    estimate,
                    anchored ? anchorTime : double.NaN,
                    correctionMeters,
                    correctionYaw,
                    correctionInterval
                );
            }
        }

        private Pose3d Apply(Pose3d vio) =>
            new Pose3d(
                vio.Translation.RotateBy(anchorYaw).Plus(anchorOffset),
                vio.Rotation.RotateBy(anchorYaw)
            );

        /// <summary>
        /// Interpolates the VIO pose at <paramref name="time"/> between the two samples around it.
        /// Both must be tracking, share an epoch, and be at most <see cref="MaxSampleGapSeconds"/>
        /// apart. A capture time just past the newest sample uses that sample.
        /// </summary>
        private bool TryInterpolate(double time, out uint epoch, out Pose3d pose, out string reason)
        {
            epoch = 0;
            pose = null;
            if (count == 0)
            {
                reason = "No VIO samples recorded";
                return false;
            }

            Sample later = history[(next - 1 + history.Length) % history.Length];
            if (time >= later.Time)
            {
                if (time - later.Time > MaxSampleGapSeconds || !later.Tracking)
                {
                    reason = "No tracking VIO sample at the measurement's capture time";
                    return false;
                }
                epoch = later.Epoch;
                pose = later.Pose;
                reason = "";
                return true;
            }

            for (int i = 1; i < count; i++)
            {
                Sample earlier = history[(next - 1 - i + 2 * history.Length) % history.Length];
                if (earlier.Time > time)
                {
                    later = earlier;
                    continue;
                }
                if (
                    later.Time - earlier.Time > MaxSampleGapSeconds
                    || earlier.Epoch != later.Epoch
                    || !earlier.Tracking
                    || !later.Tracking
                )
                {
                    reason = "No tracking VIO sample at the measurement's capture time";
                    return false;
                }
                double span = later.Time - earlier.Time;
                double t = span > 0 ? (time - earlier.Time) / span : 0;
                epoch = later.Epoch;
                pose = new Pose3d(
                    earlier.Pose.Translation.Plus(
                        later.Pose.Translation.Minus(earlier.Pose.Translation).Times(t)
                    ),
                    earlier.Pose.Rotation.Interpolate(later.Pose.Rotation, t)
                );
                reason = "";
                return true;
            }

            reason = "Measurement is older than the recorded VIO history";
            return false;
        }

        private static double WrapAngle(double radians) => Math.IEEERemainder(radians, 2 * Math.PI);
    }
}
