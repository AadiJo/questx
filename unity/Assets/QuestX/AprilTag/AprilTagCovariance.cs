using System;
using System.Collections.Generic;
using QuestX.QuestX.Geometry;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>
    /// Kalman-ready covariance of an AprilTag camera pose, ported from Napa's pose solvers
    /// (zwatx/NAPA <c>covariance.py</c> and <c>solvers.py</c> at c5f25e2).
    /// </summary>
    /// <remarks>
    /// Each corner coordinate is modelled as independent Gaussian noise of
    /// <c>cornerNoisePixels</c>. The covariance starts from the Gauss-Newton approximation
    /// <c>sigma^2 (J^T J)^-1</c>, where J is the Jacobian of the projected corners with respect to
    /// the pose: the linearized Cramer-Rao bound. Two adjustments make it safe for a filter, as
    /// Napa's Monte Carlo tests established:
    ///
    /// - Ambiguity. A planar target has two poses that fit its corners. One tag is described by
    ///   both IPPE solutions, weighted by their tempered corner likelihood
    ///   <c>exp(-SSR / (2 T sigma^2))</c> with T = 2. Several tags on (nearly) one plane step along
    ///   the rotation from the solved pose toward the plane's mirror solution, re-solve the other
    ///   five degrees of freedom at each step, and weight the steps by likelihood. Tags at clearly
    ///   different depths break the ambiguity and keep the linearized covariance.
    /// - Curved error surfaces. <see cref="PoseCovariance.CorrelationSlack"/> of each variance is
    ///   added back to the diagonal, capping every correlation at 0.91.
    ///
    /// The camera is the pinhole the solver uses. This class has no Unity dependencies so it can be
    /// checked outside the editor.
    /// </remarks>
    public static class AprilTagCovariance
    {
        /// <summary>
        /// Napa's default corner noise. Deliberately conservative: motion blur, rolling shutter, and
        /// calibration error all look like corner noise to the solver.
        /// </summary>
        public const double DefaultCornerNoisePixels = 1.0;

        /// <summary>Tempering of a single tag's two solution likelihoods.</summary>
        public const double LikelihoodTempering = 2.0;

        /// <summary>Mirror-path steps are re-solved against the true corners, so they need less.</summary>
        public const double MirrorPathTempering = 1.0;

        /// <summary>
        /// The mirror path is skipped when the mirror is both this many standard deviations away
        /// along its rotation and fits <see cref="MirrorSkipSsr"/> sigma^2 worse.
        /// </summary>
        public const double MirrorSkipSigmas = 30.0;

        /// <summary>See <see cref="MirrorSkipSigmas"/>.</summary>
        public const double MirrorSkipSsr = 100.0;

        /// <summary>
        /// Fractions of the rotation from the solved pose (0) to the mirror solution (1), the
        /// solved pose first.
        /// </summary>
        private static readonly double[] MirrorPathSteps =
        {
            0.0,
            -0.25,
            0.25,
            0.5,
            0.75,
            1.0,
            1.25,
        };

        private const int MirrorPathIterations = 3;

        /// <summary>Corners closer than this to the camera plane can't be projected meaningfully.</summary>
        private const double MinimumDepthMeters = 1e-3;

        /// <summary>
        /// Points whose smallest singular value about their centroid exceeds this fraction of the
        /// largest have depth enough to break the planar ambiguity.
        /// </summary>
        private const double PlanarSingularValueRatio = 0.25;

        /// <summary>
        /// Re-expresses a pose of the OpenCV camera frame (x right, y down, z forward) as one of the
        /// WPILib camera frame (X forward, Y left, Z up). A pure rotation of the child frame, so it
        /// leaves the covariance unchanged.
        /// </summary>
        public static readonly Transform3d OpenCvToWpilibCamera = new Transform3d(
            new Translation3d(),
            new Rotation3d(new Quaternion(0.5, 0.5, -0.5, 0.5))
        );

        /// <summary>
        /// Covariance of a solved camera pose: the camera's pose in the field, in WPILib camera axes
        /// (X forward along the optical axis, Y left, Z up). Returns null when the corners don't
        /// constrain all six degrees of freedom.
        /// </summary>
        /// <param name="points2d">Pixel corners [x0, y0, x1, y1, ...], four per tag</param>
        /// <param name="points3d">Field corners [x0, y0, z0, ...], matching <paramref name="points2d"/></param>
        /// <param name="tagCount">Number of tags; corners 4i through 4i+3 belong to tag i</param>
        /// <param name="intrinsics">Pinhole intrinsics [fx, fy, cx, cy] in pixels</param>
        /// <param name="rotationWxyz">Solved world-to-camera rotation quaternion [w, x, y, z]</param>
        /// <param name="translation">Solved world-to-camera translation [x, y, z]</param>
        /// <param name="cornerNoisePixels">Standard deviation of each corner coordinate</param>
        /// <param name="inlierThresholdPixels">
        /// Tags with a corner farther than this from its reprojection are left out, as the solver's
        /// robust fit left them out. If no complete tag fits, returns null.
        /// </param>
        public static PoseCovariance Estimate(
            double[] points2d,
            double[] points3d,
            int tagCount,
            double[] intrinsics,
            double[] rotationWxyz,
            double[] translation,
            double cornerNoisePixels,
            double inlierThresholdPixels
        )
        {
            var selection = SelectInliers(
                points2d,
                points3d,
                tagCount,
                intrinsics,
                rotationWxyz,
                translation,
                inlierThresholdPixels
            );
            return Estimate(selection, intrinsics, rotationWxyz, translation, cornerNoisePixels);
        }

        /// <summary>
        /// Complete tags accepted at the native solved pose, in source order. Quality and
        /// covariance use these same packed corners; source indices identify published detections.
        /// </summary>
        internal sealed class InlierSelection
        {
            public int[] SourceTagIndices { get; }
            public double[] Points2d { get; }
            public double[] Points3d { get; }
            public int TagCount => SourceTagIndices.Length;

            public InlierSelection(int[] sourceTagIndices, double[] points2d, double[] points3d)
            {
                SourceTagIndices = sourceTagIndices;
                Points2d = points2d;
                Points3d = points3d;
            }
        }

        /// <summary>Estimates uncertainty without selecting the accepted tags a second time.</summary>
        internal static PoseCovariance Estimate(
            InlierSelection selection,
            double[] intrinsics,
            double[] rotationWxyz,
            double[] translation,
            double cornerNoisePixels
        )
        {
            if (!(cornerNoisePixels > 0) || double.IsInfinity(cornerNoisePixels))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cornerNoisePixels),
                    "must be finite and greater than zero"
                );
            }
            int kept = selection.TagCount;
            if (kept == 0)
            {
                return null;
            }
            double[] rotation = AprilTagAmbiguity.QuaternionToMatrix(rotationWxyz);
            var start = new double[] { translation[0], translation[1], translation[2] };
            double[] image = selection.Points2d;
            double[] field = selection.Points3d;
            PoseCovariance cameraFromField =
                kept == 1
                    ? SingleTag(rotation, start, image, field, intrinsics, cornerNoisePixels)
                    : MultiTag(
                        rotation,
                        start,
                        image,
                        field,
                        kept * 4,
                        intrinsics,
                        cornerNoisePixels
                    );
            return cameraFromField?.Inverse().TransformBy(OpenCvToWpilibCamera);
        }

        /// <summary>
        /// Keeps a tag only when all four corners project in front of the solved camera and each
        /// residual is within the native solver's pixel threshold. An empty selection stays empty.
        /// </summary>
        internal static InlierSelection SelectInliers(
            double[] points2d,
            double[] points3d,
            int tagCount,
            double[] intrinsics,
            double[] rotationWxyz,
            double[] translation,
            double thresholdPixels
        )
        {
            int pointCount = tagCount * 4;
            if (
                tagCount <= 0
                || points2d == null
                || points3d == null
                || points2d.Length < pointCount * 2
                || points3d.Length < pointCount * 3
            )
            {
                throw new ArgumentException("Expected four 2D and 3D corners per tag");
            }
            double[] rotation = AprilTagAmbiguity.QuaternionToMatrix(rotationWxyz);
            var tags = new List<int>(tagCount);
            var residuals = new double[8];
            for (int tag = 0; tag < tagCount; tag++)
            {
                var tagImage = new double[8];
                var tagField = new double[12];
                Array.Copy(points2d, tag * 8, tagImage, 0, 8);
                Array.Copy(points3d, tag * 12, tagField, 0, 12);
                if (
                    !Project(
                        rotation,
                        translation,
                        tagImage,
                        tagField,
                        4,
                        intrinsics,
                        residuals,
                        null,
                        out _
                    )
                )
                {
                    continue;
                }
                bool fits = true;
                for (int i = 0; i < 4; i++)
                {
                    double du = residuals[i * 2];
                    double dv = residuals[i * 2 + 1];
                    fits &= du * du + dv * dv <= thresholdPixels * thresholdPixels;
                }
                if (fits)
                {
                    tags.Add(tag);
                }
            }
            int kept = tags.Count;
            var image = new double[kept * 8];
            var field = new double[kept * 12];
            for (int i = 0; i < kept; i++)
            {
                Array.Copy(points2d, tags[i] * 8, image, i * 8, 8);
                Array.Copy(points3d, tags[i] * 12, field, i * 12, 12);
            }
            return new InlierSelection(tags.ToArray(), image, field);
        }

        /// <summary>The solved pose and the tag's other IPPE solution, each weighted by its fit.</summary>
        private static PoseCovariance SingleTag(
            double[] rotation,
            double[] translation,
            double[] image,
            double[] field,
            double[] intrinsics,
            double noise
        )
        {
            var residuals = new double[8];
            var jacobian = new double[8 * 6];
            if (
                !Project(
                    rotation,
                    translation,
                    image,
                    field,
                    4,
                    intrinsics,
                    residuals,
                    jacobian,
                    out double ssr
                )
            )
            {
                return null;
            }
            double[] covariance = CovarianceFromJacobian(jacobian, 8, noise);
            if (covariance == null)
            {
                return null;
            }

            var poses = new List<(double[] R, double[] T, double[] C, double Ssr)>
            {
                (rotation, translation, covariance, ssr),
            };
            if (
                TryFarthestSolution(
                    rotation,
                    image,
                    field,
                    field,
                    4,
                    intrinsics,
                    out var alternative
                )
                && Project(
                    alternative.Rotation,
                    alternative.Translation,
                    image,
                    field,
                    4,
                    intrinsics,
                    residuals,
                    jacobian,
                    out double alternativeSsr
                )
            )
            {
                double[] alternativeCovariance = CovarianceFromJacobian(jacobian, 8, noise);
                if (alternativeCovariance != null)
                {
                    poses.Add(
                        (
                            alternative.Rotation,
                            alternative.Translation,
                            alternativeCovariance,
                            alternativeSsr
                        )
                    );
                }
            }
            return Mixture(poses, noise, LikelihoodTempering);
        }

        /// <summary>
        /// Several tags: a linearized covariance, or one that follows the error surface toward a
        /// (nearly) planar target's mirror solution.
        /// </summary>
        private static PoseCovariance MultiTag(
            double[] rotation,
            double[] translation,
            double[] image,
            double[] field,
            int count,
            double[] intrinsics,
            double noise
        )
        {
            var residuals = new double[count * 2];
            var jacobian = new double[count * 2 * 6];
            if (
                !Project(
                    rotation,
                    translation,
                    image,
                    field,
                    count,
                    intrinsics,
                    residuals,
                    jacobian,
                    out double startSsr
                )
            )
            {
                return null;
            }
            double[] linear = CovarianceFromJacobian(jacobian, count * 2, noise);
            if (linear == null)
            {
                return null;
            }
            if (
                !TryFlattenToPlane(field, count, out double[] flattened)
                || !TryFarthestSolution(
                    rotation,
                    image,
                    flattened,
                    field,
                    count,
                    intrinsics,
                    out var mirror
                )
            )
            {
                return PoseCovariance.Linear(rotation, translation, linear);
            }
            return MirrorPath(
                rotation,
                translation,
                linear,
                startSsr,
                mirror,
                image,
                field,
                count,
                intrinsics,
                noise
            );
        }

        /// <summary>
        /// Steps along the rotation from the solved pose to <paramref name="mirror"/>, re-solving the
        /// other five degrees of freedom against the true corners at each step, and weights each
        /// step by its corner likelihood. That covers both a separate mirror minimum and the long,
        /// flat valley a wall of tags has when seen nearly head-on.
        /// </summary>
        private static PoseCovariance MirrorPath(
            double[] rotation,
            double[] translation,
            double[] linear,
            double startSsr,
            AprilTagAmbiguity.CameraPose mirror,
            double[] image,
            double[] field,
            int count,
            double[] intrinsics,
            double noise
        )
        {
            var start = new PoseCovariance.Component(rotation, translation, linear, 1.0);
            double[] direction = PoseCovariance.Difference(
                start,
                new PoseCovariance.Component(mirror.Rotation, mirror.Translation, linear, 1.0)
            );
            double[] axis = { direction[3], direction[4], direction[5] };
            double angle = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
            if (angle < 1e-9)
            {
                return PoseCovariance.Linear(rotation, translation, linear);
            }

            int rows = count * 2;
            var scratch = new double[rows];
            Project(
                mirror.Rotation,
                mirror.Translation,
                image,
                field,
                count,
                intrinsics,
                scratch,
                null,
                out double mirrorSsr
            );
            double axisVariance = 0;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    axisVariance += axis[r] * linear[(r + 3) * 6 + c + 3] * axis[c];
                }
            }
            axisVariance /= angle * angle;
            if (
                angle > MirrorSkipSigmas * Math.Sqrt(axisVariance)
                && mirrorSsr - startSsr > MirrorSkipSsr * noise * noise
            )
            {
                return PoseCovariance.Linear(rotation, translation, linear);
            }

            // Free directions: all of translation, and rotation about the two axes perpendicular to
            // the path's. Columns of the 6x5 basis.
            double[] unit = { axis[0] / angle, axis[1] / angle, axis[2] / angle };
            PerpendicularAxes(unit, out double[] first, out double[] second);
            var free = new double[6 * 5];
            for (int i = 0; i < 3; i++)
            {
                free[i * 5 + i] = 1;
                free[(i + 3) * 5 + 3] = first[i];
                free[(i + 3) * 5 + 4] = second[i];
            }

            int steps = MirrorPathSteps.Length;
            var rotations = new double[steps][];
            var translations = new double[steps][];
            var errors = new double[steps];
            var stepResiduals = new double[steps][];
            var jacobians = new double[steps][];
            for (int s = 0; s < steps; s++)
            {
                double step = MirrorPathSteps[s];
                rotations[s] = PoseCovariance.MultiplyMatrices(
                    PoseCovariance.Exp(new[] { step * axis[0], step * axis[1], step * axis[2] }),
                    rotation
                );
                translations[s] = new[]
                {
                    translation[0] + step * direction[0],
                    translation[1] + step * direction[1],
                    translation[2] + step * direction[2],
                };
                stepResiduals[s] = new double[rows];
                jacobians[s] = new double[rows * 6];
                if (
                    !Project(
                        rotations[s],
                        translations[s],
                        image,
                        field,
                        count,
                        intrinsics,
                        stepResiduals[s],
                        jacobians[s],
                        out errors[s]
                    )
                )
                {
                    errors[s] = double.PositiveInfinity;
                }
            }
            if (!double.IsFinite(errors[0]))
            {
                return PoseCovariance.Linear(rotation, translation, linear);
            }

            // Damped Gauss-Newton on the free directions; the solved pose stays fixed.
            var active = new bool[steps];
            for (int s = 1; s < steps; s++)
            {
                active[s] = double.IsFinite(errors[s]);
            }
            var candidateResiduals = new double[rows];
            var candidateJacobian = new double[rows * 6];
            for (int iteration = 0; iteration < MirrorPathIterations; iteration++)
            {
                for (int s = 1; s < steps; s++)
                {
                    if (!active[s])
                    {
                        continue;
                    }
                    double[] update = FreeUpdate(jacobians[s], stepResiduals[s], free, rows);
                    bool improved = false;
                    for (double scale = 1.0; scale >= 0.25 && update != null; scale /= 2)
                    {
                        var delta = new double[6];
                        for (int r = 0; r < 6; r++)
                        {
                            for (int c = 0; c < 5; c++)
                            {
                                delta[r] += free[r * 5 + c] * update[c] * scale;
                            }
                        }
                        double[] candidateRotation = PoseCovariance.MultiplyMatrices(
                            PoseCovariance.Exp(new[] { delta[3], delta[4], delta[5] }),
                            rotations[s]
                        );
                        double[] candidateTranslation =
                        {
                            translations[s][0] + delta[0],
                            translations[s][1] + delta[1],
                            translations[s][2] + delta[2],
                        };
                        if (
                            Project(
                                candidateRotation,
                                candidateTranslation,
                                image,
                                field,
                                count,
                                intrinsics,
                                candidateResiduals,
                                candidateJacobian,
                                out double candidateError
                            )
                            && candidateError < errors[s]
                        )
                        {
                            rotations[s] = candidateRotation;
                            translations[s] = candidateTranslation;
                            errors[s] = candidateError;
                            Array.Copy(candidateResiduals, stepResiduals[s], rows);
                            Array.Copy(candidateJacobian, jacobians[s], rows * 6);
                            improved = true;
                            break;
                        }
                    }
                    // A step no damped update improves has converged.
                    active[s] = improved;
                }
            }

            var poses = new List<(double[] R, double[] T, double[] C, double Ssr)>(steps);
            for (int s = 0; s < steps; s++)
            {
                if (!double.IsFinite(errors[s]))
                {
                    continue;
                }
                double[] covariance =
                    s == 0 ? linear : CovarianceFromJacobian(jacobians[s], rows, noise);
                if (covariance != null)
                {
                    poses.Add((rotations[s], translations[s], covariance, errors[s]));
                }
            }
            return Mixture(poses, noise, MirrorPathTempering);
        }

        /// <summary>The damped Gauss-Newton update in the free basis, or null if it is undefined.</summary>
        private static double[] FreeUpdate(
            double[] jacobian,
            double[] residuals,
            double[] free,
            int rows
        )
        {
            var reduced = new double[rows * 5];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < 5; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        sum += jacobian[r * 6 + k] * free[k * 5 + c];
                    }
                    reduced[r * 5 + c] = sum;
                }
            }
            var normal = new double[5, 5];
            var rhs = new double[5];
            double trace = 0;
            for (int i = 0; i < 5; i++)
            {
                for (int j = 0; j < 5; j++)
                {
                    double sum = 0;
                    for (int r = 0; r < rows; r++)
                    {
                        sum += reduced[r * 5 + i] * reduced[r * 5 + j];
                    }
                    normal[i, j] = sum;
                }
                trace += normal[i, i];
                for (int r = 0; r < rows; r++)
                {
                    rhs[i] += reduced[r * 5 + i] * residuals[r];
                }
            }
            // Levenberg-style damping scaled to the step's own information keeps a near-singular
            // step well posed.
            double damping = 1e-9 * trace / 5.0 + 1e-12;
            for (int i = 0; i < 5; i++)
            {
                normal[i, i] += damping;
            }
            return SolveLinear(normal, rhs, 5) ? rhs : null;
        }

        /// <summary>Solutions weighted by tempered likelihood; the first is the reported pose.</summary>
        private static PoseCovariance Mixture(
            List<(double[] R, double[] T, double[] C, double Ssr)> poses,
            double noise,
            double tempering
        )
        {
            double best = double.PositiveInfinity;
            foreach (var pose in poses)
            {
                best = Math.Min(best, pose.Ssr);
            }
            var components = new PoseCovariance.Component[poses.Count];
            for (int i = 0; i < poses.Count; i++)
            {
                double weight = Math.Exp(
                    -(poses[i].Ssr - best) / (2.0 * tempering * noise * noise)
                );
                components[i] = new PoseCovariance.Component(
                    poses[i].R,
                    poses[i].T,
                    poses[i].C,
                    weight
                );
            }
            return new PoseCovariance(components);
        }

        /// <summary>
        /// The IPPE solution of the planar corners <paramref name="planar"/> whose rotation is
        /// farthest from <paramref name="rotation"/>, among those that put every true corner in
        /// <paramref name="field"/> in front of the camera.
        /// </summary>
        private static bool TryFarthestSolution(
            double[] rotation,
            double[] image,
            double[] planar,
            double[] field,
            int count,
            double[] intrinsics,
            out AprilTagAmbiguity.CameraPose farthest
        )
        {
            farthest = default;
            if (
                !AprilTagAmbiguity.TrySolvePlanar(
                    image,
                    planar,
                    0,
                    count,
                    intrinsics,
                    out var solutions
                )
            )
            {
                return false;
            }
            double farthestAngle = -1;
            var residuals = new double[count * 2];
            foreach (var solution in solutions)
            {
                if (
                    !Project(
                        solution.Rotation,
                        solution.Translation,
                        image,
                        field,
                        count,
                        intrinsics,
                        residuals,
                        null,
                        out _
                    )
                )
                {
                    continue;
                }
                double[] relative = PoseCovariance.MultiplyMatrices(
                    solution.Rotation,
                    PoseCovariance.Transpose(rotation)
                );
                double angle = Math.Acos(
                    Math.Clamp((relative[0] + relative[4] + relative[8] - 1.0) / 2.0, -1.0, 1.0)
                );
                if (angle > farthestAngle)
                {
                    farthestAngle = angle;
                    farthest = solution;
                }
            }
            return farthestAngle >= 0;
        }

        /// <summary>
        /// Projects the points onto their best-fit plane, as Napa's planar alternative does, so the
        /// mirror solution doesn't depend on which tag comes first. Returns false when the points
        /// have depth enough to break the planar ambiguity.
        /// </summary>
        private static bool TryFlattenToPlane(double[] field, int count, out double[] flattened)
        {
            flattened = null;
            var centroid = new double[3];
            for (int i = 0; i < count; i++)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    centroid[axis] += field[i * 3 + axis] / count;
                }
            }
            var scatter = new double[9];
            for (int i = 0; i < count; i++)
            {
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        scatter[r * 3 + c] +=
                            (field[i * 3 + r] - centroid[r]) * (field[i * 3 + c] - centroid[c]);
                    }
                }
            }
            SymmetricEigen(scatter, 3, out double[] values, out double[] vectors);
            int smallest = 0;
            int largest = 0;
            for (int k = 1; k < 3; k++)
            {
                if (values[k] < values[smallest])
                {
                    smallest = k;
                }
                if (values[k] > values[largest])
                {
                    largest = k;
                }
            }
            // Singular values are the square roots of the scatter's eigenvalues.
            if (
                Math.Max(0.0, values[smallest])
                > PlanarSingularValueRatio * PlanarSingularValueRatio * values[largest]
            )
            {
                return false;
            }
            double[] normal = { vectors[smallest], vectors[3 + smallest], vectors[6 + smallest] };
            flattened = new double[count * 3];
            for (int i = 0; i < count; i++)
            {
                double depth = 0;
                for (int axis = 0; axis < 3; axis++)
                {
                    depth += (field[i * 3 + axis] - centroid[axis]) * normal[axis];
                }
                for (int axis = 0; axis < 3; axis++)
                {
                    flattened[i * 3 + axis] = field[i * 3 + axis] - depth * normal[axis];
                }
            }
            return true;
        }

        /// <summary>
        /// Reprojects the field corners through a camera-from-field pose. Fills the residuals
        /// (observed minus projected) and, when given, the Jacobian of the projected pixels with
        /// respect to the pose's error, row-major (2 * count) x 6. Returns false, with an infinite
        /// squared error, if a corner can't be projected.
        /// </summary>
        internal static bool Project(
            double[] rotation,
            double[] translation,
            double[] image,
            double[] field,
            int count,
            double[] intrinsics,
            double[] residuals,
            double[] jacobian,
            out double squaredError
        )
        {
            double fx = intrinsics[0];
            double fy = intrinsics[1];
            double cx = intrinsics[2];
            double cy = intrinsics[3];
            squaredError = 0;
            for (int i = 0; i < count; i++)
            {
                double x = field[i * 3];
                double y = field[i * 3 + 1];
                double z = field[i * 3 + 2];
                // The rotated point q = R X; the camera point is q + t.
                double qx = rotation[0] * x + rotation[1] * y + rotation[2] * z;
                double qy = rotation[3] * x + rotation[4] * y + rotation[5] * z;
                double qz = rotation[6] * x + rotation[7] * y + rotation[8] * z;
                double px = qx + translation[0];
                double py = qy + translation[1];
                double pz = qz + translation[2];
                if (!(pz > MinimumDepthMeters) || !double.IsFinite(px) || !double.IsFinite(py))
                {
                    squaredError = double.PositiveInfinity;
                    return false;
                }
                double inverse = 1.0 / pz;
                double du = image[i * 2] - (fx * px * inverse + cx);
                double dv = image[i * 2 + 1] - (fy * py * inverse + cy);
                residuals[i * 2] = du;
                residuals[i * 2 + 1] = dv;
                squaredError += du * du + dv * dv;
                if (jacobian == null)
                {
                    continue;
                }
                // Each pixel row a = d(pixel)/d(camera point). A small rotation r moves the point
                // by r x q, so the row gains a . (r x q) = (q x a) . r.
                double au = fx * inverse;
                double cu = -fx * px * inverse * inverse;
                double bv = fy * inverse;
                double cv = -fy * py * inverse * inverse;
                int u = i * 2 * 6;
                jacobian[u] = au;
                jacobian[u + 1] = 0;
                jacobian[u + 2] = cu;
                jacobian[u + 3] = qy * cu;
                jacobian[u + 4] = qz * au - qx * cu;
                jacobian[u + 5] = -qy * au;
                int v = u + 6;
                jacobian[v] = 0;
                jacobian[v + 1] = bv;
                jacobian[v + 2] = cv;
                jacobian[v + 3] = qy * cv - qz * bv;
                jacobian[v + 4] = -qx * cv;
                jacobian[v + 5] = qx * bv;
            }
            return double.IsFinite(squaredError);
        }

        /// <summary>
        /// <c>sigma^2 (J^T J)^-1</c> for a (rows x 6) Jacobian, or null when the corners don't
        /// constrain all six degrees of freedom.
        /// </summary>
        internal static double[] CovarianceFromJacobian(double[] jacobian, int rows, double noise)
        {
            var information = new double[36];
            for (int i = 0; i < 6; i++)
            {
                for (int j = i; j < 6; j++)
                {
                    double sum = 0;
                    for (int r = 0; r < rows; r++)
                    {
                        sum += jacobian[r * 6 + i] * jacobian[r * 6 + j];
                    }
                    information[i * 6 + j] = sum;
                    information[j * 6 + i] = sum;
                }
            }
            SymmetricEigen(information, 6, out double[] values, out double[] vectors);
            double smallest = double.PositiveInfinity;
            double largest = 0;
            foreach (double value in values)
            {
                smallest = Math.Min(smallest, value);
                largest = Math.Max(largest, value);
            }
            if (!(smallest > 1e-12 * largest))
            {
                return null;
            }
            var covariance = new double[36];
            for (int r = 0; r < 6; r++)
            {
                for (int c = 0; c < 6; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        sum += vectors[r * 6 + k] * vectors[c * 6 + k] / values[k];
                    }
                    covariance[r * 6 + c] = noise * noise * sum;
                }
            }
            return covariance;
        }

        /// <summary>
        /// Eigenvalues and eigenvectors of a symmetric row-major n x n matrix by cyclic Jacobi
        /// rotations. Eigenvector k is column k of <paramref name="vectors"/>.
        /// </summary>
        internal static void SymmetricEigen(
            double[] matrix,
            int n,
            out double[] values,
            out double[] vectors
        )
        {
            var a = (double[])matrix.Clone();
            vectors = new double[n * n];
            for (int i = 0; i < n; i++)
            {
                vectors[i * n + i] = 1;
            }
            for (int sweep = 0; sweep < 64; sweep++)
            {
                double off = 0;
                double diagonal = 0;
                for (int p = 0; p < n; p++)
                {
                    diagonal += a[p * n + p] * a[p * n + p];
                    for (int q = p + 1; q < n; q++)
                    {
                        off += a[p * n + q] * a[p * n + q];
                    }
                }
                if (off <= 1e-30 * diagonal || off == 0)
                {
                    break;
                }
                for (int p = 0; p < n; p++)
                {
                    for (int q = p + 1; q < n; q++)
                    {
                        double apq = a[p * n + q];
                        if (apq == 0)
                        {
                            continue;
                        }
                        double theta = (a[q * n + q] - a[p * n + p]) / (2.0 * apq);
                        double t =
                            (theta >= 0 ? 1.0 : -1.0)
                            / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                        double c = 1.0 / Math.Sqrt(t * t + 1.0);
                        double s = t * c;
                        for (int k = 0; k < n; k++)
                        {
                            double akp = a[k * n + p];
                            double akq = a[k * n + q];
                            a[k * n + p] = c * akp - s * akq;
                            a[k * n + q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double apk = a[p * n + k];
                            double aqk = a[q * n + k];
                            a[p * n + k] = c * apk - s * aqk;
                            a[q * n + k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double vkp = vectors[k * n + p];
                            double vkq = vectors[k * n + q];
                            vectors[k * n + p] = c * vkp - s * vkq;
                            vectors[k * n + q] = s * vkp + c * vkq;
                        }
                    }
                }
            }
            values = new double[n];
            for (int i = 0; i < n; i++)
            {
                values[i] = a[i * n + i];
            }
        }

        /// <summary>Two unit vectors that complete <paramref name="axis"/> to an orthonormal basis.</summary>
        private static void PerpendicularAxes(
            double[] axis,
            out double[] first,
            out double[] second
        )
        {
            // Cross with the coordinate axis least aligned with this one.
            int least = 0;
            for (int i = 1; i < 3; i++)
            {
                if (Math.Abs(axis[i]) < Math.Abs(axis[least]))
                {
                    least = i;
                }
            }
            var unit = new double[3];
            unit[least] = 1;
            first = Normalize(Cross(axis, unit));
            second = Cross(axis, first);
        }

        private static double[] Cross(double[] a, double[] b) =>
            new[]
            {
                a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0],
            };

        private static double[] Normalize(double[] v)
        {
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return new[] { v[0] / length, v[1] / length, v[2] / length };
        }

        /// <summary>Gaussian elimination with partial pivoting. The solution replaces b.</summary>
        private static bool SolveLinear(double[,] a, double[] b, int n)
        {
            for (int column = 0; column < n; column++)
            {
                int pivot = column;
                for (int r = column + 1; r < n; r++)
                {
                    if (Math.Abs(a[r, column]) > Math.Abs(a[pivot, column]))
                    {
                        pivot = r;
                    }
                }
                if (!(Math.Abs(a[pivot, column]) > 0))
                {
                    return false;
                }
                if (pivot != column)
                {
                    for (int c = 0; c < n; c++)
                    {
                        (a[column, c], a[pivot, c]) = (a[pivot, c], a[column, c]);
                    }
                    (b[column], b[pivot]) = (b[pivot], b[column]);
                }
                for (int r = column + 1; r < n; r++)
                {
                    double factor = a[r, column] / a[column, column];
                    for (int c = column; c < n; c++)
                    {
                        a[r, c] -= factor * a[column, c];
                    }
                    b[r] -= factor * b[column];
                }
            }
            for (int r = n - 1; r >= 0; r--)
            {
                double sum = b[r];
                for (int c = r + 1; c < n; c++)
                {
                    sum -= a[r, c] * b[c];
                }
                b[r] = sum / a[r, r];
                if (!double.IsFinite(b[r]))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
