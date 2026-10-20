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
import edu.wpi.first.math.geometry.Pose2d;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.List;
import org.junit.jupiter.api.Test;

class MeasurementWireFormatTest {
  private double[] fixture(String name) throws IOException {
    return fixture("measurement-v2", name);
  }

  private double[] fixture(String directory, String name) throws IOException {
    try (var input = getClass().getResourceAsStream("/" + directory + "/" + name + ".txt")) {
      assertNotNull(
          input, "Generate fixtures using QuestX.Editor.CI.MeasurementWireFixtures.Export");
      return Arrays.stream(
              new String(input.readAllBytes(), StandardCharsets.UTF_8).trim().split(","))
          .mapToDouble(Double::parseDouble)
          .toArray();
    }
  }

  @Test
  void decodesCSharpVioWithoutLosing64BitFields() throws IOException {
    var frame = MeasurementWireFormat.vio(fixture("vio"), 5_000_123);
    assertNotNull(frame);
    assertEquals(0xfedcba9876543210L, frame.sequenceId());
    assertEquals(0x0020000000000001L, frame.captureTimestampMicros());
    assertEquals(5.000123, frame.measurementTimestamp());
    assertEquals(12.5, frame.appTimestamp());
    assertEquals(0xffff_ffffL, frame.trackingEpoch());
    assertTrue(frame.isTracking());
    assertEquals(1.25, frame.questPose3d().getX());
    assertEquals(-2.5, frame.questPose3d().getY());
    assertEquals(0.75, frame.questPose3d().getZ());
    var q = frame.questPose3d().getRotation().getQuaternion();
    assertEquals(0.5, q.getW());
    assertEquals(0.5, q.getX());
    assertEquals(-0.5, q.getY());
    assertEquals(0.5, q.getZ());
  }

  @Test
  void preservesRestartAndTrackingLoss() throws IOException {
    var frame = MeasurementWireFormat.vio(fixture("vio-restarted"), 6_000_000);
    assertNotNull(frame);
    assertEquals(0, frame.sequenceId());
    assertEquals(0, frame.trackingEpoch());
    assertFalse(frame.isTracking());
  }

  @Test
  void decodesCSharpAprilTagQualityAndTimestamp() throws IOException {
    var frame = MeasurementWireFormat.aprilTag(fixture("apriltag"), 7_000_000);
    assertNotNull(frame);
    assertEquals(7, frame.measurementTimestamp());
    assertEquals(987654321, frame.captureTimestampMicros());
    assertEquals(4.5, frame.appTimestamp());
    assertEquals(42, frame.sequenceId());
    assertEquals(List.of(1, 22, 586), frame.tagIds());
    assertEquals(3, frame.tagCount());
    assertEquals(10, frame.inlierCount());
    assertEquals(12, frame.cornerCount());
    assertEquals(10.0 / 12, frame.inlierRatio());
    assertEquals(2.75, frame.averageTagDistanceMeters());
    assertEquals(1.25, frame.fieldPose3d().getX());
    assertEquals(
        new PoseStandardDeviations(0.1, 0.2, 0.3, 0.01, 0.02, 0.03), frame.standardDeviations());
    assertFalse(frame.hasAmbiguity(), "v2 arrays do not carry ambiguity");
    assertTrue(Double.isNaN(frame.reprojectionErrorPixels()));
    assertTrue(frame.tagAmbiguities().isEmpty());
    assertTrue(Double.isNaN(frame.processingLatencyMilliseconds()));
  }

  @Test
  void decodesCSharpAprilTagV3Ambiguity() throws IOException {
    var frame = MeasurementWireFormat.aprilTagV3(fixture("measurement-v3", "apriltag"), 7_000_000);
    assertNotNull(frame);
    var v2 = MeasurementWireFormat.aprilTag(fixture("apriltag"), 7_000_000);
    // The synthetic v3+ fixture uses a nonsingular planar heading; the v2 fixture stays frozen.
    assertEquals(v2.fieldPose3d().getTranslation(), frame.fieldPose3d().getTranslation());
    assertEquals(0.12, frame.fieldPose3d().getRotation().getX(), 1e-12);
    assertEquals(0.3, frame.fieldPose3d().getRotation().getY(), 1e-12);
    assertEquals(0.6, frame.fieldPose3d().getRotation().getZ(), 1e-12);
    assertEquals(v2.sequenceId(), frame.sequenceId());
    assertEquals(v2.captureTimestampMicros(), frame.captureTimestampMicros());
    assertEquals(v2.tagIds(), frame.tagIds());
    assertEquals(v2.inlierCount(), frame.inlierCount());
    assertEquals(v2.cornerCount(), frame.cornerCount());
    assertEquals(v2.inlierRatio(), frame.inlierRatio());
    assertEquals(v2.standardDeviations(), frame.standardDeviations());
    assertTrue(frame.hasAmbiguity());
    assertEquals(0.125, frame.ambiguity());
    assertEquals(0.875, frame.reprojectionErrorPixels());
    assertEquals(38.5, frame.processingLatencyMilliseconds());
    assertEquals(List.of(0.5, 0.25, 0.0625), frame.tagAmbiguities());
  }

