using System;
using System.Globalization;
using System.IO;
using System.Linq;
using QuestX.Network;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;

/// <summary>Checks the v3 and v4 AprilTag arrays and Limelight raw fiducials; exports Java fixtures.</summary>
static class WireChecks
{
    // Same v3 values as QuestX.Editor.CI.MeasurementWireFixtures. The CLI also exports v4.
    static readonly Pose3d FixturePose = new Pose3d(
        1.25, -2.5, 0.75, new Rotation3d(0.12, 0.3, 0.6));

    static double[] AprilTagV3Fixture()
    {
        var tags = new double[MeasurementWireFormat.AprilTagV3HeaderLength + 2 * 3];
        MeasurementWireFormat.WriteAprilTagV3(tags, 42, 987654321, 4.5, FixturePose,
            new[] { 1, 22, 586 }, 10, 12, 10.0 / 12, 2.75, 0.1, 0.2, 0.3, 0.01, 0.02, 0.03,
            0.125, 0.875, 38.5, new[] { 0.5, 0.25, 0.0625 });
        return tags;
    }

    /// <summary>
    /// Covariance of the v4 fixture: variances 0.01, 0.04, 0.09 m^2 and 1e-4, 4e-4, 9e-4 rad^2
    /// along the field axes once the correlation slack is added, with x-y and y-yaw correlation.
    /// </summary>
    public static PoseCovariance FixtureCovariance()
    {
        double slack = 1 + PoseCovariance.CorrelationSlack;
        var covariance = new double[36];
        double[] variances = { 0.01, 0.04, 0.09, 1e-4, 4e-4, 9e-4 };
        for (int i = 0; i < 6; i++) covariance[i * 7] = variances[i] / slack;
        covariance[1] = covariance[6] = 0.005;
        covariance[11] = covariance[31] = -0.003;
        var m = FixturePose.Rotation.ToMatrix();
        return PoseCovariance.Linear(
            new[] { m[0, 0], m[0, 1], m[0, 2], m[1, 0], m[1, 1], m[1, 2], m[2, 0], m[2, 1], m[2, 2] },
            new[] { FixturePose.X, FixturePose.Y, FixturePose.Z }, covariance);
    }

    static double[] AprilTagV4Fixture()
    {
        var tags = new double[MeasurementWireFormat.AprilTagV4HeaderLength + 2 * 3];
        MeasurementWireFormat.WriteAprilTagV4(tags, 42, 987654321, 4.5, FixturePose, FixtureCovariance(),
            new[] { 1, 22, 586 }, 10, 12, 10.0 / 12, 2.75, 0.125, 0.875, 38.5, new[] { 0.5, 0.25, 0.0625 });
        return tags;
    }

    /// <summary>
    /// Encodes a realistic frame as v3 and v4 arrays: every corner is an inlier, and the pose and
    /// its uncertainty come from the production covariance.
    /// </summary>
    public static (double[] V3, double[] V4) EncodeSample(
        ulong sequence,
        long captureMicros,
        double appTimestamp,
        PoseCovariance headset,
        int[] tagIds,
        double averageDistance,
        AprilTagAmbiguity.Result quality,
        double processingLatencyMilliseconds)
    {
        int count = tagIds.Length;
        var r = headset.Rotation;
        var t = headset.Translation;
        var pose = new Pose3d(t[0], t[1], t[2], new Rotation3d(new double[,] {
            { r[0], r[1], r[2] }, { r[3], r[4], r[5] }, { r[6], r[7], r[8] } }));
        var s = headset.EstimatorStdDevs();
        var v3 = new double[MeasurementWireFormat.AprilTagV3HeaderLength + 2 * count];
        MeasurementWireFormat.WriteAprilTagV3(v3, sequence, captureMicros, appTimestamp, pose,
            tagIds, (uint)(4 * count), (uint)(4 * count), 1.0, averageDistance,
            s[0], s[1], s[2], s[3], s[4], s[5],
            quality.Ambiguity, quality.ReprojectionErrorPixels, processingLatencyMilliseconds,
            quality.TagAmbiguities);
        var v4 = new double[MeasurementWireFormat.AprilTagV4HeaderLength + 2 * count];
        MeasurementWireFormat.WriteAprilTagV4(v4, sequence, captureMicros, appTimestamp, pose, headset,
            tagIds, (uint)(4 * count), (uint)(4 * count), 1.0, averageDistance,
            quality.Ambiguity, quality.ReprojectionErrorPixels, processingLatencyMilliseconds,
            quality.TagAmbiguities);
        return (v3, v4);
    }

