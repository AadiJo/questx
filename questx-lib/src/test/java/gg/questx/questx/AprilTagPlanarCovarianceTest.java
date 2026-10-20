/*
* QUESTX
  https://github.com/QuestX
* Copyright (C) 2026 QuestX
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the MIT License as published.
*/
package gg.questx.questx;

import static org.junit.jupiter.api.Assertions.*;

import edu.wpi.first.math.Matrix;
import edu.wpi.first.math.Nat;
import edu.wpi.first.math.VecBuilder;
import edu.wpi.first.math.estimator.DifferentialDrivePoseEstimator;
import edu.wpi.first.math.geometry.Pose2d;
import edu.wpi.first.math.geometry.Pose3d;
import edu.wpi.first.math.geometry.Rotation2d;
import edu.wpi.first.math.geometry.Transform2d;
import edu.wpi.first.math.kinematics.DifferentialDriveKinematics;
import edu.wpi.first.math.numbers.N3;
import java.util.List;
import org.junit.jupiter.api.Test;

class AprilTagPlanarCovarianceTest {
  private static AprilTagFrame frame(Pose2d measured, Matrix<N3, N3> covariance) {
    return new AprilTagFrame(
        new Pose3d(measured),
        1,
        1_000_000,
        1,
        1,
        List.of(1),
        1,
        4,
        4,
        1,
        2,
        new PoseStandardDeviations(0.1, 0.2, 0.3, 0.01, 0.02, 0.03),
        0,
        0.5,
        List.of(0.0),
        30,
        null,
        covariance);
  }

  private static Matrix<N3, N3> correlatedCovariance() {
    // L L^T is positive definite, with translation/heading correlations and different units.
    var factor = new Matrix<>(Nat.N3(), Nat.N3());
    factor.set(0, 0, 0.4);
    factor.set(1, 0, 0.3);
    factor.set(1, 1, 0.2);
    factor.set(2, 0, -0.04);
    factor.set(2, 1, 0.03);
    factor.set(2, 2, 0.05);
    return factor.times(factor.transpose());
  }

  @Test
  void constructorAndAccessorDefensivelyCopyPlanarCovariance() {
    var source = correlatedCovariance();
    var frame = frame(new Pose2d(), source);
    var equal = frame(new Pose2d(), source.copy());
    source.fill(99);
    frame.planarCovariance().fill(42);
    assertEquals(equal, frame);
    assertEquals(equal.hashCode(), frame.hashCode());
    assertEquals(0.16, frame.planarCovariance().get(0, 0), 1e-15);
  }

  @Test
  void compatibilityConstructorsDoNotInventPlanarCovariance() {
    var full = new Matrix<>(Nat.N6(), Nat.N6());
    for (int i = 0; i < 6; i++) full.set(i, i, 1);
    var frame =
        new AprilTagFrame(
            new Pose3d(),
            1,
            1,
            1,
            1,
            List.of(1),
            1,
            4,
            4,
            1,
            2,
            new PoseStandardDeviations(0.1, 0.2, 0.3, 0.01, 0.02, 0.03),
            0,
            0.5,
            List.of(0.0),
            30,
            full);
    assertTrue(frame.hasCovariance());
    assertFalse(frame.hasPlanarCovariance());
    assertThrows(IllegalStateException.class, () -> frame.pose2dStdDevs(new Pose2d()));
    full.set(0, 0, 99);
    assertEquals(1, frame.covariance().get(0, 0));
    var absent = frame(new Pose2d(), null);
    assertFalse(absent.hasPlanarCovariance());
    assertEquals(absent, frame(new Pose2d(), null));
    assertThrows(IllegalStateException.class, () -> absent.pose2dStdDevs(new Pose2d()));
  }

  @Test
  void jacobianMatchesActualWpilibTransformAtNonzeroWrappedAndZeroInnovation() {
    Pose2d[][] cases = {
      {new Pose2d(1, -2, new Rotation2d(0.7)), new Pose2d(4, 1, new Rotation2d(-1.1))},
      {
        new Pose2d(2, 1, new Rotation2d(Math.PI - 0.03)),
        new Pose2d(-1, 4, new Rotation2d(-Math.PI + 0.07))
      },
      {new Pose2d(2, 1, new Rotation2d(-2.1)), new Pose2d(2, 1, new Rotation2d(-2.1))}
    };
    for (var poses : cases) {
      var analytic = AprilTagFrame.pose2dInnovationJacobian(poses[0]);
      var numeric = finiteDifferenceJacobian(poses[0], poses[1]);
      for (int i = 0; i < 3; i++) {
        for (int j = 0; j < 3; j++) {
          assertEquals(numeric.get(i, j), analytic.get(i, j), 1e-8, i + "," + j);
        }
      }
    }
  }

  private static Matrix<N3, N3> finiteDifferenceJacobian(Pose2d predicted, Pose2d measured) {
    double step = 1e-6;
    var result = new Matrix<>(Nat.N3(), Nat.N3());
    for (int column = 0; column < 3; column++) {
      var plus = perturb(measured, column, step).minus(predicted);
      var minus = perturb(measured, column, -step).minus(predicted);
      result.set(0, column, (plus.getX() - minus.getX()) / (2 * step));
      result.set(1, column, (plus.getY() - minus.getY()) / (2 * step));
      result.set(
          2, column, plus.getRotation().minus(minus.getRotation()).getRadians() / (2 * step));
    }
    return result;
  }

