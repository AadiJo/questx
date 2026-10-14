/*
* QUESTX
  https://github.com/QuestX
* Copyright (C) 2025 QuestX
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the MIT License as published.
*/
package gg.questx.questx;

import static edu.wpi.first.units.Units.Microseconds;
import static edu.wpi.first.units.Units.Milliseconds;
import static edu.wpi.first.units.Units.Seconds;

import edu.wpi.first.math.geometry.Pose3d;
import edu.wpi.first.math.geometry.proto.Pose3dProto;
import edu.wpi.first.math.proto.Geometry3D;
import edu.wpi.first.networktables.DoubleArraySubscriber;
import edu.wpi.first.networktables.NetworkTable;
import edu.wpi.first.networktables.NetworkTableInstance;
import edu.wpi.first.networktables.ProtobufPublisher;
import edu.wpi.first.networktables.ProtobufSubscriber;
import edu.wpi.first.networktables.PubSubOption;
import edu.wpi.first.networktables.StringSubscriber;
import edu.wpi.first.wpilibj.DriverStation;
import edu.wpi.first.wpilibj.Timer;
import gg.questx.questx.protos.generated.Commands;
import gg.questx.questx.protos.generated.Data;
import gg.questx.questx.protos.wpilib.AprilTagDataProto;
import gg.questx.questx.protos.wpilib.CommandProto;
import gg.questx.questx.protos.wpilib.CommandResponseProto;
import gg.questx.questx.protos.wpilib.DeviceDataProto;
import gg.questx.questx.protos.wpilib.FrameDataProto;
import gg.questx.questx.protos.wpilib.VioDataProto;
import java.util.Arrays;
import java.util.Objects;
import java.util.Optional;
import java.util.OptionalDouble;
import java.util.OptionalInt;
import java.util.OptionalLong;
import java.util.function.Consumer;
import java.util.function.IntConsumer;

/**
 * The QuestX class provides a comprehensive interface for communicating with an Oculus/Meta Quest
 * VR headset for robot localization and tracking in FRC robotics applications.
 *
 * <p>This class handles all aspects of Quest-robot communication including:
 *
 * <ul>
 *   <li>Real-time pose tracking and localization data
 *   <li>Command sending and response handling
 *   <li>Device status monitoring (battery, tracking state, connectivity)
 *   <li>NetworkTables-based communication protocol
 *   <li>Event-driven callbacks for connection, tracking, battery, and command state changes
 * </ul>
 *
 * <h2>Basic Usage</h2>
 *
 * <pre>{@code
 * // Create QuestX instance
 * QuestX questX = new QuestX();
 *
 * // Register callbacks
 * questX.onConnected(() -> System.out.println("Quest connected!"));
 * questX.onDisconnected(() -> DriverStation.reportWarning("Quest disconnected!", false));
 * questX.onTrackingLost(() -> DriverStation.reportWarning("Quest tracking lost!", false));
 * questX.onTrackingAcquired(() -> System.out.println("Quest tracking acquired!"));
 * questX.onLowBattery(20, level -> DriverStation.reportWarning("Quest battery low: " + level + "%", false));
 *
 * // In robot periodic methods
 * public void robotPeriodic() {
 *   questX.commandPeriodic(); // Process command responses and fire callbacks
 *
 *   for (VioFrame frame : questX.getAllUnreadVioFrames()) {
 *     // Update the robot-owned transform from the local VIO frame to the field frame.
 *   }
 *   for (AprilTagFrame frame : questX.getAllUnreadAprilTagFrames()) {
 *     // Apply robot-side rejection, then fuse frame.fieldPose3d(),
 *     // frame.measurementTimestamp(), and frame.pose2dStdDevs().
 *   }
 * }
 * }</pre>
 *
 * <h2>Coordinate Systems</h2>
 *
 * <p>Both streams use WPILib's axis and unit conventions:
 *
 * <ul>
 *   <li><strong>X-axis:</strong> Forward direction (towards opposing alliance)
 *   <li><strong>Y-axis:</strong> Left direction (when facing forward)
 *   <li><strong>Rotation:</strong> Counter-clockwise positive (standard mathematical convention)
 *   <li><strong>Units:</strong> Meters for translation, radians for rotation
 * </ul>
 *
 * <p>{@link VioFrame} poses are expressed in the Quest's local tracking frame. {@link
 * AprilTagFrame} poses are expressed in the field frame. QuestXLib does not fuse the streams; robot
 * code owns alignment, rejection, and fusion.
 *
 * <h2>Threading and Performance</h2>
 *
 * <p>This class is designed for use in FRC robot code and follows WPILib threading conventions:
 *
 * <ul>
 *   <li>All methods are thread-safe for typical FRC usage patterns
 *   <li>Uses cached objects to minimize garbage collection pressure
 *   <li>NetworkTables handles the underlying communication asynchronously
 *   <li>Call {@link #commandPeriodic()} regularly to process command responses and fire callbacks
 * </ul>
 *
 * <h2>Error Handling</h2>
 *
 * <p>The class provides robust error handling:
 *
 * <ul>
 *   <li>Methods return {@link java.util.Optional} types when data might not be available
 *   <li>Connection status can be checked with {@link #isConnected()}
 *   <li>Tracking status can be monitored with {@link #isTracking()}
 *   <li>Command failures are reported through DriverStation error logging and via {@link
 *       #onCommandFailure(Consumer)}
 * </ul>
 *
 * @see VioFrame
 * @see AprilTagFrame
 * @see edu.wpi.first.math.geometry.Pose2d
 * @see edu.wpi.first.networktables.NetworkTableInstance
 * @since 2025.1.0
 * @author QuestX Team
 */
