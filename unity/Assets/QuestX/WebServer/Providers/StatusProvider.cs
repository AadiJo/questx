using QuestX.QuestX.AprilTag;
using UnityEngine;

namespace QuestX.WebServer
{
    /// <summary>
    /// Provides access to runtime status information for the web interface.
    /// Collects headset pose, tracking, battery, and network status.
    /// Updated at 3Hz from QuestX.SlowUpdate() and served via /api/status endpoint.
    /// Plain C# class designed for dependency injection.
    /// Thread-safe for concurrent access from main thread (UpdateStatus) and background thread (GetStatus).
    /// </summary>
    public class StatusProvider
    {
        #region Fields
        /// <summary>
        /// Lock object for thread-safe access to status fields
        /// </summary>
        private readonly object statusLock = new object();

        /// <summary>
        /// Current position of VR headset (FRC coordinates)
        /// </summary>
        private Vector3 position;

        /// <summary>
        /// Current rotation of VR headset (FRC coordinates)
        /// </summary>
        private Quaternion rotation;

        /// <summary>
        /// Whether headset is currently tracking
        /// </summary>
        private bool isTracking;

        /// <summary>
        /// Number of tracking loss events this session
        /// </summary>
        private int trackingLostEvents;

        /// <summary>
        /// Current battery level (0.0 to 1.0)
        /// </summary>
        private float batteryLevel;

        /// <summary>
        /// Current battery charging status
        /// </summary>
        private BatteryStatus batteryStatus;

        /// <summary>
        /// Whether connected to NetworkTables
        /// </summary>
        private bool isConnected;

        /// <summary>
        /// Current IP address of headset
        /// </summary>
        private string ipAddress = "0.0.0.0";

        /// <summary>
        /// Current team number for connection
        /// </summary>
        private int teamNumber;

        /// <summary>
        /// Current robot IP address (resolved from team number or debug override)
        /// </summary>
        private string robotIpAddress = "";

        /// <summary>
        /// Current frames per second
        /// </summary>
        private float fps;

        /// <summary>
        /// Unity frame count
        /// </summary>
        private int frameCount;

        /// <summary>
        /// Number of connected web clients
        /// </summary>
        private int connectedClients;

        /// <summary>
        /// Whether the camera arbiter currently has a high-priority reservation.
        /// True when the AprilTag detector is enabled and pinning the camera resolution.
        /// </summary>
        private bool passthroughResolutionLockedByAprilTag;

        /// <summary>
        /// The resolution actually being applied to the camera, or null if disabled.
        /// May differ from the passthrough stream's requested resolution when locked.
        /// </summary>
        private Vector2Int? effectivePassthroughResolution;

        private uint trackingEpoch;
        private ulong vioPublishCount;
        private ulong aprilTagPublishCount;
        private bool aprilTagDetectorActive;
        private long aprilTagCameraFrameCount;
        private long aprilTagCameraFailureCount;
        private long aprilTagSolveCount;
        private long aprilTagMeasurementCount;
        private double aprilTagMeasurementAgeMs = -1;
        private int[] aprilTagIds = System.Array.Empty<int>();
        private int aprilTagInlierCount;
        private int aprilTagCornerCount;
        private double aprilTagInlierRatio;
        private double aprilTagAverageDistanceMeters;
        private double aprilTagLinearStdDevMeters;
        private string aprilTagLastOutcome = "Not started";
        private AprilTagDiagnosticsSnapshot aprilTagDiagnostics;
        private string aprilTagLastReason = "";
        private int passthroughCaptureFailureCount;
        private int passthroughConsecutiveCaptureFailures;
        private double passthroughLastFrameAgeMs = -1;
        private int passthroughConnectedClients;
        #endregion

        #region Public Update Methods
        /// <summary>
        /// Updates all status information at once.
        /// Called from WebServerManager.UpdateStatus() which is invoked from QuestX.SlowUpdate() at 3Hz.
        /// </summary>
        /// <param name="pos">Current position of VR headset (FRC coordinates)</param>
        /// <param name="rot">Current rotation of VR headset (FRC coordinates)</param>
        /// <param name="tracking">Whether headset is currently tracking</param>
        /// <param name="lostEvents">Number of tracking loss events this session</param>
        /// <param name="battery">Battery level (0.0 to 1.0)</param>
        /// <param name="status">Battery charging status</param>
        /// <param name="connected">Whether connected to NetworkTables</param>
        /// <param name="ip">Current IP address of headset</param>
        /// <param name="team">Current team number for connection</param>
        /// <param name="robotIp">Robot IP address (from debug override or calculated from team number)</param>
        /// <param name="currentFps">Current frames per second</param>
        /// <param name="currentFrameCount">Unity frame count</param>
        public void UpdateStatus(
            Vector3 pos,
            Quaternion rot,
            bool tracking,
            int lostEvents,
            float battery,
            BatteryStatus status,
            bool connected,
            string ip,
            int team,
            string robotIp,
            float currentFps,
            int currentFrameCount
        )
        {
            lock (statusLock)
            {
                position = pos;
                rotation = rot;
                isTracking = tracking;
                trackingLostEvents = lostEvents;
                batteryLevel = battery;
                batteryStatus = status;
                isConnected = connected;
                ipAddress = ip;
                teamNumber = team;
                robotIpAddress = robotIp;
                fps = currentFps;
                frameCount = currentFrameCount;
            }
        }

