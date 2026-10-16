using System;
using System.Collections.Generic;

namespace QuestX.VioQuality
{
    /// <summary>Plain double 3-vector (no UnityEngine dependency so the estimator can be tested off-device).</summary>
    public readonly struct Vec3d
    {
        public readonly double X,
            Y,
            Z;

        public Vec3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vec3d operator +(Vec3d a, Vec3d b) =>
            new Vec3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vec3d operator -(Vec3d a, Vec3d b) =>
            new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Vec3d operator *(double s, Vec3d a) => new Vec3d(s * a.X, s * a.Y, s * a.Z);

        public double Dot(Vec3d b) => X * b.X + Y * b.Y + Z * b.Z;

        public double SquaredLength => X * X + Y * Y + Z * Z;
    }

    /// <summary>
    /// One environment-depth hit: a surface point and its unit normal, both in the VIO frame (FRC axes), and the
    /// distance from the headset (m), which sets its depth noise.
    /// </summary>
    public readonly struct DepthPoint
    {
        public readonly Vec3d Position;
        public readonly Vec3d Normal;
        public readonly double Range;

        public DepthPoint(Vec3d position, Vec3d normal, double range)
        {
            Position = position;
            Normal = normal;
            Range = range;
        }
    }

    public enum DriftStatus
    {
        /// <summary>A comparison against the keyframe was made; drift fields are valid.</summary>
        Ok = 0,
        NotSupported = 1,
        NotReady = 2,
        NotTracking = 3,
        TooFewHits = 4,

        /// <summary>No comparable keyframe (first sample, epoch change, or keyframe just replaced).</summary>
        NewKeyframe = 5,
        TooFewMatches = 6,
    }

    /// <summary>Result of one keyframe comparison. Drift and standard deviations are in the frame of the input points (FRC axes).</summary>
    public struct DriftResult
    {
        public DriftStatus Status;
        public double BaselineSeconds;
        public int Hits;
        public int Matched;
        public int Inliers;
        public double InlierRatio;
        public Vec3d Drift;
        public Vec3d DriftStdDev;
        public double ResidualRms;
    }

    /// <summary>
    /// Measures Quest VIO error from environment depth.
    ///
    /// Depth hits are placed in the local tracking frame using the headset's own VIO pose. A static surface therefore
    /// stays in the same place in that frame unless VIO is wrong: if VIO's reported displacement since a keyframe is
    /// off by e, every static surface appears shifted by e. The estimator matches the current hits to the keyframe's
    /// hits (nearest point, point-to-plane, re-matched ICP style), solves for that common translation by weighted
    /// robust least squares with a per-point noise model, and reports it with a covariance from the plane geometry.
    /// Directions the visible surfaces cannot constrain (for example horizontal motion when only the floor is
    /// visible) are reported as zero drift with a large standard deviation instead of a fake small error.
    /// Moving objects (other robots) are rejected as outliers; the inlier ratio says how much of the view agreed.
    ///
    /// tests/vio-quality/depth_drift_mirror.py is a line-for-line Python mirror used for the synthetic checks; keep
    /// the two in sync.
    /// </summary>
    public sealed class DepthDriftEstimator
    {
        public sealed class Settings
        {
            /// <summary>Minimum hits for a cloud to be used as a keyframe or compared.</summary>
            public int MinHits = 40;

            /// <summary>Minimum matched points for a comparison.</summary>
            public int MinMatches = 30;

            /// <summary>Nearest-keyframe-point search radius (m).</summary>
            public double MatchRadius = 0.30;

            /// <summary>Minimum |dot| between current and keyframe normals for a match.</summary>
            public double MinNormalAgreement = 0.7;

            /// <summary>Per-point depth noise = base + perMeter * range (m).</summary>
            public double DepthNoiseBase = 0.005;
            public double DepthNoisePerMeter = 0.01;

            /// <summary>Normal direction noise (rad); multiplied by the in-plane offset between matched points.</summary>
            public double NormalNoiseRad = 0.03;

            /// <summary>Inlier gate in per-point standard deviations (after the first, looser pass).</summary>
            public double InlierSigmas = 3.0;

            /// <summary>Minimum first-pass inlier gate (m) so an initial offset is not rejected wholesale.</summary>
            public double InitialInlierThreshold = 0.10;

            /// <summary>
            /// A direction is observable when its information exceeds MinObservableCount points at ReferenceSigma,
            /// i.e. eigenvalue > MinObservableCount / ReferenceSigma^2. Unobservable directions get zero drift and
            /// UnobservableStd, so noise in nearly-parallel normals cannot invent a confident horizontal error.
            /// </summary>
            public double ReferenceSigma = 0.03;
            public double MinObservableCount = 3.0;
            public double UnobservableStd = 2.0;

            /// <summary>Floor on reported per-axis standard deviation (m): sparse sampling errors are correlated.</summary>
            public double MinStd = 0.01;

