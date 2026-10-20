using System;
using System.Collections.Generic;
using System.Linq;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;

/// <summary>
/// Checks the pose covariances ported from Napa: the math pieces exactly, then Monte Carlo
/// calibration in Napa's style. A calibrated covariance leaves 5% of errors outside its 95%
/// ellipse; Napa accepts up to about 10% in its hardest geometries.
/// </summary>
static class CovarianceChecks
{
    static readonly double[] Intrinsics = { 880, 880, 640, 640 };
    const double TagSize = 0.1651;
    const double Chi2Six95 = 12.592;

    public static void Run(Action<bool, string> check)
    {
        CheckRotations(check);
        CheckEigen(check);
        CheckJacobian(check);
        CheckTransforms(check);
        CheckLocalStdDevs(check);
        CheckFailureCases(check);
        CheckObservationSelection(check);
        CheckCalibration(check);
    }

    static void CheckRotations(Action<bool, string> check)
    {
        foreach (var v in new[] {
            new[] { 1e-9, -2e-9, 3e-9 }, new[] { 0.3, -0.2, 0.1 }, new[] { 0, 0, Math.PI - 1e-9 },
            new[] { 1.2, -1.9, 0.4 }, new[] { -Math.PI + 1e-7, 0, 0 } })
        {
            double[] back = PoseCovariance.Log(PoseCovariance.Exp(v));
            double angle = Math.Sqrt(v.Sum(x => x * x));
            // At a half turn v and -v are the same rotation.
            bool same = Enumerable.Range(0, 3).All(i => Math.Abs(back[i] - v[i]) < 1e-6)
                || (angle > Math.PI - 1e-6 && Enumerable.Range(0, 3).All(i => Math.Abs(back[i] + v[i]) < 1e-6));
            check(same, $"Log inverts Exp for [{string.Join(", ", v)}]");
        }
    }

    static void CheckEigen(Action<bool, string> check)
    {
        var random = new Random(3);
        var a = new double[36];
        for (int i = 0; i < 6; i++)
            for (int j = 0; j <= i; j++)
                a[i * 6 + j] = a[j * 6 + i] = random.NextDouble() - 0.5;
        AprilTagCovariance.SymmetricEigen(a, 6, out var values, out var vectors);
        double worst = 0;
        for (int r = 0; r < 6; r++)
            for (int c = 0; c < 6; c++)
            {
                double sum = 0;
                for (int k = 0; k < 6; k++) sum += vectors[r * 6 + k] * values[k] * vectors[c * 6 + k];
                worst = Math.Max(worst, Math.Abs(sum - a[r * 6 + c]));
            }
        check(worst < 1e-12, "Jacobi eigendecomposition reconstructs the matrix");
    }

    /// <summary>The analytic reprojection Jacobian matches central differences.</summary>
    static void CheckJacobian(Action<bool, string> check)
    {
        var field = Flatten(TagCorners(2.5, 0.4, 1.2, Math.PI - 0.5));
        var (r, t) = LookAt(new[] { 0.0, 0, 1 }, new[] { 2.5, 0.4, 1.2 });
        var image = new double[8];
        var residuals = new double[8];
        var jacobian = new double[48];
        AprilTagCovariance.Project(r, t, image, field, 4, Intrinsics, residuals, jacobian, out _);
        double worst = 0;
        const double h = 1e-6;
        for (int k = 0; k < 6; k++)
        {
            var plus = new double[8];
            var minus = new double[8];
            var delta = new double[6];
            delta[k] = h;
            Perturbed(r, t, delta, out var rp, out var tp);
            AprilTagCovariance.Project(rp, tp, image, field, 4, Intrinsics, plus, null, out _);
            delta[k] = -h;
            Perturbed(r, t, delta, out var rm, out var tm);
            AprilTagCovariance.Project(rm, tm, image, field, 4, Intrinsics, minus, null, out _);
            // Residuals are observed minus projected, so the projection's derivative is negated.
            for (int row = 0; row < 8; row++)
                worst = Math.Max(worst, Math.Abs(-(plus[row] - minus[row]) / (2 * h) - jacobian[row * 6 + k]));
        }
        check(worst < 1e-3, $"Reprojection Jacobian matches finite differences (worst {worst:E2} px)");
    }

