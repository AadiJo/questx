using QuestX.QuestX.Geometry;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>
    /// A field-relative AprilTag headset-center pose measurement produced on the Quest.
    /// This is an unfused sensor measurement; the robot is responsible for deciding
    /// whether and how to apply it to its field-pose estimator.
    /// </summary>
    public sealed class AprilTagMeasurement
    {
        public long CaptureTimestampMicros { get; }
        public double AppTimestamp { get; }
        public Pose3d Pose { get; }
        public int[] TagIds { get; }
        public int InlierCount { get; }
        public int CornerCount { get; }
        public double InlierRatio { get; }
        public double AverageTagDistanceMeters { get; }
        public double XStdDevMeters { get; }
        public double YStdDevMeters { get; }
        public double ZStdDevMeters { get; }
        public double RollStdDevRadians { get; }
        public double PitchStdDevRadians { get; }
        public double YawStdDevRadians { get; }

        public AprilTagMeasurement(
            long captureTimestampMicros,
            double appTimestamp,
            Pose3d pose,
            int[] tagIds,
            int inlierCount,
            int cornerCount,
            double inlierRatio,
            double averageTagDistanceMeters,
            double xStdDevMeters,
            double yStdDevMeters,
            double zStdDevMeters,
            double rollStdDevRadians,
            double pitchStdDevRadians,
            double yawStdDevRadians
        )
        {
            CaptureTimestampMicros = captureTimestampMicros;
            AppTimestamp = appTimestamp;
            Pose = pose;
            TagIds = tagIds;
            InlierCount = inlierCount;
            CornerCount = cornerCount;
            InlierRatio = inlierRatio;
            AverageTagDistanceMeters = averageTagDistanceMeters;
            XStdDevMeters = xStdDevMeters;
            YStdDevMeters = yStdDevMeters;
            ZStdDevMeters = zStdDevMeters;
            RollStdDevRadians = rollStdDevRadians;
            PitchStdDevRadians = pitchStdDevRadians;
            YawStdDevRadians = yawStdDevRadians;
        }
    }

    /// <summary>Latest detector health and measurement-production state for remote diagnostics.</summary>
    public sealed class AprilTagDiagnosticsSnapshot
    {
        public bool DetectorActive { get; }
        public long CameraFrameCount { get; }
        public long CameraFailureCount { get; }
        public long SolveCount { get; }
        public long MeasurementCount { get; }
        public double LastMeasurementAppTimestamp { get; }
        public int[] LastTagIds { get; }
        public int LastInlierCount { get; }
        public int LastCornerCount { get; }
        public double LastInlierRatio { get; }
        public double LastAverageTagDistanceMeters { get; }
        public double LastLinearStdDevMeters { get; }
        public string LastOutcome { get; }
        public string LastReason { get; }
        public string FieldLayoutFile { get; set; } = "";
        public bool LayoutLoaded { get; set; }
        public int LayoutTagCount { get; set; }
        public int[] DetectedIds { get; set; } = System.Array.Empty<int>();

        public AprilTagDiagnosticsSnapshot(
            bool detectorActive,
            long cameraFrameCount,
            long cameraFailureCount,
            long solveCount,
            long measurementCount,
            double lastMeasurementAppTimestamp,
            int[] lastTagIds,
            int lastInlierCount,
            int lastCornerCount,
            double lastInlierRatio,
            double lastAverageTagDistanceMeters,
            double lastLinearStdDevMeters,
            string lastOutcome,
            string lastReason
        )
        {
            DetectorActive = detectorActive;
            CameraFrameCount = cameraFrameCount;
            CameraFailureCount = cameraFailureCount;
            SolveCount = solveCount;
            MeasurementCount = measurementCount;
            LastMeasurementAppTimestamp = lastMeasurementAppTimestamp;
            LastTagIds = lastTagIds;
            LastInlierCount = lastInlierCount;
            LastCornerCount = lastCornerCount;
            LastInlierRatio = lastInlierRatio;
            LastAverageTagDistanceMeters = lastAverageTagDistanceMeters;
            LastLinearStdDevMeters = lastLinearStdDevMeters;
            LastOutcome = lastOutcome;
            LastReason = lastReason;
        }
    }
}