            /// <summary>Correspondence rounds (re-match with the current drift estimate, ICP style) and solve iterations per round.</summary>
            public int MatchRounds = 3;
            public int Iterations = 4;

            /// <summary>Replace the keyframe after this long (s), so each report covers a bounded baseline.</summary>
            public double KeyframeMaxAge = 1.0;

            /// <summary>Replace the keyframe when fewer than this fraction of hits matched it (view changed).</summary>
            public double KeyframeMinMatchRatio = 0.5;
        }

        private readonly Settings settings;
        private List<DepthPoint> keyframe;
        private double keyframeTime;
        private uint keyframeEpoch;

        private readonly List<Vec3d> matchNormals = new List<Vec3d>();
        private readonly List<double> matchResiduals = new List<double>();
        private readonly List<double> matchVariances = new List<double>();

        public DepthDriftEstimator(Settings settings = null)
        {
            this.settings = settings ?? new Settings();
        }

        public void Reset()
        {
            keyframe = null;
        }

        /// <summary>Process one cloud captured at <paramref name="time"/> (s). <paramref name="tracking"/> is the headset position-tracked state.</summary>
        public DriftResult Update(
            IReadOnlyList<DepthPoint> points,
            double time,
            bool tracking,
            uint epoch
        )
        {
            var result = new DriftResult { Hits = points?.Count ?? 0 };
            if (!tracking)
            {
                keyframe = null;
                result.Status = DriftStatus.NotTracking;
                return result;
            }
            if (result.Hits < settings.MinHits)
            {
                result.Status = DriftStatus.TooFewHits;
                return result;
            }
            if (keyframe == null || epoch != keyframeEpoch || time <= keyframeTime)
            {
                SetKeyframe(points, time, epoch);
                result.Status = DriftStatus.NewKeyframe;
                return result;
            }

            result.BaselineSeconds = time - keyframeTime;
            Compare(points, ref result);

            bool viewChanged = result.Matched < settings.KeyframeMinMatchRatio * result.Hits;
            if (result.BaselineSeconds >= settings.KeyframeMaxAge || viewChanged)
            {
                SetKeyframe(points, time, epoch);
            }
            return result;
        }

        private void SetKeyframe(IReadOnlyList<DepthPoint> points, double time, uint epoch)
        {
            keyframe = new List<DepthPoint>(points);
            keyframeTime = time;
            keyframeEpoch = epoch;
        }

        /// <summary>Nearest keyframe point for each current point shifted by -d; residual r = n . (p - q) and its variance.</summary>
        private void Match(IReadOnlyList<DepthPoint> points, Vec3d d)
        {
            matchNormals.Clear();
            matchResiduals.Clear();
            matchVariances.Clear();
            double radius2 = settings.MatchRadius * settings.MatchRadius;
            foreach (var p in points)
            {
                var ps = p.Position - d;
                int best = -1;
                double bestD2 = radius2;
                for (int k = 0; k < keyframe.Count; k++)
                {
                    double d2 = (ps - keyframe[k].Position).SquaredLength;
                    if (d2 <= bestD2)
                    {
                        bestD2 = d2;
                        best = k;
                    }
                }
                if (best < 0)
                    continue;
                var q = keyframe[best];
                if (Math.Abs(p.Normal.Dot(q.Normal)) < settings.MinNormalAgreement)
                    continue;
                var off = ps - q.Position;
                double along = q.Normal.Dot(off);
                double tangential2 = Math.Max(0, off.SquaredLength - along * along);
                double depthSigma =
                    settings.DepthNoiseBase
                    + settings.DepthNoisePerMeter * Math.Max(p.Range, q.Range);
                double variance =
                    depthSigma * depthSigma
                    + settings.NormalNoiseRad * settings.NormalNoiseRad * tangential2;
                matchNormals.Add(q.Normal);
                matchResiduals.Add(q.Normal.Dot(p.Position - q.Position));
                matchVariances.Add(variance);
            }
        }

