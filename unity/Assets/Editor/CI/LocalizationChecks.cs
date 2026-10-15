using System;
using System.IO;
using Newtonsoft.Json;
using QuestX.Network;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;
using QuestX.Utils;
using UnityEngine;
using UQuaternion = UnityEngine.Quaternion;

namespace QuestX.Editor.CI
{
    /// <summary>Deterministic checks of the actual production conversion/transport code.</summary>
    public static class LocalizationChecks
    {
        private static int checks;
        private static void Assert(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException(message);
        }
        private static void Near(double actual, double expected, string message) =>
            Assert(Math.Abs(actual - expected) < 1e-5, message + $": {actual} vs {expected}");

        public static void Run()
        {
            var field = JsonConvert.DeserializeObject<AprilTagFieldLayout>(File.ReadAllText(
                Path.Combine(Application.streamingAssetsPath, "apriltag/fieldlayouts/2026-rebuilt-welded.json")));
            Assert(field.Tags.Count == 32, "REBUILT map contains 32 tags");
            Near(field.Field.X, 16.541, "WPILib field length");
            Near(field.Field.Y, 8.069, "WPILib field width");

            var offset = new Pose(new Vector3(-0.04f, 0.03f, 0.08f), UQuaternion.Euler(4, -7, 2));
            var offsetFrc = new Pose3d(Conversions.UnityToFrc3d(offset.position, offset.rotation));
            var mount = new Transform3d(offsetFrc.Translation, offsetFrc.Rotation);
            foreach (double yaw in new[] {0.0, Math.PI / 2, -2.4})
            {
                var headset = new Pose3d(2, 3, 1, new Rotation3d(0.1, -0.2, yaw));
                var camera = headset.TransformBy(mount);
                var recovered = AprilTagPoseValidation.CameraToHeadset(camera, offset);
                Near(recovered.X, headset.X, "camera-to-headset X");
                Near(recovered.Y, headset.Y, "camera-to-headset Y");
                Near(recovered.Z, headset.Z, "camera-to-headset Z");
                Near(recovered.Rotation.Z, headset.Rotation.Z, "camera-to-headset yaw");
            }
            var good = new Pose3d(2, 3, 1, new Rotation3d(0, 0, Math.PI / 2));
            Assert(AprilTagPoseValidation.TryValidate(good, field.Field, 4, 4, out _), "single tag accepted");
            Assert(AprilTagPoseValidation.TryValidate(good, field.Field, 6, 8, out _), "75% inliers accepted");
            Assert(!AprilTagPoseValidation.TryValidate(good, field.Field, 5, 8, out _), "poor solve rejected");
            Assert(!AprilTagPoseValidation.TryValidate(new Pose3d(2, 3, -1, Rotation3d.Zero), field.Field, 4, 4, out _), "below floor rejected");
            Assert(!AprilTagPoseValidation.TryValidate(new Pose3d(100, 3, 1, Rotation3d.Zero), field.Field, 4, 4, out _), "far field rejected");
            Assert(!AprilTagPoseValidation.TryValidate(new Pose3d(double.NaN, 3, 1, Rotation3d.Zero), field.Field, 4, 4, out _), "NaN rejected");
            var forwardCamera = new Pose3d(2, 3, 1, Rotation3d.Zero);
            var facingTag = new Pose3d(4, 3, 1, new Rotation3d(0, 0, Math.PI));
            Assert(AprilTagPoseValidation.IsFacingCamera(forwardCamera, facingTag), "printed tag face in front accepted");
            Assert(!AprilTagPoseValidation.IsFacingCamera(new Pose3d(2, 3, 1, new Rotation3d(0, 0, Math.PI)), facingTag), "tag behind camera rejected");
            Assert(!AprilTagPoseValidation.IsFacingCamera(forwardCamera, new Pose3d(4, 3, 1, Rotation3d.Zero)), "camera behind printed tag rejected");
            var observations = new[] {
                new AprilTagObservation(7, 0.15, -3, 2, 0.4, 1.7, 1.75),
                new AprilTagObservation(8, 0.05, 4, -1, 0.6, 1.9, 1.95),
            };
            var ll = LimelightPoseFormat.Encode(good, 35, 1.8, observations);
            Assert(ll.Length == 11 + 7 * 2, "Limelight length includes raw fiducials");
            Near(ll[5], 90, "Limelight angles in degrees");
            Near(ll[6], 35, "Limelight latency in milliseconds");
            Near(ll[7], 2, "Limelight tag count");
            Near(ll[9], 1.8, "Limelight mean distance");
            Near(ll[10], 0.5, "Limelight mean area");
            Near(ll[11], 7, "Limelight fiducial ID");
            Near(ll[17], 0.15, "Limelight fiducial ambiguity");
            Near(ll[24], 0.05, "Limelight second fiducial ambiguity");
            var raw = LimelightPoseFormat.RawFiducials(observations);
            Assert(raw.Length == 14, "Limelight raw fiducial length");
            Near(raw[7], 8, "Limelight raw fiducial order");
            Near(raw[12], 1.95, "Limelight raw distance to robot");
            Assert(!LimelightPoseFormat.IsFresh(100, 0), "no observation invalid");
            Assert(LimelightPoseFormat.IsFresh(300000, 50000), "freshness boundary");
            Assert(!LimelightPoseFormat.IsFresh(300001, 50000), "stale observation invalid");
            Assert(!LimelightPoseFormat.IsFresh(50000, 50001), "future observation invalid");
            var packet = new double[25];
            MeasurementWireFormat.WriteAprilTag(packet, 0, 123456, 2, good, new[]{1,2}, 8, 8, 1,
                1.8, 0.1, 0.1, 0.2, 0.3, 0.3, 0.15);
            Near(packet[12], 2, "v2 tag count unchanged");
            Near(packet[22], 0.15, "v2 yaw sigma remains radians");
            Near(packet[24], 2, "v2 IDs unchanged");
            var v3 = new double[MeasurementWireFormat.AprilTagV3HeaderLength + 4];
            MeasurementWireFormat.WriteAprilTagV3(v3, 0, 123456, 2, good, new[]{1,2}, 8, 8, 1,
                1.8, 0.1, 0.1, 0.2, 0.3, 0.3, 0.15, 0.04, 0.7, new[]{0.2, 0.6});
            Near(v3[22], 0.15, "v3 keeps v2 header");
            Near(v3[23], 0.04, "v3 ambiguity");
            Near(v3[24], 0.7, "v3 reprojection error");
            Near(v3[26], 2, "v3 IDs follow header");
            Near(v3[28], 0.6, "v3 per-tag ambiguities follow IDs");
            string report = Path.GetFullPath(Path.Combine(Application.dataPath, "../../submission/localization-checks.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            File.WriteAllText(report, JsonConvert.SerializeObject(new { passed = checks, failed = 0, utc = DateTime.UtcNow }));
            Debug.Log($"LOCALIZATION CHECKS PASSED: {checks}");
        }

        public static void RunThenBuild()
        {
            Run();
            global::CI.Build.PerformAndroid();
        }
    }
}