  @Test
  void decodesEncoderSampleFrames() throws IOException {
    var single =
        MeasurementWireFormat.aprilTagV3(fixture("measurement-v3", "apriltag-single-tag"), 1);
    assertNotNull(single);
    assertEquals(1041, single.sequenceId());
    assertEquals(81_234_567, single.captureTimestampMicros());
    assertEquals(List.of(26), single.tagIds());
    assertEquals(0.306, single.ambiguity(), 0.001);
    assertEquals(List.of(single.ambiguity()), single.tagAmbiguities());
    assertEquals(41.25, single.processingLatencyMilliseconds());

    var pair = MeasurementWireFormat.aprilTagV3(fixture("measurement-v3", "apriltag-two-tags"), 1);
    assertNotNull(pair);
    assertEquals(List.of(25, 26), pair.tagIds());
    assertEquals(8, pair.cornerCount());
    assertEquals(2, pair.tagAmbiguities().size());
    assertTrue(pair.ambiguity() < pair.tagAmbiguities().get(1), "coplanar pair solved jointly");
    assertEquals(39.5, pair.processingLatencyMilliseconds());
  }

  @Test
  void rejectsMalformedAprilTagV3() throws IOException {
    var valid = fixture("measurement-v3", "apriltag");
    assertNull(MeasurementWireFormat.aprilTagV3(fixture("apriltag"), 1), "v2 layout is not v3");
    assertNull(MeasurementWireFormat.aprilTagV3(Arrays.copyOf(valid, 26), 1));
    assertNull(MeasurementWireFormat.aprilTagV3(Arrays.copyOf(valid, valid.length - 1), 1));
    for (int i = 0; i < valid.length; i++) {
      var invalid = valid.clone();
      invalid[i] = Double.NaN;
      assertNull(MeasurementWireFormat.aprilTagV3(invalid, 1));
    }
    // Ambiguity, reprojection error, processing latency, and per-tag ambiguity ranges.
    for (int index : new int[] {23, 24, 25, 29, 30, 31}) {
      var invalid = valid.clone();
      invalid[index] = -0.01;
      assertNull(MeasurementWireFormat.aprilTagV3(invalid, 1));
    }
    for (int index : new int[] {23, 29, 31}) {
      var invalid = valid.clone();
      invalid[index] = 1.01;
      assertNull(MeasurementWireFormat.aprilTagV3(invalid, 1));
    }
    var largeError = valid.clone();
    largeError[24] = 40;
    assertNotNull(
        MeasurementWireFormat.aprilTagV3(largeError, 1), "Reprojection error is unbounded");
    var slow = valid.clone();
    slow[25] = 5_000;
    assertNotNull(MeasurementWireFormat.aprilTagV3(slow, 1), "Latency has no upper bound");
    var badId = valid.clone();
    badId[27] = 2.5;
    assertNull(MeasurementWireFormat.aprilTagV3(badId, 1));
  }

