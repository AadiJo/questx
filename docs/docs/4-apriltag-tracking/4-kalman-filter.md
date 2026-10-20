---
title: Robot-Side Fusion
---

# Robot-Side Fusion

QuestX no longer contains a Kalman filter. The robot is the single owner of localization state and combines drivetrain odometry, aligned Quest VIO, and field-relative AprilTag measurements.

## Recommended responsibilities

Robot code should:

1. drain every unread VIO and AprilTag sample;
2. reject VIO samples when tracking is invalid or their epoch is unknown;
3. maintain a local-VIO-to-field transform for each tracking epoch;
4. use the published poses directly as robot poses, since the headset already applies the robot-to-Quest mount;
5. reject AprilTag measurements using field bounds, innovation, tag geometry, age, and quality;
6. pass accepted poses, NetworkTables measurement timestamps, and standard deviations to the drivetrain estimator;
7. log both accepted and rejected measurements with a reason.

## Java API

The example uses `edu.wpi.first.wpilibj.Timer` and the default v4 transport.

```java
for (VioFrame frame : questX.getAllUnreadVioFrames()) {
  // Handle trackingEpoch changes before using local-frame motion.
}

for (AprilTagFrame frame : questX.getAllUnreadAprilTagFrames()) {
  if (frame.inlierRatio() < minimumInlierRatio) {
    continue;
  }
  if (frame.tagCount() == 1 && frame.ambiguity() > maximumSingleTagAmbiguity) {
    continue;
  }

  // Gate age before sampleAt(): WPILib clamps requests outside its history.
  double age = Timer.getFPGATimestamp() - frame.measurementTimestamp();
  if (age < 0 || age > 0.5 || !frame.hasPlanarCovariance()) {
    continue;
  }
  var predicted = drivePoseEstimator.sampleAt(frame.measurementTimestamp());
  if (predicted.isEmpty()) {
    continue;
  }
  // Apply your field-bounds and innovation gates against predicted.get() here.
  // fieldPose3d() is already the robot pose; the headset applies the mount.
  drivePoseEstimator.addVisionMeasurement(
      frame.fieldPose3d().toPose2d(),
      frame.measurementTimestamp(),
      frame.pose2dStdDevs(predicted.get()));
}
```

The timestamp passed to the estimator comes from the queued NetworkTables value and is translated into the robot's clock domain. `captureTimestampMicros` and `appTimestamp` remain available for diagnostics.

## Uncertainty