    /// <summary>Transforms move the pose like Pose3d and the covariance to first order.</summary>
    static void CheckTransforms(Action<bool, string> check)
    {
        var rotation = new Rotation3d(0.1, -0.2, 2.4);
        var pose = new Pose3d(3, -1, 0.5, rotation);
        var covariance = RandomCovariance(11);
        var linear = PoseCovariance.Linear(Flatten(rotation.ToMatrix()), new[] { 3.0, -1, 0.5 }, covariance);
        var offset = new Transform3d(new Translation3d(0.2, -0.1, 0.4), new Rotation3d(0.3, 0.1, -0.5));
        var moved = linear.TransformBy(offset);
        var expected = pose.TransformBy(offset);
        check(Close(moved.Translation, new[] { expected.X, expected.Y, expected.Z }, 1e-12)
            && Close(moved.Rotation, Flatten(expected.Rotation.ToMatrix()), 1e-12),
            "TransformBy moves the pose as Pose3d.TransformBy does");

        // Pushing samples through the exact map agrees with the propagated covariance. Every summary
        // adds the correlation slack once, in its own frame, so compare without it.
        var random = new Random(5);
        var sum = new double[36];
        const int samples = 20000;
        var unit = new double[36];
        for (int i = 0; i < 6; i++) unit[i * 7] = 1;
        var baseline = PoseCovariance.Linear(linear.Rotation, linear.Translation, unit);
        var chol = Cholesky(covariance);
        for (int s = 0; s < samples; s++)
        {
            var z = Enumerable.Range(0, 6).Select(_ => Gaussian(random) * 1e-3).ToArray();
            var e = new double[6];
            for (int r = 0; r < 6; r++) for (int c = 0; c <= r; c++) e[r] += chol[r * 6 + c] * z[c];
            Perturbed(linear.Rotation, linear.Translation, e, out var rs, out var ts);
            var sample = PoseCovariance.Linear(rs, ts, unit).TransformBy(offset);
            var d = PoseCovariance.Difference(Component(moved), Component(sample));
            for (int r = 0; r < 6; r++) for (int c = 0; c < 6; c++) sum[r * 6 + c] += d[r] * d[c] / (samples * 1e-6);
        }
        var expectedCovariance = moved.Covariance;
        for (int i = 0; i < 6; i++) expectedCovariance[i * 7] /= 1.0 + PoseCovariance.CorrelationSlack;
        double scale = expectedCovariance.Select(Math.Abs).Max();
        double worst = Enumerable.Range(0, 36).Max(i => Math.Abs(sum[i] - expectedCovariance[i]));
        check(worst < 0.05 * scale, $"TransformBy covariance matches sampled transforms ({worst / scale:P1} off)");

        var roundTrip = linear.Inverse().Inverse();
        check(Close(roundTrip.Translation, linear.Translation, 1e-12) && Close(roundTrip.Rotation, linear.Rotation, 1e-12),
            "Inverse twice restores the pose");
        check(Close(roundTrip.Covariance, linear.Covariance, 1e-12), "Inverse twice restores the covariance");
        check(baseline.UpperTriangle().Length == 21 && baseline.UpperTriangle()[0] == 1.1 && baseline.UpperTriangle()[1] == 0,
            "Upper triangle is row-major with the slack on the diagonal");
    }