  @Test
  void decodesCSharpAprilTagV4Covariance() throws IOException {
    var data = fixture("measurement-v4", "apriltag");
    var frame = MeasurementWireFormat.aprilTagV4(data, 7_000_000);
    assertNotNull(frame);
    var v3 = MeasurementWireFormat.aprilTagV3(fixture("measurement-v3", "apriltag"), 7_000_000);
    assertEquals(v3.fieldPose3d(), frame.fieldPose3d());
    assertEquals(v3.sequenceId(), frame.sequenceId());
    assertEquals(v3.tagIds(), frame.tagIds());
    assertEquals(v3.ambiguity(), frame.ambiguity());
    assertEquals(v3.reprojectionErrorPixels(), frame.reprojectionErrorPixels());
    assertEquals(v3.processingLatencyMilliseconds(), frame.processingLatencyMilliseconds());
    assertEquals(v3.tagAmbiguities(), frame.tagAmbiguities());
    assertFalse(v3.hasCovariance(), "v3 arrays do not carry a covariance");
    assertTrue(Double.isNaN(v3.covariance().get(2, 3)));

    assertTrue(frame.hasCovariance());
    var covariance = frame.covariance();
    assertEquals(0.01, covariance.get(0, 0), 1e-15);
    assertEquals(0.005, covariance.get(0, 1));
    assertEquals(0.005, covariance.get(1, 0), "symmetric");
    assertEquals(-0.003, covariance.get(1, 5));
    assertEquals(0.0009, covariance.get(5, 5), 1e-15);
    covariance.set(0, 0, 99);
    assertEquals(0.01, frame.covariance().get(0, 0), 1e-15, "covariance() returns a copy");

    assertTrue(frame.hasPlanarCovariance());
    assertFalse(v3.hasPlanarCovariance());
    assertTrue(Double.isNaN(v3.planarCovariance().get(1, 2)));
    var planar = frame.planarCovariance();
    for (int row = 0, index = 47; row < 3; row++) {
      for (int column = row; column < 3; column++, index++) {
        assertEquals(data[index], planar.get(row, column));
        assertEquals(data[index], planar.get(column, row));
      }
    }
    planar.set(0, 0, 99);
    assertEquals(data[47], frame.planarCovariance().get(0, 0));
    data[47] = 99;
    assertEquals(0.01, frame.planarCovariance().get(0, 0));

    // The reported marginals use measured heading, derived from the separately projected block.
    var heading = AprilTagFrame.pose2dInnovationJacobian(frame.fieldPose3d().toPose2d());
    var local = heading.times(frame.planarCovariance()).times(heading.transpose());
    var standard = frame.pose2dStdDevs();
    for (int i = 0; i < 3; i++) {
      assertEquals(Math.sqrt(local.get(i, i)), standard.get(i, 0), 1e-12);
    }
  }

  @Test
  void decodesEncoderSampleFramesWithCovariance() throws IOException {
    for (var name : List.of("apriltag-single-tag", "apriltag-two-tags")) {
      var frame = MeasurementWireFormat.aprilTagV4(fixture("measurement-v4", name), 1);
      assertNotNull(frame, name);
      var v3 = MeasurementWireFormat.aprilTagV3(fixture("measurement-v3", name), 1);
      assertEquals(v3.fieldPose3d(), frame.fieldPose3d(), name);
      assertEquals(v3.standardDeviations(), frame.standardDeviations(), name);

      // Up, roll, and pitch are the covariance's, rotated into the robot's frame.
      var rotation = frame.fieldPose3d().getRotation().toMatrix();
      var basis = new Matrix<>(Nat.N6(), Nat.N6());
      basis.assignBlock(0, 0, rotation.transpose());
      basis.assignBlock(3, 3, rotation.transpose());
      var local = basis.times(frame.covariance()).times(basis.transpose());
      var s = frame.standardDeviations();
      assertEquals(s.zMeters(), Math.sqrt(local.get(2, 2)), 1e-9 * s.zMeters(), name);
      assertEquals(s.rollRadians(), Math.sqrt(local.get(3, 3)), 1e-9 * s.rollRadians(), name);
      assertEquals(s.pitchRadians(), Math.sqrt(local.get(4, 4)), 1e-9 * s.pitchRadians(), name);

      assertTrue(frame.hasPlanarCovariance(), name);
      var heading = AprilTagFrame.pose2dInnovationJacobian(frame.fieldPose3d().toPose2d());
      var planar = heading.times(frame.planarCovariance()).times(heading.transpose());
      double[] reported = {s.xMeters(), s.yMeters(), s.yawRadians()};
      for (int i = 0; i < 3; i++) {
        assertEquals(Math.sqrt(planar.get(i, i)), reported[i], 1e-9 * reported[i], name);
      }
    }
    var single =
        MeasurementWireFormat.aprilTagV4(fixture("measurement-v4", "apriltag-single-tag"), 1);
    var pair = MeasurementWireFormat.aprilTagV4(fixture("measurement-v4", "apriltag-two-tags"), 1);
    assertTrue(
        pair.standardDeviations().xMeters() < single.standardDeviations().xMeters(),
        "two nearer tags constrain the pose better than one farther, ambiguous tag");
    assertTrue(single.pose2dStdDevs().get(2, 0) > pair.pose2dStdDevs().get(2, 0));
  }