public class QuestX {

  /** Interval at which to check and log if the QuestXLib version matches the QuestX app version */
  private static final double VERSION_CHECK_INTERVAL_SECONDS = 5.0;

  /** Battery percentage at or below which {@link #onLowBatteryCallback} fires */
  private int lowBatteryThreshold = 20;

  /** NetworkTable instance used for communication */
  private final NetworkTableInstance nt4Instance = NetworkTableInstance.getDefault();

  /** NetworkTable for Quest navigation data */
  private final NetworkTable questXTable = nt4Instance.getTable("QuestX");

  /** Protobuf instance for CommandResponse */
  private final CommandResponseProto commandResponseProto = new CommandResponseProto();

  /** Protobuf instance for Command */
  private final CommandProto commandProto = new CommandProto();

  /** Protobuf instance for Pose3d */
  private final Pose3dProto pose3dProto = new Pose3dProto();

  /** Protobuf instance for device data */
  private final DeviceDataProto deviceDataProto = new DeviceDataProto();

  /** Protobuf instance for frame data */
  private final FrameDataProto frameDataProto = new FrameDataProto();

  /** Protobuf instance for local-frame VIO data */
  private final VioDataProto vioDataProto = new VioDataProto();

  /** Protobuf instance for field-relative AprilTag measurements */
  private final AprilTagDataProto aprilTagDataProto = new AprilTagDataProto();

  /** Subscriber for command response */
  private final ProtobufSubscriber<Commands.ProtobufQuestXCommandResponse> responseSubscriber =
      questXTable
          .getProtobufTopic("response", commandResponseProto)
          .subscribe(
              Commands.ProtobufQuestXCommandResponse.newInstance(),
              PubSubOption.periodic(0.05),
              PubSubOption.sendAll(true),
              PubSubOption.pollStorage(20));

  /**
   * Subscriber for the legacy Quest-side fused frame data.
   *
   * @deprecated New integrations must consume the separate VIO and AprilTag measurement streams.
   */
  @Deprecated(forRemoval = false, since = "2026.3.0")
  private final ProtobufSubscriber<Data.ProtobufQuestXFrameData> frameDataSubscriber =
      questXTable
          .getProtobufTopic("frameData", frameDataProto)
          .subscribe(
              Data.ProtobufQuestXFrameData.newInstance(),
              PubSubOption.periodic(0.01),
              PubSubOption.sendAll(true),
              PubSubOption.pollStorage(20));

  private final ProtobufSubscriber<Data.ProtobufQuestXVioData> vioDataSubscriber;
  private final ProtobufSubscriber<Data.ProtobufQuestXAprilTagData> aprilTagDataSubscriber;
  private final DoubleArraySubscriber vioV2Subscriber;
  private final DoubleArraySubscriber aprilTagV2Subscriber;

  /** Subscriber for device data */
  private final ProtobufSubscriber<Data.ProtobufQuestXDeviceData> deviceDataSubscriber =
      questXTable
          .getProtobufTopic("deviceData", deviceDataProto)
          .subscribe(Data.ProtobufQuestXDeviceData.newInstance());

  /** Subscriber for QuestX app version */
  private final StringSubscriber versionSubscriber =
      questXTable.getStringTopic("version").subscribe("unknown");

  /** Publisher for command requests */
  private final ProtobufPublisher<Commands.ProtobufQuestXCommand> requestPublisher =
      questXTable.getProtobufTopic("request", commandProto).publish();

  /** Cached request to lessen GC pressure */
  private final Commands.ProtobufQuestXCommand cachedCommandRequest =
      Commands.ProtobufQuestXCommand.newInstance();

