using System;
using System.Collections.Generic;
using QuestX.Network;
using QuestX.VioQuality;

/// <summary>Checks the depth drift estimator on noise-free planes and the /v2/vioQuality layout.</summary>
static class VioQualityChecks
{
    static readonly Vec3d Eye = new Vec3d(0, 0, 0.5);

    /// <summary>A 0.25 m grid on the floor and, optionally, a front wall (x = 3) and a left wall (y = 2).</summary>
    static List<DepthPoint> Scene(Vec3d vioError, bool walls)
    {
        var points = new List<DepthPoint>();
        void Add(double x, double y, double z, Vec3d normal)
        {
            var p = new Vec3d(x, y, z);
            points.Add(new DepthPoint(p + vioError, normal, Math.Sqrt((p - Eye).SquaredLength)));
        }
        for (double x = 0.5; x <= 2.51; x += 0.25)
            for (double y = -1.5; y <= 1.51; y += 0.25)
                Add(x, y, 0, new Vec3d(0, 0, 1));
        if (!walls)
            return points;
        for (double y = -1.5; y <= 1.51; y += 0.25)
            for (double z = 0.25; z <= 1.51; z += 0.25)
                Add(3, y, z, new Vec3d(-1, 0, 0));
        for (double x = 0.5; x <= 2.51; x += 0.25)
            for (double z = 0.25; z <= 1.51; z += 0.25)
                Add(x, 2, z, new Vec3d(0, -1, 0));
        return points;
    }

    public static void Run(Action<bool, string> check)
    {
        var zero = new Vec3d(0, 0, 0);
        var error = new Vec3d(0.04, -0.03, 0.01);

        var estimator = new DepthDriftEstimator();
        check(estimator.Update(Scene(zero, true), 0, true, 0).Status == DriftStatus.NewKeyframe,
            "VIO quality: first cloud becomes the keyframe");
        var rich = estimator.Update(Scene(error, true), 0.1, true, 0);
        var miss = rich.Drift - error;
        check(rich.Status == DriftStatus.Ok, "VIO quality: walls and floor give a drift report");
        check(Math.Sqrt(miss.SquaredLength) < 0.005, "VIO quality: drift recovers the VIO error within 5 mm");
        check(rich.DriftStdDev.X < 0.05 && rich.DriftStdDev.Y < 0.05 && rich.DriftStdDev.Z < 0.05,
            "VIO quality: every axis is constrained by walls and floor");
        check(Math.Abs(rich.BaselineSeconds - 0.1) < 1e-12, "VIO quality: baseline is the keyframe age");
        check(estimator.Update(Scene(error, true), 0.2, true, 1).Status == DriftStatus.NewKeyframe,
            "VIO quality: a new tracking epoch restarts the keyframe");
        check(estimator.Update(Scene(error, true), 0.3, false, 1).Status == DriftStatus.NotTracking,
            "VIO quality: not tracking drops the keyframe");

        var floor = new DepthDriftEstimator();
        floor.Update(Scene(zero, false), 0, true, 0);
        var flat = floor.Update(Scene(error, false), 0.1, true, 0);
        check(flat.Status == DriftStatus.Ok, "VIO quality: floor alone still reports");
        check(flat.DriftStdDev.X > 1 && flat.DriftStdDev.Y > 1 && flat.Drift.X == 0 && flat.Drift.Y == 0,
            "VIO quality: horizontal drift is unobservable from the floor alone");
        check(Math.Abs(flat.Drift.Z - error.Z) < 0.005 && flat.DriftStdDev.Z < 0.05,
            "VIO quality: vertical drift is observable from the floor");

        var sample = new VioQualitySample
        {
            Drift = rich,
            TrackingEpoch = 3,
            PositionTracked = true,
            PositionValid = true,
            OrientationTracked = true,
            AngularSpeedRadPerSec = 0.5,
            LinearSpeedMetersPerSec = 1.25,
            Rays = 160,
            NearFraction = 0.125,
        };
        var wire = new double[MeasurementWireFormat.VioQualityLength];
        MeasurementWireFormat.WriteVioQuality(wire, (1UL << 32) + 7, 987654321, 4.5, sample);
        check(wire.Length == 25, "vioQuality is 25 doubles");
        check(wire[0] == 7 && wire[1] == 1 && wire[2] == 987654321 && wire[3] == 0 && wire[4] == 4.5,
            "vioQuality sequence, capture time, and app time words");
        check(wire[5] == 0 && wire[6] == 3 && wire[7] == 1 && wire[8] == 1 && wire[9] == 1,
            "vioQuality status, epoch, and tracking flags");
        check(wire[10] == 0.5 && wire[11] == 1.25 && wire[13] == 160 && wire[15] == 0.125,
            "vioQuality head speeds, rays, and near fraction");
        check(wire[12] == rich.BaselineSeconds && wire[14] == rich.Hits && wire[16] == rich.Matched
            && wire[17] == rich.InlierRatio, "vioQuality baseline, hits, matches, and inlier ratio");
        check(wire[18] == rich.Drift.X && wire[19] == rich.Drift.Y && wire[20] == rich.Drift.Z
            && wire[21] == rich.DriftStdDev.X && wire[22] == rich.DriftStdDev.Y && wire[23] == rich.DriftStdDev.Z
            && wire[24] == rich.ResidualRms, "vioQuality drift, standard deviation, and RMS");
    }
}
