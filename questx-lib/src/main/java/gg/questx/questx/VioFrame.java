/*
* QUESTX
  https://github.com/QuestX
* Copyright (C) 2025 QuestX
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the MIT License as published.
*/
package gg.questx.questx;

import edu.wpi.first.math.geometry.Pose3d;

/**
 * One visual-inertial odometry measurement produced by the Quest.
 *
 * <p>The VIO pose is the robot's pose (the headset pose through the robot-to-Quest transform set in
 * the QuestX web interface), expressed in the Quest tracking coordinate frame, re-based by the most
 * recent {@link QuestX#setPose(edu.wpi.first.math.geometry.Pose3d)}; it is not fused with AprilTag
 * measurements. Robot code should maintain the transform between this frame and the field frame and
 * reset that transform whenever {@link #trackingEpoch()} changes.
 *
 * <p>The epoch changes when a pose reset is applied, when the headset recenters, and when the
 * robot-to-Quest transform changes. Never compute a pose delta between two frames whose epochs
 * differ: that interval contains the frame change, not motion. For about 100 ms after a recenter
 * the Quest repeats the last pre-recenter pose in its own epoch with {@link #isTracking()} false,
 * then resumes live poses in another epoch, so no epoch mixes two origins.
 *
 * @param questPose3d local-frame pose of the robot, derived on the Quest from its headset pose
 * @param measurementTimestamp NetworkTables server timestamp in seconds, translated to the robot
 *     clock; use this timestamp when fusing the measurement
 * @param captureTimestampMicros capture timestamp transmitted by the app, in microseconds; retained
 *     for diagnostics and cross-checking the NetworkTables timestamp
 * @param appTimestamp Quest app uptime timestamp in seconds, for diagnostics only
 * @param sequenceId unsigned sequence number, represented as a Java {@code long}
 * @param isTracking whether headset tracking was valid when this measurement was captured
 * @param trackingEpoch unsigned identifier for the current continuous tracking frame, represented
 *     as a Java {@code long}
 */
public record VioFrame(
    Pose3d questPose3d,
    double measurementTimestamp,
    long captureTimestampMicros,
    double appTimestamp,
    long sequenceId,
    boolean isTracking,
    long trackingEpoch) {}