  @Test
  void framesWithEqualValuesAreEqualAndHashAlike() throws IOException {
    var data = fixture("measurement-v4", "apriltag");
    var a = MeasurementWireFormat.aprilTagV4(data, 1);
    var b = MeasurementWireFormat.aprilTagV4(data.clone(), 1);
    assertEquals(a, b);
    assertEquals(a.hashCode(), b.hashCode());
    assertTrue(new java.util.HashSet<>(List.of(a)).contains(b));
    var changed = data.clone();
    changed[27] = 0.004;
    assertNotEquals(a, MeasurementWireFormat.aprilTagV4(changed, 1), "covariance takes part");
    changed = data.clone();
    changed[48] *= 0.9;
    var changedPlanar = MeasurementWireFormat.aprilTagV4(changed, 1);
    assertNotNull(changedPlanar);
    assertNotEquals(a, changedPlanar, "planar covariance takes part");
    assertNotEquals(a.hashCode(), changedPlanar.hashCode());

    // Frames without a covariance compare equal too, though theirs holds NaN.
    var v3 = fixture("measurement-v3", "apriltag");
    var c = MeasurementWireFormat.aprilTagV3(v3, 1);
    var d = MeasurementWireFormat.aprilTagV3(v3.clone(), 1);
    assertEquals(c, d);
    assertEquals(c.hashCode(), d.hashCode());
  }

  @Test
  void rejectsMalformedAprilTagV4() throws IOException {
    var valid = fixture("measurement-v4", "apriltag");
    assertNull(MeasurementWireFormat.aprilTagV4(fixture("measurement-v3", "apriltag"), 1));
    assertNull(MeasurementWireFormat.aprilTagV4(Arrays.copyOf(valid, 52), 1));
    assertNull(MeasurementWireFormat.aprilTagV4(Arrays.copyOf(valid, valid.length - 1), 1));
    for (int i = 0; i < valid.length; i++) {
      var invalid = valid.clone();
      invalid[i] = Double.NaN;
      assertNull(MeasurementWireFormat.aprilTagV4(invalid, 1), "NaN at " + i);
    }
    // Variances (the diagonal) must be positive.
    for (int index : new int[] {26, 32, 37, 41, 44, 46, 47, 50, 52}) {
      var invalid = valid.clone();
      invalid[index] = 0;
      assertNull(MeasurementWireFormat.aprilTagV4(invalid, 1), "variance at " + index);
    }
    // A correlation beyond 1: |cov(x, y)| > sqrt(var x * var y) = 0.02.
    var invalid = valid.clone();
    invalid[27] = -0.021;
    assertNull(MeasurementWireFormat.aprilTagV4(invalid, 1));
    // Every pairwise correlation is -0.9, yet the x, y, z block has a negative eigenvalue.
    invalid = valid.clone();
    invalid[27] = -0.018;
    invalid[28] = -0.027;
    invalid[33] = -0.054;
    assertNull(MeasurementWireFormat.aprilTagV4(invalid, 1), "not positive definite");
    var badId = valid.clone();
    badId[54] = 2.5;
    assertNull(MeasurementWireFormat.aprilTagV4(badId, 1));
  }

  @Test
  void planarSentinelIsExactlyTheAllZeroBlock() throws IOException {
    var data = fixture("measurement-v4", "apriltag");
    Arrays.fill(data, 47, 53, 0);
    var frame = MeasurementWireFormat.aprilTagV4(data, 1);
    assertNotNull(frame);
    assertTrue(frame.hasCovariance());
    assertFalse(frame.hasPlanarCovariance());
    for (double entry : frame.planarCovariance().getData()) assertTrue(Double.isNaN(entry));
    assertThrows(IllegalStateException.class, () -> frame.pose2dStdDevs(new Pose2d()));
    for (int index = 47; index < 53; index++) {
      var incomplete = data.clone();
      incomplete[index] = 0.01;
      assertNull(MeasurementWireFormat.aprilTagV4(incomplete, 1), "partial block at " + index);
    }
  }

