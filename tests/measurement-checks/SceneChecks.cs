using System;
using QuestX.Core;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;

/// <summary>
/// Checks the Quest-side robot mount, the single-tag camera-frame solve behind the headset view,
/// the display-only VIO-to-field alignment behind the field view, and mount-aware pose resets.
/// </summary>
static class SceneChecks
{
    const double TagSize = 0.1651;
    static readonly double[] Intrinsics = { 880, 880, 640, 640 };

    public static void Run(Action<bool, string> check)
    {
        CheckRobotMount(check);
        CheckCameraPose(check);
        CheckFieldAlignment(check);
        CheckMountedReset(check);
    }

    static void CheckRobotMount(Action<bool, string> check)
    {
        var mount = new RobotMount();
        check(mount.Set(0.25, -0.1, 0.6, 5, -20, 90), "Mount: a new transform reports a change");
        check(!mount.Set(0.25, -0.1, 0.6, 5, -20, 90), "Mount: the same transform is not a change");

        var robot = new Pose3d(new Translation3d(4, 2, 0), new Rotation3d(0.02, -0.01, 1.2));
        var headset = robot.TransformBy(mount.RobotToHeadset);
        check(SamePose(mount.RobotToHeadsetPose(robot), headset), "Mount: robot pose maps to the headset pose");
        check(SamePose(mount.HeadsetToRobotPose(headset), robot), "Mount: headset pose maps back to the robot pose");

        var t = mount.RobotToHeadset;
        check(Math.Abs(t.X - 0.25) < 1e-12 && Math.Abs(t.Y + 0.1) < 1e-12 && Math.Abs(t.Z - 0.6) < 1e-12,
            "Mount: translation is in meters");
        check(Math.Abs(t.Rotation.Z - Math.PI / 2) < 1e-9 && Math.Abs(t.Rotation.Y + 20 * Math.PI / 180) < 1e-9,
            "Mount: angles are in degrees");
    }

    /// <summary>
    /// Projects a field tag through a field camera with the solvers' corner order and pinhole
    /// model, then checks the single-tag solve against the field-derived camera-to-tag pose.
    /// </summary>
    static void CheckCameraPose(Action<bool, string> check)
    {
        var tag = new Pose3d(new Translation3d(11.88, 7.42, 0.889), new Rotation3d(0, 0, Math.PI));
        var cameras = new[]
        {
            new Pose3d(new Translation3d(9.8, 7.0, 0.6), new Rotation3d(0, -0.15, 0.2)),
            new Pose3d(new Translation3d(10.6, 6.2, 0.4), new Rotation3d(0.4, -0.05, 0.8)),
            // Rolled onto its side, like a headset mounted Left.
            new Pose3d(new Translation3d(10.9, 7.6, 0.9), new Rotation3d(-Math.PI / 2, 0, -0.1)),
            new Pose3d(new Translation3d(10.2, 7.3, 1.1), new Rotation3d(Math.PI, 0.1, 0.05)),
        };
        foreach (var camera in cameras)
        {
            var corners2d = new double[8];
            var local = AprilTagCameraPose.TagLocalCorners(TagSize);
            for (int i = 0; i < 4; i++)
            {
                var field = tag.Plus(new Transform3d(local[i], new Rotation3d())).Translation;
                var p = new Pose3d(field, new Rotation3d()).RelativeTo(camera).Translation;
                // WPILib camera axes (X forward, Y left, Z up) to OpenCV pixels.
                corners2d[i * 2] = Intrinsics[0] * -p.Y / p.X + Intrinsics[2];
                corners2d[i * 2 + 1] = Intrinsics[1] * -p.Z / p.X + Intrinsics[3];
            }
            check(AprilTagCameraPose.TrySolve(corners2d, TagSize, Intrinsics, out var solved),
                "Camera pose: an in-view tag solves");
            var expected = tag.RelativeTo(camera);
            check(solved.CameraToTag.Translation.GetDistance(expected.Translation) < 1e-6,
                $"Camera pose: translation matches the field geometry (error {solved.CameraToTag.Translation.GetDistance(expected.Translation):E2} m)");
            check(SameRotation(solved.CameraToTag.Rotation, expected.Rotation, 1e-6),
                "Camera pose: rotation matches the field geometry");
            check(solved.ReprojectionErrorPixels < 1e-6, "Camera pose: noiseless corners reproject exactly");
        }

        check(!AprilTagCameraPose.TrySolve(new double[6], TagSize, Intrinsics, out _),
            "Camera pose: fewer than four corners are refused");
    }