  /** Cached pose reset request to lessen GC pressure */
  private final Commands.ProtobufQuestXPoseResetPayload cachedPoseResetPayload =
      Commands.ProtobufQuestXPoseResetPayload.newInstance();

  /** Cached proto pose (for reset requests) to lessen GC pressure */
  private final Geometry3D.ProtobufPose3d cachedProtoPose = Geometry3D.ProtobufPose3d.newInstance();

  /** Last sent request id */
  private int lastSentRequestId = 0;

  /** True to check for QuestXLib and QuestX version match at an interval */
  private boolean versionCheckEnabled = true;

  /** The last time QuestXLib and QuestX were checked for a match */
  private double lastVersionCheckTime = 0.0;

  // Callback state tracking

  /** Cached connection state from the previous commandPeriodic() call */
  private boolean lastConnectedState = false;

  /** Cached tracking state from the previous commandPeriodic() call */
  private boolean lastTrackingState = false;

  /** Whether the low-battery callback has already fired for the current threshold crossing */
  private boolean lowBatteryFired = false;

  // Callbacks

  /** Fired once when the Quest transitions from disconnected → connected */
  private Runnable onConnectedCallback = null;

  /** Fired once when the Quest transitions from connected → disconnected */
  private Runnable onDisconnectedCallback = null;

  /** Fired once when the Quest transitions from not-tracking → tracking */
  private Runnable onTrackingAcquiredCallback = null;

  /** Fired once when the Quest transitions from tracking → not-tracking */
  private Runnable onTrackingLostCallback = null;

  /**
   * Fired once when battery drops at or below {@link #lowBatteryThreshold}. Resets when battery
   * rises above the threshold again.
   */
  private IntConsumer onLowBatteryCallback = null;

  /** Fired for each command response where {@code getSuccess() == true} */
  private Consumer<Commands.ProtobufQuestXCommandResponse> onSuccessCallback = null;

  /** Fired for each command response where {@code getSuccess() == false} */
  private Consumer<Commands.ProtobufQuestXCommandResponse> onFailureCallback = null;

  /**
   * Creates a new QuestX instance for communicating with a Quest headset.
   *
   * <p>This constructor initializes all necessary NetworkTables subscribers and publishers for
   * communication with the Quest device. The instance is ready to receive separate local VIO and
   * field-relative AprilTag measurements immediately.
   *
   * <p>The constructor sets up:
   *
   * <ul>
   *   <li>NetworkTables communication on the "QuestX" table
   *   <li>Version 2 atomic numeric arrays for measurements
   *   <li>Cached objects to minimize garbage collection
   *   <li>Subscribers for VIO data, AprilTag data, device data, and command responses
   *   <li>Publisher for sending commands to the Quest
   * </ul>
   */
  public QuestX() {
    this(MeasurementTransport.V2);
  }

  /**
   * Creates an instance using exactly one measurement transport. Select LEGACY_PROTOBUF for older
   * Quest apps. Transport selection never falls back or combines queues automatically.
   *
   * @param transport measurement transport supported by the installed Quest app
   */
  public QuestX(MeasurementTransport transport) {
    Objects.requireNonNull(transport, "transport");
    var options =
        new PubSubOption[] {
          PubSubOption.periodic(0.01), PubSubOption.sendAll(true), PubSubOption.pollStorage(20)
        };
    if (transport == MeasurementTransport.V2) {
      vioV2Subscriber = questXTable.getDoubleArrayTopic("v2/vio").subscribe(new double[0], options);
      aprilTagV2Subscriber =
          questXTable.getDoubleArrayTopic("v2/apriltag").subscribe(new double[0], options);
      vioDataSubscriber = null;
      aprilTagDataSubscriber = null;
    } else {
      vioV2Subscriber = null;
      aprilTagV2Subscriber = null;
      vioDataSubscriber =
          questXTable
              .getProtobufTopic("vioData", vioDataProto)
              .subscribe(Data.ProtobufQuestXVioData.newInstance(), options);
      aprilTagDataSubscriber =
          questXTable
              .getProtobufTopic("aprilTagData", aprilTagDataProto)
              .subscribe(Data.ProtobufQuestXAprilTagData.newInstance(), options);
    }
  }

  // Callback registration

  /**
   * Registers a callback that fires once when the Quest headset transitions from disconnected to
   * connected. The callback is evaluated each {@link #commandPeriodic()} call.
   *
   * @param callback Runnable to invoke on connection
   */
  public void onConnected(Runnable callback) {
    this.onConnectedCallback = callback;
  }

