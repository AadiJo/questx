using System;
using System.Threading;
using QuestX.Config;
using QuestX.Core;
using QuestX.Native.NTCore;
using QuestX.Protos.Generated;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;
using QuestX.Utils;
using QuestX.VioQuality;
using UnityEngine;
using Quaternion = UnityEngine.Quaternion;

namespace QuestX.Network
{
    /// <summary>
    /// Interface for NetworkTables connection management.
    /// </summary>
    public interface INetworkTableConnection
    {
        /// <summary>
        /// Fired when connection is established.
        /// </summary>
        public event Action OnConnect;

        /// <summary>
        /// Fired when connection is lost.
        /// </summary>
        public event Action OnDisconnect;

        /// <summary>
        /// Fired when the local IP address changes.
        /// </summary>
        public event Action<string> OnIpAddressChanged;

        /// <summary>
        /// Gets whether the connection is currently established.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Gets whether the connection is ready to connect.
        /// </summary>
        /// <returns>true when either an IP or team number has been set</returns>
        bool IsReadyToConnect { get; }

        /// <summary>
        /// Gets the current NT time
        /// </summary>
        long NtNow { get; }

        /// <summary>
        /// The headset's local IPv4 address, or <see cref="LocalAddressMonitor.NoAddress"/>
        /// ("127.0.0.1") when none is known. Refreshed off the main thread.
        /// </summary>
        string IpAddress { get; }

        /// <summary>
        /// Publishes legacy, Quest-side fused frame data to NetworkTables.
        /// </summary>
        /// <param name="frameCount">Current frame index</param>
        /// <param name="timeStamp">Current timestamp</param>
        /// <param name="pose">Current field-relative position of the Quest headset</param>
        /// <param name="isTracking">Is the headset is currently tracking its position</param>
        [Obsolete("Use PublishVioData and PublishAprilTagData instead.")]
        void PublishFrameData(int frameCount, double timeStamp, Pose3d pose, bool isTracking);

        /// <summary>
        /// Publishes an unfused, local-frame VIO measurement to NetworkTables.
        /// </summary>
        void PublishVioData(
            long captureTimestampMicros,
            double appTimestamp,
            Pose3d pose,
            bool isTracking,
            uint trackingEpoch
        );

        /// <summary>
        /// Publishes a field-relative AprilTag measurement and its quality metadata.
        /// </summary>
        void PublishAprilTagData(AprilTagMeasurement measurement);

        /// <summary>
        /// Publishes a VIO quality report (environment-depth drift check and head tracking state) to /v2/vioQuality.
        /// </summary>
        void PublishVioQuality(
            long captureTimestampMicros,
            double appTimestamp,
            VioQualitySample quality
        );

        /// <summary>
        /// Gets the number of VIO measurements successfully handed to NTCore.
        /// </summary>
        ulong VioPublishCount { get; }

        /// <summary>
        /// Gets the number of v4 AprilTag measurements successfully handed to NTCore locally; not a receipt acknowledgement.
        /// </summary>
        ulong AprilTagPublishCount { get; }

        /// <summary>
        /// The robot's alliance from the Driver Station (/FMSInfo/IsRedAlliance): true for red,
        /// false for blue, or null while disconnected or before the robot publishes it.
        /// </summary>
        bool? IsRedAlliance { get; }

        /// <summary>
        /// Publishes device data to NetworkTables.
        /// </summary>
        /// <param name="trackingLostCounter">Number of tracking lost events this session</param>
        /// <param name="batteryPercent">Current battery percentage</param>
        void PublishDeviceData(int trackingLostCounter, int batteryPercent);

        /// <summary>
        /// Publishes the web dashboard URL to NetworkTables when it changes.
        /// </summary>
        /// <param name="webUrl">Dashboard URL, or null/empty when the headset has no address</param>
        void PublishWebUrl(string webUrl);

        /// <summary>
        /// Returns a camera source with the given sub-topic
        /// </summary>
        /// <param name="name">The name of the camera source</param>
        /// <returns>An ICameraSource for the given topic or null</returns>
        INtCameraSource CreateCameraSource(string name);

        /// <summary>
        /// Gets all command requests from the robot since the last read, or an empty array if none available
        /// </summary>
        /// <returns>All command requests since the last read</returns>
        TimestampedValue<ProtobufQuestXCommand>[] GetCommandRequests();

        /// <summary>
        /// Sends a command processing success response back to the robot
        /// </summary>
        /// <param name="commandId">command_id</param>
        void SendCommandSuccessResponse(uint commandId);

        /// <summary>
        /// Sends a command processing error response back to the robot
        /// </summary>
        /// <param name="commandId">command_id</param>
        /// <param name="errorMessage">error message</param>
        void SendCommandErrorResponse(uint commandId, string errorMessage);

