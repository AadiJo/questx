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
import edu.wpi.first.math.geometry.Pose3d;
import edu.wpi.first.math.numbers.N1;
import edu.wpi.first.math.numbers.N3;
import java.util.List;

/**
 * One field-relative AprilTag pose measurement produced by the Quest.
 *
 * <p>This record contains a solved pose and its quality metadata. It is not fused with Quest VIO.
 * Robot code should apply its own rejection policy before adding the measurement to an estimator.
 *
 * <p>{@link #ambiguity()} follows PhotonVision's {@code getPoseAmbiguity()}: a planar tag usually
 * admits two poses, and ambiguity is the lower RMS reprojection error divided by the higher (0 =
 * one pose clearly fits, 1 = indistinguishable). Several coplanar tags are evaluated as one plane;
 * tags on different planes report 0. A common policy rejects single-tag frames whose ambiguity
 * exceeds about 0.2. Ambiguity fields are NaN or empty when the selected transport does not carry
 * them ({@link MeasurementTransport#V2} and {@link MeasurementTransport#LEGACY_PROTOBUF}).
 *
 * @param fieldPose3d field-relative pose of the Quest headset
 * @param measurementTimestamp NetworkTables server timestamp in seconds, translated to the robot
 *     clock; use this timestamp when fusing the measurement
 * @param captureTimestampMicros camera capture timestamp transmitted by the app, in microseconds;
 *     retained for diagnostics and cross-checking the NetworkTables timestamp
 * @param appTimestamp Quest app uptime timestamp in seconds, for diagnostics only
 * @param sequenceId unsigned sequence number, represented as a Java {@code long}
 * @param tagIds IDs of tags used by the pose solver
 * @param tagCount unsigned number of tags associated with the solve
 * @param inlierCount unsigned number of accepted corners
 * @param cornerCount unsigned number of candidate corners
 * @param inlierRatio accepted-corner ratio from zero to one
 * @param averageTagDistanceMeters average distance to the observed tags in meters
 * @param standardDeviations per-axis measurement standard deviations
 * @param ambiguity planar pose ambiguity from 0 to 1, or NaN when not reported
 * @param reprojectionErrorPixels RMS pixel error of the solved pose over every observed corner, or
 *     NaN when not reported
 * @param tagAmbiguities single-tag ambiguity for each tag, in {@code tagIds} order; empty when not
 *     reported
 */
public record AprilTagFrame(
    Pose3d fieldPose3d,
    double measurementTimestamp,
    long captureTimestampMicros,
    double appTimestamp,
    long sequenceId,
    List<Integer> tagIds,
    long tagCount,
    long inlierCount,
    long cornerCount,
    double inlierRatio,
    double averageTagDistanceMeters,
    PoseStandardDeviations standardDeviations,
    double ambiguity,
    double reprojectionErrorPixels,
    List<Double> tagAmbiguities) {

  /**
   * Creates an AprilTag frame and defensively copies its tag ID and ambiguity lists.
   *
   * @param fieldPose3d field-relative pose of the Quest headset
   * @param measurementTimestamp NetworkTables server timestamp in seconds
   * @param captureTimestampMicros camera capture timestamp transmitted by the app, in microseconds
   * @param appTimestamp Quest app uptime timestamp in seconds
   * @param sequenceId unsigned sequence number represented as a Java {@code long}
   * @param tagIds IDs of tags used by the pose solver
   * @param tagCount unsigned number of tags associated with the solve
   * @param inlierCount unsigned number of accepted corners
   * @param cornerCount unsigned number of candidate corners
   * @param inlierRatio accepted-corner ratio from zero to one
   * @param averageTagDistanceMeters average distance to the observed tags in meters
   * @param standardDeviations per-axis measurement standard deviations
   * @param ambiguity planar pose ambiguity from 0 to 1, or NaN when not reported
   * @param reprojectionErrorPixels RMS reprojection error in pixels, or NaN when not reported
   * @param tagAmbiguities single-tag ambiguity for each tag; empty when not reported
   */
  public AprilTagFrame {
    tagIds = List.copyOf(tagIds);
    tagAmbiguities = List.copyOf(tagAmbiguities);
  }

  /**
   * Returns whether the Quest reported pose ambiguity for this measurement.
   *
   * @return true when {@link #ambiguity()} is a number
   */
  public boolean hasAmbiguity() {
    return !Double.isNaN(ambiguity);
  }

  /**
   * Returns this measurement's X, Y, and yaw standard deviations for a WPILib 2D pose estimator.
   *
   * @return a three-element column vector containing X meters, Y meters, and yaw radians
   */
  public Matrix<N3, N1> pose2dStdDevs() {
    return standardDeviations.toPose2dStdDevs();
  }
}
