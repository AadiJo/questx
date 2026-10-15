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
  }

  @Test
  void decodesCSharpAprilTagV3Ambiguity() throws IOException {
    var frame = MeasurementWireFormat.aprilTagV3(fixture("measurement-v3", "apriltag"), 7_000_000);
    assertNotNull(frame);
    var v2 = MeasurementWireFormat.aprilTag(fixture("apriltag"), 7_000_000);
    assertEquals(v2.fieldPose3d(), frame.fieldPose3d());
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
    assertEquals(List.of(0.5, 0.25, 0.0625), frame.tagAmbiguities());
  }

  @Test
  void rejectsMalformedAprilTagV3() throws IOException {
    var valid = fixture("measurement-v3", "apriltag");
    assertNull(MeasurementWireFormat.aprilTagV3(fixture("apriltag"), 1), "v2 layout is not v3");
    assertNull(MeasurementWireFormat.aprilTagV3(Arrays.copyOf(valid, 25), 1));
    assertNull(MeasurementWireFormat.aprilTagV3(Arrays.copyOf(valid, valid.length - 1), 1));
    for (int i = 0; i < valid.length; i++) {
      var invalid = valid.clone();
      invalid[i] = Double.NaN;
      assertNull(MeasurementWireFormat.aprilTagV3(invalid, 1));
    }
    // Ambiguity, reprojection error, and per-tag ambiguity ranges.
    for (int index : new int[] {23, 24, 28, 29, 30}) {
      var invalid = valid.clone();
      invalid[index] = -0.01;
      assertNull(MeasurementWireFormat.aprilTagV3(invalid, 1));
    }
    for (int index : new int[] {23, 28, 30}) {
      var invalid = valid.clone();
      invalid[index] = 1.01;
      assertNull(MeasurementWireFormat.aprilTagV3(invalid, 1));
    }
    var largeError = valid.clone();
    largeError[24] = 40;
    assertNotNull(
        MeasurementWireFormat.aprilTagV3(largeError, 1), "Reprojection error is unbounded");
    var badId = valid.clone();
    badId[26] = 2.5;
    assertNull(MeasurementWireFormat.aprilTagV3(badId, 1));
  }

  @Test
  void decodesAprilTagV3WithoutTags() throws IOException {
    var data = Arrays.copyOf(fixture("measurement-v3", "apriltag"), 25);
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