    /// <summary>Robot-frame standard deviations follow the robot's heading.</summary>
    static void CheckLocalStdDevs(Action<bool, string> check)
    {
        var covariance = new double[36];
        double[] variances = { 0.01, 0.09, 0.04, 0.0004, 0.0009, 0.0016 };
        for (int i = 0; i < 6; i++) covariance[i * 7] = variances[i];
        var facingLeft = PoseCovariance.Linear(Flatten(new Rotation3d(0, 0, Math.PI / 2).ToMatrix()), new double[3], covariance);
        double slack = Math.Sqrt(1 + PoseCovariance.CorrelationSlack);
        var field = facingLeft.StdDevs();
        var local = facingLeft.LocalStdDevs();
        check(Math.Abs(field[0] - 0.1 * slack) < 1e-12 && Math.Abs(field[1] - 0.3 * slack) < 1e-12, "Field-axis std devs");
        check(Math.Abs(local[0] - 0.3 * slack) < 1e-12 && Math.Abs(local[1] - 0.1 * slack) < 1e-12,
            "Facing +Y, the robot's forward error is the field's Y error");
        check(Math.Abs(local[3] - 0.03 * slack) < 1e-12 && Math.Abs(local[4] - 0.02 * slack) < 1e-12
            && Math.Abs(local[5] - 0.04 * slack) < 1e-12, "Facing +Y, roll and pitch swap field axes and yaw stays");
        check(Close(facingLeft.EstimatorStdDevs(), local, 1e-12), "A level pose's planar std devs are its local ones");

        // Pitched 60 degrees, rotations about the field's X and Z both move the heading: with
        // isotropic rotation variance 0.01, the planar heading's variance is 0.04.
        var isotropic = new double[36];
        for (int i = 0; i < 6; i++) isotropic[i * 7] = i < 3 ? 0.0001 : 0.01;
        var pitched = PoseCovariance.Linear(Flatten(new Rotation3d(0, Math.PI / 3, 0).ToMatrix()), new double[3], isotropic);
        check(Math.Abs(pitched.EstimatorStdDevs()[5] - Math.Sqrt(0.04 * 1.1)) < 1e-12,
            "Heading std dev of a pitched pose comes from the planar projection");
        check(Math.Abs(pitched.LocalStdDevs()[5] - Math.Sqrt(0.01 * 1.1)) < 1e-12, "Body yaw differs from planar heading");

        // Solutions on either side of +-180 degrees are 2 degrees apart, not 358.
        var small = new double[36];
        for (int i = 0; i < 6; i++) small[i * 7] = 1e-8;
        var wrapped = new PoseCovariance(new[] {
            new PoseCovariance.Component(Flatten(new Rotation3d(0, 0, Math.PI - 0.01745).ToMatrix()), new double[3], small, 1),
            new PoseCovariance.Component(Flatten(new Rotation3d(0, 0, -Math.PI + 0.01745).ToMatrix()), new double[3], small, 1),
        });
        double expectedYaw = Math.Sqrt(1.1 * (1e-8 + 0.5 * 0.0349 * 0.0349));
        check(Math.Abs(wrapped.EstimatorStdDevs()[5] - expectedYaw) < 1e-4, "Heading spread wraps at +-180 degrees");
    }

