// Napa vs libapriltag through the real QuestX ITagDetector backends and the bundled Editor
// binaries. Each trial renders a tag36h11 tag (from libapriltag's own bitmap) under a random
// perspective warp into a bottom-up RGBA readback, the format PassthroughCameraAccess returns,
// then checks that Napa's converted corners, center and IDs match libapriltag's conventions.
//   dotnet run -c Release --project tests/napa-detector
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using QuestX.Native.AprilTag;
using QuestX.Native.Napa;
using Unity.Collections;
using UnityEngine;

int assertions = 0;

// Feeds a readback to a backend the way the headset's detection worker does: a pointer to
// the bottom-up RGBA bytes and how many are readable.
static unsafe bool Detect(ITagDetector detector, NativeArray<Color32> colors, int width, int height, List<TagDetection> found) =>
    detector.Detect(
        colors.IsCreated ? (IntPtr)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(colors) : IntPtr.Zero,
        colors.IsCreated ? (long)colors.Length * 4 : 0,
        width,
        height,
        found);

void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
    assertions++;
}

var family = new Tag36h11();
var patterns = new Dictionary<int, byte[,]>();
unsafe byte[,] Pattern(int id)
{
    if (patterns.TryGetValue(id, out var cached))
        return cached;
    var image = AprilTagNatives.apriltag_to_image(family.Handle, (uint)id);
    var pattern = new byte[image->height, image->width];
    for (int y = 0; y < image->height; y++)
        for (int x = 0; x < image->width; x++)
            pattern[y, x] = ((byte*)image->buf)[y * image->stride + x];
    AprilTagNatives.image_u8_destroy(image);
    return patterns[id] = pattern;
}

// Homography mapping (u, v) to (x, y) for four point pairs, h33 = 1.
static double[] Homography(double[,] from, double[,] to)
{
    var a = new double[8, 9];
    for (int i = 0; i < 4; i++)
    {
        double u = from[i, 0], v = from[i, 1], x = to[i, 0], y = to[i, 1];
        double[] r0 = { u, v, 1, 0, 0, 0, -u * x, -v * x, x };
        double[] r1 = { 0, 0, 0, u, v, 1, -u * y, -v * y, y };
        for (int j = 0; j < 9; j++)
        {
            a[2 * i, j] = r0[j];
            a[2 * i + 1, j] = r1[j];
        }
    }
    for (int c = 0; c < 8; c++)
    {
        int pivot = c;
        for (int r = c + 1; r < 8; r++)
            if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c]))
                pivot = r;
        for (int j = 0; j < 9; j++)
            (a[c, j], a[pivot, j]) = (a[pivot, j], a[c, j]);
        for (int r = 0; r < 8; r++)
        {
            if (r == c)
                continue;
            double f = a[r, c] / a[c, c];
            for (int j = c; j < 9; j++)
                a[r, j] -= f * a[c, j];
        }
    }
    var h = new double[9];
    for (int i = 0; i < 8; i++)
        h[i] = a[i, 8] / a[i, i];
    h[8] = 1;
    return h;
}

static (double, double) Apply(double[] h, double u, double v)
{
    double w = h[6] * u + h[7] * v + h[8];
    return ((h[0] * u + h[1] * v + h[2]) / w, (h[3] * u + h[4] * v + h[5]) / w);
}

