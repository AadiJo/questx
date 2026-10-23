using System;
using System.Collections.Generic;
using System.Linq;
using QuestX.QuestX.AprilTag;

/// <summary>Exercises plane-frame selection, nonzero corner offsets, and degenerate fit returns.</summary>
static class AmbiguityRegressionChecks
{
    internal sealed record Case(string Name, double[] Pixels, double[] Corners, int Tags, bool Coplanar, bool Solvable);
    internal static readonly double[] Intrinsics = { 880, 870, 640, 630 };
    internal static readonly double[] Quaternion = { 1, 0, 0, 0 };
    internal static readonly double[] Translation = { .04, .1, 2 };

    // Shared with the isolated before/after audit harness. No Unity, native solver,
    // timing assumptions, or reference implementation is needed by this suite.
    internal static IEnumerable<Case> Cases()
    {
        foreach (int tags in new[] { 1, 2, 4 })
        foreach (bool coplanar in new[] { true, false })
        {
            if (tags == 1 && !coplanar) continue;
            for (int seed = 0; seed < 100; seed++)
            {
                var random = new Random(719 + seed);
                var corners = new double[tags * 12];
                var pixels = new double[tags * 8];
                double tilt = -.7 + random.NextDouble() * 1.4;
                double sigma = seed % 3 == 0 ? 0 : seed % 3 == 1 ? .1 : .5;
                for (int tag = 0; tag < tags; tag++)
                for (int corner = 0; corner < 4; corner++)
                {
                    double u = tag * .3 + (corner == 0 || corner == 3 ? -.08 : .08);
                    double v = (tag % 2) * .12 + (corner < 2 ? -.08 : .08);
                    double x = Math.Cos(tilt) * u;
                    double y = v;
                    double z = Math.Sin(tilt) * u + (coplanar ? 0 : tag * .2);
                    int index = tag * 4 + corner;
                    corners[index * 3] = x;
                    corners[index * 3 + 1] = y;
                    corners[index * 3 + 2] = z;
                    pixels[index * 2] = Intrinsics[0] * (x + Translation[0]) / (z + Translation[2]) + Intrinsics[2] + sigma * (random.NextDouble() - .5);
                    pixels[index * 2 + 1] = Intrinsics[1] * (y + Translation[1]) / (z + Translation[2]) + Intrinsics[3] + sigma * (random.NextDouble() - .5);
                }
                for (int order = 0; order < 3; order++)
                {
                    var reorderedCorners = new double[corners.Length];
                    var reorderedPixels = new double[pixels.Length];
                    for (int tag = 0; tag < tags; tag++)
                    for (int corner = 0; corner < 4; corner++)
                    {
                        int sourceTag = order == 1 ? tags - tag - 1 : tag;
                        int sourceCorner = order == 2 ? (3 - corner + seed % 4) % 4 : corner;
                        int source = sourceTag * 4 + sourceCorner, destination = tag * 4 + corner;
                        Array.Copy(corners, source * 3, reorderedCorners, destination * 3, 3);
                        Array.Copy(pixels, source * 2, reorderedPixels, destination * 2, 2);
                    }
                    yield return new Case($"tags={tags}, coplanar={coplanar}, seed={seed}, order={order}", reorderedPixels, reorderedCorners, tags, coplanar, true);
                }
            }
        }
        var squarePixels = new double[] { 600, 600, 680, 600, 680, 680, 600, 680 };
        yield return new Case("collapsed corners", squarePixels, new double[12], 1, true, false);
        yield return new Case("collinear corners", squarePixels, new double[] { 0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0 }, 1, true, false);
        yield return new Case("tiny plane", squarePixels, new double[] { -1e-12, -1e-12, 0, 1e-12, -1e-12, 0, 1e-12, 1e-12, 0, -1e-12, 1e-12, 0 }, 1, true, false);
        yield return new Case("singular image", Enumerable.Repeat(640.0, 8).ToArray(), new double[] { -.08, -.08, 0, .08, -.08, 0, .08, .08, 0, -.08, .08, 0 }, 1, true, false);
    }

    public static void Run(Action<bool, string> check)
    {
        int cases = 0;
        foreach (var input in Cases())
        {
            var corners = (double[])input.Corners.Clone();
            var pixels = (double[])input.Pixels.Clone();
            var result = AprilTagAmbiguity.Evaluate(pixels, corners, input.Tags, Intrinsics, Quaternion, Translation);
            check(result.Ambiguity >= 0 && result.Ambiguity <= 1, input.Name + ": bounded ambiguity");
            check(double.IsFinite(result.ReprojectionErrorPixels), input.Name + ": finite fit error");
            if (!input.Coplanar) check(result.Ambiguity == 0, input.Name + ": noncoplanar measurement has no planar ambiguity");
            for (int tag = 0; tag < input.Tags; tag++)
            {
                var tagCorners = corners.Skip(tag * 12).Take(12).ToArray();
                var tagPixels = pixels.Skip(tag * 8).Take(8).ToArray();
                var single = AprilTagAmbiguity.Evaluate(tagPixels, tagCorners, 1, Intrinsics, Quaternion, Translation);
                check(BitConverter.DoubleToInt64Bits(single.Ambiguity) == BitConverter.DoubleToInt64Bits(result.TagAmbiguities[tag]),
                    input.Name + ": per-tag output and nonzero corner offset agree exactly");
                bool solved = AprilTagAmbiguity.TrySolvePlanar(pixels, corners, tag * 4, 4, Intrinsics, out var poses);
                check(solved == input.Solvable, input.Name + ": planar success/failure return");
                check(solved ? poses.Length == 2 : poses == null, input.Name + ": two poses or null on failure");
            }
            if (!input.Solvable) check(result.Ambiguity == 1, input.Name + ": degenerate ambiguity is maximal");
            check(pixels.SequenceEqual(input.Pixels) && corners.SequenceEqual(input.Corners), input.Name + ": caller buffers unchanged");
            cases++;
        }
        check(!AprilTagAmbiguity.TrySolvePlanar(new double[6], new double[9], 0, 3, Intrinsics, out var shortResult) && shortResult == null,
            "Fewer than four corners returns false with null solutions");
        Console.WriteLine($"Ambiguity plane regressions: {cases} noisy, reordered, noncoplanar and degenerate cases");
    }
}
