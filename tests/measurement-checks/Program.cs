using System;
using System.IO;
using System.Linq;
using QuestX.QuestX.AprilTag;

int assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}

// Quest 3 passthrough-like pinhole camera, 1280x1280.
double[] intrinsics = { 880, 880, 640, 640 };
const double TagSize = 0.1651;

// WPILib tag frame: +X is the printed face normal; corners wrap the face in order.
double[][] TagCorners(double x, double y, double z, double normalYaw)
{
    double h = TagSize / 2;
    var local = new[] { (-h, -h), (h, -h), (h, h), (-h, h) };
    return local
        .Select(c => new[] {
            x - Math.Sin(normalYaw) * c.Item1,
            y + Math.Cos(normalYaw) * c.Item1,
            z + c.Item2,
        })
        .ToArray();
}

// World-to-camera pose (OpenCV axes: x right, y down, z forward) looking from eye at target.
(double[] R, double[] T) LookAt(double[] eye, double[] target)
{
    double[] f = Normalize(Sub(target, eye));
    double[] right = Normalize(Cross(f, new[] { 0.0, 0, 1 }));
    double[] down = Cross(f, right);
    var r = new[] { right[0], right[1], right[2], down[0], down[1], down[2], f[0], f[1], f[2] };
    var t = new[] {
        -(r[0] * eye[0] + r[1] * eye[1] + r[2] * eye[2]),
        -(r[3] * eye[0] + r[4] * eye[1] + r[5] * eye[2]),
        -(r[6] * eye[0] + r[7] * eye[1] + r[8] * eye[2]),
    };
    return (r, t);
}

// Projects corners and adds seeded Gaussian pixel noise. Returns the 2D buffer and the noise RMS.
(double[] Points2d, double NoiseRms) Project(double[][] corners, double[] r, double[] t, double sigma, int seed)
{
    var random = new Random(seed);
    var points = new double[corners.Length * 2];
    double sum = 0;
    for (int i = 0; i < corners.Length; i++)
    {
        var c = corners[i];
        double cx = r[0] * c[0] + r[1] * c[1] + r[2] * c[2] + t[0];
        double cy = r[3] * c[0] + r[4] * c[1] + r[5] * c[2] + t[1];
        double cz = r[6] * c[0] + r[7] * c[1] + r[8] * c[2] + t[2];
        double nx = Gaussian(random) * sigma;
        double ny = Gaussian(random) * sigma;
        sum += nx * nx + ny * ny;
        points[i * 2] = intrinsics[0] * cx / cz + intrinsics[2] + nx;
        points[i * 2 + 1] = intrinsics[1] * cy / cz + intrinsics[3] + ny;
    }
    return (points, Math.Sqrt(sum / corners.Length));
}

double Gaussian(Random random) =>
    Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

double[] Flatten(double[][] corners) => corners.SelectMany(c => c).ToArray();

double[] MatrixToQuaternion(double[] m)
{
    double trace = m[0] + m[4] + m[8];
    double w, x, y, z;
    if (trace > 0)
    {
        double s = Math.Sqrt(trace + 1) * 2;
        w = s / 4; x = (m[7] - m[5]) / s; y = (m[2] - m[6]) / s; z = (m[3] - m[1]) / s;
    }
    else if (m[0] > m[4] && m[0] > m[8])
    {
        double s = Math.Sqrt(1 + m[0] - m[4] - m[8]) * 2;
        w = (m[7] - m[5]) / s; x = s / 4; y = (m[1] + m[3]) / s; z = (m[2] + m[6]) / s;
    }
    else if (m[4] > m[8])
    {
        double s = Math.Sqrt(1 + m[4] - m[0] - m[8]) * 2;
        w = (m[2] - m[6]) / s; x = (m[1] + m[3]) / s; y = s / 4; z = (m[5] + m[7]) / s;
    }
    else
    {
        double s = Math.Sqrt(1 + m[8] - m[0] - m[4]) * 2;
        w = (m[3] - m[1]) / s; x = (m[2] + m[6]) / s; y = (m[5] + m[7]) / s; z = s / 4;
    }
    return new[] { w, x, y, z };
}

double RotationErrorDegrees(double[] a, double[] b)
{
    // trace(A^T B) = 1 + 2 cos(theta)
    double trace = 0;
    for (int i = 0; i < 9; i++) trace += a[i] * b[i];
    return Math.Acos(Math.Clamp((trace - 1) / 2, -1, 1)) * 180 / Math.PI;
}

double[] CameraCenter(double[] r, double[] t) => new[] {
    -(r[0] * t[0] + r[3] * t[1] + r[6] * t[2]),
    -(r[1] * t[0] + r[4] * t[1] + r[7] * t[2]),
    -(r[2] * t[0] + r[5] * t[1] + r[8] * t[2]),
};

double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
double[] Cross(double[] a, double[] b) =>
    new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
double Norm(double[] a) => Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
double[] Normalize(double[] a) { double n = Norm(a); return new[] { a[0] / n, a[1] / n, a[2] / n }; }

