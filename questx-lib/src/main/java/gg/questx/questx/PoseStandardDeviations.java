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
 * @param xMeters X translation standard deviation in meters
 * @param yMeters Y translation standard deviation in meters
 * @param zMeters Z translation standard deviation in meters
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
   * Returns the X, Y, and yaw standard deviations expected by WPILib 2D pose estimators.
   *
   * @return a three-element column vector containing X meters, Y meters, and yaw radians
   */
  public Matrix<N3, N1> toPose2dStdDevs() {
    return VecBuilder.fill(xMeters, yMeters, yawRadians);
  }
}