    static void CheckFailureCases(Action<bool, string> check)
    {
        var (r, t) = LookAt(new[] { 0.0, 0, 1 }, new[] { 2.3, 0, 1 });
        var box = Flatten(ThreeWalls());
        var image = Project(box, r, t);
        var clean = AprilTagCovariance.Estimate(image, box, 3, Intrinsics, Quaternion(r), t, 1.0, 12);
        check(clean != null && clean.SolutionCount == 1, "Tags at different depths get a linearized covariance");

        // A misread fourth tag that the robust solve rejected must not shrink the covariance.
        var misread = Flatten(TagCorners(3, 0.6, 1.2, Math.PI));
        var four = box.Concat(misread).ToArray();
        var imageFour = image.Concat(Project(misread, r, t).Select(v => v + 30)).ToArray();
        var withOutlier = AprilTagCovariance.Estimate(imageFour, four, 4, Intrinsics, Quaternion(r), t, 1.0, 12);
        check(withOutlier != null && Close(withOutlier.Covariance, clean.Covariance, 1e-15),
            "A tag outside the inlier threshold is left out");

        // The mirror comes from the best-fit plane of all corners, so tag order doesn't matter: two
        // tags 8 m away, 1 m apart, with normals 10 degrees apart.
        var (r3, t3) = LookAt(new[] { 0.0, 0, 1 }, new[] { 8.0, 0, 1 });
        var first = Flatten(TagCorners(8, -0.5, 1, Math.PI));
        var second = Flatten(TagCorners(8, 0.5, 1, Math.PI - 10 * Math.PI / 180));
        var inOrder = first.Concat(second).ToArray();
        var reversed = second.Concat(first).ToArray();
        var ab = AprilTagCovariance.Estimate(Project(inOrder, r3, t3), inOrder, 2, Intrinsics, Quaternion(r3), t3, 1.0, 12);
        var ba = AprilTagCovariance.Estimate(Project(reversed, r3, t3), reversed, 2, Intrinsics, Quaternion(r3), t3, 1.0, 12);
        double scaleAb = ab.Covariance.Select(Math.Abs).Max();
        check(ab.Covariance.Zip(ba.Covariance).All(p => Math.Abs(p.First - p.Second) < 1e-6 * scaleAb),
            "The covariance doesn't depend on tag order");

        // Two walls at one height lie nearly in one vertical plane, so they follow the mirror path.
        var (r2, t2) = LookAt(new[] { 0.0, 0, 1 }, new[] { 3.5, 1.5, 1 });
        var walls = Flatten(TagCorners(4, 0.5, 1, Math.PI).Concat(TagCorners(3, 2.5, 1, -Math.PI / 2)).ToArray());
        var wallPose = AprilTagCovariance.Estimate(Project(walls, r2, t2), walls, 2, Intrinsics, Quaternion(r2), t2, 1.0, 12);
        check(wallPose != null && wallPose.SolutionCount > 1, "Nearly coplanar tags follow the mirror path");

        var flat = new double[] { 640, 640, 641, 640, 641, 641, 640, 641 };
        var degenerate = AprilTagCovariance.Estimate(flat, new double[12], 1, Intrinsics, new[] { 1.0, 0, 0, 0 }, new[] { 0.0, 0, 2 }, 1.0, 12);
        check(degenerate == null, "Corners that don't constrain the pose have no covariance");

        bool threw = false;
        try { AprilTagCovariance.Estimate(image, box, 3, Intrinsics, Quaternion(r), t, 0, 12); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        check(threw, "Corner noise must be positive");

        // The camera pose comes back in WPILib camera axes: X along the optical axis.
        double[] forward = { clean.Rotation[0], clean.Rotation[3], clean.Rotation[6] };
        double[] left = { clean.Rotation[1], clean.Rotation[4], clean.Rotation[7] };
        check(Close(clean.Translation, new[] { 0.0, 0, 1 }, 1e-9), "Camera position in the field");
        check(Close(forward, new[] { r[6], r[7], r[8] }, 1e-12) && Close(left, new[] { -r[0], -r[1], -r[2] }, 1e-12),
            "Camera axes are WPILib's: X forward, Y left");
    }

    static void CheckObservationSelection(Action<bool, string> check)
    {
        var (r, t) = LookAt(new[] { 0.0, 0, 1 }, new[] { 3.0, 0, 1 });
        var field = Flatten(TagCorners(3, -0.5, 1, Math.PI)
            .Concat(TagCorners(3, 0, 1, Math.PI))
            .Concat(TagCorners(3, 0.5, 1, Math.PI)).ToArray());
        var image = Project(field, r, t);
        // Keep a nonzero planar ambiguity so the rejected off-plane tag cannot mask it as zero.
        image[0] += 0.2;
        image[3] -= 0.15;
        var q = Quaternion(r);
        var cleanSelection = AprilTagCovariance.SelectInliers(image, field, 3, Intrinsics, q, t, 12);
        check(cleanSelection.SourceTagIndices.SequenceEqual(new[] { 0, 1, 2 })
            && Close(cleanSelection.Points2d, image, 0) && Close(cleanSelection.Points3d, field, 0),
            "Clean selection preserves every tag and its original corner order");

        var cleanQuality = AprilTagAmbiguity.Evaluate(image, field, 3, Intrinsics, q, t);
        var cleanCovariance = AprilTagCovariance.Estimate(image, field, 3, Intrinsics, q, t, 1, 12);
        var selectedCovariance = AprilTagCovariance.Estimate(cleanSelection, Intrinsics, q, t, 1);
        check(cleanCovariance != null && selectedCovariance != null
            && Close(selectedCovariance.Covariance, cleanCovariance.Covariance, 1e-15),
            "Already-selected covariance preserves clean public Estimate behavior");

        // Insert the rejected tag between valid ones to check source-index and ambiguity mapping.
        var offPlane = Flatten(TagCorners(4, 0.8, 1.2, Math.PI - 0.4));
        var candidates3d = field.Take(12).Concat(offPlane).Concat(field.Skip(12)).ToArray();
        var candidates2d = image.Take(8).Concat(Project(offPlane, r, t).Select(v => v + 30))
            .Concat(image.Skip(8)).ToArray();
        var selection = AprilTagCovariance.SelectInliers(candidates2d, candidates3d, 4, Intrinsics, q, t, 12);
        check(selection.TagCount == 3 && selection.SourceTagIndices.SequenceEqual(new[] { 0, 2, 3 })
            && Close(selection.Points2d, image, 0) && Close(selection.Points3d, field, 0),
            "Three coplanar tags contribute; a rejected off-plane tag keeps its source index out");
        var selectedQuality = AprilTagAmbiguity.Evaluate(selection.Points2d, selection.Points3d,
            selection.TagCount, Intrinsics, q, t);
        var allCandidateQuality = AprilTagAmbiguity.Evaluate(candidates2d, candidates3d, 4, Intrinsics, q, t);
        check(cleanQuality.Ambiguity > 1e-6 && allCandidateQuality.Ambiguity == 0
            && Math.Abs(selectedQuality.Ambiguity - cleanQuality.Ambiguity) < 1e-15,
            "Rejected off-plane geometry cannot erase the contributing coplanar ambiguity");
        check(Close(selectedQuality.TagAmbiguities, cleanQuality.TagAmbiguities, 1e-15)
            && Math.Abs(selectedQuality.ReprojectionErrorPixels - cleanQuality.ReprojectionErrorPixels) < 1e-15,
            "Per-tag ambiguity order and aggregate reprojection error follow the same selection");
        var withOutlier = AprilTagCovariance.Estimate(selection, Intrinsics, q, t, 1);
        var nativeCamera = PoseCovariance.Linear(r, t, Identity()).Inverse()
            .TransformBy(AprilTagCovariance.OpenCvToWpilibCamera);
        check(withOutlier != null && Close(withOutlier.Covariance, cleanCovariance.Covariance, 1e-15)
            && Close(withOutlier.Translation, nativeCamera.Translation, 1e-12)
            && Close(withOutlier.Rotation, nativeCamera.Rotation, 1e-12),
            "Shared selection preserves covariance and its native-pose first component");

        var rejectedImage = image.Select(v => v + 30).ToArray();
        var rejected = AprilTagCovariance.SelectInliers(rejectedImage, field, 3, Intrinsics, q, t, 12);
        check(rejected.TagCount == 0 && rejected.Points2d.Length == 0 && rejected.Points3d.Length == 0,
            "All rejected tags leave empty source indices and corner arrays");
        check(AprilTagCovariance.Estimate(rejectedImage, field, 3, Intrinsics, q, t, 1, 12) == null
            && AprilTagCovariance.Estimate(rejected, Intrinsics, q, t, 1) == null,
            "All rejected tags produce no covariance");

        var oneBadCornerEach = (double[])image.Clone();
        for (int tag = 0; tag < 3; tag++) oneBadCornerEach[tag * 8] += 30;
        check(AprilTagCovariance.SelectInliers(oneBadCornerEach, field, 3, Intrinsics, q, t, 12).TagCount == 0
            && AprilTagCovariance.Estimate(oneBadCornerEach, field, 3, Intrinsics, q, t, 1, 12) == null,
            "75% point inliers with one bad corner per tag cannot restore rejected tags");

        var invalidCorner = (double[])image.Clone();
        invalidCorner[8] = double.NaN;
        var finite = AprilTagCovariance.SelectInliers(invalidCorner, field, 3, Intrinsics, q, t, 12);
        check(finite.SourceTagIndices.SequenceEqual(new[] { 0, 2 }),
            "A non-finite corner rejects its whole tag without disturbing the others");
    }

    /// <summary>Monte Carlo: errors of a refined solve against the reported covariance.</summary>
    static void CheckCalibration(Action<bool, string> check)
    {
        var eye = new[] { 0.0, 0, 1 };
        // Three walls at different heights: depth breaks the ambiguity, so the covariance is linear.
        Calibrate(check, "three walls", eye, new[] { 2.3, 0, 1 }, ThreeWalls(),
            0.5, 300, 1, solveFromTruth: true, minimumMeanNees: 3, maximumMeanNees: 8);
        // Two walls at one height: nearly one vertical plane.
        Calibrate(check, "two walls", eye, new[] { 3.5, 1.5, 1 },
            TagCorners(4, 0.5, 1, Math.PI).Concat(TagCorners(3, 2.5, 1, -Math.PI / 2)).ToArray(),
            0.5, 300, 5, solveFromTruth: true);
        // A near, oblique tag: one clear solution.
        Calibrate(check, "near oblique tag", eye, new[] { 1.5, 0.3, 1.1 },
            TagCorners(1.5, 0.3, 1.1, Math.PI - 0.7), 0.5, 300, 2, solveFromTruth: false,
            minimumMeanNees: 2, maximumMeanNees: 9);
        // A distant, face-on tag: the two solutions trade places under noise.
        Calibrate(check, "far face-on tag", eye, new[] { 3.5, 0, 1 },
            TagCorners(3.5, 0, 1, Math.PI - 0.05), 0.5, 300, 3, solveFromTruth: false);
        // Two tags on one wall, far and nearly head-on: the mirror path covers the flat valley.
        double yaw = Math.PI - 0.15;
        Calibrate(check, "far coplanar pair", eye, new[] { 6.0, 0, 1 },
            TagCorners(6 + 0.5 * Math.Sin(yaw), -0.5 * Math.Cos(yaw), 1, yaw)
                .Concat(TagCorners(6 - 0.5 * Math.Sin(yaw), 0.5 * Math.Cos(yaw), 1, yaw)).ToArray(),
            0.5, 300, 4, solveFromTruth: false);
    }

    /// <summary>Tags ahead, left, and right of the camera at different heights.</summary>
    static double[][] ThreeWalls() =>
        TagCorners(3, 0, 1.5, Math.PI)
            .Concat(TagCorners(2, 1.2, 0.5, -Math.PI / 2))
            .Concat(TagCorners(2, -1.2, 1.0, Math.PI / 2))
            .ToArray();

    static void Calibrate(Action<bool, string> check, string name, double[] eye, double[] target,
        double[][] corners, double sigma, int trials, int seed, bool solveFromTruth,
        double minimumMeanNees = 0, double maximumMeanNees = double.PositiveInfinity)
    {
        var (r, t) = LookAt(eye, target);
        var field = Flatten(corners);
        int count = corners.Length;
        var truth = PoseCovariance.Linear(r, t, Identity()).Inverse();
        var random = new Random(seed);
        int outside = 0;
        int linearOutside = 0;
        double neesSum = 0;
        int solved = 0;
        for (int trial = 0; trial < trials; trial++)
        {
            var image = Project(field, r, t).Select(v => v + Gaussian(random) * sigma).ToArray();
            if (!TrySolve(image, field, count, solveFromTruth ? (r, t) : ((double[], double[])?)null, out var rotation, out var translation))
                continue;
            solved++;
            var estimate = AprilTagCovariance.Estimate(image, field, count / 4, Intrinsics, Quaternion(rotation), translation, sigma, 12);
            check(estimate != null, $"{name}: covariance is defined");
            // The OpenCV-axes camera pose, to compare with the truth.
            var opencv = estimate.TransformBy(AprilTagCovariance.OpenCvToWpilibCamera.Inverse());
            var error = PoseCovariance.Difference(Component(opencv), Component(truth));
            double nees = Mahalanobis(error, opencv.Covariance);
            neesSum += nees;
            if (nees > Chi2Six95) outside++;

            var linearPose = PoseCovariance.Linear(rotation, translation, LinearCovariance(rotation, translation, image, field, count, sigma)).Inverse();
            if (Mahalanobis(PoseCovariance.Difference(Component(linearPose), Component(truth)), linearPose.Covariance) > Chi2Six95)
                linearOutside++;
        }
        double fraction = outside / (double)solved;
        double linearFraction = linearOutside / (double)solved;
        double meanNees = neesSum / solved;
        Console.WriteLine($"Covariance {name}: {fraction:P1} outside the 95% ellipse "
            + $"(linearized alone {linearFraction:P1}), mean NEES {meanNees:F2} over {solved} solves");
        check(solved > trials * 0.9, $"{name}: the reference solver converges");
        check(fraction <= 0.10, $"{name}: at most 10% of errors fall outside the 95% ellipse ({fraction:P1})");
        check(meanNees >= minimumMeanNees && meanNees <= maximumMeanNees, $"{name}: mean NEES {meanNees:F2} in range");
    }

    /// <summary>
    /// A reference solve like PoseLib's: start from the truth, or from the better-fitting IPPE
    /// solution as a minimal solver would, then refine all six degrees of freedom.
    /// </summary>
    static bool TrySolve(double[] image, double[] field, int count, (double[] R, double[] T)? start,
        out double[] rotation, out double[] translation)
    {
        rotation = translation = null;
        if (start.HasValue)
        {
            rotation = start.Value.R;
            translation = start.Value.T;
        }
        else
        {
            if (!AprilTagAmbiguity.TrySolvePlanar(image, field, 0, count, Intrinsics, out var solutions)) return false;
            double best = double.PositiveInfinity;
            var residuals = new double[count * 2];
            foreach (var solution in solutions)
            {
                if (AprilTagCovariance.Project(solution.Rotation, solution.Translation, image, field, count, Intrinsics, residuals, null, out double ssr)
                    && ssr < best)
                {
                    best = ssr;
                    rotation = solution.Rotation;
                    translation = solution.Translation;
                }
            }
            if (rotation == null) return false;
        }
        var res = new double[count * 2];
        var jacobian = new double[count * 12];
        for (int iteration = 0; iteration < 30; iteration++)
        {
            if (!AprilTagCovariance.Project(rotation, translation, image, field, count, Intrinsics, res, jacobian, out _)) return false;
            var inverse = AprilTagCovariance.CovarianceFromJacobian(jacobian, count * 2, 1.0);
            if (inverse == null) return false;
            var step = new double[6];
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 6; j++)
                    for (int row = 0; row < count * 2; row++)
                        step[i] += inverse[i * 6 + j] * jacobian[row * 6 + j] * res[row];
            Perturbed(rotation, translation, step, out rotation, out translation);
            if (step.Sum(x => x * x) < 1e-24) break;
        }
        return true;
    }

