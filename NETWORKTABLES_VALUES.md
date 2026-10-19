# QuestX NetworkTables Values

## Scope

This file documents the custom build derived from repository commit `afc6ee4` (`feat: publish unfused poses`) plus the local native-packaging and AprilTag timestamp/intrinsics patches. The Unity project identifies itself as Android package `gg.QuestX.QuestX` with application version `afc6ee4-dev`.

It does **not** describe the older `gg.QuestNav.QuestNavExperimental` APK that remains installed on the development headset. That APK reports version `c79dd2b-alignment-dev`; its auto-start setting is disabled so it does not compete with this build at boot.

The app connects as an NT4 client on port `5810`. Application data uses the `/QuestX` table; CameraServer-compatible metadata uses `/CameraPublisher/Passthrough`.

## Active Quest-to-robot publications

| NT topic | NT type | Update behavior | Value |
| --- | --- | --- | --- |
| `/QuestX/vioData` | `proto:questx.protos.data.ProtobufQuestXVioData` | Nominally 120 Hz after app initialization | Unfused center-eye VIO pose in the Quest's local tracking frame, plus timing, sequence, and tracking-state metadata. The app explicitly uses `capture_timestamp_micros` as the NT value timestamp. |
| `/QuestX/aprilTagData` | `proto:questx.protos.data.ProtobufQuestXAprilTagData` | Event-driven; only when the detector produces a locally accepted pose | Field-relative AprilTag pose plus contributing-tag, solve-quality, distance, and suggested-uncertainty metadata. The app explicitly uses `capture_timestamp_micros` as the NT value timestamp. |
| `/QuestX/deviceData` | `proto:questx.protos.data.ProtobufQuestXDeviceData` | Nominally 3 Hz | Battery percentage and number of transitions from tracking to not-tracking during the current app process. |
| `/QuestX/response` | `proto:questx.protos.commands.ProtobufQuestXCommandResponse` | Once per handled `/QuestX/request` command | Command ID, success/failure status, and an error message on failure. |
| `/QuestX/version` | `string` | Set during NT initialization | Unity's `Application.version`; for this build, `afc6ee4-dev`. |

### `/QuestX/vioData` fields

| Field | Meaning |
| --- | --- |
| `sequence_id` | Unsigned counter starting at `0` for each app process and incrementing for every VIO publication. |
| `capture_timestamp_micros` | NT clock time, in microseconds, sampled beside the pose. The same value is supplied as the NT publication timestamp. |
| `app_timestamp` | Unity `Time.timeAsDouble`, in seconds since app startup. This is diagnostic time, not the robot-estimator timestamp. |
| `pose3d.translation.{x,y,z}` | Center-eye position in meters, relative to the current Quest tracking origin. Axes are converted to WPILib convention: FRC X = Unity Z, FRC Y = -Unity X, FRC Z = Unity Y. This pose is **not field-aligned** and is not fused with AprilTags. |
| `pose3d.rotation.q.{w,x,y,z}` | Center-eye orientation as a unit quaternion in WPILib/FRC axes. The conversion is FRC X = -Unity Z, FRC Y = Unity X, FRC Z = -Unity Y, FRC W = Unity W. |
| `is_tracking` | `true` when `OVRManager.tracker.isPositionTracked` was true for this sample; otherwise `false`. |
| `tracking_epoch` | Unsigned local-frame generation counter. It starts at `0` and increments when Oculus reports a recenter/tracking-origin change. Robot code should re-establish its local-to-field transform when this changes. |

### `/QuestX/aprilTagData` fields

