/*
* QUESTX
  https://github.com/QuestX
* Copyright (C) 2026 QuestX
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the MIT License as published.
*/
package gg.questx.questx;

import edu.wpi.first.math.geometry.Pose3d;
import edu.wpi.first.math.geometry.Quaternion;
import edu.wpi.first.math.geometry.Rotation3d;
import java.util.ArrayList;

/** Decoder for the versioned atomic arrays documented in protos/measurement-v2.md. */
final class MeasurementWireFormat {
  private MeasurementWireFormat() {}

  static VioFrame vio(double[] data, long serverTimeMicros) {
    if (data.length != 14
        || !validCommon(data, serverTimeMicros)
        || (data[12] != 0 && data[12] != 1)
        || !uint32(data[13])) {
      return null;
    }
    return new VioFrame(
        pose(data),
        serverTimeMicros / 1_000_000.0,
        int64(data, 2),
        data[4],
        int64(data, 0),
        data[12] == 1,
        (long) data[13]);
  }

  static AprilTagFrame aprilTag(double[] data, long serverTimeMicros) {
    if (data.length < 23
        || !validCommon(data, serverTimeMicros)
        || !uint32(data[12])
        || data[12] != data.length - 23
        || !uint32(data[13])
        || !uint32(data[14])
        || data[13] > data[14]
        || data[15] < 0
        || data[15] > 1) {
      return null;
    }
    for (int i = 16; i < 23; i++) {
      if (data[i] < 0) return null;
    }
    var ids = new ArrayList<Integer>(data.length - 23);
    for (int i = 23; i < data.length; i++) {
      if (data[i] < 0 || data[i] > Integer.MAX_VALUE || data[i] != Math.rint(data[i])) {
        return null;
      }
      ids.add((int) data[i]);
    }
    return new AprilTagFrame(
        pose(data),
        serverTimeMicros / 1_000_000.0,
        int64(data, 2),
        data[4],
        int64(data, 0),
        ids,
        (long) data[12],
        (long) data[13],
        (long) data[14],
        data[15],
        data[16],
        new PoseStandardDeviations(data[17], data[18], data[19], data[20], data[21], data[22]));
  }

  private static boolean validCommon(double[] data, long serverTimeMicros) {
    if (serverTimeMicros <= 0) return false;
    for (double value : data) {
      if (!Double.isFinite(value)) return false;
    }
    for (int i = 0; i < 4; i++) {
      if (!uint32(data[i])) return false;
    }
    if (int64(data, 2) <= 0 || data[4] < 0) return false;
    double norm = 0;
    for (int i = 8; i < 12; i++) norm += data[i] * data[i];
    return Double.isFinite(norm) && norm > 1e-12;
  }

  private static boolean uint32(double value) {
    return value >= 0 && value <= 0xffff_ffffL && value == Math.rint(value);
  }

  private static long int64(double[] data, int offset) {
    return ((long) data[offset + 1] << 32) | (long) data[offset];
  }

  private static Pose3d pose(double[] data) {
    return new Pose3d(
        data[5],
        data[6],
        data[7],
        new Rotation3d(new Quaternion(data[8], data[9], data[10], data[11])));
  }
}
