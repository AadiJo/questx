/*
* QUESTX
  https://github.com/QuestX
* Copyright (C) 2025 QuestX
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the MIT License as published.
*/
package gg.questx.questx;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import edu.wpi.first.math.geometry.Pose2d;
import edu.wpi.first.math.geometry.Pose3d;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;

class MeasurementFrameTest {
  @Test
  void aprilTagFrameDefensivelyCopiesTagLists() {
    var sourceTagIds = new ArrayList<>(List.of(1, 2));
    var sourceTagAmbiguities = new ArrayList<>(List.of(0.3, 0.4));
    var frame =
        new AprilTagFrame(
            new Pose3d(),
            12.5,
            12_500_000,
            4.0,
            9,
            sourceTagIds,
            2,
            8,
            8,
            1.0,
            2.5,
            new PoseStandardDeviations(0.1, 0.2, 0.3, 0.01, 0.02, 0.03),
            0.1,
            0.6,
            sourceTagAmbiguities,
            41.25);

    sourceTagIds.add(3);
    sourceTagAmbiguities.add(0.9);

    assertEquals(List.of(1, 2), frame.tagIds());
    assertThrows(UnsupportedOperationException.class, () -> frame.tagIds().add(4));
    assertEquals(List.of(0.3, 0.4), frame.tagAmbiguities());
    assertThrows(UnsupportedOperationException.class, () -> frame.tagAmbiguities().add(0.5));
    assertTrue(frame.hasAmbiguity());
    assertFalse(frame.hasCovariance());
    assertFalse(frame.hasPlanarCovariance());
    assertThrows(IllegalStateException.class, () -> frame.pose2dStdDevs(new Pose2d()));
  }

  @Test
  void poseStandardDeviationsPreserveAxes() {
    var standardDeviations = new PoseStandardDeviations(0.1, 0.2, 0.3, 0.01, 0.02, 0.03);

    assertEquals(0.1, standardDeviations.xMeters());
    assertEquals(0.2, standardDeviations.yMeters());
    assertEquals(0.3, standardDeviations.zMeters());
    assertEquals(0.01, standardDeviations.rollRadians());
    assertEquals(0.02, standardDeviations.pitchRadians());
    assertEquals(0.03, standardDeviations.yawRadians());
  }
}