        private void Compare(IReadOnlyList<DepthPoint> points, ref DriftResult result)
        {
            var d = new Vec3d(0, 0, 0);
            double chi2 = 1,
                rms = 0;
            int inliers = 0,
                matched = 0;
            double[] h = new double[9];
            double[] values = null;
            double[,] vectors = null;
            var observable = new bool[3];
            double threshold =
                settings.MinObservableCount / (settings.ReferenceSigma * settings.ReferenceSigma);

            for (int round = 0; round < settings.MatchRounds; round++)
            {
                Match(points, d);
                matched = matchNormals.Count;
                if (matched < settings.MinMatches)
                    break;
                bool first = true;
                for (int iter = 0; iter < settings.Iterations; iter++)
                {
                    Array.Clear(h, 0, 9);
                    var b = new Vec3d(0, 0, 0);
                    inliers = 0;
                    for (int i = 0; i < matched; i++)
                    {
                        var n = matchNormals[i];
                        double s2 = matchVariances[i];
                        double e = matchResiduals[i] - n.Dot(d);
                        double gate = first
                            ? Math.Max(
                                settings.InitialInlierThreshold,
                                settings.InlierSigmas * Math.Sqrt(s2)
                            )
                            : settings.InlierSigmas * Math.Sqrt(s2 * chi2);
                        if (Math.Abs(e) > gate)
                            continue;
                        inliers++;
                        double w = 1.0 / s2;
                        AddOuter(h, n, w);
                        b = b + (w * matchResiduals[i]) * n;
                    }
                    first = false;
                    if (inliers < settings.MinMatches)
                        break;

                    // Solve only along observable eigen-directions of the information matrix.
                    EigenSymmetric3(h, out values, out vectors);
                    var next = new Vec3d(0, 0, 0);
                    for (int k = 0; k < 3; k++)
                    {
                        observable[k] = values[k] > threshold;
                        if (!observable[k])
                            continue;
                        var v = new Vec3d(vectors[0, k], vectors[1, k], vectors[2, k]);
                        next = next + (v.Dot(b) / values[k]) * v;
                    }
                    d = next;

                    double sumW = 0,
                        sumE2 = 0;
                    int count = 0;
                    for (int i = 0; i < matched; i++)
                    {
                        double s2 = matchVariances[i];
                        double e = matchResiduals[i] - matchNormals[i].Dot(d);
                        if (Math.Abs(e) <= settings.InlierSigmas * Math.Sqrt(s2 * chi2))
                        {
                            sumW += e * e / s2;
                            sumE2 += e * e;
                            count++;
                        }
                    }
                    chi2 = count > 3 ? Math.Max(1.0, sumW / (count - 3)) : 1.0;
                    rms = count > 0 ? Math.Sqrt(sumE2 / count) : 0;
                }
            }

            result.Matched = matched;
            if (matched < settings.MinMatches)
            {
                result.Status = DriftStatus.TooFewMatches;
                return;
            }
            result.Inliers = inliers;
            result.InlierRatio = (double)inliers / matched;
            if (inliers < settings.MinMatches || values == null)
            {
                result.Status = DriftStatus.TooFewMatches;
                return;
            }

            // Covariance = chi2 * V diag(1/lambda) V^T over observable directions, UnobservableStd^2 elsewhere.
            var cov = new double[9];
            for (int k = 0; k < 3; k++)
            {
                double variance = observable[k]
                    ? chi2 / values[k]
                    : settings.UnobservableStd * settings.UnobservableStd;
                for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    cov[row * 3 + col] += variance * vectors[row, k] * vectors[col, k];
            }
            double floor2 = settings.MinStd * settings.MinStd;
            result.Drift = d;
            result.DriftStdDev = new Vec3d(
                Math.Sqrt(cov[0] + floor2),
                Math.Sqrt(cov[4] + floor2),
                Math.Sqrt(cov[8] + floor2)
            );
            result.ResidualRms = rms;
            result.Status = DriftStatus.Ok;
        }

        private static void AddOuter(double[] h, Vec3d n, double w)
        {
            h[0] += w * n.X * n.X;
            h[1] += w * n.X * n.Y;
            h[2] += w * n.X * n.Z;
            h[4] += w * n.Y * n.Y;
            h[5] += w * n.Y * n.Z;
            h[8] += w * n.Z * n.Z;
            h[3] = h[1];
            h[6] = h[2];
            h[7] = h[5];
        }

        /// <summary>Cyclic Jacobi eigen-decomposition of a symmetric 3x3 (row-major). Eigenvectors are the columns of <paramref name="v"/>.</summary>
        internal static void EigenSymmetric3(double[] h, out double[] values, out double[,] v)
        {
            var a = new double[3, 3]
            {
                { h[0], h[1], h[2] },
                { h[3], h[4], h[5] },
                { h[6], h[7], h[8] },
            };
            v = new double[3, 3]
            {
                { 1, 0, 0 },
                { 0, 1, 0 },
                { 0, 0, 1 },
            };
            int[,] pairs =
            {
                { 0, 1 },
                { 0, 2 },
                { 1, 2 },
            };
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
                if (off < 1e-30)
                    break;
                for (int pi = 0; pi < 3; pi++)
                {
                    int p = pairs[pi, 0],
                        q = pairs[pi, 1];
                    if (Math.Abs(a[p, q]) < 1e-300)
                        continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t =
                        (theta >= 0 ? 1.0 : -1.0)
                        / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1),
                        s = t * c;
                    for (int k = 0; k < 3; k++)
                    {
                        double akp = a[k, p],
                            akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double apk = a[p, k],
                            aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p],
                            vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
            }
            values = new[] { a[0, 0], a[1, 1], a[2, 2] };
        }
    }
}