    static double[] LinearCovariance(double[] rotation, double[] translation, double[] image, double[] field, int count, double sigma)
    {
        var res = new double[count * 2];
        var jacobian = new double[count * 12];
        AprilTagCovariance.Project(rotation, translation, image, field, count, Intrinsics, res, jacobian, out _);
        return AprilTagCovariance.CovarianceFromJacobian(jacobian, count * 2, sigma);
    }

    static double Mahalanobis(double[] error, double[] covariance)
    {
        AprilTagCovariance.SymmetricEigen(covariance, 6, out var values, out var vectors);
        double sum = 0;
        for (int k = 0; k < 6; k++)
        {
            double projection = 0;
            for (int i = 0; i < 6; i++) projection += vectors[i * 6 + k] * error[i];
            sum += projection * projection / values[k];
        }
        return sum;
    }

    static PoseCovariance.Component Component(PoseCovariance pose) =>
        new PoseCovariance.Component(pose.Rotation, pose.Translation, pose.Covariance, 1);

    static void Perturbed(double[] r, double[] t, double[] delta, out double[] rotation, out double[] translation)
    {
        rotation = PoseCovariance.MultiplyMatrices(PoseCovariance.Exp(new[] { delta[3], delta[4], delta[5] }), r);
        translation = new[] { t[0] + delta[0], t[1] + delta[1], t[2] + delta[2] };
    }