        /// <summary>
        /// Performs periodic updates including connection polling and logging.
        /// </summary>
        void Periodic();

        void PublishAprilTagDiagnostics(AprilTagDiagnosticsSnapshot diagnostics);

        /// <summary>
        /// Populates subscribed events with initial values
        /// </summary>
        void Initialize();
    }

    /// <summary>
    /// Manages NetworkTables connections for communication with an FRC robot.
    /// </summary>
    public class NetworkTableConnection : INetworkTableConnection
    {
        #region Fields
        /// <summary>
        /// ConfigManager for reading team number and IP override
        /// </summary>
        private readonly IConfigManager configManager;

        /// <summary>
        /// NetworkTables connection for FRC data communication
        /// </summary>
        private readonly NtInstance ntInstance;

        /// <summary>
        /// Logger for NetworkTables internal messages
        /// </summary>
        private PolledLogger ntInstanceLogger;

        /// <summary>
        /// Shared local address source for the UI, web server and mDNS
        /// </summary>
        private readonly LocalAddressMonitor localAddress = new LocalAddressMonitor();

        /// <summary>
        /// Publisher for frame data (position/rotation updates)
        /// </summary>
        private readonly ProtobufPublisher<ProtobufQuestXFrameData> frameDataPublisher;

        /// <summary>
        /// Publisher for unfused Quest VIO measurements.
        /// </summary>
        private readonly ProtobufPublisher<ProtobufQuestXVioData> vioDataPublisher;

        /// <summary>
        /// Publisher for field-relative AprilTag measurements.
        /// </summary>
        private readonly ProtobufPublisher<ProtobufQuestXAprilTagData> aprilTagDataPublisher;

        private readonly DoubleArrayPublisher vioV2Publisher;
        private readonly DoubleArrayPublisher aprilTagV2Publisher;
        private readonly DoubleArrayPublisher aprilTagV3Publisher;
        private readonly DoubleArrayPublisher aprilTagV4Publisher;
        private readonly MeasurementTelemetry vioTelemetry;
        private readonly MeasurementTelemetry aprilTagTelemetry;
        private readonly LimelightTelemetry limelightTelemetry;
        private readonly double[] vioV2 = new double[MeasurementWireFormat.VioLength];
        private readonly DoubleArrayPublisher vioQualityPublisher;
        private readonly double[] vioQualityV2 = new double[MeasurementWireFormat.VioQualityLength];
        private ulong nextVioQualitySequenceId;
        private double[] aprilTagV2 = new double[MeasurementWireFormat.AprilTagHeaderLength];
        private double[] aprilTagV3 = new double[MeasurementWireFormat.AprilTagV3HeaderLength];
        private double[] aprilTagV4 = new double[MeasurementWireFormat.AprilTagV4HeaderLength];
        private long nextTelemetryMicros;

        /// <summary>
        /// Publisher for device data (tracking status, battery, etc.)
        /// </summary>
        private readonly ProtobufPublisher<ProtobufQuestXDeviceData> deviceDataPublisher;

        /// <summary>
        /// Publisher for the headset battery percentage as a standard NT integer.
        /// </summary>
        private readonly IntegerPublisher batteryPercentPublisher;

        /// <summary>
        /// Publisher for command responses (Quest to robot)
        /// </summary>
        private readonly ProtobufPublisher<ProtobufQuestXCommandResponse> commandResponsePublisher;

        /// <summary>
        /// Subscriber for command requests (robot to Quest)
        /// </summary>
        private readonly ProtobufSubscriber<ProtobufQuestXCommand> commandRequestSubscriber;

        /// <summary>
        /// Subscriber for the Driver Station's alliance, which WPILib robots publish under FMSInfo.
        /// </summary>
        private readonly BooleanSubscriber redAllianceSubscriber;

        /// <summary>
        /// Publisher for video streams
        /// </summary>
        private StringArrayPublisher streamsPublisher;

        /// <summary>
        /// Publisher for the web dashboard URL
        /// </summary>
        private readonly StringPublisher webUrlPublisher;

        /// <summary>
        /// Last published web dashboard URL, used to skip redundant publishes
        /// </summary>
        private string lastWebUrl;

        /// <summary>
        /// Flag indicating if a team number has been set
        /// </summary>
        private bool teamNumberSet = false;

        /// <summary>
        /// Flag indicating if an IP address has been set
        /// </summary>
        private bool ipAddressSet = false;

        /// <summary>
        /// Tracks previous connection state for change detection
        /// </summary>
        private bool wasConnected;
        #endregion

