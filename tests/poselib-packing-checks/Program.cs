using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Meta.XR;
using QuestX.Native.AprilTag;
using QuestX.Native.PoseLib;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;
using QuestX.QuestX.Native.PoseLib;

const double tagSize = 0.1651;
var layout = new AprilTagFieldLayout(tagSize)
{
    Tags = new List<AprilTagFieldEntry>
    {
        new() { ID = 9, Pose = new Pose3d(1.2, -2.3, 4.5, new Rotation3d(0.37, -0.61, 1.12)) },
        new() { ID = 2, Pose = new Pose3d(-2.1, 0.8, 3.2, new Rotation3d(-0.52, 0.29, -1.76)) },
    },
};
var intrinsics = new PassthroughCameraAccess.CameraIntrinsics
{
    FocalLength = (880, 890), PrincipalPoint = (640, 630), SensorResolution = (1280, 1280),
};
var solver = new PoseLibSolver(layout, intrinsics);
TagDetection Detection(int id, double start) => new(id, 0, 50, default,
    new Point2D(start, start + 1), new Point2D(start + 2, start + 3),
    new Point2D(start + 4, start + 5), new Point2D(start + 6, start + 7));
var detections = new[] { Detection(2, 100), Detection(9, 200) };

if (args.Contains("--measure"))
{
    foreach (int count in new[] { 1, 2, 4 })
    {
        var input = Enumerable.Range(0, count).Select(i => detections[i % 2]).ToArray();
        for (int i = 0; i < 2000; i++) solver.PoseLibSolve(input);
        const int iterations = 20000;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++) solver.PoseLibSolve(input);
        double elapsed = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / iterations;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"{count} tags: {allocated / iterations} B/solve, {elapsed:F0} ns/solve ({iterations} iterations)");
    }
    return;
}

int assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < 1e-12, message);
// Independent Euler Rz(yaw)*Ry(pitch)*Rx(roll) reference, with explicit detector order.
double[] Expected(Pose3d pose, double roll, double pitch, double yaw)
{
    double cr = Math.Cos(roll), sr = Math.Sin(roll), cp = Math.Cos(pitch), sp = Math.Sin(pitch),
        cy = Math.Cos(yaw), sy = Math.Sin(yaw), h = tagSize / 2;
    return new[] { (-h, -h), (h, -h), (h, h), (-h, h) }.SelectMany(c => new[]
    {
        pose.X + (cy * sp * sr - sy * cr) * c.Item1 + (cy * sp * cr + sy * sr) * c.Item2,
        pose.Y + (sy * sp * sr + cy * cr) * c.Item1 + (sy * sp * cr - cy * sr) * c.Item2,
        pose.Z + cp * sr * c.Item1 + cp * cr * c.Item2,
    }).ToArray();
}
var expected2 = Expected(layout.GetTagPose(2), -0.52, 0.29, -1.76);
var expected9 = Expected(layout.GetTagPose(9), 0.37, -0.61, 1.12);
Check(solver.PoseLibSolve(detections) == null, "native failure returns null");
Check(PoseLibNatives.NumPoints == 8 && PoseLibNatives.Points2d.Length == 16 && PoseLibNatives.Points3d.Length == 24,
    "exact native count and packed lengths");
for (int i = 0; i < 16; i++) Near(PoseLibNatives.Points2d[i], (i < 8 ? 100 : 192) + i, "2D detection/corner order");
var expected3d = expected2.Concat(expected9).ToArray();
for (int i = 0; i < 24; i++) Near(PoseLibNatives.Points3d[i], expected3d[i], "3D rotated corner and detection order");
Check(PoseLibNatives.Model == 1 && PoseLibNatives.Width == 1280 && PoseLibNatives.Height == 1280
    && PoseLibNatives.NumCameraParams == 4 && PoseLibNatives.Threshold == 12, "native camera and RANSAC arguments");
int calls = PoseLibNatives.Calls;
Check(solver.PoseLibSolve((IReadOnlyList<TagDetection>)null) == null
    && solver.PoseLibSolve(Array.Empty<TagDetection>()) == null && PoseLibNatives.Calls == calls, "empty input skips native");

var firstPoints2d = PoseLibNatives.Points2d;
var firstPoints3d = PoseLibNatives.Points3d;
solver.PoseLibSolve(new[] { detections[1], detections[0], detections[1] });
Check(PoseLibNatives.NumPoints == 12 && PoseLibNatives.Points3d.Length == 36, "repeated IDs retain every correspondence");
Check(!ReferenceEquals(firstPoints2d, PoseLibNatives.Points2d)
    && !ReferenceEquals(firstPoints3d, PoseLibNatives.Points3d), "each solve owns its native input buffers");
for (int i = 0; i < 24; i++) Near(firstPoints3d[i], expected3d[i], "later solve preserves prior buffer");

calls = PoseLibNatives.Calls;
Check(solver.PoseLibSolve(new[] { detections[0], Detection(999, 300), detections[1] }) == null
    && PoseLibNatives.Calls == calls, "unknown ID cannot send incomplete correspondences to native");

// Native identity pose, real projections: exercise the unchanged quality/covariance/output path.
TagDetection Projected(int id)
{
    var corners = layout.GetTagCorners(id).Select(c => new Point2D(880 * c.X / c.Z + 640, 890 * c.Y / c.Z + 630)).ToArray();
    return new TagDetection(id, 0, 50, default, corners[0], corners[1], corners[2], corners[3]);
}
PoseLibNatives.Status = 0;
var projected = new[] { Projected(2), Projected(9) };
var solved = solver.PoseLibSolve(projected);
Check(solved.TotalPoints == 8 && solved.AcceptedPoints == 8 && solved.AcceptedTagIndices.SequenceEqual(new[] { 0, 1 }),
    "native count and accepted complete-tag order preserved");
Near(solved.CameraPose.X, 0, "native pose translation");
Near(solved.CameraPose.Rotation.Quaternion.W, 1, "native pose rotation");
Check(solved.Quality.ReprojectionErrorPixels < 1e-10 && solved.Covariance != null, "real quality and covariance computed from packed corners");
var rejected = solver.PoseLibSolve(new[] { projected[0], Detection(9, 10000) });
Check(rejected.TotalPoints == 8 && rejected.AcceptedPoints == 8 && rejected.AcceptedTagIndices.SequenceEqual(new[] { 0 }),
    "rejected complete tag keeps native diagnostics and source index");
var allRejected = solver.PoseLibSolve(detections);
Check(allRejected.AcceptedTagIndices.Count == 0 && allRejected.Covariance == null
    && allRejected.Quality.Ambiguity == 1 && double.IsPositiveInfinity(allRejected.Quality.ReprojectionErrorPixels),
    "all-rejected output retains conservative quality and null covariance");

intrinsics.FocalLength = (900, 910);
intrinsics.PrincipalPoint = (320, 310);
intrinsics.SensorResolution = (640, 640);
solver.RefreshIntrinsics(intrinsics);
PoseLibNatives.Status = 1;
solver.PoseLibSolve(detections);
Check(PoseLibNatives.CameraParams.SequenceEqual(new double[] { 900, 910, 320, 310 })
    && PoseLibNatives.Width == 640 && PoseLibNatives.Height == 640, "refresh still uses new intrinsics/resolution");

assertions += await LayoutChecks.Run();
Console.WriteLine($"PoseLib packing checks: {assertions} assertions passed.");