    static double[] Project(double[] field, double[] r, double[] t)
    {
        int count = field.Length / 3;
        var residuals = new double[count * 2];
        AprilTagCovariance.Project(r, t, new double[count * 2], field, count, Intrinsics, residuals, null, out _);
        return residuals.Select(v => -v).ToArray();
    }

    static double[] RandomCovariance(int seed)
    {
        var random = new Random(seed);
        var a = new double[36];
        for (int i = 0; i < 36; i++) a[i] = (random.NextDouble() - 0.5) * 0.2;
        var c = new double[36];
        for (int r = 0; r < 6; r++)
            for (int col = 0; col < 6; col++)
                for (int k = 0; k < 6; k++) c[r * 6 + col] += a[r * 6 + k] * a[col * 6 + k];
        for (int i = 0; i < 6; i++) c[i * 7] += 0.01;
        return c;
    }

    static double[] Cholesky(double[] a)
    {
        var l = new double[36];
        for (int i = 0; i < 6; i++)
            for (int j = 0; j <= i; j++)
            {
                double sum = a[i * 6 + j];
                for (int k = 0; k < j; k++) sum -= l[i * 6 + k] * l[j * 6 + k];
                l[i * 6 + j] = i == j ? Math.Sqrt(sum) : sum / l[j * 6 + j];
            }
        return l;
    }