AprilTagAmbiguity.Result Evaluate(double[][] corners, double[] points2d, int tags, double[] r, double[] t) =>
    AprilTagAmbiguity.Evaluate(points2d, Flatten(corners), tags, intrinsics, MatrixToQuaternion(r), t);

var eye = new[] { 0.0, 0, 1 };

// 1. The truth pose reprojects with exactly the injected noise.
{
    var tag = TagCorners(1.2, 0.3, 1.1, Math.PI - 0.8);
    var (r, t) = LookAt(eye, new[] { 1.2, 0.3, 1.1 });
    var (points, noise) = Project(tag, r, t, 0.4, 1);
    var result = Evaluate(tag, points, 1, r, t);
    Check(Math.Abs(result.ReprojectionErrorPixels - noise) < 1e-9, "Truth pose error must equal the injected noise RMS");
    Check(result.TagAmbiguities.Length == 1, "One ambiguity per tag");
    Check(result.Ambiguity == result.TagAmbiguities[0], "Single-tag measurement ambiguity is the tag's ambiguity");
}

// 2. IPPE recovers the true pose as its better solution, and the flipped one fits worse.
for (int seed = 0; seed < 20; seed++)
{
    var tag = TagCorners(1.2, 0.3, 1.1, Math.PI - 0.8);
    var (r, t) = LookAt(eye, new[] { 1.2, 0.3, 1.1 });
    var (points, _) = Project(tag, r, t, 0.3, 100 + seed);
    Check(AprilTagAmbiguity.TrySolvePlanar(points, Flatten(tag), 0, 4, intrinsics, out var solutions), "Planar solve succeeds");
    var errors = solutions
        .Select(s => AprilTagAmbiguity.RmsError(s.Rotation, s.Translation, points, Flatten(tag), 0, 4, intrinsics))
        .ToArray();
    var best = solutions[errors[0] <= errors[1] ? 0 : 1];
    Check(RotationErrorDegrees(best.Rotation, r) < 3, $"Best rotation within 3 degrees (seed {seed})");
    Check(Norm(Sub(CameraCenter(best.Rotation, best.Translation), eye)) < 0.03, $"Best camera position within 3 cm (seed {seed})");
    Check(errors.Max() > errors.Min(), "Alternate solution fits worse");

    // Publishing the flipped pose yields a larger fit error than the truth.
    var alternate = solutions[errors[0] <= errors[1] ? 1 : 0];
    var wrong = Evaluate(tag, points, 1, alternate.Rotation, alternate.Translation);
    Check(wrong.ReprojectionErrorPixels > Evaluate(tag, points, 1, r, t).ReprojectionErrorPixels, "Flip has larger error");
}

// 3. A distant, face-on tag is ambiguous; a near, oblique tag is not.
double MeanAmbiguity(double[][] corners, double[] target, double sigma, Func<AprilTagAmbiguity.Result, double> pick, int tags = 1)
{
    var (r, t) = LookAt(eye, target);
    return Enumerable.Range(0, 40)
        .Select(seed => pick(Evaluate(corners, Project(corners, r, t, sigma, 500 + seed).Points2d, tags, r, t)))
        .Average();
}
double farFrontal = MeanAmbiguity(TagCorners(5, 0, 1, Math.PI), new[] { 5.0, 0, 1 }, 0.3, x => x.Ambiguity);
double nearOblique = MeanAmbiguity(TagCorners(1.2, 0, 1, Math.PI - 0.9), new[] { 1.2, 0, 1 }, 0.3, x => x.Ambiguity);
Console.WriteLine($"Mean ambiguity: far frontal {farFrontal:F3}, near oblique {nearOblique:F3}");
Check(farFrontal > 0.4, "Far, face-on single tag should be ambiguous");
Check(nearOblique < 0.1, "Near, oblique single tag should not be ambiguous");

// 4. Non-coplanar tags report 0 at the measurement level but keep per-tag values.
{
    var a = TagCorners(4, 0.5, 1, Math.PI);
    var b = TagCorners(3, 2.5, 1, -Math.PI / 2);
    var corners = a.Concat(b).ToArray();
    var (r, t) = LookAt(eye, new[] { 3.5, 1.5, 1 });
    var (points, _) = Project(corners, r, t, 0.3, 7);
    var result = Evaluate(corners, points, 2, r, t);
    Check(result.Ambiguity == 0, "Tags on different planes have no shared planar ambiguity");
    Check(result.TagAmbiguities.Length == 2, "Per-tag ambiguity for each tag");
    Check(result.TagAmbiguities.All(x => x >= 0 && x <= 1), "Per-tag ambiguities lie in [0, 1]");
}