  private static Pose2d perturb(Pose2d pose, int axis, double delta) {
    return new Pose2d(
        pose.getX() + (axis == 0 ? delta : 0),
        pose.getY() + (axis == 1 ? delta : 0),
        pose.getRotation().plus(new Rotation2d(axis == 2 ? delta : 0)));
  }

  @Test
  void rotatesAnisotropyAlongPredictedHeadingRatherThanMeasuredHeading() {
    var covariance = new Matrix<>(Nat.N3(), Nat.N3());
    covariance.set(0, 0, 0.01);
    covariance.set(1, 1, 0.09);
    covariance.set(2, 2, 0.0004);
    var frame = frame(new Pose2d(8, -4, new Rotation2d(0.6)), covariance);
    var deviations = frame.pose2dStdDevs(new Pose2d(1, 2, new Rotation2d(Math.PI / 2)));
    assertEquals(0.3, deviations.get(0, 0), 1e-12);
    assertEquals(0.1, deviations.get(1, 0), 1e-12);
    assertEquals(0.02, deviations.get(2, 0), 1e-12);
    // Changing the measured pose does not introduce Pose2d.log's translation/yaw coupling.
    var other = frame(new Pose2d(-12, 9, new Rotation2d(-1.8)), covariance);
    assertArrayEquals(
        deviations.getData(),
        other.pose2dStdDevs(new Pose2d(1, 2, new Rotation2d(Math.PI / 2))).getData(),
        1e-12);
    assertDoesNotThrow(() -> frame.pose2dStdDevs(new Pose2d(0, 0, new Rotation2d(0.6 + Math.PI))));
  }

  @Test
  void diagonalBoundDominatesCorrelatedCovarianceInEverySampledDirection() {
    var covariance = correlatedCovariance();
    var measured = new Pose2d(3, -4, new Rotation2d(-1.2));
    var frame = frame(measured, covariance);
    for (double yaw : new double[] {0, 0.3, 1.1, -2.8}) {
      var predicted = new Pose2d(1, 2, new Rotation2d(yaw));
      var jacobian = finiteDifferenceJacobian(predicted, measured);
      var transformed = jacobian.times(covariance).times(jacobian.transpose());
      var deviations = frame.pose2dStdDevs(predicted);
      double scale = deviations.get(0, 0) * deviations.get(0, 0) / transformed.get(0, 0);
      assertTrue(scale > 1 && scale <= 3 + 1e-8);
      for (int i = 0; i < 3; i++) {
        assertEquals(
            scale, deviations.get(i, 0) * deviations.get(i, 0) / transformed.get(i, i), 1e-8);
      }
      // Congruence-normalized D-B: sample mixed meter/radian directions without unit dominance.
      for (int x = -6; x <= 6; x++) {
        for (int y = -6; y <= 6; y++) {
          for (int z = -6; z <= 6; z++) {
            double[] direction = {x, y, z};
            double quadratic = 0;
            for (int i = 0; i < 3; i++) {
              for (int j = 0; j < 3; j++) {
                double bound = i == j ? deviations.get(i, 0) * deviations.get(i, 0) : 0;
                quadratic +=
                    direction[i]
                        * direction[j]
                        * (bound - transformed.get(i, j))
                        / Math.sqrt(transformed.get(i, i) * transformed.get(j, j));
              }
            }
            assertTrue(quadratic >= -1e-7, "D-B must be positive semidefinite: " + quadratic);
          }
        }
      }
    }
  }

  @Test
  void rejectsNonfinitePrediction() {
    var frame = frame(new Pose2d(), correlatedCovariance());
    for (var predicted :
        List.of(
            new Pose2d(Double.NaN, 0, new Rotation2d()),
            new Pose2d(0, Double.POSITIVE_INFINITY, new Rotation2d()),
            new Pose2d(0, 0, new Rotation2d(Double.NaN)))) {
      assertThrows(IllegalArgumentException.class, () -> frame.pose2dStdDevs(predicted));
    }
  }

  @Test
  void actual2026EstimatorAppliesTransformCorrectionWithReturnedNoise() {
    var predicted = new Pose2d(1, -2, new Rotation2d(0.7));
    var measured = new Pose2d(4, 1, new Rotation2d(-1.1));
    var frame = frame(measured, correlatedCovariance());
    var stateNoise = VecBuilder.fill(0.2, 0.3, 0.1);
    var estimator =
        new DifferentialDrivePoseEstimator(
            new DifferentialDriveKinematics(0.6),
            predicted.getRotation(),
            0,
            0,
            predicted,
            stateNoise,
            VecBuilder.fill(1, 1, 1));
    estimator.updateWithTime(1, predicted.getRotation(), 0, 0);
    var noise = frame.pose2dStdDevs(estimator.sampleAt(1).orElseThrow());
    var innovation = measured.minus(predicted);
    double[] residual = {
      innovation.getX(), innovation.getY(), innovation.getRotation().getRadians()
    };
    double[] correction = new double[3];
    for (int i = 0; i < 3; i++) {
      correction[i] = stateNoise.get(i, 0) / (stateNoise.get(i, 0) + noise.get(i, 0)) * residual[i];
    }
    var expected =
        predicted.plus(
            new Transform2d(correction[0], correction[1], new Rotation2d(correction[2])));
    estimator.addVisionMeasurement(measured, 1, noise);
    var actual = estimator.getEstimatedPosition();
    assertEquals(expected.getX(), actual.getX(), 1e-12);
    assertEquals(expected.getY(), actual.getY(), 1e-12);
    assertEquals(expected.getRotation().getRadians(), actual.getRotation().getRadians(), 1e-12);
  }
}