    public static void Run(Action<bool, string> check)
    {
        var v3 = AprilTagV3Fixture();
        var v2 = new double[MeasurementWireFormat.AprilTagHeaderLength + 3];
        MeasurementWireFormat.WriteAprilTag(v2, 42, 987654321, 4.5, FixturePose,
            new[] { 1, 22, 586 }, 10, 12, 10.0 / 12, 2.75, 0.1, 0.2, 0.3, 0.01, 0.02, 0.03);
        check(MeasurementWireFormat.AprilTagV3HeaderLength == 26, "v3 header is 26 doubles");
        check(v3.Length == 32, "v3 length is 26 + 2 * tagCount");
        check(v3.Take(23).SequenceEqual(v2.Take(23)), "v3 repeats the v2 header exactly");
        check(v3[23] == 0.125 && v3[24] == 0.875, "v3 ambiguity and reprojection error");
        check(v3[25] == 38.5, "v3 processing latency");
        check(v3.Skip(26).SequenceEqual(new[] { 1, 22, 586, 0.5, 0.25, 0.0625 }), "v3 IDs then per-tag ambiguity");
        check(v2.Skip(23).SequenceEqual(new double[] { 1, 22, 586 }), "v2 layout is unchanged");

        var v4 = AprilTagV4Fixture();
        var covariance = FixtureCovariance();
        check(MeasurementWireFormat.AprilTagV4HeaderLength == 53, "v4 header is 53 doubles");
        check(v4.Length == 59, "v4 length is 53 + 2 * tagCount");
        check(v4.Take(17).SequenceEqual(v3.Take(17)) && v4.Skip(23).Take(3).SequenceEqual(v3.Skip(23).Take(3)),
            "v4 repeats the v3 header outside the standard deviations");
        check(v4.Skip(17).Take(6).SequenceEqual(covariance.EstimatorStdDevs()), "v4 standard deviations are the estimator's");
        check(covariance.TryToPose2d(out _, out var planar), "v4 fixture has defined planar heading");
        check(v4.Skip(47).Take(6).SequenceEqual(new[] { planar[0], planar[1], planar[2], planar[4], planar[5], planar[8] }),
            "v4 transmits component-wise planar covariance, in field axes");
        check(v4.Skip(26).Take(21).SequenceEqual(covariance.UpperTriangle()), "v4 carries the covariance's upper triangle");
        check(Math.Abs(v4[26] - 0.01) < 1e-15 && v4[27] == 0.005 && v4[46] == covariance.Covariance[35],
            "v4 covariance is row-major: xx, xy, ..., yaw-yaw");
        check(v4.Skip(53).SequenceEqual(new[] { 1, 22, 586, 0.5, 0.25, 0.0625 }), "v4 IDs then per-tag ambiguity");

        // A vertical +X axis has no heading. Keep its useful 6D covariance, but explicitly mark
        // the planar projection unavailable, including when only an alternative is singular.
        var identity = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        var vertical = new double[] { 0, 0, 1, 0, 1, 0, -1, 0, 0 };
        var noise = new double[36];
        for (int i = 0; i < 6; i++) noise[i * 7] = 0.01;
        foreach (var components in new[] {
            new[] { new PoseCovariance.Component(vertical, new double[3], noise, 1) },
            new[] { new PoseCovariance.Component(identity, new double[3], noise, 1),
                new PoseCovariance.Component(vertical, new double[3], noise, 1) } })
        {
            var singular = new PoseCovariance(components);
            check(!singular.TryToPose2d(out _, out _), "Primary or alternative singular heading is unavailable");
            // Reuse the previously valid packet, just as the live publisher does.
            MeasurementWireFormat.WriteAprilTagV4(v4, 42, 987654321, 4.5, FixturePose, singular,
                new[] { 1, 22, 586 }, 10, 12, 10.0 / 12, 2.75, 0.125, 0.875, 38.5,
                new[] { 0.5, 0.25, 0.0625 });
            check(v4.Skip(47).Take(6).All(x => x == 0), "Undefined projection clears a reused planar covariance block");
            check(v4.Skip(26).Take(21).SequenceEqual(singular.UpperTriangle()), "Singular heading preserves full 6D uncertainty");
        }

        var observations = new[] {
            new AprilTagObservation(7, 0.15, -3, 2, 0.4, 1.7, 1.75),
            new AprilTagObservation(8, 0.05, 4, -1, 0.6, 1.9, 1.95),
        };
        var botpose = LimelightPoseFormat.Encode(FixturePose, 35, 1.8, observations);
        // LimelightHelpers only parses raw fiducials when length == 11 + 7 * tagCount.
        check(botpose.Length == 11 + 7 * (int)botpose[7], "botpose length lets LimelightHelpers read raw fiducials");
        check(botpose[9] == 1.8 && Math.Abs(botpose[10] - 0.5) < 1e-12, "botpose distance and mean area");
        check(botpose.Skip(11).SequenceEqual(new[] { 7, -3, 2, 0.4, 1.7, 1.75, 0.15, 8, 4, -1, 0.6, 1.9, 1.95, 0.05 }),
            "botpose raw fiducials: id, txnc, tync, ta, distToCamera, distToRobot, ambiguity");
        check(LimelightPoseFormat.RawFiducials(observations).SequenceEqual(botpose.Skip(11)), "rawfiducials matches botpose");
        check(LimelightPoseFormat.Encode(FixturePose, 0, 0, Array.Empty<AprilTagObservation>()).Length == 11,
            "botpose without tags keeps the 11-value header");
    }

