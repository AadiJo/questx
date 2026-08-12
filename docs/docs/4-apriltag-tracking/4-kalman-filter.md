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
4. convert the Quest camera pose to the robot-center pose using the mount transform;
5. reject AprilTag measurements using field bounds, innovation, tag geometry, age, and quality;
6. pass accepted poses, NetworkTables measurement timestamps, and standard deviations to the drivetrain estimator;
7. log both accepted and rejected measurements with a reason.

## Java API

```java
for (VioFrame frame : questX.getAllUnreadVioFrames()) {
  // Handle trackingEpoch changes before using local-frame motion.
}

for (AprilTagFrame frame : questX.getAllUnreadAprilTagFrames()) {
  if (frame.inlierRatio() < minimumInlierRatio) {
    continue;
  }

  Pose3d robotPose = frame.fieldPose3d().transformBy(ROBOT_TO_QUEST.inverse());
  drivePoseEstimator.addVisionMeasurement(
      robotPose.toPose2d(),
      frame.measurementTimestamp(),
      frame.pose2dStdDevs());
}
```

The timestamp passed to the estimator comes from the queued NetworkTables value and is translated into the robot's clock domain. `captureTimestampMicros` and `appTimestamp` remain available for diagnostics.

## Uncertainty

`pose2dStdDevs()` returns the Quest's suggested X, Y, and yaw standard deviations. Treat these as a starting point. Teams can derive a different measurement-noise model from tag count, inlier ratio, average distance, motion state, and empirical logs.

## Compatibility

The legacy fused `frameData`, `PoseFrame`, and `setPose()` APIs remain temporarily available but are deprecated. New integrations should use the separate streams; `setPose()` does not align raw VIO.