  /**
   * Registers a callback that fires once when the Quest headset transitions from connected to
   * disconnected. The callback is evaluated each {@link #commandPeriodic()} call.
   *
   * @param callback Runnable to invoke on disconnection
   */
  public void onDisconnected(Runnable callback) {
    this.onDisconnectedCallback = callback;
  }

  /**
   * Registers a callback that fires once when the Quest headset transitions from not-tracking to
   * actively tracking. The callback is evaluated each {@link #commandPeriodic()} call.
   *
   * @param callback Runnable to invoke when tracking is acquired
   */
  public void onTrackingAcquired(Runnable callback) {
    this.onTrackingAcquiredCallback = callback;
  }

  /**
   * Registers a callback that fires once when the Quest headset transitions from actively tracking
   * to not-tracking. The callback is evaluated each {@link #commandPeriodic()} call.
   *
   * @param callback Runnable to invoke when tracking is lost
   */
  public void onTrackingLost(Runnable callback) {
    this.onTrackingLostCallback = callback;
  }

  /**
   * Registers a callback that fires once when the Quest battery level drops at or below the given
   * threshold. The callback will not fire again until the battery rises above the threshold and
   * drops back down.
   *
   * @param thresholdPercent Battery percentage (0–100) at or below which the callback fires
   * @param callback IntConsumer receiving the current battery percentage when the threshold is
   *     crossed
   */
  public void onLowBattery(int thresholdPercent, IntConsumer callback) {
    this.lowBatteryThreshold = thresholdPercent;
    this.onLowBatteryCallback = callback;
  }

  /**
   * Registers a callback that fires for each command response where the command succeeded. Called
   * from {@link #commandPeriodic()}.
   *
   * @param callback Consumer receiving the raw {@link Commands.ProtobufQuestXCommandResponse}
   */
  public void onCommandSuccess(Consumer<Commands.ProtobufQuestXCommandResponse> callback) {
    this.onSuccessCallback = callback;
  }

  /**
   * Registers a callback that fires for each command response where the command failed. Called from
   * {@link #commandPeriodic()}. The failure is also reported to the DriverStation regardless of
   * whether a callback is registered.
   *
   * @param callback Consumer receiving the raw {@link Commands.ProtobufQuestXCommandResponse}
   */
  public void onCommandFailure(Consumer<Commands.ProtobufQuestXCommandResponse> callback) {
    this.onFailureCallback = callback;
  }

  // Internal helpers

  /**
   * Checks the version of QuestXLib and compares it to the version of QuestX on the headset. If the
   * headset is connected and the versions don't match, a warning will be sent to the driverstation
   * at an interval.
   *
   * @see #VERSION_CHECK_INTERVAL_SECONDS
   * @see #getLibVersion()
   * @see #getQuestXVersion()
   */
  private void checkVersionMatch() {
    if (!versionCheckEnabled || !isConnected()) {
      return;
    }

    var currentTime = Timer.getTimestamp();
    if ((currentTime - lastVersionCheckTime) < VERSION_CHECK_INTERVAL_SECONDS) {
      return;
    }
    lastVersionCheckTime = currentTime;

    var libVersion = getLibVersion();
    var questXVersion = getQuestXVersion();

    if (!questXVersion.equals(libVersion)) {
      String warningMessage =
          String.format(
              "WARNING FROM QUESTX: QuestXLib version (%s) on your robot does not match QuestX app version (%s) on your headset. "
                  + "This may cause compatibility issues. Check the version of your vendordep and the app running on your headset.",
              libVersion, questXVersion);

      DriverStation.reportWarning(warningMessage, false);
    }
  }

  /**
   * Evaluates connection, tracking, and battery state against cached values and fires any
   * registered callbacks when state transitions are detected.
   */
  private void checkStateCallbacks() {
    // --- Connection state ---
    boolean connected = isConnected();
    if (connected != lastConnectedState) {
      if (connected) {
        if (onConnectedCallback != null) onConnectedCallback.run();
      } else {
        if (onDisconnectedCallback != null) onDisconnectedCallback.run();
      }
      lastConnectedState = connected;
    }

    // --- Tracking state ---
    boolean tracking = isTracking();
    if (tracking != lastTrackingState) {
      if (tracking) {
        if (onTrackingAcquiredCallback != null) onTrackingAcquiredCallback.run();
      } else {
        if (onTrackingLostCallback != null) onTrackingLostCallback.run();
      }
      lastTrackingState = tracking;
    }

    // --- Low battery ---
    if (onLowBatteryCallback != null) {
      getBatteryPercent()
          .ifPresent(
              level -> {
                if (level <= lowBatteryThreshold && !lowBatteryFired) {
                  onLowBatteryCallback.accept(level);
                  lowBatteryFired = true;
                } else if (level > lowBatteryThreshold) {
                  // Reset so the callback can fire again if battery dips below again
                  lowBatteryFired = false;
                }
              });
    }
  }