static double[] Invert(double[] m)
{
    double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
    double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
    return new[]
    {
        (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
        (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
        (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det,
    };
}

// Renders tags into a top-left-origin grayscale frame with 4x4 supersampling, then stores it
// bottom-up as RGBA (r = g = b, so QuestX's luma weights reproduce the grey level exactly).
// Ground-truth black-square corners are returned in pixel-edge coordinates, libapriltag's
// convention (pixel (0, 0)'s center is (0.5, 0.5)).
(NativeArray<Color32> Colors, Dictionary<int, double[,]> Truth) Render(
    int width,
    int height,
    IEnumerable<(int Id, double[,] Quad)> tags,
    Random noise
)
{
    var gray = new double[height, width];
    for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            gray[y, x] = 110 + (x * 7 + y * 3) % 23;
    var truth = new Dictionary<int, double[,]>();
    foreach (var (id, quad) in tags)
    {
        var pattern = Pattern(id);
        int n = pattern.GetLength(0);
        double[,] square = { { 0, 0 }, { n, 0 }, { n, n }, { 0, n } };
        var h = Homography(square, quad);
        var inverse = Invert(h);
        var black = new double[4, 2];
        double[,] blackSquare = { { 1, 1 }, { n - 1, 1 }, { n - 1, n - 1 }, { 1, n - 1 } };
        for (int k = 0; k < 4; k++)
            (black[k, 0], black[k, 1]) = Apply(h, blackSquare[k, 0], blackSquare[k, 1]);
        truth[id] = black;

        int x0 = (int)Math.Max(0, Enumerable.Range(0, 4).Min(k => quad[k, 0]) - 2);
        int x1 = (int)Math.Min(width - 1, Enumerable.Range(0, 4).Max(k => quad[k, 0]) + 2);
        int y0 = (int)Math.Max(0, Enumerable.Range(0, 4).Min(k => quad[k, 1]) - 2);
        int y1 = (int)Math.Min(height - 1, Enumerable.Range(0, 4).Max(k => quad[k, 1]) + 2);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double sum = 0;
                int inside = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        var (u, v) = Apply(inverse, x + (sx + 0.5) / 4, y + (sy + 0.5) / 4);
                        if (u < 0 || v < 0 || u >= n || v >= n)
                            continue;
                        sum += pattern[(int)v, (int)u];
                        inside++;
                    }
                if (inside > 0)
                    gray[y, x] = (sum + gray[y, x] * (16 - inside)) / 16;
            }
    }
    var colors = new NativeArray<Color32>(width * height * 4, Allocator.Persistent); // SDK over-allocates 4x
    for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            byte level = (byte)Math.Clamp(Math.Round(gray[y, x] + noise.NextDouble() * 4 - 2), 0, 255);
            colors[(height - 1 - y) * width + x] = new Color32(level, level, level, 255);
        }
    return (colors, truth);
}

static double Distance(Point2D a, double x, double y) => Math.Sqrt((a.x - x) * (a.x - x) + (a.y - y) * (a.y - y));

// Corner i of libapriltag's output in terms of the black-square corners above, found from data.
static int[] BestOrder(TagDetection detection, double[,] truth)
{
    int[][] orders =
    {
        new[] { 0, 1, 2, 3 }, new[] { 1, 2, 3, 0 }, new[] { 2, 3, 0, 1 }, new[] { 3, 0, 1, 2 },
        new[] { 3, 2, 1, 0 }, new[] { 0, 3, 2, 1 }, new[] { 1, 0, 3, 2 }, new[] { 2, 1, 0, 3 },
    };
    var corners = detection.GetCorners();
    return orders
        .OrderBy(o => Enumerable.Range(0, 4).Max(i => Distance(corners[i], truth[o[i], 0], truth[o[i], 1])))
        .First();
}

static double Median(List<double> values) => values.OrderBy(v => v).ElementAt(values.Count / 2);

static double Percentile(List<double> values, double p) =>
    values.OrderBy(v => v).ElementAt(Math.Min(values.Count - 1, (int)(p * values.Count)));

// The overlay preview encodes ITagDetector.Image, so it must be the same pixels either way.
static unsafe bool SameImage(GrayImage a, GrayImage b, int width, int height)
{
    if (!a.IsValid || !b.IsValid || a.Width != width || b.Width != width || a.Height != height || b.Height != height)
        return false;
    for (int y = 0; y < height; y++)
    {
        var rowA = new ReadOnlySpan<byte>((byte*)a.Buffer + (long)y * a.Stride, width);
        var rowB = new ReadOnlySpan<byte>((byte*)b.Buffer + (long)y * b.Stride, width);
        if (!rowA.SequenceEqual(rowB))
            return false;
    }
    return true;
}

