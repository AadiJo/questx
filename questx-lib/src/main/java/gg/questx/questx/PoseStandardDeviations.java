/*
* QUESTX
  https://github.com/QuestX
* Copyright (C) 2025 QuestX
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the MIT License as published.
*/
package gg.questx.questx;

import edu.wpi.first.math.Matrix;
import edu.wpi.first.math.VecBuilder;
import edu.wpi.first.math.numbers.N1;
import edu.wpi.first.math.numbers.N3;

/**
 * Per-axis standard deviations accompanying an AprilTag pose measurement.
 *
 * <p>The Quest reports marginal uncertainty along the measured pose's axes. Forward, left, and yaw
 * belong to {@code Pose3d.toPose2d()}, along its heading; up, roll, and pitch are along the
 * measured 3D pose's own axes. These marginals discard correlations and approximate WPILib noise
 * only near zero innovation. For WPILib 2026.2.1's predicted-frame innovation, prefer {@link
 * AprilTagFrame#pose2dStdDevs(edu.wpi.first.math.geometry.Pose2d)} with a capture-time prediction.
 *
 * @param xMeters forward translation standard deviation in meters
 * @param yMeters left translation standard deviation in meters
 * @param zMeters up translation standard deviation in meters
 * @param rollRadians roll standard deviation in radians
 * @param pitchRadians pitch standard deviation in radians
 * @param yawRadians yaw standard deviation in radians
 */
public record PoseStandardDeviations(
    double xMeters,
    double yMeters,
    double zMeters,
    double rollRadians,
    double pitchRadians,
    double yawRadians) {

  /**
   * Returns measured-heading forward, left, and yaw marginals. For fusion, prefer {@link
   * AprilTagFrame#pose2dStdDevs(edu.wpi.first.math.geometry.Pose2d)}; these values approximate the
   * estimator's noise only near zero innovation and discard correlations.
   *
   * @return a three-element column vector containing X meters, Y meters, and yaw radians
   */
  public Matrix<N3, N1> toPose2dStdDevs() {
    return VecBuilder.fill(xMeters, yMeters, yawRadians);
  }
}