| Field | Meaning |
| --- | --- |
| `sequence_id` | Unsigned counter starting at `0` for each app process and incrementing for every published AprilTag measurement. |
| `capture_timestamp_micros` | NT clock time associated with the camera's hardware exposure, in microseconds; also used as the NT publication timestamp. QuestX subtracts the bounded exposure age reported by the Meta camera API from the current NT clock. |
| `app_timestamp` | Quest app uptime at the camera's hardware exposure, in seconds. Diagnostic only; it is calculated by subtracting the same bounded exposure age from `Time.timeAsDouble`. |
| `pose3d` | Field-relative 3D pose of the passthrough camera in WPILib coordinates. It is an AprilTag solve and is **not fused with VIO**; robot code can apply the camera-to-robot/headset extrinsic. Translation is meters; rotation is a quaternion. |
| `tag_ids` | IDs of the kept AprilTags used by the solve. |
| `tag_count` | Number of IDs in `tag_ids`. |
| `inlier_count` | Number of corner points accepted by PoseLib. |
| `corner_count` | Total candidate corner-point count passed to PoseLib. |
| `inlier_ratio` | `inlier_count / corner_count`, or `0.0` when `corner_count` is zero. |
| `average_tag_distance_meters` | Mean 3D distance from the solved camera position to the kept tags. This is camera-to-tag distance, not distance from the field origin. |
| `pose_std_devs.x_meters` | Suggested X uncertainty: `0.05 * distance^2 / max(1, tag_count)`. |
| `pose_std_devs.y_meters` | Same suggested value as X. |
| `pose_std_devs.z_meters` | Twice the suggested X/Y value. |
| `pose_std_devs.yaw_radians` | `max(0.01, 0.05 * distance / sqrt(max(1, tag_count)))`. |
| `pose_std_devs.roll_radians` | Twice the suggested yaw uncertainty. |
| `pose_std_devs.pitch_radians` | Twice the suggested yaw uncertainty. |

The standard deviations are a Quest-side suggestion derived from observed geometry; they are not covariance supplied by Meta or the AprilTag SDK. Robot code remains responsible for rejection and estimator tuning.

### `/QuestX/deviceData` fields

| Field | Meaning |
| --- | --- |
| `tracking_lost_counter` | Count of tracked-to-untracked transitions since this app process started. |
| `battery_percent` | Integer conversion of `SystemInfo.batteryLevel * 100`; normally `0` through `100`. |

### `/QuestX/response` fields

| Field | Meaning |
| --- | --- |
| `command_id` | Echo of the ID from the corresponding `/QuestX/request`. |
| `success` | `true` when the command completed; `false` when rejected or failed. |
| `error_message` | Empty/default on success; human-readable reason on failure, such as a stale or superseded pose-reset command. |

## CameraServer-compatible publications

These values exist for the single camera source named `Passthrough`.

| NT topic | NT type | Value |
| --- | --- | --- |
| `/CameraPublisher/Passthrough/source` | `string` | Currently always `unknown:`; no later source assignment exists in this code. |
| `/CameraPublisher/Passthrough/streams` | `string[]` | Empty until the web server has an address, then normally `["mjpg:http://<quest-ip>:5801/video"]`. |
| `/CameraPublisher/Passthrough/description` | `string` | `Quest Headset Camera` once passthrough streaming initializes. |
| `/CameraPublisher/Passthrough/connected` | `boolean` | `true` while the passthrough frame source is initialized; `false` when it is disabled after having been initialized. |
| `/CameraPublisher/Passthrough/mode` | `string` | Selected stream mode in the form `<width>x<height> MJPEG <fps> fps`, for example `640x360 MJPEG 24 fps`. |
| `/CameraPublisher/Passthrough/modes` | `string[]` | Every Meta-supported camera resolution crossed with supported FPS values `1, 5, 15, 24, 30, 48, 60`, using the same mode string format. |

`mode` and `modes` describe the camera's own capture resolution. The MJPEG frames are rotated upright for the configured camera orientation, so with Left or Right their width and height are swapped relative to `mode`.

Some camera values are change-triggered. For example, the initial in-memory `connected = false` is not sent by its property setter; a consumer should not assume that absence is equivalent to an explicitly published `false`.

## Publishers with no current application value

| NT topic | Status |
| --- | --- |
| `/QuestX/frameData` | A legacy protobuf publisher and deprecated `PublishFrameData()` method remain for binary compatibility, but the app does not call that method. The current custom app therefore emits no legacy fused frame values. |
| `/QuestX/streams` | A `string[]` publisher is constructed, but there is no `Set()` call. Use `/CameraPublisher/Passthrough/streams` instead. |

`/QuestX/request` is not a Quest publication. It is a robot-to-Quest protobuf command input. The current command is the deprecated `POSE_RESET`; the split architecture expects field alignment and VIO/AprilTag fusion to happen on the robot.

The NT instance also registers protobuf schema metadata for decoding `data.proto` and its imported geometry schema. `commands.proto` schema registration is deliberately skipped to avoid a known conflicting-schema problem with the robot publisher.

## Robot-side QuestXLib returns