  // Public API

  /**
   * Turns the version check on or off. When on, a warning will be reported to the DriverStation if
   * the QuestXLib and QuestX app versions do not match.
   *
   * @param enabled true to enable version checking, false to disable it. Default is true.
   */
  public void setVersionCheckEnabled(boolean enabled) {
    this.versionCheckEnabled = enabled;
  }

  /**
   * Commands older Quest apps to apply a field-relative display-space pose offset.
   *
   * <p>This command does not field-align the split {@link VioFrame} stream. New robot code should
   * align local VIO and fuse field-relative {@link AprilTagFrame} measurements in its robot-side
   * estimator.
   *
   * <p>The command is sent asynchronously. Success or failure is reported via {@link
   * #onCommandSuccess(Consumer)} and {@link #onCommandFailure(Consumer)} respectively, and failures
   * are also logged to the DriverStation.
   *
   * @param pose The Quest's current field-relative pose in WPILib coordinates (meters for
   *     translation, radians for rotation)
   * @throws IllegalArgumentException if pose contains NaN or infinite values
   * @see #onCommandSuccess(Consumer)
   * @see #onCommandFailure(Consumer)
   * @see #isConnected()
   * @deprecated The split measurement architecture performs alignment and fusion on the robot.
   */
  @Deprecated(forRemoval = true, since = "2026.3.0")
  public void setPose(Pose3d pose) {
    cachedProtoPose.clear();
    pose3dProto.pack(cachedProtoPose, pose);
    cachedCommandRequest.clear();
    var requestToSend =
        cachedCommandRequest
            .setType(Commands.QuestXCommandType.POSE_RESET)
            .setCommandId(++lastSentRequestId)
            .setPoseResetPayload(cachedPoseResetPayload.clear().setTargetPose(cachedProtoPose));

    requestPublisher.set(requestToSend);
  }

  /**
   * Returns the Quest headset's current battery level as a percentage.
   *
   * <p>Battery level guidelines:
   *
   * <ul>
   *   <li><strong>80-100%:</strong> Excellent - full match capability
   *   <li><strong>50-80%:</strong> Good - normal operation expected
   *   <li><strong>20-50%:</strong> Fair - consider charging after match
   *   <li><strong>10-20%:</strong> Low - charge soon, monitor closely
   *   <li><strong>0-10%:</strong> Critical - immediate charging required
   * </ul>
   *
   * @return An {@link OptionalInt} containing the battery percentage (0-100), or empty if no device
   *     data is available or Quest is disconnected
   * @see #onLowBattery(int, IntConsumer)
   * @see #isConnected()
   */
  public OptionalInt getBatteryPercent() {
    Data.ProtobufQuestXDeviceData latestDeviceData = deviceDataSubscriber.get();
    if (latestDeviceData != null) {
      return OptionalInt.of(latestDeviceData.getBatteryPercent());
    }
    return OptionalInt.empty();
  }

  /**
   * Gets the current frame count from the legacy Quest-side fused stream.
   *
   * @return The frame count value, or empty if no frame data is available
   * @deprecated Use {@link #getVioSequenceId()} for the split measurement protocol.
   */
  @Deprecated(forRemoval = false, since = "2026.3.0")
  public OptionalInt getFrameCount() {
    Data.ProtobufQuestXFrameData latestFrameData = frameDataSubscriber.get();
    if (latestFrameData != null) {
      return OptionalInt.of(latestFrameData.getFrameCount());
    }
    return OptionalInt.empty();
  }

  /**
   * Gets the latest VIO sequence identifier.
   *
   * @return the unsigned sequence identifier represented as a Java {@code long}, or empty before
   *     the first VIO measurement arrives
   */
  public OptionalLong getVioSequenceId() {
    if (vioV2Subscriber != null) {
      var frame = getLatestVioFrame();
      return frame.isPresent() ? OptionalLong.of(frame.get().sequenceId()) : OptionalLong.empty();
    }
    var latestVioData = vioDataSubscriber.getAtomic();
    if (latestVioData.serverTime > 0) {
      return OptionalLong.of(latestVioData.value.getSequenceId());
    }
    return OptionalLong.empty();
  }

  /**
   * Gets the number of tracking lost events since the Quest connected to the robot.
   *
   * @return The tracking lost counter value, or empty if no device data is available
   */
  public OptionalInt getTrackingLostCounter() {
    Data.ProtobufQuestXDeviceData latestDeviceData = deviceDataSubscriber.get();
    if (latestDeviceData != null) {
      return OptionalInt.of(latestDeviceData.getTrackingLostCounter());
    }
    return OptionalInt.empty();
  }