        /// <summary>
        /// Updates the number of connected web clients.
        /// Can be called from ConfigServer background thread.
        /// Thread-safe.
        /// </summary>
        /// <param name="clients">Number of active web clients</param>
        public void UpdateConnectedClients(int clients)
        {
            lock (statusLock)
            {
                connectedClients = clients;
            }
        }

        /// <summary>
        /// Updates the camera arbitration state for the web UI. Called from
        /// <see cref="WebServerManager"/> in response to
        /// <c>QuestX.Camera.CameraResourceManager.OnResolutionChanged</c>.
        /// </summary>
        /// <param name="lockedByAprilTag">True when AprilTag detector holds the camera lock.</param>
        /// <param name="effectiveResolution">Actual camera resolution, or null when disabled.</param>
        public void UpdateCameraStatus(bool lockedByAprilTag, Vector2Int? effectiveResolution)
        {
            lock (statusLock)
            {
                passthroughResolutionLockedByAprilTag = lockedByAprilTag;
                this.effectivePassthroughResolution = effectiveResolution;
            }
        }

        /// <summary>Updates split-measurement publisher and AprilTag pipeline diagnostics.</summary>
        public void UpdateMeasurementStatus(
            uint currentTrackingEpoch,
            double currentAppTimestamp,
            ulong currentVioPublishCount,
            ulong currentAprilTagPublishCount,
            AprilTagDiagnosticsSnapshot aprilTag
        )
        {
            if (aprilTag == null)
            {
                return;
            }

            lock (statusLock)
            {
                trackingEpoch = currentTrackingEpoch;
                vioPublishCount = currentVioPublishCount;
                aprilTagPublishCount = currentAprilTagPublishCount;
                aprilTagDetectorActive = aprilTag.DetectorActive;
                aprilTagCameraFrameCount = aprilTag.CameraFrameCount;
                aprilTagCameraFailureCount = aprilTag.CameraFailureCount;
                aprilTagSolveCount = aprilTag.SolveCount;
                aprilTagMeasurementCount = aprilTag.MeasurementCount;
                aprilTagMeasurementAgeMs =
                    aprilTag.LastMeasurementAppTimestamp > 0
                        ? System.Math.Max(
                            0,
                            (currentAppTimestamp - aprilTag.LastMeasurementAppTimestamp) * 1000.0
                        )
                        : -1;
                aprilTagIds = aprilTag.LastTagIds ?? System.Array.Empty<int>();
                aprilTagInlierCount = aprilTag.LastInlierCount;
                aprilTagCornerCount = aprilTag.LastCornerCount;
                aprilTagInlierRatio = aprilTag.LastInlierRatio;
                aprilTagAverageDistanceMeters = aprilTag.LastAverageTagDistanceMeters;
                aprilTagLinearStdDevMeters = aprilTag.LastLinearStdDevMeters;
                aprilTagLastOutcome = aprilTag.LastOutcome;
                aprilTagDiagnostics = aprilTag;
                aprilTagLastReason = aprilTag.LastReason;
            }
        }

        /// <summary>Updates passthrough capture and MJPEG client health.</summary>
        public void UpdatePassthroughStreamStatus(
            long currentUnixTimestampMs,
            long lastSuccessfulFrameTimestampMs,
            int captureFailureCount,
            int consecutiveCaptureFailures,
            int streamConnectedClients
        )
        {
            lock (statusLock)
            {
                passthroughCaptureFailureCount = captureFailureCount;
                passthroughConsecutiveCaptureFailures = consecutiveCaptureFailures;
                passthroughConnectedClients = streamConnectedClients;
                passthroughLastFrameAgeMs =
                    lastSuccessfulFrameTimestampMs > 0
                        ? System.Math.Max(
                            0,
                            currentUnixTimestampMs - lastSuccessfulFrameTimestampMs
                        )
                        : -1;
            }
        }
        #endregion