        /// <summary>
        /// Initializes a new NetworkTables connection with publishers and subscribers for QuestX communication.
        ///
        /// QUESTX COMMUNICATION TOPICS:
        /// Publishers (Quest → Robot):
        /// - /QuestX/vioData: High-frequency local VIO measurements
        /// - /QuestX/aprilTagData: Field-relative AprilTag measurements
        /// - /QuestX/deviceData: Device status updates (3Hz)
        /// - /QuestX/response: Command execution results
        /// - /QuestX/webUrl: Web dashboard URL
        ///
        /// Subscribers (Robot → Quest):
        /// - /QuestX/request: Commands from robot (pose resets, etc.)
        ///
        /// PROTOBUF SERIALIZATION:
        /// Uses Protocol Buffers for efficient, versioned message serialization.
        /// This provides type safety, backward compatibility, and compact encoding.
        /// </summary>
        public NetworkTableConnection(IConfigManager configManager)
        {
            this.configManager = configManager;
            localAddress.Changed += OnLocalAddressChanged;

            // Create NetworkTables instance with QuestX namespace
            // This isolates QuestX topics from other NetworkTables data
            ntInstance = new NtInstance(QuestXConstants.Topics.NT_BASE_PATH);

            vioV2Publisher = ntInstance.GetDoubleArrayPublisher(
                QuestXConstants.Topics.VIO_DATA_V2,
                QuestXConstants.Network.NtPublisherSettings
            );
            aprilTagV2Publisher = ntInstance.GetDoubleArrayPublisher(
                QuestXConstants.Topics.APRIL_TAG_DATA_V2,
                QuestXConstants.Network.NtPublisherSettings
            );
            aprilTagV3Publisher = ntInstance.GetDoubleArrayPublisher(
                QuestXConstants.Topics.APRIL_TAG_DATA_V3,
                QuestXConstants.Network.NtPublisherSettings
            );
            aprilTagV4Publisher = ntInstance.GetDoubleArrayPublisher(
                QuestXConstants.Topics.APRIL_TAG_DATA_V4,
                QuestXConstants.Network.NtPublisherSettings
            );
            vioQualityPublisher = ntInstance.GetDoubleArrayPublisher(
                QuestXConstants.Topics.VIO_QUALITY_V2,
                QuestXConstants.Network.NtPublisherSettings
            );
            limelightTelemetry = new LimelightTelemetry(ntInstance);
            vioTelemetry = new MeasurementTelemetry(
                ntInstance,
                QuestXConstants.Topics.VIO_TELEMETRY,
                true
            );
            aprilTagTelemetry = new MeasurementTelemetry(
                ntInstance,
                QuestXConstants.Topics.APRIL_TAG_TELEMETRY,
                false
            );

            /*
             * PUBLISHER SETUP - Quest sends data TO robot
             * Each publisher is configured with:
             * - Topic name: Hierarchical path for organization
             * - Protobuf schema: Ensures type safety and versioning
             * - Publisher options: Reliability, frequency, etc.
             */

            // Legacy publisher is retained so the deprecated compatibility method remains
            // binary-compatible. The app no longer calls PublishFrameData.
            frameDataPublisher = ntInstance.GetProtobufPublisher<ProtobufQuestXFrameData>(
                QuestXConstants.Topics.FRAME_DATA,
                ProtobufQuestXFrameData.Descriptor,
                QuestXConstants.Network.NtPublisherSettings
            );

            vioDataPublisher = ntInstance.GetProtobufPublisher<ProtobufQuestXVioData>(
                QuestXConstants.Topics.VIO_DATA,
                ProtobufQuestXVioData.Descriptor,
                QuestXConstants.Network.NtPublisherSettings
            );

            aprilTagDataPublisher = ntInstance.GetProtobufPublisher<ProtobufQuestXAprilTagData>(
                QuestXConstants.Topics.APRIL_TAG_DATA,
                ProtobufQuestXAprilTagData.Descriptor,
                QuestXConstants.Network.NtPublisherSettings
            );

            // Low-frequency device status (3Hz) - robot uses this for diagnostics
            deviceDataPublisher = ntInstance.GetProtobufPublisher<ProtobufQuestXDeviceData>(
                QuestXConstants.Topics.DEVICE_DATA,
                ProtobufQuestXDeviceData.Descriptor,
                QuestXConstants.Network.NtPublisherSettings
            );
            batteryPercentPublisher = ntInstance.GetIntegerPublisher(
                QuestXConstants.Topics.BATTERY_PERCENT,
                QuestXConstants.Network.NtPublisherSettings
            );

            // Command responses - Quest confirms command execution to robot
            commandResponsePublisher =
                ntInstance.GetProtobufPublisher<ProtobufQuestXCommandResponse>(
                    QuestXConstants.Topics.COMMAND_RESPONSE,
                    ProtobufQuestXCommandResponse.Descriptor,
                    QuestXConstants.Network.NtPublisherSettings
                );

            // Video streams - dashboards use this to expose video streams
            streamsPublisher = ntInstance.GetStringArrayPublisher(
                QuestXConstants.Topics.VIDEO_STREAMS,
                QuestXConstants.Network.NtPublisherSettings
            );

            // Web dashboard URL - lets users find the headset's address without wearing it
            webUrlPublisher = ntInstance.GetStringPublisher(
                QuestXConstants.Topics.WEB_URL,
                QuestXConstants.Network.NtPublisherSettings
            );
            PublishWebUrl("");

            // Publish the QuestX application version
            ntInstance
                .GetStringEntry(
                    QuestXConstants.Topics.VERSION,
                    new PubSubOptions { Periodic = 0.01 }
                )
                .Set(Application.version);

            /*
             * SUBSCRIBER SETUP - Quest receives data FROM robot
             * Robot can send commands like pose resets, calibration requests, etc.
             */
            redAllianceSubscriber = ntInstance.GetBooleanSubscriber(
                "/FMSInfo/IsRedAlliance",
                PubSubOptions.AllDefault
            );

            commandRequestSubscriber = ntInstance.GetProtobufSubscriber<ProtobufQuestXCommand>(
                QuestXConstants.Topics.COMMAND_REQUEST,
                "questx.protos.commands.ProtobufQuestXCommand",
                new PubSubOptions
                {
                    SendAll = true,
                    KeepDuplicates = true,
                    Periodic = 0.005,
                    PollStorage = 20,
                }
            );

            // Attach local methods to config event methods
            configManager.OnTeamNumberChanged += OnTeamNumberChanged;
            configManager.OnDebugIpOverrideChanged += OnDebugIpOverrideChanged;
            configManager.OnEnableDebugLoggingChanged += OnEnableDebugLoggingChanged;
        }

