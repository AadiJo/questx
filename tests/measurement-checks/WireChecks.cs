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
            0.125, 0.875, new[] { 0.5, 0.25, 0.0625 });
        return tags;
    }

    public static void Run(Action<bool, string> check)
    {
        var v3 = AprilTagV3Fixture();
        var v2 = new double[MeasurementWireFormat.AprilTagHeaderLength + 3];
        MeasurementWireFormat.WriteAprilTag(v2, 42, 987654321, 4.5, FixturePose,
            new[] { 1, 22, 586 }, 10, 12, 10.0 / 12, 2.75, 0.1, 0.2, 0.3, 0.01, 0.02, 0.03);
        check(v3.Length == 31, "v3 length is 25 + 2 * tagCount");
        check(v3.Take(23).SequenceEqual(v2.Take(23)), "v3 repeats the v2 header exactly");
        check(v3[23] == 0.125 && v3[24] == 0.875, "v3 ambiguity and reprojection error");
        check(v3.Skip(25).SequenceEqual(new[] { 1, 22, 586, 0.5, 0.25, 0.0625 }), "v3 IDs then per-tag ambiguity");
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

    public static void Export(string repositoryRoot)
    {
        string directory = Path.Combine(repositoryRoot, "questx-lib/src/test/resources/measurement-v3");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "apriltag.txt"),
            string.Join(",", AprilTagV3Fixture().Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "\n");
        Console.WriteLine("Exported v3 fixture to " + directory);
    }
}