        #region Public Query Methods
        /// <summary>
        /// Gets current status as an object for JSON serialization.
        /// Called from ConfigServer background thread via /api/status endpoint.
        /// Returns anonymous object suitable for JSON.NET serialization.
        /// Position and rotation are provided in FRC robot coordinates.
        /// Thread-safe.
        /// </summary>
        /// <returns>Status data object with all current values</returns>
        public object GetStatus()
        {
            lock (statusLock)
            {
                var eulerAngles = rotation.eulerAngles;

                return new
                {
                    // Pose (in FRC coordinates, converted by QuestX before passing)
                    position = new
                    {
                        x = position.x,
                        y = position.y,
                        z = position.z,
                    },
                    rotation = new
                    {
                        x = rotation.x,
                        y = rotation.y,
                        z = rotation.z,
                        w = rotation.w,
                    },
                    eulerAngles = new
                    {
                        pitch = eulerAngles.z,
                        yaw = eulerAngles.y,
                        roll = eulerAngles.x,
                    },

                    // Tracking
                    isTracking = isTracking,
                    trackingLostEvents = trackingLostEvents,
                    trackingEpoch = trackingEpoch,

                    // Battery
                    batteryPercent = (int)(batteryLevel * 100),
                    batteryLevel = batteryLevel,
                    batteryStatus = batteryStatus.ToString(),
                    batteryCharging = batteryStatus == BatteryStatus.Charging,

                    // Network
                    networkConnected = isConnected,
                    ipAddress = ipAddress,
                    teamNumber = teamNumber,
                    robotIpAddress = robotIpAddress,

                    // Performance
                    fps = Mathf.Round(fps),
                    frameCount = frameCount,
                    vioPublishCount = vioPublishCount,

                    // Latest unfused AprilTag measurement and pipeline health
                    aprilTag = new
                    {
                        detectorActive = aprilTagDetectorActive,
                        fieldLayout = aprilTagDiagnostics?.FieldLayoutFile ?? "",
                        layoutLoaded = aprilTagDiagnostics?.LayoutLoaded ?? false,
                        layoutTagCount = aprilTagDiagnostics?.LayoutTagCount ?? 0,
                        detectedIds = aprilTagDiagnostics?.DetectedIds ?? System.Array.Empty<int>(),
                        cameraFrameCount = aprilTagCameraFrameCount,
                        cameraFailureCount = aprilTagCameraFailureCount,
                        solveCount = aprilTagSolveCount,
                        measurementCount = aprilTagMeasurementCount,
                        publishCount = aprilTagPublishCount,
                        measurementAgeMs = aprilTagMeasurementAgeMs,
                        tagIds = aprilTagIds,
                        inlierCount = aprilTagInlierCount,
                        cornerCount = aprilTagCornerCount,
                        inlierRatio = aprilTagInlierRatio,
                        averageTagDistanceMeters = aprilTagAverageDistanceMeters,
                        linearStdDevMeters = aprilTagLinearStdDevMeters,
                        lastOutcome = aprilTagLastOutcome,
                        lastReason = aprilTagLastReason,
                        detector = aprilTagDiagnostics?.DetectorName ?? "",
                        lastDetectMs = aprilTagDiagnostics?.LastDetectMilliseconds ?? 0,
                        averageDetectMs = aprilTagDiagnostics?.AverageDetectMilliseconds ?? 0,
                        averageReadbackMs = aprilTagDiagnostics?.AverageReadbackMilliseconds ?? 0,
                        averageSolveMs = aprilTagDiagnostics?.AverageSolveMilliseconds ?? 0,
                        averagePreviewMs = aprilTagDiagnostics?.AveragePreviewMilliseconds ?? 0,
                        averageLatencyMs = aprilTagDiagnostics?.AverageLatencyMilliseconds ?? 0,
                        cameraFps = aprilTagDiagnostics?.CameraFramesPerSecond ?? 0,
                        processedFps = aprilTagDiagnostics?.ProcessedFramesPerSecond ?? 0,
                        measurementFps = aprilTagDiagnostics?.MeasurementsPerSecond ?? 0,
                        busyFrameCount = aprilTagDiagnostics?.BusyFrameCount ?? 0,
                    },

                    passthroughStream = new
                    {
                        captureFailureCount = passthroughCaptureFailureCount,
                        consecutiveCaptureFailures = passthroughConsecutiveCaptureFailures,
                        lastFrameAgeMs = passthroughLastFrameAgeMs,
                        connectedClients = passthroughConnectedClients,
                    },

                    // Web Interface
                    connectedClients = connectedClients,

                    // Camera arbitration (AprilTag wins over passthrough stream)
                    passthroughResolutionLockedByAprilTag = passthroughResolutionLockedByAprilTag,
                    effectivePassthroughResolution = effectivePassthroughResolution.HasValue
                        ? (object)
                            new
                            {
                                width = effectivePassthroughResolution.Value.x,
                                height = effectivePassthroughResolution.Value.y,
                            }
                        : null,

                    // Timestamp
                    timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
            }
        }
        #endregion
    }
}