        #region Properties

        /// <inheritdoc />
        public string IpAddress => localAddress.Address ?? LocalAddressMonitor.NoAddress;

        /// <summary>
        /// Gets whether the connection is currently established.
        /// </summary>
        public bool IsConnected => ntInstance.IsConnected();

        /// <summary>
        /// Gets whether the connection is currently established.
        /// </summary>
        public bool IsReadyToConnect => teamNumberSet || ipAddressSet;

        /// <summary>
        /// Gets the current NT time
        /// </summary>
        public long NtNow => ntInstance.Now();

        /// <inheritdoc />
        public ulong VioPublishCount { get; private set; }

        /// <inheritdoc />
        public ulong AprilTagPublishCount { get; private set; }

        /// <summary>Local v4 writes rejected by NetworkTables; successful writes do not confirm receipt.</summary>
        public ulong AprilTagV4PublishFailureCount { get; private set; }

        /// <inheritdoc />
        public bool? IsRedAlliance
        {
            get
            {
                if (!IsConnected)
                {
                    return null;
                }
                // The subscriber returns the default only when no value exists, so two reads
                // with opposite defaults disagree exactly when nothing has been published.
                bool readFalse = redAllianceSubscriber.Get(false);
                bool readTrue = redAllianceSubscriber.Get(true);
                return readFalse == readTrue ? readFalse : null;
            }
        }
        #endregion

        #region Event Publishers
        /// <summary>
        /// Fired when connection is established.
        /// </summary>
        public event Action OnConnect;

        /// <summary>
        /// Fired when connection is lost.
        /// </summary>
        public event Action OnDisconnect;

        /// <summary>
        /// Fired when the local IP address changes.
        /// </summary>
        public event Action<string> OnIpAddressChanged;
        #endregion

        #region Event Subscribers
        /// <summary>
        /// Changes to team number resolution mode and triggers an asynchronous connection reset
        /// </summary>
        /// <param name="teamNumber">The team number to resolve</param>
        private void OnTeamNumberChanged(int teamNumber)
        {
            // Skip if -1 (indicates debug IP override in use)
            if (teamNumber.Equals(-1))
                return;

            // Standard mode: Use team number to resolve robot address
            QueuedLogger.Log($"Setting Team number to {teamNumber}");
            ntInstance.SetTeamNumber(teamNumber, QuestXConstants.Network.NT_SERVER_PORT);
            teamNumberSet = true;
            ipAddressSet = false;
        }