  /**
   * Determines if the Quest headset is currently connected to the robot. Connection is determined
   * by how stale the last received VIO measurement from the Quest is. AprilTag detections may be
   * intermittent and are therefore not used as a connection heartbeat.
   *
   * @return Boolean indicating if the Quest is connected (true) or not (false)
   */
  public boolean isConnected() {
    if (vioV2Subscriber != null) {
      return getVioLatency() < 120;
    }
    var latestVioData = vioDataSubscriber.getAtomic();
    return latestVioData.serverTime > 0
        && Seconds.of(Timer.getTimestamp())
            .minus(Microseconds.of(latestVioData.serverTime))
            .lt(Milliseconds.of(120));
  }

  /**
   * Gets the age of the latest VIO measurement. Returns the time between the current robot time and
   * the measurement's NetworkTables server timestamp.
   *
   * @return The latency in milliseconds
   */
  public double getLatency() {
    return getVioLatency();
  }

  /**
   * Gets the age of the latest VIO measurement.
   *
   * @return measurement age in milliseconds
   */
  public double getVioLatency() {
    if (vioV2Subscriber != null) {
      var message = vioV2Subscriber.getAtomic();
      return MeasurementWireFormat.vio(message.value, message.serverTime) == null
          ? Double.POSITIVE_INFINITY
          : measurementAgeMillis(message.serverTime);
    }
    return measurementAgeMillis(vioDataSubscriber.getAtomic().serverTime);
  }

  /**
   * Gets the age of the latest AprilTag measurement.
   *
   * <p>Because AprilTag measurements are only published when a pose is solved, this age may grow
   * while the VIO connection remains healthy.
   *
   * @return measurement age in milliseconds
   */
  public double getAprilTagLatency() {
    if (aprilTagV2Subscriber != null) {
      var message = aprilTagV2Subscriber.getAtomic();
      return MeasurementWireFormat.aprilTag(message.value, message.serverTime) == null
          ? Double.POSITIVE_INFINITY
          : measurementAgeMillis(message.serverTime);
    }
    return measurementAgeMillis(aprilTagDataSubscriber.getAtomic().serverTime);
  }

  /**
   * Returns the Quest app's uptime timestamp for debugging and diagnostics.
   *
   * <p><strong>Important:</strong> For integration with a pose estimator, use the timestamp from
   * {@link VioFrame#measurementTimestamp()} instead.
   *
   * @return An {@link OptionalDouble} containing the Quest app uptime in seconds, or empty if no
   *     VIO data is available
   * @see VioFrame#measurementTimestamp()
   * @see #getAllUnreadVioFrames()
   */
  public OptionalDouble getAppTimestamp() {
    if (vioV2Subscriber != null) {
      var frame = getLatestVioFrame();
      return frame.isPresent()
          ? OptionalDouble.of(frame.get().appTimestamp())
          : OptionalDouble.empty();
    }
    var latestVioData = vioDataSubscriber.getAtomic();
    if (latestVioData.serverTime > 0) {
      return OptionalDouble.of(latestVioData.value.getAppTimestamp());
    }
    return OptionalDouble.empty();
  }

  /**
   * Gets the current tracking state of the Quest headset.
   *
   * <p><strong>Important:</strong> When tracking is lost, pose data becomes unreliable and should
   * not be used for robot control. Implement fallback localization methods (wheel odometry, vision,
   * etc.) for when Quest tracking is unavailable.
   *
   * @return {@code true} if the Quest is actively tracking and pose data is reliable, {@code false}
   *     if tracking is lost or no device data is available
   * @see #onTrackingLost(Runnable)
   * @see #onTrackingAcquired(Runnable)
   * @see #isConnected()
   */
  public boolean isTracking() {
    if (vioV2Subscriber != null) {
      return getLatestVioFrame().map(VioFrame::isTracking).orElse(false);
    }
    var vioData = vioDataSubscriber.getAtomic();
    if (vioData.serverTime > 0) {
      return vioData.value.getIsTracking();
    }
    return false;
  }

