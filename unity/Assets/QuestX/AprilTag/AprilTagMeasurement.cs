using System;
using QuestX.QuestX.Geometry;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>
    /// A field-relative AprilTag pose measurement produced on the Quest.
    /// This is an unfused sensor measurement; the robot is responsible for deciding
    /// whether and how to apply it to its field-pose estimator.
    /// </summary>
    public sealed class AprilTagMeasurement
    {
        public long CaptureTimestampMicros { get; }
        public double AppTimestamp { get; }

        /// <summary>
        /// The published pose: the robot's field pose once <see cref="ForRobot"/> has applied
        /// the mount, otherwise the same as <see cref="HeadsetPose"/>.
        /// </summary>
        public Pose3d Pose { get; }

        /// <summary>The solved field pose of the headset center eye.</summary>
        public Pose3d HeadsetPose { get; private set; }

        /// <summary>
        /// <see cref="Pose"/> with the covariance of its error, ordered [x, y, z, rx, ry, rz] along
        /// the field axes. See <see cref="PoseCovariance"/>.
        /// </summary>
        public PoseCovariance Covariance { get; }

        /// <summary><see cref="HeadsetPose"/> with its covariance.</summary>
        public PoseCovariance HeadsetCovariance { get; private set; }

        public int[] TagIds { get; }
        public int InlierCount { get; }
        public int CornerCount { get; }
        public double InlierRatio { get; }
        public double AverageTagDistanceMeters { get; }

        // Marginal standard deviations of the published pose from Covariance (see
        // PoseCovariance.EstimatorStdDevs): forward, left, and yaw of the planar
        // pose along its heading, then up, roll, and pitch along the pose's own axes.
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
            PoseCovariance covariance,
            int[] tagIds,
            int inlierCount,
            int cornerCount,
            double inlierRatio,
            double averageTagDistanceMeters,
            double ambiguity,
            double reprojectionErrorPixels,
            AprilTagObservation[] observations
        )
        {
            CaptureTimestampMicros = captureTimestampMicros;
            AppTimestamp = appTimestamp;
            Pose = pose;
            Covariance = covariance ?? throw new ArgumentNullException(nameof(covariance));
            TagIds = tagIds;
            InlierCount = inlierCount;
            CornerCount = cornerCount;
            InlierRatio = inlierRatio;
            AverageTagDistanceMeters = averageTagDistanceMeters;
            double[] stdDevs = covariance.EstimatorStdDevs();
            XStdDevMeters = stdDevs[0];
            YStdDevMeters = stdDevs[1];
            ZStdDevMeters = stdDevs[2];
            RollStdDevRadians = stdDevs[3];
            PitchStdDevRadians = stdDevs[4];
            YawStdDevRadians = stdDevs[5];
            Ambiguity = ambiguity;
            ReprojectionErrorPixels = reprojectionErrorPixels;
            Observations = observations;
            HeadsetPose = pose;
            HeadsetCovariance = covariance;
        }

        /// <summary>
        /// The same measurement published as the robot's pose, keeping <see cref="HeadsetPose"/>.
        /// The covariance moves with the pose; the mount itself is treated as exact.
        /// </summary>
        public AprilTagMeasurement ForRobot(Transform3d headsetToRobot) =>
            new AprilTagMeasurement(
                CaptureTimestampMicros,
                AppTimestamp,
                HeadsetPose.TransformBy(headsetToRobot),
                HeadsetCovariance.TransformBy(headsetToRobot),
                TagIds,
                InlierCount,
                CornerCount,
                InlierRatio,
                AverageTagDistanceMeters,
                Ambiguity,
                ReprojectionErrorPixels,
                Observations
            )
            {
                HeadsetPose = HeadsetPose,
                HeadsetCovariance = HeadsetCovariance,
            };
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

        /// <summary>Larger of the latest headset pose's forward and left standard deviations.</summary>
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

        // Pipeline stages, each an exponential moving average (alpha 0.05) in milliseconds.

        /// <summary>From requesting a frame's GPU copy to the copy arriving in CPU memory.</summary>
        public double AverageReadbackMilliseconds { get; set; }

        /// <summary>
        /// Where frames come from: "Native" (QuestX's Camera2 client) or "Mruk" (Meta's camera
        /// access); empty while disabled.
        /// </summary>
        public string CaptureBackend { get; set; } = "";

        /// <summary>Native capture: from exposure until the frame reached the capture thread.</summary>
        public double AverageDeliveryMilliseconds { get; set; }

        /// <summary>PoseLib solve, ambiguity, covariance and local validation.</summary>
        public double AverageSolveMilliseconds { get; set; }

        /// <summary>Encoding the synchronized detection preview, when a viewer is open.</summary>
        public double AveragePreviewMilliseconds { get; set; }

        /// <summary>From camera exposure to the measurement being handed to the publisher.</summary>
        public double AverageLatencyMilliseconds { get; set; }

        /// <summary>New camera images per second while the detector runs.</summary>
        public double CameraFramesPerSecond { get; set; }

        /// <summary>Frames the detector processed per second.</summary>
        public double ProcessedFramesPerSecond { get; set; }

        /// <summary>Measurements published per second.</summary>
        public double MeasurementsPerSecond { get; set; }

        /// <summary>Meta capture frames skipped because no capture buffer was free.</summary>
        public long BusyFrameCount { get; set; }

        /// <summary>Pending frames replaced by fresher captures, excluding shutdown discards.</summary>
        public long ReplacedFrameCount { get; set; }

        /// <summary>Native camera frames overwritten before acquisition, across camera sessions.</summary>
        public long NativeOverwrittenFrameCount { get; set; }

        /// <summary>Pending previews replaced before encoding; excludes reset discards.</summary>
        public long PreviewReplacedFrameCount { get; set; }

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