| QuestXLib method | Actual return behavior in this version |
| --- | --- |
| `getAllUnreadVioFrames()` | Drains and returns queued `/QuestX/vioData` messages as `VioFrame[]` in arrival order. Each record contains the local `Pose3d`, robot-clock `measurementTimestamp`, transmitted capture/app timestamps, sequence ID, tracking flag, and tracking epoch. |
| `getLatestVioFrame()` | Latest `VioFrame` without draining the queue, or `Optional.empty()` before a VIO value arrives. |
| `getAllUnreadAprilTagFrames()` | Drains and returns queued `/QuestX/aprilTagData` messages as `AprilTagFrame[]` in arrival order. Each contains the field pose and all quality/uncertainty fields above. |
| `getLatestAprilTagFrame()` | Latest `AprilTagFrame` without draining the queue, or `Optional.empty()` before a value arrives. |
| `isConnected()` | `true` when the latest VIO NT timestamp is less than 120 ms old; otherwise `false`. AprilTag data is intentionally not used as the heartbeat. |
| `isTracking()` | Latest VIO `is_tracking`; `false` before the first VIO value. |
| `getVioSequenceId()` | Latest unsigned VIO sequence ID represented as `OptionalLong`, or empty before the first VIO value. |
| `getVioLatency()` / `getLatency()` | Age of the latest VIO NT timestamp in milliseconds, or positive infinity before a value arrives. |
| `getAprilTagLatency()` | Age of the latest AprilTag NT timestamp in milliseconds, or positive infinity before a value arrives. It can grow normally when no tag solve is available. |
| `getAppTimestamp()` | Latest VIO app-uptime timestamp as `OptionalDouble`, or empty before the first VIO value. |
| `getBatteryPercent()` | Latest `/QuestX/deviceData` battery value as `OptionalInt`. Because the subscriber has a non-null default protobuf, the present implementation normally returns `OptionalInt.of(0)` before the first packet rather than an empty optional. |
| `getTrackingLostCounter()` | Latest device tracking-loss count as `OptionalInt`; for the same default-message reason, it normally returns `OptionalInt.of(0)` before the first packet. |
| `getQuestXVersion()` | Latest `/QuestX/version` string, or subscriber default `unknown`. |
| `getAllUnreadPoseFrames()` / `getFrameCount()` | Legacy accessors. With this app no longer writing `/QuestX/frameData`, the unread array remains empty; `getFrameCount()` normally observes the subscriber's default protobuf value (`0`). |

`AprilTagFrame.pose2dStdDevs()` returns a WPILib three-element vector containing `[xMeters, yMeters, yawRadians]` for use with a 2D pose estimator.

## Source references

- Topic paths and update rates: [`unity/Assets/QuestX/Core/QuestXConstants.cs`](unity/Assets/QuestX/Core/QuestXConstants.cs)
- App publication values: [`unity/Assets/QuestX/Network/NetworkTableConnection.cs`](unity/Assets/QuestX/Network/NetworkTableConnection.cs)
- VIO capture and app loops: [`unity/Assets/QuestX/Core/QuestX.cs`](unity/Assets/QuestX/Core/QuestX.cs)
- Coordinate conversion: [`unity/Assets/QuestX/Utils/Conversions.cs`](unity/Assets/QuestX/Utils/Conversions.cs)
- AprilTag quality and uncertainty calculation: [`unity/Assets/QuestX/AprilTag/AprilTagManager.cs`](unity/Assets/QuestX/AprilTag/AprilTagManager.cs)
- Camera metadata: [`unity/Assets/QuestX/Network/NtCameraSource.cs`](unity/Assets/QuestX/Network/NtCameraSource.cs) and [`unity/Assets/QuestX/Camera/PassthroughFrameSource.cs`](unity/Assets/QuestX/Camera/PassthroughFrameSource.cs)
- Wire schemas: [`protos/data.proto`](protos/data.proto), [`protos/commands.proto`](protos/commands.proto), and [`protos/geometry3d.proto`](protos/geometry3d.proto)
- Robot-side return values: [`questx-lib/src/main/java/gg/questx/questx/QuestX.java`](questx-lib/src/main/java/gg/questx/questx/QuestX.java), [`VioFrame.java`](questx-lib/src/main/java/gg/questx/questx/VioFrame.java), and [`AprilTagFrame.java`](questx-lib/src/main/java/gg/questx/questx/AprilTagFrame.java)
