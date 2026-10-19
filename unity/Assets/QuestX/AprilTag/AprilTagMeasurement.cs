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

        /// <summary>Planar pose ambiguity from 0 to 1; see <see cref="AprilTagAmbiguity.Result.Ambiguity"/>.</summary>
        public double Ambiguity { get; }

        /// <summary>RMS pixel error of the solved pose over every observed corner.</summary>
        public double ReprojectionErrorPixels { get; }

        /// <summary>Per-tag details in the same order as <see cref="TagIds"/>.</summary>
        public AprilTagObservation[] Observations { get; }

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
            double yawStdDevRadians,
            double ambiguity,
            double reprojectionErrorPixels,
            AprilTagObservation[] observations
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
            Ambiguity = ambiguity;
            ReprojectionErrorPixels = reprojectionErrorPixels;
            Observations = observations;
        }
    }

    /// <summary>
    /// One tag that contributed to a measurement. The angle, area, and distance fields follow
    /// Limelight's raw-fiducial definitions so they can be republished in that format.
    /// </summary>
    public readonly struct AprilTagObservation
    {
        public int Id { get; }

        /// <summary>Single-tag planar pose ambiguity from 0 to 1.</summary>
        public double Ambiguity { get; }

        /// <summary>Horizontal angle from the principal point to the tag center; right is positive.</summary>
        public double TxDegrees { get; }

        /// <summary>Vertical angle from the principal point to the tag center; up is positive.</summary>
        public double TyDegrees { get; }

        /// <summary>Tag outline area as a percentage of the image area.</summary>
        public double AreaPercent { get; }

        /// <summary>Distance from the passthrough camera to the tag center.</summary>
        public double CameraDistanceMeters { get; }

        /// <summary>Distance from the headset center to the tag center.</summary>
        public double HeadsetDistanceMeters { get; }

        public AprilTagObservation(
            int id,
            double ambiguity,
            double txDegrees,
            double tyDegrees,
            double areaPercent,
            double cameraDistanceMeters,
            double headsetDistanceMeters
        )
        {
            Id = id;
            Ambiguity = ambiguity;
            TxDegrees = txDegrees;
            TyDegrees = tyDegrees;
            AreaPercent = areaPercent;
            CameraDistanceMeters = cameraDistanceMeters;
            HeadsetDistanceMeters = headsetDistanceMeters;
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
        public double LastAmbiguity { get; set; }
        public double LastReprojectionErrorPixels { get; set; }
        public string LastOutcome { get; }
        public string LastReason { get; }
        public string FieldLayoutFile { get; set; } = "";
        public bool LayoutLoaded { get; set; }
        public int LayoutTagCount { get; set; }
        public int[] DetectedIds { get; set; } = System.Array.Empty<int>();

        /// <summary>Backend that is actually running ("Napa" or "AprilTag"), empty when disabled.</summary>
        public string DetectorName { get; set; } = "";

        /// <summary>Grayscale conversion plus detection for the latest frame, in milliseconds.</summary>
        public double LastDetectMilliseconds { get; set; }

        /// <summary>Exponential moving average (alpha 0.05) of <see cref="LastDetectMilliseconds"/>.</summary>
        public double AverageDetectMilliseconds { get; set; }

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