        /// <summary>
        /// Changes to direct IP override mode for debugging.
        /// </summary>
        /// <param name="ipOverride">The IP address to connect to directly</param>
        private void OnDebugIpOverrideChanged(string ipOverride)
        {
            // Skip if empty (indicates team number being used)
            if (string.IsNullOrEmpty(ipOverride))
                return;

            // Debug mode: Use direct IP address (bypasses team number resolution)
            QueuedLogger.LogWarning(
                $"[DEBUG MODE] Using IP Override: {ipOverride} - This should only be used for debugging!"
            );
            ntInstance.SetAddresses(
                new (string addr, int port)[]
                {
                    (ipOverride, QuestXConstants.Network.NT_SERVER_PORT),
                }
            );
            ipAddressSet = true;
            teamNumberSet = false;
        }

        /// <summary>
        /// Updates the internal logger's verbosity level.
        /// </summary>
        /// <param name="enableDebugLogging">Whether to enable debug-level logging</param>
        private void OnEnableDebugLoggingChanged(bool enableDebugLogging)
        {
            ntInstanceLogger = ntInstance.CreateLogger(
                enableDebugLogging
                    ? QuestXConstants.Logging.NT_LOG_LEVEL_MIN_DEBUG
                    : QuestXConstants.Logging.NT_LOG_LEVEL_MIN_STANDARD,
                QuestXConstants.Logging.NT_LOG_LEVEL_MAX
            );
        }
        #endregion

        #region Data Publishing Methods

        /// <summary>
        /// Reusable frame data object to avoid allocations
        /// </summary>
        private readonly ProtobufQuestXFrameData frameData = new();

        /// <summary>
        /// Reusable VIO message and its next sequence ID.
        /// </summary>
        private readonly ProtobufQuestXVioData vioData = new();
        private ulong nextVioSequenceId;

        /// <summary>
        /// Reusable AprilTag message, uncertainty message, and next sequence ID.
        /// </summary>
        private readonly ProtobufQuestXAprilTagData aprilTagData = new();

        /// <summary>
        /// Serializes worker publications and their reusable protocol buffers. Main-thread
        /// telemetry reads a completed snapshot and never acquires this publication lock.
        /// </summary>
        private readonly object aprilTagLock = new object();
        private readonly object aprilTagDiagnosticsLock = new object();
        private AprilTagTelemetrySnapshot aprilTagSnapshot;

        private sealed class AprilTagTelemetrySnapshot
        {
            public readonly double[] Measurement;
            public readonly Pose3d Pose;
            public readonly ulong PublishCount;
            public readonly long CaptureMicros;
            public readonly int FirstId;

            public AprilTagTelemetrySnapshot(
                double[] measurement,
                Pose3d pose,
                ulong publishCount,
                long captureMicros,
                int firstId
            )
            {
                // Only this snapshot owns the copy; the worker reuses its v3 buffer.
                // One object and 26 + 2 * tagCount doubles per measurement, not per VIO tick.
                Measurement = (double[])measurement.Clone();
                Pose = pose; // Pose3d and its translation/rotation are immutable.
                PublishCount = publishCount;
                CaptureMicros = captureMicros;
                FirstId = firstId;
            }
        }

        private readonly ProtobufQuestXPoseStdDevs aprilTagPoseStdDevs = new();
        private ulong nextAprilTagSequenceId;

        /// <summary>
        /// Publishes current frame data to NetworkTables including position, rotation, and timing information
        /// </summary>
        /// <param name="frameCount">Unity frame count</param>
        /// <param name="timeStamp">Unity time stamp</param>
        /// <param name="pose">Current VR headset position</param>
        /// <param name="isTracking">Is the headset is currently tracking its position</param>
        [Obsolete("Use PublishVioData and PublishAprilTagData instead.")]
        public void PublishFrameData(int frameCount, double timeStamp, Pose3d pose, bool isTracking)
        {
            frameData.FrameCount = frameCount;
            frameData.Timestamp = timeStamp;
            frameData.Pose3D = pose.ToProtobuf();
            frameData.IsTracking = isTracking;

            // Publish data
            frameDataPublisher.Set(frameData);
        }

        /// <inheritdoc />
        public void PublishVioData(
            long captureTimestampMicros,
            double appTimestamp,
            Pose3d pose,
            bool isTracking,
            uint trackingEpoch
        )
        {
            vioData.SequenceId = nextVioSequenceId++;
            vioData.CaptureTimestampMicros = captureTimestampMicros;
            vioData.AppTimestamp = appTimestamp;
            vioData.Pose3D = pose.ToProtobuf();
            vioData.IsTracking = isTracking;
            vioData.TrackingEpoch = trackingEpoch;

            // Keep v1 available to existing robots during the migration release.
            vioDataPublisher.Set(vioData, captureTimestampMicros);
            MeasurementWireFormat.WriteVio(
                vioV2,
                vioData.SequenceId,
                captureTimestampMicros,
                appTimestamp,
                pose,
                isTracking,
                trackingEpoch
            );
            if (vioV2Publisher.Set(vioV2, captureTimestampMicros))
            {
                VioPublishCount++;
            }
            vioTelemetry.Update(vioV2, pose);
            PublishTelemetry();
            // Send the sample now rather than at ntcore's next periodic send, so the robot
            // sees it as early as possible.
            ntInstance.Flush();
        }

