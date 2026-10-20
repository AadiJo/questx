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
import edu.wpi.first.math.Nat;
import edu.wpi.first.math.VecBuilder;
import edu.wpi.first.math.geometry.Pose2d;
import edu.wpi.first.math.geometry.Pose3d;
import edu.wpi.first.math.numbers.N1;
import edu.wpi.first.math.numbers.N3;
import edu.wpi.first.math.numbers.N6;
import java.util.Arrays;
import java.util.List;
import java.util.Objects;

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
 * them ({@link MeasurementTransport#V2} and {@link MeasurementTransport#LEGACY_PROTOBUF}); the same
 * applies to {@link #processingLatencyMilliseconds()}.
 *
 * <p>The uncertainty follows Napa's covariance model: each corner is treated as carrying 1 px of
 * noise, and the pose's covariance follows from the corners' geometry (distance, viewing angle, tag
 * count and spread). Where a planar target's mirror solution also fits, as for a lone distant tag
 * seen head-on, the covariance widens to cover it. This can downweight an ambiguous measurement,
 * but does not guarantee rejection of a biased mode. For WPILib 2026.2.1, prefer {@link
 * #pose2dStdDevs(Pose2d)} with the predicted pose at capture. A filter that accepts a full
 * covariance can use {@link #covariance()}, which requires {@link MeasurementTransport#V4}.
 *
 * @param fieldPose3d field-relative pose of the robot (the solved headset pose through the
 *     robot-to-Quest transform set in the QuestX web interface)
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
 * @param processingLatencyMilliseconds time from camera capture to publication on the Quest, in
 *     milliseconds; the same value as {@code /QuestX/limelight/latencyMilliseconds}, or NaN when
 *     not reported
 * @param covariance 6x6 covariance of {@code fieldPose3d}'s error, ordered x, y, z, then a small
 *     rotation about the field's x, y, and z axes; filled with NaN when not reported. See {@link
 *     #covariance()}.
 * @param planarCovariance 3x3 covariance of field x, field y, and heading, projected component-wise
 *     on the Quest after applying the robot mount; filled with NaN when unavailable
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
    List<Double> tagAmbiguities,
    double processingLatencyMilliseconds,
    Matrix<N6, N6> covariance,
    Matrix<N3, N3> planarCovariance) {

  /**
   * Creates an AprilTag frame and defensively copies its lists and covariance matrices.
   *
   * @param fieldPose3d field-relative pose of the robot (the solved headset pose through the
   *     robot-to-Quest transform set in the QuestX web interface)
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
   * @param processingLatencyMilliseconds capture-to-publication time on the Quest in milliseconds,
   *     or NaN when not reported
   * @param covariance 6x6 covariance of {@code fieldPose3d}'s error, or null when not reported
   * @param planarCovariance field x/y/heading covariance, or null when unavailable
   */
  public AprilTagFrame {
    tagIds = List.copyOf(tagIds);
    tagAmbiguities = List.copyOf(tagAmbiguities);
    covariance = covariance == null ? unreportedCovariance() : covariance.copy();
    if (planarCovariance == null) {
      planarCovariance = new Matrix<>(Nat.N3(), Nat.N3());
      planarCovariance.fill(Double.NaN);
    } else {
      planarCovariance = planarCovariance.copy();
    }
  }

  /**
   * Creates an AprilTag frame with a full covariance but without component-wise planar covariance.
   *
   * @param fieldPose3d field-relative pose of the robot
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
   * @param processingLatencyMilliseconds capture-to-publication time on the Quest in milliseconds,
   *     or NaN when not reported
   * @param covariance 6x6 field covariance, or null when not reported
   */
  public AprilTagFrame(
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
      List<Double> tagAmbiguities,
      double processingLatencyMilliseconds,
      Matrix<N6, N6> covariance) {
    this(
        fieldPose3d,
        measurementTimestamp,
        captureTimestampMicros,
        appTimestamp,
        sequenceId,
        tagIds,
        tagCount,
        inlierCount,
        cornerCount,
        inlierRatio,
        averageTagDistanceMeters,
        standardDeviations,
        ambiguity,
        reprojectionErrorPixels,
        tagAmbiguities,
        processingLatencyMilliseconds,
        covariance,
        null);
  }

  /**
   * Creates an AprilTag frame without a covariance, as the transports before {@link
   * MeasurementTransport#V4} report it.
   *
   * @param fieldPose3d field-relative pose of the robot
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
   * @param processingLatencyMilliseconds capture-to-publication time on the Quest in milliseconds,
   *     or NaN when not reported
   */
  public AprilTagFrame(
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
      List<Double> tagAmbiguities,
      double processingLatencyMilliseconds) {
    this(
        fieldPose3d,
        measurementTimestamp,
        captureTimestampMicros,
        appTimestamp,
        sequenceId,
        tagIds,
        tagCount,
        inlierCount,
        cornerCount,
        inlierRatio,
        averageTagDistanceMeters,
        standardDeviations,
        ambiguity,
        reprojectionErrorPixels,
        tagAmbiguities,
        processingLatencyMilliseconds,
        null);
  }

  private static Matrix<N6, N6> unreportedCovariance() {
    var covariance = new Matrix<>(Nat.N6(), Nat.N6());
    covariance.fill(Double.NaN);
    return covariance;
  }

  /**
   * Returns the 6x6 covariance of {@link #fieldPose3d()}'s error, computed on the Quest.
   *
   * <p>Rows and columns are ordered x, y, z in meters, then a small rotation in radians about the
   * field's x, y, and z axes. The rotation is applied on the left of the measured one, so the true
   * rotation is {@code exp([r]x) R}. This is the convention of ROS's {@code PoseWithCovariance} and
   * of Napa. For a 6-D filter update, use it as the measurement noise with an innovation in the
   * same convention: the translation difference, and the rotation vector of {@code R_measured *
   * R_predicted^T}.
   *
   * @return a copy of the covariance, filled with NaN when the transport does not carry it
   */
  @Override
  public Matrix<N6, N6> covariance() {
    return covariance.copy();
  }

  /**
   * Compares every component, including both covariances element by element. An unreported
   * covariance equals another unreported one.
   *
   * @param other object to compare with
   * @return true when every component matches
   */
  @Override
  public boolean equals(Object other) {
    return other instanceof AprilTagFrame frame
        && Objects.equals(fieldPose3d, frame.fieldPose3d)
        && Double.compare(measurementTimestamp, frame.measurementTimestamp) == 0
        && captureTimestampMicros == frame.captureTimestampMicros
        && Double.compare(appTimestamp, frame.appTimestamp) == 0
        && sequenceId == frame.sequenceId
        && tagIds.equals(frame.tagIds)
        && tagCount == frame.tagCount
        && inlierCount == frame.inlierCount
        && cornerCount == frame.cornerCount
        && Double.compare(inlierRatio, frame.inlierRatio) == 0
        && Double.compare(averageTagDistanceMeters, frame.averageTagDistanceMeters) == 0
        && Objects.equals(standardDeviations, frame.standardDeviations)
        && Double.compare(ambiguity, frame.ambiguity) == 0
        && Double.compare(reprojectionErrorPixels, frame.reprojectionErrorPixels) == 0
        && tagAmbiguities.equals(frame.tagAmbiguities)
        && Double.compare(processingLatencyMilliseconds, frame.processingLatencyMilliseconds) == 0
        && Arrays.equals(covariance.getData(), frame.covariance.getData())
        && Arrays.equals(planarCovariance.getData(), frame.planarCovariance.getData());
  }

  /**
   * Returns a hash code consistent with {@link #equals(Object)}.
   *
   * @return the hash code
   */
  @Override
  public int hashCode() {
    return Objects.hash(
        fieldPose3d,
        measurementTimestamp,
        captureTimestampMicros,
        appTimestamp,
        sequenceId,
        tagIds,
        tagCount,
        inlierCount,
        cornerCount,
        inlierRatio,
        averageTagDistanceMeters,
        standardDeviations,
        ambiguity,
        reprojectionErrorPixels,
        tagAmbiguities,
        processingLatencyMilliseconds,
        Arrays.hashCode(covariance.getData()),
        Arrays.hashCode(planarCovariance.getData()));
  }

  /**
   * Returns whether the Quest reported this measurement's full covariance.
   *
   * @return true when {@link #covariance()} holds numbers
   */
  public boolean hasCovariance() {
    return !Double.isNaN(covariance.get(0, 0));
  }

  /**
   * Returns the component-wise projected covariance of field x, field y, and heading.
   *
   * <p>This cannot be reconstructed from the full 6x6 covariance: projecting each weighted pose
   * component before reducing the mixture preserves nonlinear heading uncertainty. The covariance
   * is unavailable when any weighted component has undefined planar heading.
   *
   * @return a defensive copy, filled with NaN when unavailable
   */
  @Override
  public Matrix<N3, N3> planarCovariance() {
    return planarCovariance.copy();
  }

  /**
   * Returns whether the Quest reported a usable component-wise planar covariance.
   *
   * @return true when {@link #planarCovariance()} holds numbers
   */
  public boolean hasPlanarCovariance() {
    return !Double.isNaN(planarCovariance.get(0, 0));
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
   * Returns this measurement's forward, left, and yaw standard deviations for a WPILib 2D pose
   * estimator, as measured-heading marginals. This approximation is intended only for near-zero
   * innovation; it discards correlations and uses the measured heading rather than the predicted
   * heading. Prefer {@link #pose2dStdDevs(Pose2d)} for WPILib 2026.2.1.
   *
   * @return a three-element column vector containing X meters, Y meters, and yaw radians
   */
  public Matrix<N3, N1> pose2dStdDevs() {
    return standardDeviations.toPose2dStdDevs();
  }

  /**
   * Returns conservative diagonal standard deviations for WPILib 2026.2.1's vision innovation.
   *
   * <p>Pass the estimator's predicted pose at this frame's capture timestamp. WPILib 2026.2.1 uses
   * {@code measuredPose.minus(predictedPose)}, a {@code Transform2d} whose translation is expressed
   * along the predicted heading. This rotates the field planar covariance into that frame, then
   * scales all marginal variances by the maximum absolute row sum of its correlation matrix. The
   * resulting diagonal covariance bounds the full transformed covariance without mixing meters and
   * radians; its variance scale is between 1 and 3 for a positive definite input.
   *
   * <p>This contract does not cover WPILib 2025's {@code Pose2d.log} innovation. It also does not
   * make a large or multimodal residual Gaussian: robot code must still gate the innovation before
   * fusion. This method does not mutate an estimator or subscribe to additional data.
   *
   * @param predictedPoseAtCapture finite predicted field pose at the measurement timestamp
   * @return a column vector containing predicted-frame X meters, Y meters, and yaw radians
   * @throws IllegalStateException if component-wise planar covariance is unavailable or cannot
   *     produce finite positive variances
   * @throws IllegalArgumentException if the predicted pose is not finite
   */
  public Matrix<N3, N1> pose2dStdDevs(Pose2d predictedPoseAtCapture) {
    if (!hasPlanarCovariance()) {
      throw new IllegalStateException("Component-wise planar covariance is unavailable");
    }
    var jacobian = pose2dInnovationJacobian(predictedPoseAtCapture);
    var local = jacobian.times(planarCovariance).times(jacobian.transpose());
    double[] marginal = new double[3];
    for (int i = 0; i < 3; i++) {
      double variance = local.get(i, i);
      if (!(variance > 0) || !Double.isFinite(variance)) {
        throw new IllegalStateException("Planar covariance must produce finite positive variances");
      }
      marginal[i] = Math.sqrt(variance);
    }
    // The largest eigenvalue of the correlation matrix is bounded by its absolute row sums.
    // Congruence by the marginal standard deviations then makes alpha * diag(local) - local PSD.
    double alpha = 1;
    for (int i = 0; i < 3; i++) {
      double rowSum = 0;
      for (int j = 0; j < 3; j++) {
        rowSum += Math.abs(local.get(i, j) / marginal[i] / marginal[j]);
      }
      alpha = Math.max(alpha, rowSum);
    }
    if (!Double.isFinite(alpha)) {
      throw new IllegalStateException("Planar covariance must produce finite correlations");
    }
    double scale = Math.sqrt(alpha);
    return VecBuilder.fill(scale * marginal[0], scale * marginal[1], scale * marginal[2]);
  }

  // Derivative of measured.minus(predicted) with respect to measured field x, y, heading.
  static Matrix<N3, N3> pose2dInnovationJacobian(Pose2d predicted) {
    if (!Double.isFinite(predicted.getX())
        || !Double.isFinite(predicted.getY())
        || !Double.isFinite(predicted.getRotation().getRadians())) {
      throw new IllegalArgumentException("Predicted pose must be finite");
    }
    double cos = predicted.getRotation().getCos();
    double sin = predicted.getRotation().getSin();
    var jacobian = new Matrix<>(Nat.N3(), Nat.N3());
    jacobian.set(0, 0, cos);
    jacobian.set(0, 1, sin);
    jacobian.set(1, 0, -sin);
    jacobian.set(1, 1, cos);
    jacobian.set(2, 2, 1);
    return jacobian;
  }
}