// 5. Coplanar tags are solved jointly; the wider baseline is less ambiguous than either tag.
// (Face-on planes are excluded: both IPPE poses coincide there, so the ratio stays near 1.)
{
    double yaw = Math.PI - 0.7;
    double along = 0.6;
    var corners = TagCorners(5 + along * Math.Sin(yaw), -along * Math.Cos(yaw), 1, yaw)
        .Concat(TagCorners(5 - along * Math.Sin(yaw), along * Math.Cos(yaw), 1, yaw))
        .ToArray();
    var target = new[] { 5.0, 0, 1 };
    double joint = MeanAmbiguity(corners, target, 0.3, x => x.Ambiguity, 2);
    double single = MeanAmbiguity(corners, target, 0.3, x => x.TagAmbiguities.Average(), 2);
    Console.WriteLine($"Coplanar pair: joint {joint:F3}, mean single-tag {single:F3}");
    Check(joint > 0 && joint <= 1, "Joint coplanar ambiguity is a ratio");
    Check(joint < single, "Two coplanar tags 1.2 m apart are less ambiguous than one tag");
}

// 6. Degenerate geometry reports maximal ambiguity instead of throwing.
{
    var points3d = new double[12];
    var points2d = new double[] { 640, 640, 641, 640, 641, 641, 640, 641 };
    var result = AprilTagAmbiguity.Evaluate(points2d, points3d, 1, intrinsics, new[] { 1.0, 0, 0, 0 }, new[] { 0.0, 0, 2 });
    Check(result.Ambiguity == 1, "Degenerate tag is maximally ambiguous");
    Check(double.IsFinite(result.ReprojectionErrorPixels), "Fit error is still finite");
}

// 7. Malformed buffers are programmer errors.
{
    bool threw = false;
    try { AprilTagAmbiguity.Evaluate(new double[4], new double[12], 1, intrinsics, new[] { 1.0, 0, 0, 0 }, new double[3]); }
    catch (ArgumentException) { threw = true; }
    Check(threw, "Short 2D buffer is rejected");
}

VioFrameChecks.Run(Check);
WireChecks.Run(Check);
VioQualityChecks.Run(Check);
OrientationChecks.Run(Check);
SceneChecks.Run(Check);
CovarianceChecks.Run(Check);

// 8. Sample v3 and v4 frames for robot-side decoder tests: real 2026 REBUILT tags on the blue hub
// face (25 and 26 share the x = 4.022 m plane and face the blue wall), seen from headset height.
// The pose is the camera's; ambiguity, fit error, and covariance come from the production solver,
// with the default 1 px corner noise, and the encoder is the production one.
(double[] V3, double[] V4) SampleFrame(double[] eyePosition, int[] ids, double[][] centers, int seed, ulong sequence,
    long captureMicros, double appTimestamp, double latencyMs)
{
    var corners = centers.SelectMany(c => TagCorners(c[0], c[1], c[2], Math.PI)).ToArray();
    var aim = new[] { centers.Average(c => c[0]), centers.Average(c => c[1]), centers.Average(c => c[2]) };
    var (r, t) = LookAt(eyePosition, aim);
    var points = Project(corners, r, t, 0.3, seed).Points2d;
    var quality = Evaluate(corners, points, ids.Length, r, t);
    var camera = AprilTagCovariance.Estimate(points, Flatten(corners), ids.Length, intrinsics, MatrixToQuaternion(r), t,
        AprilTagCovariance.DefaultCornerNoisePixels, 12);
    double distance = centers.Average(c => Norm(Sub(c, eyePosition)));
    return WireChecks.EncodeSample(sequence, captureMicros, appTimestamp, camera, ids, distance, quality, latencyMs);
}
var tag25 = new[] { 4.022, 4.39, 1.124 };
var tag26 = new[] { 4.022, 4.035, 1.124 };
var singleTag = SampleFrame(new[] { 1.0, 2.8, 0.55 }, new[] { 26 }, new[] { tag26 }, 2468, 1041, 81_234_567, 812.25, 41.25);
var twoTags = SampleFrame(new[] { 2.0, 4.6, 0.55 }, new[] { 25, 26 }, new[] { tag25, tag26 }, 2468, 1042, 81_267_890, 812.283, 39.5);
WireChecks.CheckSample(Check, singleTag, "single-tag sample");
WireChecks.CheckSample(Check, twoTags, "two-tag sample");
Check(singleTag.V3[23] > 0.25 && singleTag.V3[23] < 0.35, "Single-tag sample has ambiguity near 0.3");
Check(twoTags.V4[17] < singleTag.V4[17] && twoTags.V4[18] < singleTag.V4[18],
    "Two nearer tags are more certain than one farther tag");
Console.WriteLine("Single-tag v4 sample: " + WireChecks.Format(singleTag.V4));
Console.WriteLine("Two-tag v4 sample: " + WireChecks.Format(twoTags.V4));
Console.WriteLine($"Sample std devs (forward, left, yaw): single tag {singleTag.V4[17]:F3} m, {singleTag.V4[18]:F3} m, "
    + $"{singleTag.V4[22] * 180 / Math.PI:F1} deg; two tags {twoTags.V4[17]:F3} m, {twoTags.V4[18]:F3} m, {twoTags.V4[22] * 180 / Math.PI:F1} deg");

if (args.Contains("--export-fixtures"))
{
    WireChecks.Export(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")), singleTag, twoTags);
}

Console.WriteLine($"{assertions} measurement checks passed");
