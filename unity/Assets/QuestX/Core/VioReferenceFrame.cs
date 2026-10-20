using QuestX.QuestX.Geometry;

namespace QuestX.Core
{
    /// <summary>
    /// Owns the frame that VIO is published in and its tracking epoch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published VIO is the headset's local tracking pose, re-based by the most recent pose reset.
    /// A reset rotates the frame only about the vertical axis and then translates it, so x, y, z,
    /// and yaw match the requested pose while roll and pitch stay gravity-referenced measurements.
    /// </para>
    /// <para>
    /// Every frame change increments the epoch, and the first sample published in the new frame
    /// carries the new epoch. Consumers that integrate VIO deltas drop the one interval whose ends
    /// have different epochs, so a reset or recenter never appears as motion.
    /// </para>
    /// <para>
    /// A headset recenter is announced before the runtime moves the tracking origin, and the move
    /// can land a frame or more later. For a settle window after the announcement, samples carry
    /// their own epoch, are marked not tracking, and repeat the last pose published before the
    /// announcement. Live samples then resume in another new epoch. Every epoch therefore holds
    /// poses from a single origin, and the stream keeps its rate (consumers use VIO age as a
    /// connection heartbeat).
    /// </para>
    /// </remarks>
    public sealed class VioReferenceFrame
    {
        private readonly double settleSeconds;
        private Rotation3d yawCorrection = Rotation3d.Zero;
        private Translation3d offset = Translation3d.Zero;
        private bool settling;
        private double settleUntil = double.NegativeInfinity;
        private Pose3d heldPose;
        private Pose3d latestLocalPose;
        private bool latestTracking;

        /// <param name="settleSeconds">How long samples are held after a recenter announcement</param>
        public VioReferenceFrame(double settleSeconds)
        {
            this.settleSeconds = settleSeconds;
        }

        /// <summary>Identifier of the continuous frame that published samples are expressed in.</summary>
        public uint Epoch { get; private set; }

        /// <summary>True while a recenter may still be moving the local tracking origin.</summary>
        public bool IsSettling => settling;

        /// <summary>The pose to publish for the newest sample, or null before the first sample.</summary>
        public Pose3d PublishedPose
        {
            get
            {
                if (settling && heldPose != null)
                {
                    return heldPose;
                }
                return latestLocalPose == null ? null : Apply(latestLocalPose);
            }
        }

        /// <summary>Tracking flag to publish: false while a recenter settles.</summary>
        public bool PublishedTracking => latestTracking && !settling;

        /// <summary>Records the newest local-frame headset pose.</summary>
        /// <param name="localPose">Center-eye pose in the headset's local tracking frame</param>
        /// <param name="isTracking">Whether the headset reports valid positional tracking</param>
        /// <param name="now">Monotonic time in seconds, on the same clock as recenter announcements</param>
        public void Sample(Pose3d localPose, bool isTracking, double now)
        {
            latestLocalPose = localPose;
            latestTracking = isTracking;
            if (settling && !(now < settleUntil))
            {
                // Live samples resume in a fresh epoch, separate from the held samples.
                settling = false;
                heldPose = null;
                Epoch++;
            }
        }

        /// <summary>Yaw (radians) that rotates the local frame into the published frame.</summary>
        public double YawCorrectionRadians => yawCorrection.Z;

        /// <summary>Translation added after the yaw correction to reach the published frame.</summary>
        public Translation3d Offset => offset;

        /// <summary>Expresses a local-frame pose in the published frame.</summary>
        public Pose3d Apply(Pose3d localPose) =>
            new Pose3d(
                localPose.Translation.RotateBy(yawCorrection).Plus(offset),
                localPose.Rotation.RotateBy(yawCorrection)
            );

        /// <summary>
        /// Handles an announced change of the local tracking origin (for example a recenter).
        /// Repeated announcements inside one settle window describe the same change; they extend
        /// the window without starting another epoch.
        /// </summary>
        public void BeginOriginChange(double now)
        {
            if (!settling)
            {
                heldPose = PublishedPose;
                settling = true;
                Epoch++;
            }
            settleUntil = now + settleSeconds;
        }

        /// <summary>
        /// Starts a new epoch at the next published sample because something downstream of the
        /// frame changed what samples report (for example the robot-to-Quest transform). The
        /// current reset is kept.
        /// </summary>
        public void BeginNewEpoch()
        {
            Epoch++;
        }

        /// <summary>
        /// Re-bases the published frame so the newest sample reports <paramref name="target"/>'s
        /// position and yaw, and starts a new epoch at the next published sample.
        /// </summary>
        /// <returns>False, with a reason, when the newest sample cannot anchor a reset.</returns>
        public bool TryReset(Pose3d target, out string reason)
        {
            if (latestLocalPose == null)
            {
                reason = "No VIO sample has been captured yet";
                return false;
            }
            if (settling)
            {
                reason = "Headset tracking origin is changing; retry the reset after it settles";
                return false;
            }
            if (!latestTracking)
            {
                reason = "Headset is not tracking; VIO frame was not reset";
                return false;
            }

            yawCorrection = new Rotation3d(0, 0, target.Rotation.Z - latestLocalPose.Rotation.Z);
            offset = target.Translation.Minus(latestLocalPose.Translation.RotateBy(yawCorrection));
            Epoch++;
            reason = "";
            return true;
        }
    }
}
