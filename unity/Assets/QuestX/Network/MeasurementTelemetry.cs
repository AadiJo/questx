using QuestX.Native.NTCore;
using QuestX.QuestX.Geometry;

namespace QuestX.Network
{
    /// <summary>Named diagnostic snapshots. Never use these independent topics for fusion.</summary>
    internal sealed class MeasurementTelemetry
    {
        private readonly DoublePublisher[] posePublishers;
        private readonly DoublePublisher agePublisher;
        private readonly DoublePublisher appTimePublisher;
        private readonly IntegerPublisher sequencePublisher;
        private readonly IntegerPublisher capturePublisher;
        private readonly BooleanPublisher hasMeasurementPublisher;
        private readonly BooleanPublisher trackingPublisher;
        private readonly IntegerPublisher epochPublisher;
        private readonly DoubleArrayPublisher tagIdsPublisher;
        private readonly DoubleArrayPublisher tagAmbiguitiesPublisher;
        private readonly DoublePublisher[] qualityPublishers;
        private readonly IntegerPublisher publishCountPublisher;
        private double[] measurement;
        private Pose3d pose;
        private double[] tagIds = new double[0];
        private double[] tagAmbiguities = new double[0];

        public MeasurementTelemetry(NtInstance nt, string path, bool vio)
        {
            var options = PubSubOptions.AllDefault;
            options.Periodic = 0.1;
            options.SendAll = false;
            options.KeepDuplicates = false;
            string[] axes =
            {
                "xMeters",
                "yMeters",
                "zMeters",
                "rollRadians",
                "pitchRadians",
                "yawRadians",
            };
            posePublishers = new DoublePublisher[axes.Length];
            for (int i = 0; i < axes.Length; i++)
                posePublishers[i] = nt.GetDoublePublisher(path + axes[i], options);
            agePublisher = nt.GetDoublePublisher(path + "ageMilliseconds", options);
            appTimePublisher = nt.GetDoublePublisher(path + "appTimestampSeconds", options);
            sequencePublisher = nt.GetIntegerPublisher(path + "sequenceId", options);
            capturePublisher = nt.GetIntegerPublisher(path + "captureTimestampMicros", options);
            hasMeasurementPublisher = nt.GetBooleanPublisher(path + "hasMeasurement", options);
            publishCountPublisher = nt.GetIntegerPublisher(path + "publishCount", options);
            if (vio)
            {
                trackingPublisher = nt.GetBooleanPublisher(path + "isTracking", options);
                epochPublisher = nt.GetIntegerPublisher(path + "trackingEpoch", options);
            }
            else
            {
                tagIdsPublisher = nt.GetDoubleArrayPublisher(path + "tagIds", options);
                tagAmbiguitiesPublisher = nt.GetDoubleArrayPublisher(path + "tagAmbiguities", options);
                string[] quality =
                {
                    "tagCount",
                    "inlierCount",
                    "cornerCount",
                    "inlierRatio",
                    "averageTagDistanceMeters",
                    "stdDevXMeters",
                    "stdDevYMeters",
                    "stdDevZMeters",
                    "stdDevRollRadians",
                    "stdDevPitchRadians",
                    "stdDevYawRadians",
                    "ambiguity",
                    "reprojectionErrorPixels",
                    "processingLatencyMilliseconds",
                };
                qualityPublishers = new DoublePublisher[quality.Length];
                for (int i = 0; i < quality.Length; i++)
                    qualityPublishers[i] = nt.GetDoublePublisher(path + quality[i], options);
            }
        }

        // The connection owns and reuses the arrays; all callbacks run on Unity's main thread.
        // AprilTag telemetry reads the v3 layout (MeasurementWireFormat.AprilTagV3HeaderLength).
        public void Update(double[] value, Pose3d capturedPose)
        {
            measurement = value;
            pose = capturedPose;
        }

        public void Publish(long nowMicros, ulong publishCount)
        {
            hasMeasurementPublisher.Set(measurement != null);
            publishCountPublisher.Set(unchecked((long)publishCount));
            if (measurement == null)
            {
                agePublisher.Set(-1);
                return;
            }
            long capture = unchecked((long)(((ulong)measurement[3] << 32) | (uint)measurement[2]));
            sequencePublisher.Set(
                unchecked((long)(((ulong)measurement[1] << 32) | (uint)measurement[0]))
            );
            capturePublisher.Set(capture);
            agePublisher.Set((nowMicros - capture) / 1000.0);
            appTimePublisher.Set(measurement[4]);
            posePublishers[0].Set(pose.X);
            posePublishers[1].Set(pose.Y);
            posePublishers[2].Set(pose.Z);
            posePublishers[3].Set(pose.Rotation.X);
            posePublishers[4].Set(pose.Rotation.Y);
            posePublishers[5].Set(pose.Rotation.Z);
            if (trackingPublisher != null)
            {
                trackingPublisher.Set(measurement[12] == 1);
                epochPublisher.Set((long)measurement[13]);
            }
            else
            {
                for (int i = 0; i < qualityPublishers.Length; i++)
                    qualityPublishers[i].Set(measurement[12 + i]);
                int count = (int)measurement[12];
                if (tagIds.Length != count)
                {
                    tagIds = new double[count];
                    tagAmbiguities = new double[count];
                }
                System.Array.Copy(
                    measurement,
                    MeasurementWireFormat.AprilTagV3HeaderLength,
                    tagIds,
                    0,
                    count
                );
                System.Array.Copy(
                    measurement,
                    MeasurementWireFormat.AprilTagV3HeaderLength + count,
                    tagAmbiguities,
                    0,
                    count
                );
                tagIdsPublisher.Set(tagIds);
                tagAmbiguitiesPublisher.Set(tagAmbiguities);
            }
        }
    }
}