  /**
   * Retrieves all new pose frames received from the Quest since the last call to this method.
   *
   * <p><strong>Deprecated:</strong> This consumes the legacy {@code /QuestX/frameData} stream,
   * whose pose may already have been fused on the Quest. New robot code must consume {@link
   * #getAllUnreadVioFrames()} and {@link #getAllUnreadAprilTagFrames()} and fuse measurements once,
   * on the robot.
   *
   * <p><strong>Important:</strong> This method consumes the frame queue, so each frame is only
   * returned once. Call this method regularly (every robot loop) to avoid missing frames.
   *
   * @return Array of new {@link PoseFrame} objects received since the last call. Empty array if no
   *     new frames are available or Quest is disconnected.
   * @see PoseFrame
   * @see #isTracking()
   * @see #isConnected()
   * @deprecated Use {@link #getAllUnreadVioFrames()} and {@link #getAllUnreadAprilTagFrames()}.
   */
  @Deprecated(forRemoval = false, since = "2026.3.0")
  public PoseFrame[] getAllUnreadPoseFrames() {
    var frameDataArray = frameDataSubscriber.readQueue();
    var result = new PoseFrame[frameDataArray.length];
    for (int i = 0; i < result.length; i++) {
      var frameData = frameDataArray[i];
      result[i] =
          new PoseFrame(
              pose3dProto.unpack(frameData.value.getPose3D()),
              Microseconds.of(frameData.serverTime).in(Seconds),
              frameData.value.getTimestamp(),
              frameData.value.getFrameCount(),
              frameData.value.getIsTracking());
    }
    return result;
  }

  /**
   * Retrieves all new local-frame VIO measurements received since the last call.
   *
   * <p>This method consumes the VIO subscriber queue. The VIO pose is deliberately not fused or
   * field-aligned by QuestXLib. Use {@link VioFrame#measurementTimestamp()} as the timestamp for
   * estimator or interpolation work and treat {@link VioFrame#appTimestamp()} as diagnostic only.
   *
   * @return all unread VIO measurements in arrival order
   */
  public VioFrame[] getAllUnreadVioFrames() {
    if (vioV2Subscriber != null) {
      return Arrays.stream(vioV2Subscriber.readQueue())
          .map(message -> MeasurementWireFormat.vio(message.value, message.serverTime))
          .filter(Objects::nonNull)
          .toArray(VioFrame[]::new);
    }
    var messages = vioDataSubscriber.readQueue();
    var frames = new VioFrame[messages.length];
    for (int i = 0; i < messages.length; i++) {
      frames[i] = toVioFrame(messages[i].value, messages[i].serverTime);
    }
    return frames;
  }

  /**
   * Retrieves all new field-relative AprilTag measurements received since the last call.
   *
   * <p>This method consumes the AprilTag subscriber queue. Use {@link
   * AprilTagFrame#measurementTimestamp()} when adding an accepted measurement to a robot-side pose
   * estimator. QuestXLib does not apply drivetrain-specific rejection or fusion.
   *
   * @return all unread AprilTag measurements in arrival order
   */
  public AprilTagFrame[] getAllUnreadAprilTagFrames() {
    if (aprilTagV2Subscriber != null) {
      return Arrays.stream(aprilTagV2Subscriber.readQueue())
          .map(message -> MeasurementWireFormat.aprilTag(message.value, message.serverTime))
          .filter(Objects::nonNull)
          .toArray(AprilTagFrame[]::new);
    }
    var messages = aprilTagDataSubscriber.readQueue();
    var frames = new AprilTagFrame[messages.length];
    for (int i = 0; i < messages.length; i++) {
      frames[i] = toAprilTagFrame(messages[i].value, messages[i].serverTime);
    }
    return frames;
  }

  /**
   * Gets the latest VIO measurement without consuming the unread queue.
   *
   * @return the latest VIO measurement, or empty before the first measurement arrives
   */
  public Optional<VioFrame> getLatestVioFrame() {
    if (vioV2Subscriber != null) {
      var message = vioV2Subscriber.getAtomic();
      return Optional.ofNullable(MeasurementWireFormat.vio(message.value, message.serverTime));
    }
    var message = vioDataSubscriber.getAtomic();
    if (message.serverTime <= 0) {
      return Optional.empty();
    }
    return Optional.of(toVioFrame(message.value, message.serverTime));
  }

  /**
   * Gets the latest AprilTag measurement without consuming the unread queue.
   *
   * @return the latest AprilTag measurement, or empty before the first measurement arrives
   */
  public Optional<AprilTagFrame> getLatestAprilTagFrame() {
    if (aprilTagV2Subscriber != null) {
      var message = aprilTagV2Subscriber.getAtomic();
      return Optional.ofNullable(MeasurementWireFormat.aprilTag(message.value, message.serverTime));
    }
    var message = aprilTagDataSubscriber.getAtomic();
    if (message.serverTime <= 0) {
      return Optional.empty();
    }
    return Optional.of(toAprilTagFrame(message.value, message.serverTime));
  }