var napa = new NapaTagDetector(24, 3);
var aprilTag = new LibAprilTagDetector();
Check(napa.Name == "Napa" && aprilTag.Name == "AprilTag", "backend names");

var random = new Random(2468);
var napaDetections = new List<TagDetection>();
var aprilTagDetections = new List<TagDetection>();
var cornerDelta = new List<double>();
var centerDelta = new List<double>();
var napaTruthError = new List<double>();
var aprilTagTruthError = new List<double>();
var orderCounts = new Dictionary<string, int>();
var misses = new List<string>();
int trials = 300, napaFound = 0, aprilTagFound = 0, both = 0;
for (int trial = 0; trial < trials; trial++)
{
    const int width = 960, height = 720;
    int id = random.Next(0, 587);
    double side = 40 + random.NextDouble() * 180; // black-square side, pixels
    double full = side * 10 / 8;
    double angle = random.NextDouble() * 2 * Math.PI;
    double cx = 220 + random.NextDouble() * (width - 440), cy = 200 + random.NextDouble() * (height - 400);
    var quad = new double[4, 2];
    double[,] unit = { { -0.5, -0.5 }, { 0.5, -0.5 }, { 0.5, 0.5 }, { -0.5, 0.5 } };
    for (int k = 0; k < 4; k++)
    {
        double ux = unit[k, 0] + (random.NextDouble() - 0.5) * 0.2;
        double uy = unit[k, 1] + (random.NextDouble() - 0.5) * 0.2;
        quad[k, 0] = cx + full * (ux * Math.Cos(angle) - uy * Math.Sin(angle));
        quad[k, 1] = cy + full * (ux * Math.Sin(angle) + uy * Math.Cos(angle));
    }
    var (colors, truth) = Render(width, height, new[] { (id, quad) }, random);
    Check(Detect(napa, colors, width, height, napaDetections), "napa converted frame");
    Check(Detect(aprilTag, colors, width, height, aprilTagDetections), "apriltag converted frame");
    if (trial < 5)
        Check(SameImage(napa.Image, aprilTag.Image, width, height), "both backends expose the same top-down grayscale image");
    colors.Dispose();

    Check(napaDetections.All(d => d.Id == id), $"trial {trial}: napa false ID {string.Join(",", napaDetections.Select(d => d.Id))}");
    Check(aprilTagDetections.All(d => d.Id == id), $"trial {trial}: apriltag false ID");
    bool napaHit = napaDetections.Count == 1, aprilTagHit = aprilTagDetections.Count == 1;
    napaFound += napaHit ? 1 : 0;
    aprilTagFound += aprilTagHit ? 1 : 0;
    if (!napaHit || !aprilTagHit)
    {
        misses.Add($"trial {trial}: id {id} side {side:F0}px angle {angle * 180 / Math.PI:F0}deg napa={napaHit} apriltag={aprilTagHit}");
        continue;
    }
    both++;

    var n = napaDetections[0];
    var a = aprilTagDetections[0];
    var order = BestOrder(a, truth[id]);
    orderCounts[string.Join("", order)] = orderCounts.GetValueOrDefault(string.Join("", order)) + 1;
    var nc = n.GetCorners();
    var ac = a.GetCorners();
    cornerDelta.Add(Enumerable.Range(0, 4).Max(i => Distance(nc[i], ac[i].x, ac[i].y)));
    centerDelta.Add(Distance(n.Center, a.Center.x, a.Center.y));
    napaTruthError.Add(Enumerable.Range(0, 4).Max(i => Distance(nc[i], truth[id][order[i], 0], truth[id][order[i], 1])));
    aprilTagTruthError.Add(Enumerable.Range(0, 4).Max(i => Distance(ac[i], truth[id][order[i], 0], truth[id][order[i], 1])));
    Check(n.Hamming >= 0 && n.Hamming <= 3, "napa hamming range");
}

