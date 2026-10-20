/*
* QUESTX
  https://github.com/QuestX
* Copyright (C) 2026 QuestX
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the MIT License as published.
*/
package gg.questx.questx;

import edu.wpi.first.math.Matrix;
import edu.wpi.first.math.Nat;
import edu.wpi.first.math.geometry.Pose3d;
import edu.wpi.first.math.geometry.Quaternion;
import edu.wpi.first.math.geometry.Rotation3d;
import edu.wpi.first.math.numbers.N3;
import edu.wpi.first.math.numbers.N6;
import java.util.ArrayList;
import java.util.List;

/**
 * Decoder for the versioned atomic arrays documented in protos/measurement-v2.md,
 * protos/measurement-v3.md, and protos/measurement-v4.md.
 */
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
    if (!validAprilTagHeader(data, serverTimeMicros, 23, 1)) {
      return null;
    }
    var ids = tagIds(data, 23, (int) data[12]);
    if (ids == null) {
      return null;
    }
    return aprilTagFrame(
        data, serverTimeMicros, ids, Double.NaN, Double.NaN, List.of(), Double.NaN, null, null);
  }

  /**
   * Decodes a v3 AprilTag array: the v2 fields at 0-22, ambiguity (23), reprojection error in
   * pixels (24), processing latency in milliseconds (25), then the tag IDs followed by one
   * ambiguity per tag.
   */
  static AprilTagFrame aprilTagV3(double[] data, long serverTimeMicros) {
    return aprilTagWithQuality(data, serverTimeMicros, 26, null, null);
  }

  /**
   * Decodes a v4 AprilTag array: the v3 fields at 0-25, the 21 upper-triangle entries of the pose's
   * 6x6 covariance row by row (26-46), the six upper-triangle planar covariance entries (47-52),
   * then the tag IDs followed by one ambiguity per tag. An all-zero planar block means unavailable.
   */
  static AprilTagFrame aprilTagV4(double[] data, long serverTimeMicros) {
    if (data.length < 53) {
      return null;
    }
    var covariance = new Matrix<>(Nat.N6(), Nat.N6());
    for (int row = 0, i = 26; row < 6; row++) {
      for (int column = row; column < 6; column++, i++) {
        covariance.set(row, column, data[i]);
        covariance.set(column, row, data[i]);
      }
    }
    if (!positiveDefinite(covariance)) {
      return null;
    }
    var planarCovariance = new Matrix<>(Nat.N3(), Nat.N3());
    boolean unavailable = true;
    for (int row = 0, i = 47; row < 3; row++) {
      for (int column = row; column < 3; column++, i++) {
        planarCovariance.set(row, column, data[i]);
        planarCovariance.set(column, row, data[i]);
        unavailable &= data[i] == 0;
      }
    }
    if (!unavailable && !positiveDefinite(planarCovariance)) {
      return null;
    }
    return aprilTagWithQuality(
        data, serverTimeMicros, 53, covariance, unavailable ? null : planarCovariance);
  }

  /** Whether a symmetric matrix has a Cholesky factor: positive variances and a valid shape. */
  private static boolean positiveDefinite(Matrix<?, ?> matrix) {
    int size = matrix.getNumRows();
    var factor = new double[size][size];
    for (int i = 0; i < size; i++) {
      for (int j = 0; j <= i; j++) {
        double sum = matrix.get(i, j);
        for (int k = 0; k < j; k++) sum -= factor[i][k] * factor[j][k];
        if (i == j) {
          if (!(sum > 0) || !Double.isFinite(sum)) return false;
          factor[i][i] = Math.sqrt(sum);
        } else {
          factor[i][j] = sum / factor[j][j];
        }
      }
    }
    return true;
  }

  /** v3 and later: the v2 header, then ambiguity, fit error, and latency at 23-25. */
  private static AprilTagFrame aprilTagWithQuality(
      double[] data,
      long serverTimeMicros,
      int headerLength,
      Matrix<N6, N6> covariance,
      Matrix<N3, N3> planarCovariance) {
    if (!validAprilTagHeader(data, serverTimeMicros, headerLength, 2)
        || !unitInterval(data[23])
        || data[24] < 0
        || data[25] < 0) {
      return null;
    }
    int count = (int) data[12];
    var ids = tagIds(data, headerLength, count);
    if (ids == null) {
      return null;
    }
    var ambiguities = new ArrayList<Double>(count);
    for (int i = headerLength + count; i < data.length; i++) {
      if (!unitInterval(data[i])) return null;
      ambiguities.add(data[i]);
    }
    return aprilTagFrame(
        data,
        serverTimeMicros,
        ids,
        data[23],
        data[24],
        ambiguities,
        data[25],
        covariance,
        planarCovariance);
  }

  private static boolean validAprilTagHeader(
      double[] data, long serverTimeMicros, int headerLength, int valuesPerTag) {
    if (data.length < headerLength
        || !validCommon(data, serverTimeMicros)
        || !uint32(data[12])
        || data[12] * valuesPerTag != data.length - headerLength
        || !uint32(data[13])
        || !uint32(data[14])
        || data[13] > data[14]
        || !unitInterval(data[15])) {
      return false;
    }
    for (int i = 16; i < 23; i++) {
      if (data[i] < 0) return false;
    }
    return true;
  }

  private static List<Integer> tagIds(double[] data, int offset, int count) {
    var ids = new ArrayList<Integer>(count);
    for (int i = offset; i < offset + count; i++) {
      if (data[i] < 0 || data[i] > Integer.MAX_VALUE || data[i] != Math.rint(data[i])) {
        return null;
      }
      ids.add((int) data[i]);
    }
    return ids;
  }

  private static AprilTagFrame aprilTagFrame(
      double[] data,
      long serverTimeMicros,
      List<Integer> ids,
      double ambiguity,
      double reprojectionErrorPixels,
      List<Double> tagAmbiguities,
      double processingLatencyMilliseconds,
      Matrix<N6, N6> covariance,
      Matrix<N3, N3> planarCovariance) {
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
        new PoseStandardDeviations(data[17], data[18], data[19], data[20], data[21], data[22]),
        ambiguity,
        reprojectionErrorPixels,
        tagAmbiguities,
        processingLatencyMilliseconds,
        covariance,
        planarCovariance);
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

  private static boolean unitInterval(double value) {
    return value >= 0 && value <= 1;
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