        /// <inheritdoc />
        public void PublishVioQuality(
            long captureTimestampMicros,
            double appTimestamp,
            VioQualitySample quality
        )
        {
            MeasurementWireFormat.WriteVioQuality(
                vioQualityV2,
                nextVioQualitySequenceId++,
                captureTimestampMicros,
                appTimestamp,
                quality
            );
            vioQualityPublisher.Set(vioQualityV2, captureTimestampMicros);
        }

        /// <inheritdoc />
        public void PublishAprilTagData(AprilTagMeasurement measurement)
        {
            lock (aprilTagLock)
            {
                PublishAprilTagDataLocked(measurement);
            }
            // Send now rather than at ntcore's next periodic send.
            ntInstance.Flush();
        }

        private void PublishAprilTagDataLocked(AprilTagMeasurement measurement)
        {
            long captureTimestampMicros = measurement.CaptureTimestampMicros;
            double appTimestamp = measurement.AppTimestamp;
            Pose3d pose = measurement.Pose;
            int[] tagIds = measurement.TagIds;
            uint inlierCount = (uint)measurement.InlierCount;
            uint cornerCount = (uint)measurement.CornerCount;
            double inlierRatio = measurement.InlierRatio;
            double averageTagDistanceMeters = measurement.AverageTagDistanceMeters;
            double xStdDevMeters = measurement.XStdDevMeters;
            double yStdDevMeters = measurement.YStdDevMeters;
            double zStdDevMeters = measurement.ZStdDevMeters;
            double rollStdDevRadians = measurement.RollStdDevRadians;
            double pitchStdDevRadians = measurement.PitchStdDevRadians;
            double yawStdDevRadians = measurement.YawStdDevRadians;

            aprilTagData.SequenceId = nextAprilTagSequenceId++;
            aprilTagData.CaptureTimestampMicros = captureTimestampMicros;
            aprilTagData.AppTimestamp = appTimestamp;
            aprilTagData.Pose3D = pose.ToProtobuf();
            aprilTagData.TagIds.Clear();
            if (tagIds != null)
            {
                aprilTagData.TagIds.Add(tagIds);
            }
            aprilTagData.TagCount = (uint)(tagIds?.Length ?? 0);
            aprilTagData.InlierCount = inlierCount;
            aprilTagData.CornerCount = cornerCount;
            aprilTagData.InlierRatio = inlierRatio;
            aprilTagData.AverageTagDistanceMeters = averageTagDistanceMeters;

            aprilTagPoseStdDevs.XMeters = xStdDevMeters;
            aprilTagPoseStdDevs.YMeters = yStdDevMeters;
            aprilTagPoseStdDevs.ZMeters = zStdDevMeters;
            aprilTagPoseStdDevs.RollRadians = rollStdDevRadians;
            aprilTagPoseStdDevs.PitchRadians = pitchStdDevRadians;
            aprilTagPoseStdDevs.YawRadians = yawStdDevRadians;
            aprilTagData.PoseStdDevs = aprilTagPoseStdDevs;

            aprilTagDataPublisher.Set(aprilTagData, captureTimestampMicros);
            int length = MeasurementWireFormat.AprilTagHeaderLength + (tagIds?.Length ?? 0);
            if (aprilTagV2.Length != length)
                aprilTagV2 = new double[length];
            MeasurementWireFormat.WriteAprilTag(
                aprilTagV2,
                aprilTagData.SequenceId,
                captureTimestampMicros,
                appTimestamp,
                pose,
                tagIds,
                inlierCount,
                cornerCount,
                inlierRatio,
                averageTagDistanceMeters,
                xStdDevMeters,
                yStdDevMeters,
                zStdDevMeters,
                rollStdDevRadians,
                pitchStdDevRadians,
                yawStdDevRadians
            );
            aprilTagV2Publisher.Set(aprilTagV2, captureTimestampMicros);

            int tagCount = tagIds?.Length ?? 0;
            var tagAmbiguities = new double[tagCount];
            for (int i = 0; i < tagCount; i++)
            {
                tagAmbiguities[i] = measurement.Observations[i].Ambiguity;
            }
            // Same value as /QuestX/limelight/latencyMilliseconds, so one atomic v3 read carries it.
            long now = NtNow;
            double processingLatencyMilliseconds = Math.Max(
                0,
                (now - captureTimestampMicros) / 1000.0
            );
            int v3Length = MeasurementWireFormat.AprilTagV3HeaderLength + 2 * tagCount;
            if (aprilTagV3.Length != v3Length)
                aprilTagV3 = new double[v3Length];
            MeasurementWireFormat.WriteAprilTagV3(
                aprilTagV3,
                aprilTagData.SequenceId,
                captureTimestampMicros,
                appTimestamp,
                pose,
                tagIds,
                inlierCount,
                cornerCount,
                inlierRatio,
                averageTagDistanceMeters,
                xStdDevMeters,
                yStdDevMeters,
                zStdDevMeters,
                rollStdDevRadians,
                pitchStdDevRadians,
                yawStdDevRadians,
                measurement.Ambiguity,
                measurement.ReprojectionErrorPixels,
                processingLatencyMilliseconds,
                tagAmbiguities
            );
            aprilTagV3Publisher.Set(aprilTagV3, captureTimestampMicros);
            int v4Length = MeasurementWireFormat.AprilTagV4HeaderLength + 2 * tagCount;
            if (aprilTagV4.Length != v4Length)
                aprilTagV4 = new double[v4Length];
            MeasurementWireFormat.WriteAprilTagV4(
                aprilTagV4,
                aprilTagData.SequenceId,
                captureTimestampMicros,
                appTimestamp,
                pose,
                measurement.Covariance,
                tagIds,
                inlierCount,
                cornerCount,
                inlierRatio,
                averageTagDistanceMeters,
                measurement.Ambiguity,
                measurement.ReprojectionErrorPixels,
                processingLatencyMilliseconds,
                tagAmbiguities
            );
            if (aprilTagV4Publisher.Set(aprilTagV4, captureTimestampMicros))
            {
                AprilTagPublishCount++;
            }
            else
            {
                AprilTagV4PublishFailureCount++;
                // Report the first failure and powers of two without flooding the capture loop.
                ulong failures = AprilTagV4PublishFailureCount;
                if ((failures & (failures - 1)) == 0)
                {
                    QueuedLogger.LogWarning(
                        $"AprilTag v4 local publish failed ({failures} failures)"
                    );
                }
            }
            limelightTelemetry.Update(measurement, captureTimestampMicros, now);
            // Exchange all diagnostic state together, including when v4 Set returns false.
            // No reusable buffer or worker-owned mutable state escapes to the main thread.
            Volatile.Write(
                ref aprilTagSnapshot,
                new AprilTagTelemetrySnapshot(
                    aprilTagV3,
                    pose,
                    AprilTagPublishCount,
                    captureTimestampMicros,
                    measurement.Observations.Length > 0 ? measurement.Observations[0].Id : -1
                )
            );
        }