    /// <summary>Layout checks that a robot-side decoder relies on.</summary>
    public static void CheckSample(Action<bool, string> check, (double[] V3, double[] V4) sample, string name)
    {
        var (v3, v4) = sample;
        int count = (int)v3[12];
        check(v3.Length == 26 + 2 * count && v4.Length == 53 + 2 * count, $"{name}: lengths follow tagCount");
        check(v3.All(double.IsFinite) && v4.All(double.IsFinite), $"{name}: every value is finite");
        check(v3[23] >= 0 && v3[23] <= 1 && v3[24] >= 0 && v3[25] >= 0, $"{name}: quality ranges");
        check(v3.Skip(26 + count).All(a => a >= 0 && a <= 1), $"{name}: per-tag ambiguity ranges");
        check(v4.Take(26).SequenceEqual(v3.Take(26)) && v4.Skip(53).SequenceEqual(v3.Skip(26)), $"{name}: v4 extends v3");
        // Every variance is positive and every correlation within the cap.
        var c = new double[36];
        for (int r = 0, i = 26; r < 6; r++) for (int col = r; col < 6; col++, i++) c[r * 6 + col] = c[col * 6 + r] = v4[i];
        bool capped = true;
        for (int r = 0; r < 6; r++) for (int col = 0; col < 6; col++)
            capped &= c[r * 7] > 0 && Math.Abs(c[r * 6 + col]) <= Math.Sqrt(c[r * 7] * c[col * 7]) * (r == col ? 1 : 1 / 1.1 + 1e-9);
        check(capped, $"{name}: covariance is positive with correlations capped at 1 / 1.1");
    }

    public static void Export(string repositoryRoot, (double[] V3, double[] V4) singleTag, (double[] V3, double[] V4) twoTags)
    {
        string v3 = Path.Combine(repositoryRoot, "questx-lib/src/test/resources/measurement-v3");
        Directory.CreateDirectory(v3);
        Write(v3, "apriltag", AprilTagV3Fixture());
        Write(v3, "apriltag-single-tag", singleTag.V3);
        Write(v3, "apriltag-two-tags", twoTags.V3);
        string v4 = Path.Combine(repositoryRoot, "questx-lib/src/test/resources/measurement-v4");
        Directory.CreateDirectory(v4);
        Write(v4, "apriltag", AprilTagV4Fixture());
        Write(v4, "apriltag-single-tag", singleTag.V4);
        Write(v4, "apriltag-two-tags", twoTags.V4);
        Console.WriteLine("Exported v3 and v4 fixtures to " + Path.GetDirectoryName(v3));
    }

    public static string Format(double[] values) =>
        string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    static void Write(string directory, string name, double[] values) =>
        File.WriteAllText(Path.Combine(directory, name + ".txt"), Format(values) + "\n");
}