Console.WriteLine($"trials {trials}: napa found {napaFound}, apriltag found {aprilTagFound}, both {both}");
Console.WriteLine($"libapriltag corner order vs black-square corners (TL,TR,BR,BL): {string.Join(", ", orderCounts.Select(kv => $"{kv.Key} x{kv.Value}"))}");
Console.WriteLine($"napa vs apriltag, worst corner per tag: median {Median(cornerDelta):F3} px, p95 {Percentile(cornerDelta, 0.95):F3}, max {cornerDelta.Max():F3}");
Console.WriteLine($"napa vs apriltag center: median {Median(centerDelta):F3} px, max {centerDelta.Max():F3}");
Console.WriteLine($"vs ground truth, worst corner per tag: napa median {Median(napaTruthError):F3} / p95 {Percentile(napaTruthError, 0.95):F3}, apriltag median {Median(aprilTagTruthError):F3} / p95 {Percentile(aprilTagTruthError, 0.95):F3}");
misses.Take(40).ToList().ForEach(Console.WriteLine);
Check(both >= trials * 0.95, "both backends find nearly every tag");
Check(orderCounts.Count == 1, "libapriltag corner order is fixed relative to the tag");
Check(Percentile(cornerDelta, 0.95) < 0.5 && cornerDelta.Max() < 1.0, "napa corners land on libapriltag's corners");
Check(centerDelta.Max() < 0.5, "napa center matches libapriltag's projective center");
Check(Median(napaTruthError) < 0.25, "napa corners are accurate in libapriltag's pixel convention");

// Lifecycle and edge cases.
Check(!Detect(napa, default, 640, 480, napaDetections), "uncreated readback is skipped");
Check(!napa.Image.IsValid, "a skipped frame leaves no image to preview");
var tiny = new NativeArray<Color32>(8 * 8, Allocator.Persistent);
Check(Detect(napa, tiny, 8, 8, napaDetections) && napaDetections.Count == 0, "tiny frame yields nothing");
Check(!Detect(napa, tiny, 640, 480, napaDetections), "short readback is skipped");
tiny.Dispose();

// Several tags at Quest's default 1280x1280, timed through ITagDetector (grayscale + detect;
// the conversion runs unbursted here, unlike on the headset).
{
    const int width = 1280, height = 1280;
    var scene = new List<(int, double[,])>();
    int[] ids = { 1, 5, 9, 14, 22, 31 };
    double[] sides = { 26, 34, 48, 70, 110, 170 };
    for (int k = 0; k < ids.Length; k++)
    {
        double full = sides[k] * 10 / 8, x = 80 + k * 200, y = 300 + (k % 2) * 500;
        scene.Add((ids[k], new double[,] { { x, y }, { x + full, y + 0.1 * full }, { x + full * 0.95, y + full }, { x - 0.05 * full, y + full * 0.9 } }));
    }
    var (colors, _) = Render(width, height, scene, random);
    foreach (ITagDetector detector in new ITagDetector[] { napa, aprilTag })
    {
        var found = new List<TagDetection>();
        var samples = new List<double>();
        for (int i = 0; i < 25; i++)
        {
            long start = Stopwatch.GetTimestamp();
            Detect(detector, colors, width, height, found);
            samples.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        }
        Console.WriteLine($"{detector.Name,-8} 1280x1280: median {Median(samples):F2} ms, IDs [{string.Join(",", found.Select(d => d.Id).OrderBy(i => i))}] of [{string.Join(",", ids)}]");
        if (detector == napa)
            Check(ids.All(id => found.Any(d => d.Id == id)), "napa finds every tag from 26 px up");
    }
    colors.Dispose();
}

napa.Dispose();
napa.Dispose();
aprilTag.Dispose();
aprilTag.Dispose();
Check(!napa.Image.IsValid && !aprilTag.Image.IsValid, "disposed detectors expose no image");
bool threw = false;
try
{
    Detect(napa, default, 640, 480, napaDetections);
}
catch (ObjectDisposedException)
{
    threw = true;
}
Check(threw, "disposed detector refuses to run");
Console.WriteLine($"napa-detector checks passed ({assertions} assertions)");
