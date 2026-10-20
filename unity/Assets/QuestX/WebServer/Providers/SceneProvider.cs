using System;
using System.Linq;
using QuestX.Core;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;

namespace QuestX.WebServer
{
    /// <summary>
    /// Collects what the web UI's 3D views draw: the robot's VIO and field poses, the latest
    /// AprilTag measurement, the latest detections with single-tag camera-frame poses, and the
    /// robot-to-Quest mount. Served from <c>GET /api/scene</c>.
    /// </summary>
    /// <remarks>
    /// Unity's main thread writes and the HTTP thread reads, so all state is locked. Ages use
    /// the monotonic clock shared with <see cref="AprilTagOverlay"/>; app timestamps are mapped
    /// onto it through the newest VIO sample.
    /// </remarks>
    public sealed class SceneProvider
    {
        private readonly object sync = new object();
        private readonly RobotMount robotMount;
        private readonly VioFieldAlignment fieldAlignment;
        private readonly AprilTagScene detections;
        private readonly AprilTagFieldLayout fieldLayout;

        private bool hasVio;
        private Pose3d vioRobotPose;
        private Pose3d vioHeadsetPose;
        private bool vioTracking;
        private uint vioEpoch;
        private double vioAppTime;
        private double vioClockMs;

        private AprilTagMeasurement measurement;
        private AprilTagDiagnosticsSnapshot diagnostics;
        private bool? isRedAlliance;

        public SceneProvider(
            RobotMount robotMount,
            VioFieldAlignment fieldAlignment,
            AprilTagScene detections,
            AprilTagFieldLayout fieldLayout
        )
        {
            this.robotMount = robotMount;
            this.fieldAlignment = fieldAlignment;
            this.detections = detections;
            this.fieldLayout = fieldLayout;
        }

        /// <summary>Records the newest published VIO sample. Called from MainUpdate.</summary>
        public void UpdateVio(
            double appTime,
            Pose3d headsetPose,
            Pose3d robotPose,
            bool tracking,
            uint epoch
        )
        {
            lock (sync)
            {
                hasVio = robotPose != null;
                vioHeadsetPose = headsetPose;
                vioRobotPose = robotPose;
                vioTracking = tracking;
                vioEpoch = epoch;
                vioAppTime = appTime;
                vioClockMs = AprilTagOverlay.MonotonicMilliseconds();
            }
        }

        /// <summary>Records the newest published AprilTag measurement (robot pose).</summary>
        public void UpdateMeasurement(AprilTagMeasurement robotMeasurement)
        {
            lock (sync)
            {
                measurement = robotMeasurement;
            }
        }

        public void UpdateDiagnostics(AprilTagDiagnosticsSnapshot snapshot)
        {
            lock (sync)
            {
                diagnostics = snapshot;
            }
        }

        /// <summary>Records the Driver Station alliance (true for red), or null if unknown.</summary>
        public void UpdateAlliance(bool? redAlliance)
        {
            lock (sync)
            {
                isRedAlliance = redAlliance;
            }
        }