        public void PublishAprilTagDiagnostics(AprilTagDiagnosticsSnapshot diagnostics)
        {
            // Diagnostics have separate topics and no measurement state. Keep concurrent
            // diagnostic batches serialized without waiting for worker publications.
            lock (aprilTagDiagnosticsLock)
            {
                limelightTelemetry.Diagnostics(diagnostics);
            }
        }

        private void PublishTelemetry()
        {
            long now = NtNow;
            if (now < nextTelemetryMicros)
                return;
            nextTelemetryMicros = now + 100_000;
            vioTelemetry.Publish(now, VioPublishCount);
            var snapshot = Volatile.Read(ref aprilTagSnapshot);
            aprilTagTelemetry.Publish(
                now,
                snapshot?.PublishCount ?? 0,
                snapshot?.Measurement,
                snapshot?.Pose
            );
            limelightTelemetry.Publish(now, snapshot?.CaptureMicros ?? 0, snapshot?.FirstId ?? -1);
        }

        /// <summary>
        /// Reusable device data object to avoid allocations
        /// </summary>
        private readonly ProtobufQuestXDeviceData deviceData = new();

        /// <summary>
        /// Publishes current device data to NetworkTables including tracking status and battery level
        /// </summary>
        /// <param name="trackingLostCounter">Number of times tracking was lost this session</param>
        /// <param name="batteryPercent">Current battery percentage</param>
        public void PublishDeviceData(int trackingLostCounter, int batteryPercent)
        {
            deviceData.TrackingLostCounter = trackingLostCounter;
            deviceData.BatteryPercent = batteryPercent;

            // Publish data
            deviceDataPublisher.Set(deviceData);
            batteryPercentPublisher.Set(batteryPercent);
        }