    static double[] Identity()
    {
        var m = new double[36];
        for (int i = 0; i < 6; i++) m[i * 7] = 1;
        return m;
    }

    static bool Close(double[] a, double[] b, double tolerance) =>
        a.Length == b.Length && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= tolerance);

    static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    static double[] Flatten(double[][] corners) => corners.SelectMany(c => c).ToArray();

    static double[] Flatten(double[,] m) =>
        new[] { m[0, 0], m[0, 1], m[0, 2], m[1, 0], m[1, 1], m[1, 2], m[2, 0], m[2, 1], m[2, 2] };

    // WPILib tag frame: +X is the printed face normal; corners wrap the face in order.
    static double[][] TagCorners(double x, double y, double z, double normalYaw)
    {
        double h = TagSize / 2;
        var local = new[] { (-h, -h), (h, -h), (h, h), (-h, h) };
        return local.Select(c => new[] {
            x - Math.Sin(normalYaw) * c.Item1, y + Math.Cos(normalYaw) * c.Item1, z + c.Item2 }).ToArray();
    }

    // World-to-camera pose (OpenCV axes: x right, y down, z forward) looking from eye at target.
    static (double[] R, double[] T) LookAt(double[] eye, double[] target)
    {
        double[] f = Normalize(new[] { target[0] - eye[0], target[1] - eye[1], target[2] - eye[2] });
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

    static double[] Cross(double[] a, double[] b) =>
        new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

    static double[] Normalize(double[] a)
    {
        double n = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        return new[] { a[0] / n, a[1] / n, a[2] / n };
    }

    static double[] Quaternion(double[] m)
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
}