        /// <summary>Builds the <c>/api/scene</c> payload and extends the detection lease.</summary>
        public object GetScene()
        {
            var frame = detections.Read();
            var alignment = fieldAlignment.GetSnapshot();
            double now = AprilTagOverlay.MonotonicMilliseconds();
            lock (sync)
            {
                double nowApp = vioAppTime + (now - vioClockMs) / 1000.0;
                bool frameFresh =
                    frame != null && now - frame.CapturedAtMs < AprilTagOverlay.MaxAgeMs;
                return new
                {
                    alliance = isRedAlliance.HasValue
                        ? (isRedAlliance.Value ? "red" : "blue")
                        : null,
                    robotToQuest = PoseJson(
                        new Pose3d(
                            robotMount.RobotToHeadset.Translation,
                            robotMount.RobotToHeadset.Rotation
                        )
                    ),
                    vio = hasVio
                        ? new
                        {
                            tracking = vioTracking,
                            epoch = vioEpoch,
                            ageMs = Math.Max(0, now - vioClockMs),
                            robotPose = PoseJson(vioRobotPose),
                            headsetPose = PoseJson(vioHeadsetPose),
                        }
                        : null,
                    field = new
                    {
                        estimate = alignment.Estimate == null ? null : PoseJson(alignment.Estimate),
                        lastCorrection = double.IsNaN(alignment.LastCorrectionMeters)
                            ? null
                            : new
                            {
                                meters = alignment.LastCorrectionMeters,
                                yawRadians = alignment.LastCorrectionYawRadians,
                                intervalSeconds = alignment.LastCorrectionIntervalSeconds,
                            },
                        measurement = measurement == null || !hasVio
                            ? null
                            : new
                            {
                                ageMs = Math.Max(0, (nowApp - measurement.AppTimestamp) * 1000.0),
                                robotPose = PoseJson(measurement.Pose),
                                headsetPose = PoseJson(measurement.HeadsetPose),
                                tagIds = measurement.TagIds,
                                ambiguity = measurement.Ambiguity,
                                reprojectionErrorPixels = Finite(
                                    measurement.ReprojectionErrorPixels
                                ),
                                averageTagDistanceMeters = measurement.AverageTagDistanceMeters,
                                stdDevs = new
                                {
                                    xMeters = measurement.XStdDevMeters,
                                    yMeters = measurement.YStdDevMeters,
                                    zMeters = measurement.ZStdDevMeters,
                                    rollRadians = measurement.RollStdDevRadians,
                                    pitchRadians = measurement.PitchStdDevRadians,
                                    yawRadians = measurement.YawStdDevRadians,
                                },
                            },
                    },
                    detections = !frameFresh
                        ? null
                        : new
                        {
                            ageMs = Math.Max(0, now - frame.CapturedAtMs),
                            width = frame.Width,
                            height = frame.Height,
                            intrinsics = new
                            {
                                fx = frame.Intrinsics[0],
                                fy = frame.Intrinsics[1],
                                cx = frame.Intrinsics[2],
                                cy = frame.Intrinsics[3],
                            },
                            headsetToCamera = PoseJson(frame.HeadsetToCamera),
                            tags = frame
                                .Tags.Select(tag => new
                                {
                                    id = tag.Id,
                                    status = tag.Status.ToString().ToLowerInvariant(),
                                    cameraToTag = tag.CameraToTag == null
                                        ? null
                                        : PoseJson(tag.CameraToTag),
                                    ambiguity = tag.Ambiguity,
                                    reprojectionErrorPixels = Finite(tag.ReprojectionErrorPixels),
                                })
                                .ToArray(),
                        },
                    detector = diagnostics == null
                        ? null
                        : new
                        {
                            active = diagnostics.DetectorActive,
                            lastOutcome = diagnostics.LastOutcome,
                            lastReason = diagnostics.LastReason,
                        },
                };
            }
        }

        /// <summary>
        /// The field layout loaded at startup (what the solver uses, which can differ from the
        /// selected file until the app restarts). Served from <c>GET /api/scene/layout</c>.
        /// </summary>
        public object GetLayout()
        {
            var tags = fieldLayout.Tags;
            var field = fieldLayout.Field;
            return new
            {
                file = fieldLayout.LoadedFile,
                loaded = fieldLayout.IsLoaded,
                tagSize = fieldLayout.TagSize,
                field = field == null ? null : new { length = field.X, width = field.Y },
                tags = tags == null
                    ? Array.Empty<object>()
                    : tags.Where(tag => tag?.Pose != null)
                        .Select(tag => (object)new { id = tag.ID, pose = PoseJson(tag.Pose) })
                        .ToArray(),
            };
        }

        private static object PoseJson(Pose3d pose)
        {
            if (pose == null)
            {
                return null;
            }
            var q = pose.Rotation.Quaternion;
            return new
            {
                x = pose.X,
                y = pose.Y,
                z = pose.Z,
                qw = q.W,
                qx = q.X,
                qy = q.Y,
                qz = q.Z,
            };
        }

        private static double? Finite(double value) => double.IsFinite(value) ? value : null;
    }
}