  @Test
  void rejectsIndefinitePlanarCovarianceAndOldV4Layout() throws IOException {
    var data = fixture("measurement-v4", "apriltag");
    // All pairwise correlations are valid, but the three-way block is indefinite.
    double[] indefinite = {1, -0.9, -0.9, 1, -0.9, 1};
    System.arraycopy(indefinite, 0, data, 47, 6);
    assertNull(MeasurementWireFormat.aprilTagV4(data, 1));
    data = fixture("measurement-v4", "apriltag");
    data[48] = 2 * Math.sqrt(data[47] * data[50]);
    assertNull(MeasurementWireFormat.aprilTagV4(data, 1));
    data = fixture("measurement-v4", "apriltag");
    var oldLayout = new double[data.length - 6];
    System.arraycopy(data, 0, oldLayout, 0, 47);
    System.arraycopy(data, 53, oldLayout, 47, data.length - 53);
    assertNull(MeasurementWireFormat.aprilTagV4(oldLayout, 1));
  }

  @Test
  void decodesAprilTagV3WithoutTags() throws IOException {
    var data = Arrays.copyOf(fixture("measurement-v3", "apriltag"), 26);
    data[12] = 0;
    var frame = MeasurementWireFormat.aprilTagV3(data, 1);
    assertNotNull(frame);
    assertTrue(frame.tagIds().isEmpty());
    assertTrue(frame.tagAmbiguities().isEmpty());
  }

  @Test
  void rejectsMalformedVioAndMissingTimestamp() throws IOException {
    var valid = fixture("vio");
    assertNull(MeasurementWireFormat.vio(new double[0], 1));
    assertNull(MeasurementWireFormat.vio(Arrays.copyOf(valid, 15), 1));
    assertNull(MeasurementWireFormat.vio(valid, 0));
    for (int i = 0; i < valid.length; i++) {
      var invalid = valid.clone();
      invalid[i] = Double.NaN;
      assertNull(MeasurementWireFormat.vio(invalid, 1));
    }
    for (int index : new int[] {0, 1, 2, 3, 13}) {
      var invalid = valid.clone();
      invalid[index] = 0x1_0000_0000L;
      assertNull(MeasurementWireFormat.vio(invalid, 1));
      invalid[index] = 0.5;
      assertNull(MeasurementWireFormat.vio(invalid, 1));
    }
    var invalid = valid.clone();
    invalid[12] = 2;
    assertNull(MeasurementWireFormat.vio(invalid, 1));
    Arrays.fill(invalid, 8, 12, 0);
    invalid[12] = 1;
    assertNull(MeasurementWireFormat.vio(invalid, 1));
  }

  @Test
  void rejectsMalformedTagCountsAndQuality() throws IOException {
    var valid = fixture("apriltag");
    assertNull(MeasurementWireFormat.aprilTag(Arrays.copyOf(valid, 23), 1));
    for (int i = 0; i < valid.length; i++) {
      var invalid = valid.clone();
      invalid[i] = Double.POSITIVE_INFINITY;
      assertNull(MeasurementWireFormat.aprilTag(invalid, 1));
    }
    for (int index : new int[] {12, 13, 14, 16, 17, 18, 19, 20, 21, 22, 23}) {
      var invalid = valid.clone();
      invalid[index] = -1;
      assertNull(MeasurementWireFormat.aprilTag(invalid, 1));
    }
    var invalid = valid.clone();
    invalid[15] = 1.5;
    assertNull(MeasurementWireFormat.aprilTag(invalid, 1));
    invalid = valid.clone();
    invalid[13] = 13;
    assertNull(MeasurementWireFormat.aprilTag(invalid, 1));
    invalid = valid.clone();
    invalid[23] = 1.5;
    assertNull(MeasurementWireFormat.aprilTag(invalid, 1));
  }

  @Test
  void handlesVariableTagListsWithoutTruncation() throws IOException {
    var data = Arrays.copyOf(fixture("apriltag"), 23 + 1024);
    data[12] = 1024;
    for (int i = 0; i < 1024; i++) data[23 + i] = i;
    var frame = MeasurementWireFormat.aprilTag(data, 1);
    assertNotNull(frame);
    assertEquals(1024, frame.tagIds().size());
    assertEquals(1023, frame.tagIds().get(1023));
    data = Arrays.copyOf(data, 23);
    data[12] = 0;
    assertTrue(MeasurementWireFormat.aprilTag(data, 1).tagIds().isEmpty());
  }
}