        /// <summary>
        /// Publishes the web dashboard URL to NetworkTables when it changes.
        /// </summary>
        /// <param name="webUrl">Dashboard URL, or null/empty when the headset has no address</param>
        public void PublishWebUrl(string webUrl)
        {
            webUrl ??= "";
            if (webUrl == lastWebUrl)
                return;

            if (webUrlPublisher.Set(webUrl))
            {
                lastWebUrl = webUrl;
            }
        }

        /// <summary>
        /// Returns a camera source with the given sub-topic
        /// </summary>
        /// <param name="name">The sub-topic for the camera source</param>
        /// <returns>An ICameraSource for the given topic or null</returns>
        public INtCameraSource CreateCameraSource(string name)
        {
            return new NtCameraSource(ntInstance, name);
        }

        #endregion

        #region Command Processing

        /// <summary>
        /// Default command returned when no command is available from the robot
        /// </summary>
        private readonly ProtobufQuestXCommand defaultCommand = new()
        {
            Type = QuestXCommandType.CommandTypeUnspecified,
            CommandId = 0,
        };

        /// <summary>
        /// Gets all command requests from the robot since the last read, or an empty array if none available
        /// </summary>
        /// <returns>All command requests since the last read</returns>
        public TimestampedValue<ProtobufQuestXCommand>[] GetCommandRequests()
        {
            return commandRequestSubscriber.ReadQueueValues();
        }

        /// <summary>
        /// Sends a command response back to the robot
        /// </summary>
        /// <param name="response">The response containing success status and any error messages</param>
        private void SetCommandResponse(ProtobufQuestXCommandResponse response)
        {
            commandResponsePublisher.Set(response);
        }

        /// <summary>
        /// Sends a command processing success response back to the robot
        /// </summary>
        /// <param name="commandId">command_id</param>
        public void SendCommandSuccessResponse(uint commandId)
        {
            SetCommandResponse(
                new ProtobufQuestXCommandResponse { CommandId = commandId, Success = true }
            );
        }

        /// <summary>
        /// Sends a command processing error response back to the robot
        /// </summary>
        /// <param name="commandId">command_id</param>
        /// <param name="errorMessage">error message</param>
        public void SendCommandErrorResponse(uint commandId, string errorMessage)
        {
            SetCommandResponse(
                new ProtobufQuestXCommandResponse
                {
                    CommandId = commandId,
                    Success = false,
                    ErrorMessage = errorMessage,
                }
            );
        }

        #endregion

        #region Lifecycle
        /// <summary>
        /// Populates event subscribers with initial data. Should be called AFTER all other classes subscribe.
        /// </summary>
        public void Initialize()
        {
            // Starts the first lookup; the address arrives through a later Periodic.
            localAddress.Poll();
            OnIpAddressChanged?.Invoke(IpAddress);
            if (IsConnected)
            {
                OnConnect?.Invoke();
            }
            else
            {
                OnDisconnect?.Invoke();
            }
        }

        /// <summary>
        /// Performs periodic updates including connection polling and logging.
        /// </summary>
        public void Periodic()
        {
            PublishTelemetry();
            PollForNewIpAddress();
            PollForConnectionStatus();
            PollInternalNtLog();
        }

        /// <summary>
        /// Applies a finished local address lookup and raises the change event on this thread.
        /// </summary>
        private void PollForNewIpAddress()
        {
            localAddress.Poll();
        }

        private void OnLocalAddressChanged(string address)
        {
            QueuedLogger.Log(
                address == null ? "Lost local IP address" : $"Got new IP Address: {address}"
            );
            OnIpAddressChanged?.Invoke(IpAddress);
        }

        /// <summary>
        /// Monitors connection state and raises connect/disconnect events.
        /// </summary>
        private void PollForConnectionStatus()
        {
            switch (IsConnected)
            {
                case true when !wasConnected:
                    // If we connected and weren't previously
                    QueuedLogger.Log("Connected to NT4");
                    wasConnected = true;
                    OnConnect?.Invoke();
                    break;
                case false when wasConnected:
                    // If we disconnected and were previously
                    QueuedLogger.Log("Disconnected from NT4");
                    wasConnected = false;
                    OnDisconnect?.Invoke();
                    break;
            }
        }

        /// <summary>
        /// Processes and logs any pending NetworkTables internal messages.
        /// Respects the enableDebugLogging tunable - when disabled, only WARNING and above are logged.
        /// </summary>
        private void PollInternalNtLog()
        {
            if (ntInstanceLogger == null)
            {
                QueuedLogger.LogWarning("NT Instance logger not initialized");
                return;
            }
            var messages = ntInstanceLogger.PollForMessages();
            if (messages == null)
                return;

            foreach (var message in messages)
            {
                QueuedLogger.Log($"[NTCoreInternal/{message.filename}] {message.message}");
            }
        }
        #endregion
    }
}
