using System;
using System.Globalization;
using System.IO;
using System.Linq;
using QuestX.Network;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;

/// <summary>Checks the v3 AprilTag array and Limelight raw fiducials; exports Java fixtures.</summary>
static class WireChecks
{
    // Same values as QuestX.Editor.CI.MeasurementWireFixtures, so either exporter yields one fixture.
    static readonly Pose3d FixturePose = new Pose3d(
        1.25, -2.5, 0.75, new Rotation3d(new QuestX.QuestX.Geometry.Quaternion(0.5, 0.5, -0.5, 0.5)));

    static double[] AprilTagV3Fixture()
    {
        var tags = new double[MeasurementWireFormat.AprilTagV3HeaderLength + 2 * 3];
        MeasurementWireFormat.WriteAprilTagV3(tags, 42, 987654321, 4.5, FixturePose,
            new[] { 1, 22, 586 }, 10, 12, 10.0 / 12, 2.75, 0.1, 0.2, 0.3, 0.01, 0.02, 0.03,
            0.125, 0.875, 38.5, new[] { 0.5, 0.25, 0.0625 });
        return tags;
    }

    /// <summary>
    /// Encodes a realistic frame: every corner is an inlier, and the suggested uncertainty follows
    /// AprilTagManager's distance and tag-count model.
    /// </summary>
    public static double[] EncodeSample(
        ulong sequence,
        long captureMicros,
        double appTimestamp,
        Pose3d headset,
        int[] tagIds,
        double averageDistance,
        AprilTagAmbiguity.Result quality,
        double processingLatencyMilliseconds)
    {
        int count = tagIds.Length;
        double linear = Math.Max(count == 1 ? 0.08 : 0.02, 0.05 * averageDistance * averageDistance / count);
        double angular = Math.Max(count == 1 ? 0.25 : 0.03, 0.05 * averageDistance / Math.Sqrt(count));
        var sample = new double[MeasurementWireFormat.AprilTagV3HeaderLength + 2 * count];
        MeasurementWireFormat.WriteAprilTagV3(sample, sequence, captureMicros, appTimestamp, headset,
            tagIds, (uint)(4 * count), (uint)(4 * count), 1.0, averageDistance,
            linear, linear, 2 * linear, 2 * angular, 2 * angular, angular,
            quality.Ambiguity, quality.ReprojectionErrorPixels, processingLatencyMilliseconds,
            quality.TagAmbiguities);
        return sample;
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
    public static void CheckSample(Action<bool, string> check, double[] sample, string name)
    {
        int count = (int)sample[12];
        check(sample.Length == 26 + 2 * count, $"{name}: length is 26 + 2 * tagCount");
        check(sample.All(double.IsFinite), $"{name}: every value is finite");
        check(sample[23] >= 0 && sample[23] <= 1 && sample[24] >= 0 && sample[25] >= 0, $"{name}: quality ranges");
        check(sample.Skip(26 + count).All(a => a >= 0 && a <= 1), $"{name}: per-tag ambiguity ranges");
    }

    public static void Export(string repositoryRoot, double[] singleTag, double[] twoTags)
    {
        string directory = Path.Combine(repositoryRoot, "questx-lib/src/test/resources/measurement-v3");
        Directory.CreateDirectory(directory);
        Write(directory, "apriltag", AprilTagV3Fixture());
        Write(directory, "apriltag-single-tag", singleTag);
        Write(directory, "apriltag-two-tags", twoTags);
        Console.WriteLine("Exported v3 fixtures to " + directory);
    }

    public static string Format(double[] values) =>
        string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    static void Write(string directory, string name, double[] values) =>
        File.WriteAllText(Path.Combine(directory, name + ".txt"), Format(values) + "\n");
}
