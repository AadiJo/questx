# QuestX AprilTag measurement v3

The Quest publishes `/QuestX/v3/apriltag`, an atomic NetworkTables `double[]` that
adds pose ambiguity, fit error, and processing latency to the v2 AprilTag
measurement. The app keeps
publishing `/QuestX/v2/apriltag` (unchanged) and the `aprilTagData` protobuf so
existing robot code keeps working. VIO stays at `/QuestX/v2/vio`.

The Java library's default constructor selects `MeasurementTransport.V3`, which
reads `/QuestX/v2/vio` and `/QuestX/v3/apriltag`. Select `V2` or
`LEGACY_PROTOBUF` explicitly for older headset apps; those transports report
ambiguity and processing latency as NaN and an empty per-tag list.

## Array layout

Indices 0–22 are identical to v2: the shared prefix (sequence ID and capture
timestamp as uint32 word pairs, app uptime, translation, quaternion w, x, y, z),
then tag count, inlier and total corner counts, inlier ratio, average tag
distance, and six standard deviations. v3 contains exactly **26 + 2 × tagCount**
doubles:

| Index | Field |
| --- | --- |
| 0–1 | Sequence ID, unsigned 64-bit: low uint32 word, high uint32 word |
| 2–3 | Capture timestamp, signed 64-bit microseconds on the Quest NT clock: low word, high word |
| 4 | App uptime, seconds (diagnostic only) |
| 5–7 | Headset translation x, y, z, meters (field frame) |
| 8–11 | Headset rotation quaternion w, x, y, z |
| 12 | Tag count N, uint32 |
| 13 | Inlier corner count, uint32 |
| 14 | Total corner count, uint32 |
| 15 | Inlier ratio, 0–1 |
| 16 | Average camera-to-tag distance, meters |
| 17–19 | Standard deviations x, y, z, meters |
| 20–22 | Standard deviations roll, pitch, yaw, radians |
| 23 | Measurement ambiguity, 0–1 |
| 24 | Reprojection error of the published pose, RMS pixels, nonnegative |
| 25 | Processing latency, milliseconds, nonnegative |
| 26 to 25 + N | Tag IDs, detector order |
| 26 + N to 25 + 2N | Per-tag ambiguity, 0–1, same order as the IDs |

Indices 0–22 match v2 exactly. N is the tag count at index 12. Every value must
be finite. The decoder rejects arrays whose length or ranges do not match, in
addition to the v2 checks.

## Processing latency

Index 25 is the time from camera capture to publication of this array on the
Quest: `max(0, (publishTime − captureTime) / 1000)` milliseconds, with both
times on the Quest's NetworkTables clock. It is the same number the app
publishes at `/QuestX/limelight/latencyMilliseconds` for this measurement, so
one atomic v3 read carries everything a robot needs without associating
separate topics. The array's NetworkTables value timestamp remains the capture
time (index 2–3); use it, not the latency, for fusion timestamps. There is no
upper bound; robots should apply their own age limit.

## Ambiguity

A planar tag viewed by a pinhole camera usually admits two poses that explain
its corners (the IPPE solutions). Ambiguity is the lower RMS reprojection error
of the two divided by the higher, the same quantity as PhotonVision's
`getPoseAmbiguity()` and Limelight's per-fiducial ambiguity. Near 0, one pose
clearly fits better; near 1, the image cannot distinguish them. Face-on tags
read high even though both poses then nearly coincide.

- **Per tag**: each tag's four corners are solved on their own.
- **Measurement**: one tag uses that tag's value. Tags whose corners all lie
  within 2 cm of one plane are solved jointly as one plane, so a wider baseline
  lowers the value. Tags on different planes report 0, because those
  correspondences do not share the planar two-pose ambiguity.
- A degenerate planar solve reports 1.

The reprojection error is computed over every corner given to the solver,
including RANSAC outliers. The Quest rejects a solve locally if any corner
reprojects behind the camera. It does not reject measurements by ambiguity;
the robot owns that policy (PhotonVision users commonly reject single-tag poses
above about 0.2).

## Limelight compatibility

`/QuestX/limelight/botpose_wpiblue` and `headsetpose_wpiblue` now append one
seven-value raw fiducial per tag after the 11 header values, and
`/QuestX/limelight/rawfiducials` carries the same records: ID, `txnc` and `tync`
in degrees from the principal point, area as a percentage of the image, distance
to the camera, distance to the headset (the botpose robot center), and
ambiguity. `LimelightHelpers.getBotPoseEstimate_wpiBlue` fills `rawFiducials`
only when the length is exactly `11 + 7 × tagCount`, which these arrays satisfy.
Index 10 is now the mean tag area; tag span (index 8) is still unsupported and 0.

## Telemetry

`/QuestX/telemetry/apriltag/` adds `ambiguity`, `reprojectionErrorPixels`,
`processingLatencyMilliseconds`, and `tagAmbiguities`. These are diagnostic
snapshots; do not combine them for fusion.

## Tracking epochs and pose resets

`POSE_RESET` (Java `setPose`) re-bases the published VIO frame. The Quest
rotates it about the vertical axis and translates it so the next VIO sample
reports the target's position and yaw; roll and pitch stay gravity-referenced.
That sample carries a new tracking epoch.

The runtime announces a headset recenter before it moves the tracking origin.
For 100 ms after the announcement, VIO samples carry a new epoch, report
tracking as invalid, and repeat the last pre-recenter pose. Live poses then
resume in another new epoch. No epoch mixes two origins, and VIO keeps its rate,
so its age stays usable as a connection heartbeat.

Never compute a VIO delta between samples whose epochs differ. The reset is
anchored on the newest VIO sample when the Quest processes the command; motion
between sending and processing is not compensated. The Quest rejects a reset
while the headset is not tracking or a recenter is settling.

## Validation

Run `dotnet run --project tests/measurement-checks/MeasurementChecks.csproj`
for the ambiguity, VIO-frame, and encoder checks. Add `--export-fixtures` to
regenerate the fixtures in `questx-lib/src/test/resources/measurement-v3/` from
the production C# encoder:

- `apriltag.txt`: synthetic values in every field. `MeasurementWireFixtures.Export`
  in the Unity editor writes the same values.
- `apriltag-single-tag.txt` and `apriltag-two-tags.txt`: realistic frames for
  robot-side decoder tests. They show REBUILT tags 26 and 25 + 26 (coplanar on the
  blue hub face) seen from headset height, with ambiguity and fit error computed
  by the production solver from projected corners with 0.3 px noise.

Then run `questx-lib/gradlew test` with JDK 17.
