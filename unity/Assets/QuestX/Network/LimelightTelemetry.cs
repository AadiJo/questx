using QuestX.Native.NTCore;
using QuestX.QuestX.AprilTag;

namespace QuestX.Network
{
    /// <summary>
    /// A small compatibility view. The complete v2 samples remain the fusion interface.
    /// Botpose is the robot pose after the Quest-side robot-to-Quest transform (set in the
    /// web UI); headsetpose is the headset center eye. Do not apply the mount offset again.
    /// </summary>
    internal sealed class LimelightTelemetry
    {
        private const string Root = "/QuestX/limelight/";
        private readonly DoubleArrayPublisher botpose,
            headsetpose,
            rawFiducials,
            stddevs,
            ids,
            detectedIds;
        private readonly DoublePublisher tv,
            tid,
            hb,
            latency,
            age,
            layoutTags;
        private readonly StringPublisher frame,
            layout,
            status,
            reason;
        private readonly BooleanPublisher layoutLoaded;
        private long heartbeat;

        public LimelightTelemetry(NtInstance nt)
        {
            var options = PubSubOptions.AllDefault;
            options.Periodic = 0.02;
            botpose = nt.GetDoubleArrayPublisher(Root + "botpose_wpiblue", options);
            headsetpose = nt.GetDoubleArrayPublisher(Root + "headsetpose_wpiblue", options);
            rawFiducials = nt.GetDoubleArrayPublisher(Root + "rawfiducials", options);
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
            frame.Set(
                "WPILib blue origin. botpose: robot origin (Quest-side mount applied). headsetpose: headset center."
            );
            tv.Set(0);
            tid.Set(-1);
            age.Set(-1);
        }

        public void Update(AprilTagMeasurement measurement, long capture, long now)
        {
            var tags = measurement.Observations;
            var tagIds = new double[tags.Length];
            for (int i = 0; i < tags.Length; i++)
                tagIds[i] = tags[i].Id;
            double latencyMs = System.Math.Max(0, (now - capture) / 1000.0);
            var value = LimelightPoseFormat.Encode(
                measurement.Pose,
                latencyMs,
                measurement.AverageTagDistanceMeters,
                tags
            );
            var headsetValue = LimelightPoseFormat.Encode(
                measurement.HeadsetPose,
                latencyMs,
                measurement.AverageTagDistanceMeters,
                tags
            );
            // Like Limelight, these compatibility topics carry PUBLICATION timestamps.
            // Subtract value[6] from the synchronized NT publication time once. The v2
            // topics instead carry CAPTURE timestamps and must not subtract it again.
            botpose.Set(value, now);
            headsetpose.Set(headsetValue, now);
            rawFiducials.Set(LimelightPoseFormat.RawFiducials(tags), now);
            latency.Set(latencyMs);
            ids.Set(tagIds);
            stddevs.Set(
                new[]
                {
                    measurement.XStdDevMeters,
                    measurement.YStdDevMeters,
                    measurement.ZStdDevMeters,
                    measurement.RollStdDevRadians * LimelightPoseFormat.RadiansToDegrees,
                    measurement.PitchStdDevRadians * LimelightPoseFormat.RadiansToDegrees,
                    measurement.YawStdDevRadians * LimelightPoseFormat.RadiansToDegrees,
                }
            );
        }

        // Main-thread freshness comes from the same completed sample as named telemetry.
        public void Publish(long now, long captureMicros, int firstId)
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
            for (int i = 0; i < values.Length; i++)
                values[i] = snapshot.DetectedIds[i];
            detectedIds.Set(values);
        }
    }
}