    static void CheckFieldAlignment(Action<bool, string> check)
    {
        // VIO frame = field frame rotated by 0.7 rad and shifted; the robot drives a curve.
        var vioToField = new Pose3d(new Translation3d(3, 1, 0), new Rotation3d(0, 0, 0.7));
        Pose3d Truth(double t) =>
            new Pose3d(new Translation3d(5 + t, 2 + 0.5 * t * t, 0), new Rotation3d(0, 0, 0.3 * t));
        Pose3d Vio(double t) => Truth(t).RelativeTo(vioToField);

        var alignment = new VioFieldAlignment();
        check(!alignment.Align(0.1, Truth(0.1), out _), "Alignment: refuses before any VIO");
        for (int i = 0; i <= 120; i++)
        {
            double t = i / 120.0;
            alignment.RecordVio(t, 3, true, Vio(t));
        }

        check(alignment.Align(0.4004, Truth(0.4004), out string reason), $"Alignment: anchors between samples ({reason})");
        var first = alignment.GetSnapshot();
        check(first.Estimate != null && first.Estimate.Translation.GetDistance(Truth(1).Translation) < 1e-4,
            "Alignment: the newest VIO sample lands on its true field position");
        check(Math.Abs(first.Estimate.Rotation.Z - Truth(1).Rotation.Z) < 1e-4, "Alignment: and its true yaw");
        check(double.IsNaN(first.LastCorrectionMeters), "Alignment: the first anchor has no correction");

        // A measurement that disagrees with the anchored prediction by 8 cm and 2° is the drift.
        var drifted = Truth(0.9).TransformBy(new Transform3d(0.08, 0, 0, new Rotation3d(0, 0, 0.035)));
        check(alignment.Align(0.9, drifted, out _), "Alignment: re-anchors on a later measurement");
        var second = alignment.GetSnapshot();
        check(Math.Abs(second.LastCorrectionMeters - 0.08) < 1e-6, "Alignment: reports the planar correction");
        check(Math.Abs(second.LastCorrectionYawRadians - 0.035) < 1e-6, "Alignment: reports the signed yaw correction");
        check(Math.Abs(second.LastCorrectionIntervalSeconds - 0.4996) < 1e-9, "Alignment: reports the interval");

        check(!alignment.Align(-5, Truth(0), out _), "Alignment: refuses a capture older than the history");

        // A reset or recenter starts a new epoch: no estimate until the next measurement.
        alignment.RecordVio(1.01, 4, true, Vio(1.01));
        check(alignment.GetSnapshot().Estimate == null, "Alignment: a new epoch drops the estimate");
        check(!alignment.Align(1.005, Truth(1.005), out _), "Alignment: refuses to interpolate across epochs");
        alignment.RecordVio(1.02, 4, false, Vio(1.02));
        check(!alignment.Align(1.02, Truth(1.02), out _), "Alignment: refuses a non-tracking sample");
    }

    /// <summary>
    /// A robot-pose reset re-bases the headset on the matching headset pose; the published
    /// robot pose must then report the requested position and yaw even with a tilted, rotated mount.
    /// </summary>
    static void CheckMountedReset(Action<bool, string> check)
    {
        var mount = new RobotMount();
        mount.Set(-0.2, 0.15, 0.55, 0, -25, 180);

        // The robot sits level in the headset's local tracking frame at some arbitrary pose.
        var localRobot = new Pose3d(new Translation3d(-1.3, 0.4, 0), new Rotation3d(0, 0, 2.2));
        var frame = new VioReferenceFrame(0.1);
        frame.Sample(mount.RobotToHeadsetPose(localRobot), true, 0);

        var target = new Pose3d(new Translation3d(3.2, 4.1, 0), new Rotation3d(0, 0, -0.6));
        check(frame.TryReset(mount.RobotToHeadsetPose(target), out _), "Mounted reset: the headset re-bases");
        var published = mount.HeadsetToRobotPose(frame.PublishedPose);
        check(published.Translation.GetDistance(target.Translation) < 1e-9, "Mounted reset: the robot reports the target position");
        check(Math.Abs(Math.IEEERemainder(published.Rotation.Z - target.Rotation.Z, 2 * Math.PI)) < 1e-9,
            "Mounted reset: the robot reports the target yaw");
        check(Math.Abs(published.Rotation.X) < 1e-9 && Math.Abs(published.Rotation.Y) < 1e-9,
            "Mounted reset: the level robot stays level");
    }

    static bool SamePose(Pose3d a, Pose3d b) =>
        a.Translation.GetDistance(b.Translation) < 1e-9 && SameRotation(a.Rotation, b.Rotation, 1e-9);

    static bool SameRotation(Rotation3d a, Rotation3d b, double tolerance)
    {
        var p = a.Quaternion;
        var q = b.Quaternion;
        return Math.Abs(Math.Abs(p.W * q.W + p.X * q.X + p.Y * q.Y + p.Z * q.Z) - 1) < tolerance;
    }
}