Every measurement carries a 6×6 covariance computed on the Quest with [Napa](https://github.com/zwatx/NAPA)'s model. It comes from that frame's geometry: how far the tags are, how steeply they're seen, how many there are, and how widely they're spread. A filter weighted by it trusts a close multi-tag pose and discounts a lone distant tag, with no distance-based standard-deviation table to tune.

For WPILib **2026.2.1**, use `pose2dStdDevs(predictedPoseAtCapture)` as above. WPILib applies a diagonal gain to `measured.minus(predicted)`, expressed along the historical prediction's heading. The helper rotates the transmitted planar covariance into that frame and inflates the diagonal enough to cover its discarded correlations. This trades some information for a conservative diagonal approximation. It does not mutate the estimator or choose which measurements to accept. This adapter targets the configured 2026 estimator; the 2025 estimator's log-twist update needs a different covariance Jacobian.

`planarCovariance()` preserves the full 3×3 covariance of field X, field Y, and heading, including correlations. The Quest projects each weighted solution before reducing it to a covariance; a robot cannot generally reproduce that nonlinear projection from the reduced 6×6 matrix. `hasPlanarCovariance()` is false when the transport lacks this block or any solution has an undefined heading (its forward axis is vertical). Skip that planar update; the full 6-D covariance remains available.

The older no-argument `pose2dStdDevs()` returns marginal forward, left, and yaw deviations along the **measured** heading. It approximates the estimator frame only near zero innovation and drops correlations. Prefer the overload for fusion; older transports cannot provide the exact planar covariance it requires.

A planar target, whether one tag or several on one wall, can have two poses that fit its corners. Where the other one also fits, the covariance widens to cover both. This reduces the influence of ambiguous frames; it does **not** guarantee rejection of a wrong pose. The published pose remains one solution, and repeated biased updates can still pull the estimate toward it. Keep ambiguity and innovation gates. A common policy rejects single-tag frames with `ambiguity() > 0.2`.

Do not treat VIO field-aligned from a tag as a second independent absolute observation of that same tag. The alignment shares the tag's error; use VIO for relative motion within its tracking epoch or model that correlation in your filter.

### Your own filter

With `MeasurementTransport.V4` (the default), `covariance()` returns the full 6×6 matrix for `fieldPose3d()`, ordered x, y, z, then a small rotation about the field's x, y, and z axes, applied on the left of the measured rotation. For a 6-D update, use it as the measurement noise, and compute the innovation in the same convention:

```java
Pose3d measured = frame.fieldPose3d();
var dt = measured.getTranslation().minus(predicted.getTranslation());
// R_m * R_p^T, a rotation in the field frame. Rotation3d.minus would give R_p^T * R_m instead.
var dr = predicted.getRotation().unaryMinus().rotateBy(measured.getRotation()).toVector();
var innovation = VecBuilder.fill(dt.getX(), dt.getY(), dt.getZ(), dr.get(0), dr.get(1), dr.get(2));
Matrix<N6, N6> measurementNoise = frame.covariance();
```

`hasCovariance()` is false, and `covariance()` holds NaN, on the older transports.

### Tuning

The covariance assumes 1 px of independent noise on each corner coordinate, which is deliberately pessimistic for clean frames: motion blur, rolling shutter, and calibration error all look like corner noise. To check random noise, log poses while the robot stands still and compare their scatter with the reported standard deviations. Validate absolute error against an independently measured pose, and validate capture timing while moving; stationary scatter cannot reveal a constant calibration or mount bias. Scaling the covariance by k² is exact for unambiguous measurements and approximate for widened ones. Calibration error repeats from frame to frame, which no per-frame covariance can capture, so don't scale much below what you measure.

## Ambiguity

A planar tag usually admits two camera poses that explain its corners. `ambiguity()` is the lower RMS reprojection error of those two poses divided by the higher one, matching PhotonVision's `getPoseAmbiguity()` and Limelight's per-fiducial ambiguity: near 0 means one pose clearly fits, and near 1 means the image cannot tell them apart. Face-on tags read high even though both poses then nearly coincide.

- One tag: the measurement ambiguity is that tag's ambiguity.
- Several tags on one plane: the plane is solved jointly, so a wider baseline lowers the value.
- Tags on different planes: 0, because the correspondences do not share the planar two-pose ambiguity.

The contributing tag set is selected once using the solved pose: all four corners must lie within the solver threshold. Covariance, aggregate ambiguity, fit error, IDs, and per-tag observations use that same set. No surviving tag means no measurement. Corner counts and inlier ratio still describe the original native solve, so they can include corners from omitted tags.

`tagAmbiguities()` lists each tag's single-tag value in `tagIds()` order, and `reprojectionErrorPixels()` is the RMS fit error of the published pose over every corner. These fields require `MeasurementTransport.V3` or later (`V4` is the default); the older transports report NaN and an empty list. The Limelight-compatible `botpose_wpiblue` array and `rawfiducials` topic under `/QuestX/limelight/` carry the same per-tag ambiguity for `LimelightHelpers`.

## Compatibility

The legacy fused `frameData` and `PoseFrame` APIs remain temporarily available but are deprecated. New integrations should use the separate streams. `setPose()` takes a robot pose, re-bases VIO position and yaw, and starts a new tracking epoch; it does not fuse AprilTag data on the Quest.
