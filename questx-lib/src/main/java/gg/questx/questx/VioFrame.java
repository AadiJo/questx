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
 * <p>The VIO pose is expressed in the Quest tracking coordinate frame; it is not an
 * AprilTag-aligned field pose. Robot code should maintain the transform between this local frame
 * and the field frame and reset that transform whenever {@link #trackingEpoch()} changes.
 *
 * @param questPose3d local-frame pose of the Quest headset
 * @param measurementTimestamp NetworkTables server timestamp in seconds, translated to the robot
 *     clock; use this timestamp when fusing the measurement
 * @param captureTimestampMicros capture timestamp transmitted by the app, in microseconds; retained
 *     for diagnostics and cross-checking the NetworkTables timestamp
 * @param appTimestamp Quest app uptime timestamp in seconds, for diagnostics only
 * @param sequenceId unsigned sequence number, represented as a Java {@code long}
 * @param isTracking whether headset tracking was valid when this measurement was captured
 * @param trackingEpoch unsigned identifier for the current continuous tracking origin, represented
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