  private VioFrame toVioFrame(Data.ProtobufQuestXVioData data, long serverTimeMicros) {
    return new VioFrame(
        pose3dProto.unpack(data.getPose3D()),
        Microseconds.of(serverTimeMicros).in(Seconds),
        data.getCaptureTimestampMicros(),
        data.getAppTimestamp(),
        data.getSequenceId(),
        data.getIsTracking(),
        Integer.toUnsignedLong(data.getTrackingEpoch()));
  }

  private AprilTagFrame toAprilTagFrame(
      Data.ProtobufQuestXAprilTagData data, long serverTimeMicros) {
    var stdDevs = data.getPoseStdDevs();
    return new AprilTagFrame(
        pose3dProto.unpack(data.getPose3D()),
        Microseconds.of(serverTimeMicros).in(Seconds),
        data.getCaptureTimestampMicros(),
        data.getAppTimestamp(),
        data.getSequenceId(),
        Arrays.stream(data.getTagIds().toArray()).boxed().toList(),
        Integer.toUnsignedLong(data.getTagCount()),
        Integer.toUnsignedLong(data.getInlierCount()),
        Integer.toUnsignedLong(data.getCornerCount()),
        data.getInlierRatio(),
        data.getAverageTagDistanceMeters(),
        new PoseStandardDeviations(
            stdDevs.getXMeters(),
            stdDevs.getYMeters(),
            stdDevs.getZMeters(),
            stdDevs.getRollRadians(),
            stdDevs.getPitchRadians(),
            stdDevs.getYawRadians()));
  }

  private double measurementAgeMillis(long serverTimeMicros) {
    if (serverTimeMicros <= 0) {
      return Double.POSITIVE_INFINITY;
    }
    return Seconds.of(Timer.getTimestamp())
        .minus(Microseconds.of(serverTimeMicros))
        .in(Milliseconds);
  }

  /**
   * Processes command responses from the Quest headset, fires command success/failure callbacks,
   * and evaluates connection, tracking, and battery state callbacks.
   *
   * <p>This method must be called regularly (typically in {@code robotPeriodic()}) to:
   *
   * <ul>
   *   <li>Process responses to commands sent via {@link #setPose(Pose3d)}
   *   <li>Fire {@link #onCommandSuccess(Consumer)} and {@link #onCommandFailure(Consumer)}
   *       callbacks
   *   <li>Detect and fire connection state change callbacks ({@link #onConnected(Runnable)}, {@link
   *       #onDisconnected(Runnable)})
   *   <li>Detect and fire tracking state change callbacks ({@link #onTrackingAcquired(Runnable)},
   *       {@link #onTrackingLost(Runnable)})
   *   <li>Evaluate low battery threshold and fire {@link #onLowBattery(int, IntConsumer)} callback
   *   <li>Log command failures to the DriverStation
   *   <li>Prevent command response queue overflow
   * </ul>
   *
   * <h4>Usage Pattern:</h4>
   *
   * <pre>{@code
   * public class Robot extends TimedRobot {
   *   private QuestX questX = new QuestX();
   *
   *   @Override
   *   public void robotPeriodic() {
   *     questX.commandPeriodic(); // Call every loop
   *   }
   * }
   * }</pre>
   *
   * @see #setPose(Pose3d)
   * @see #onCommandSuccess(Consumer)
   * @see #onCommandFailure(Consumer)
   * @see #onConnected(Runnable)
   * @see #onDisconnected(Runnable)
   * @see #onTrackingAcquired(Runnable)
   * @see #onTrackingLost(Runnable)
   * @see #onLowBattery(int, IntConsumer)
   */
  public void commandPeriodic() {
    checkVersionMatch();
    checkStateCallbacks();

    Commands.ProtobufQuestXCommandResponse[] responses = responseSubscriber.readQueueValues();

    for (Commands.ProtobufQuestXCommandResponse response : responses) {
      if (response.getSuccess()) {
        if (onSuccessCallback != null) {
          onSuccessCallback.accept(response);
        }
      } else {
        DriverStation.reportError("QuestX command failed!\n" + response.getErrorMessage(), false);
        if (onFailureCallback != null) {
          onFailureCallback.accept(response);
        }
      }
    }
  }

  /**
   * Retrieves the QuestX-lib version number.
   *
   * @return The version number as a String.
   */
  public String getLibVersion() {
    return BuildConfig.APP_VERSION;
  }

  /**
   * Retrieves the QuestX app version running on the Quest headset.
   *
   * @return The version number as a String, or "unknown" if unable to retrieve.
   */
  public String getQuestXVersion() {
    return versionSubscriber.get();
  }
}
