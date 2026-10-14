using QuestX.Native.NTCore;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;

namespace QuestX.Network
{
    /// <summary>
    /// A small compatibility view. The complete v2 samples remain the fusion interface.
    /// Botpose assumes the headset is at robot center with matching axes. Use the robot
    /// mounting settings for other installations, and do not apply that offset twice.
    /// </summary>
    internal sealed class LimelightTelemetry
    {
        private const string Root = "/QuestX/limelight/";
        private readonly DoubleArrayPublisher botpose, headsetpose, stddevs, ids, detectedIds;
        private readonly DoublePublisher tv, tid, hb, latency, age, layoutTags;
        private readonly StringPublisher frame, layout, status, reason;
        private readonly BooleanPublisher layoutLoaded;
        private long captureMicros;
        private int firstId = -1;
        private long heartbeat;

        public LimelightTelemetry(NtInstance nt)
        {
            var options = PubSubOptions.AllDefault;
            options.Periodic = 0.02;
            botpose = nt.GetDoubleArrayPublisher(Root + "botpose_wpiblue", options);
            headsetpose = nt.GetDoubleArrayPublisher(Root + "headsetpose_wpiblue", options);
            stddevs = nt.GetDoubleArrayPublisher(Root + "stddevs", options);
            ids = nt.GetDoubleArrayPublisher(Root + "tagIds", options);
            detectedIds = nt.GetDoubleArrayPublisher(Root + "detectedIds", options);
            tv = nt.GetDoublePublisher(Root + "tv", options);
            tid = nt.GetDoublePublisher(Root + "tid", options);
            hb = nt.GetDoublePublisher(Root + "hb", options);
            latency = nt.GetDoublePublisher(Root + "latencyMilliseconds", options);
            age = nt.GetDoublePublisher(Root + "ageMilliseconds", options);
            layoutTags = nt.GetDoublePublisher(Root + "layoutTagCount", options);
            layoutLoaded = nt.GetBooleanPublisher(Root + "layoutLoaded", options);
            frame = nt.GetStringPublisher(Root + "poseReference", options);
            layout = nt.GetStringPublisher(Root + "fieldLayout", options);
            status = nt.GetStringPublisher(Root + "status", options);
            reason = nt.GetStringPublisher(Root + "reason", options);
            frame.Set("WPILib blue origin, headset center. botpose assumes zero robot mount offset.");
            tv.Set(0);
            tid.Set(-1);
            age.Set(-1);
        }

        public void Update(Pose3d pose, double[] sample, long capture, long now)
        {
            captureMicros = capture;
            int count = (int)sample[12];
            var tagIds = new double[count];
            System.Array.Copy(sample, 23, tagIds, 0, count);
            firstId = count > 0 ? (int)tagIds[0] : -1;
            double latencyMs = System.Math.Max(0, (now - capture) / 1000.0);
            var value = LimelightPoseFormat.Encode(pose, latencyMs, count, sample[16]);
            // Like Limelight, these compatibility topics carry PUBLICATION timestamps.
            // Subtract value[6] from the synchronized NT publication time once. The v2
            // topics instead carry CAPTURE timestamps and must not subtract it again.
            botpose.Set(value, now);
            headsetpose.Set(value, now);
            latency.Set(latencyMs);
            ids.Set(tagIds);
            stddevs.Set(new[] {
                sample[17], sample[18], sample[19],
                sample[20] * LimelightPoseFormat.RadiansToDegrees,
                sample[21] * LimelightPoseFormat.RadiansToDegrees,
                sample[22] * LimelightPoseFormat.RadiansToDegrees
            });
        }

        public void Publish(long now)
        {
            bool fresh = firstId >= 0 && LimelightPoseFormat.IsFresh(now, captureMicros);
            tv.Set(fresh ? 1 : 0);
            tid.Set(fresh ? firstId : -1);
            age.Set(captureMicros > 0 ? (now - captureMicros) / 1000.0 : -1);
            hb.Set(++heartbeat);
        }

        public void Diagnostics(AprilTagDiagnosticsSnapshot snapshot)
        {
            layout.Set(snapshot.FieldLayoutFile);
            layoutLoaded.Set(snapshot.LayoutLoaded);
            layoutTags.Set(snapshot.LayoutTagCount);
            status.Set(snapshot.LastOutcome);
            reason.Set(snapshot.LastReason);
            var values = new double[snapshot.DetectedIds.Length];
            for (int i = 0; i < values.Length; i++) values[i] = snapshot.DetectedIds[i];
            detectedIds.Set(values);
        }
    }
}
