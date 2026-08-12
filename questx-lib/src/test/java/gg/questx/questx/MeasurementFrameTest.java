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
import static org.junit.jupiter.api.Assertions.assertThrows;

import edu.wpi.first.math.geometry.Pose3d;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;

class MeasurementFrameTest {
  @Test
  void aprilTagFrameDefensivelyCopiesTagIds() {
    var sourceTagIds = new ArrayList<>(List.of(1, 2));
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
            new PoseStandardDeviations(0.1, 0.2, 0.3, 0.01, 0.02, 0.03));

    sourceTagIds.add(3);

    assertEquals(List.of(1, 2), frame.tagIds());
    assertThrows(UnsupportedOperationException.class, () -> frame.tagIds().add(4));
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
