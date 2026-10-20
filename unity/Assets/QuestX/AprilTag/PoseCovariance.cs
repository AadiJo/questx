using System;
using QuestX.QuestX.Geometry;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>
    /// A pose of a child frame in a parent frame and the 6x6 covariance of its error, ported from
    /// Napa's <c>PoseWithCovariance</c> (zwatx/NAPA <c>covariance.py</c>).
    /// </summary>
    /// <remarks>
    /// The covariance is row-major and ordered [x, y, z, rx, ry, rz], as in ROS: the translation
    /// error in the parent frame, then a small rotation about the parent frame's axes applied on
    /// the left, so the true rotation is <c>exp([r]x) R</c>.
    ///
    /// A pose from a planar target is a weighted set of solutions: the first is the reported pose,
    /// and the covariance is the set's spread about it. <see cref="TransformBy"/> and
    /// <see cref="Inverse"/> move every solution exactly and recompute the spread in the new frame,
    /// because a spread that covers tens of degrees does not survive first-order propagation.
    /// Every reported covariance has <see cref="CorrelationSlack"/> of each variance added back to
    /// its diagonal; see <see cref="AprilTagCovariance"/> for why.
    ///
    /// This class has no Unity dependencies so it can be checked outside the editor.
    /// </remarks>
    public sealed class PoseCovariance
    {
        /// <summary>
        /// Fraction of each variance added to the diagonal, capping every correlation at 1 / 1.1.
        /// </summary>
        public const double CorrelationSlack = 0.1;

        /// <summary>One weighted solution: a rotation (row-major 3x3), translation, and covariance.</summary>
        internal readonly struct Component
        {
            public readonly double[] Rotation;
            public readonly double[] Translation;
            public readonly double[] Covariance;
            public readonly double Weight;

            public Component(
                double[] rotation,
                double[] translation,
                double[] covariance,
                double weight
            )
            {
                Rotation = rotation;
                Translation = translation;
                Covariance = covariance;
                Weight = weight;
            }
        }

        private readonly Component[] components;
        private readonly double[] covariance;

        /// <param name="components">Solutions, the reported pose first; weights need not sum to 1.</param>
        internal PoseCovariance(Component[] components)
        {
            if (components == null || components.Length == 0)
            {
                throw new ArgumentException("A pose needs at least one solution");
            }
            double total = 0;
            foreach (var component in components)
            {
                total += component.Weight;
            }
            if (!(total > 0) || double.IsInfinity(total))
            {
                throw new ArgumentException("Solution weights must have a positive, finite sum");
            }

            this.components = new Component[components.Length];
            covariance = new double[36];
            for (int m = 0; m < components.Length; m++)
            {
                var component = components[m];
                double weight = component.Weight / total;
                this.components[m] = new Component(
                    component.Rotation,
                    component.Translation,
                    component.Covariance,
                    weight
                );
                for (int i = 0; i < 36; i++)
                {
                    covariance[i] += weight * component.Covariance[i];
                }
                if (m > 0)
                {
                    double[] offset = Difference(components[0], component);
                    for (int r = 0; r < 6; r++)
                    {
                        for (int c = 0; c < 6; c++)
                        {
                            covariance[r * 6 + c] += weight * offset[r] * offset[c];
                        }
                    }
                }
            }
            for (int i = 0; i < 6; i++)
            {
                covariance[i * 7] *= 1.0 + CorrelationSlack;
            }
            foreach (double value in covariance)
            {
                if (!double.IsFinite(value))
                {
                    throw new ArgumentException("Covariance must be finite");
                }
            }
        }

        /// <summary>A pose with a linearized covariance and no alternative solutions.</summary>
        internal static PoseCovariance Linear(
            double[] rotation,
            double[] translation,
            double[] covariance
        ) => new PoseCovariance(new[] { new Component(rotation, translation, covariance, 1.0) });

        /// <summary>Row-major 3x3 rotation of the child frame in the parent frame (a copy).</summary>
        public double[] Rotation => (double[])components[0].Rotation.Clone();

        /// <summary>Position of the child frame in the parent frame (a copy).</summary>
        public double[] Translation => (double[])components[0].Translation.Clone();

        /// <summary>Row-major 6x6 covariance in the convention above (a copy).</summary>
        public double[] Covariance => (double[])covariance.Clone();

        /// <summary>Number of weighted solutions the covariance summarizes.</summary>
        public int SolutionCount => components.Length;

        /// <summary>Standard deviations of [x, y, z, rx, ry, rz] along the parent frame's axes.</summary>
        public double[] StdDevs() => DiagonalRoots(covariance);

        /// <summary>
        /// Standard deviations of [x, y, z, rx, ry, rz] along the child frame's own axes. For a robot
        /// pose these are forward, left, and up, then roll, pitch, and yaw.
        /// </summary>
        public double[] LocalStdDevs()
        {
            // Both halves of the error rotate into the child frame by R^T: R^T dt, and R^T r because
            // exp([r]x) R = R exp([R^T r]x).
            var basis = BlockDiagonal(Transpose(components[0].Rotation));
            return DiagonalRoots(Propagate(basis, covariance));
        }

        /// <summary>
        /// The pose projected onto the parent's X/Y plane, as <c>Pose3d.ToPose2d</c>: x, y, and
        /// the heading of the child's +X axis, with their 3x3 covariance (x and y along the parent's
        /// axes). Each solution is projected exactly before the spread is taken. Returns false when
        /// the child's +X axis is vertical, so heading is undefined.
        /// </summary>
        public bool TryToPose2d(out double[] pose, out double[] covariance)
        {
            pose = null;
            covariance = null;
            var values = new double[components.Length][];
            var summary = new double[9];
            for (int m = 0; m < components.Length; m++)
            {
                var component = components[m];
                double hx = component.Rotation[0];
                double hy = component.Rotation[3];
                double hz = component.Rotation[6];
                double planar = hx * hx + hy * hy;
                if (planar < 1e-12)
                {
                    return false;
                }
                values[m] = new[]
                {
                    component.Translation[0],
                    component.Translation[1],
                    Math.Atan2(hy, hx),
                };
                // A small rotation r turns the heading h by r x h; differentiate atan2(h_y, h_x).
                var jacobian = new double[18];
                jacobian[0] = 1;
                jacobian[7] = 1;
                jacobian[15] = -hx * hz / planar;
                jacobian[16] = -hy * hz / planar;
                jacobian[17] = 1;
                var offset = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    offset[i] = values[m][i] - values[0][i];
                }
                offset[2] = Math.IEEERemainder(offset[2], 2 * Math.PI);
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        double sum = 0;
                        for (int i = 0; i < 6; i++)
                        {
                            for (int j = 0; j < 6; j++)
                            {
                                sum +=
                                    jacobian[r * 6 + i]
                                    * component.Covariance[i * 6 + j]
                                    * jacobian[c * 6 + j];
                            }
                        }
                        summary[r * 3 + c] += component.Weight * (sum + offset[r] * offset[c]);
                    }
                }
            }
            for (int i = 0; i < 3; i++)
            {
                summary[i * 4] *= 1.0 + CorrelationSlack;
            }
            pose = values[0];
            covariance = summary;
            return true;
        }

        /// <summary>
        /// Marginal standard deviations QuestX publishes for compatibility. Forward, left,
        /// and yaw belong to the pose projected onto the parent's X/Y plane, as
        /// <c>Pose3d.ToPose2d</c> gives it, along the measured heading. WPILib 2026 applies its gain
        /// along the historical prediction's heading instead: robot code should use the v4 planar
        /// covariance and its prediction to prepare estimator noise. Up, roll,
        /// and pitch are along the child's own axes (<see cref="LocalStdDevs"/>), which also supply
        /// diagnostic values when heading is undefined. In that case the v4 planar block is
        /// explicitly unavailable, and these values must not be used for planar fusion.
        /// </summary>
        public double[] EstimatorStdDevs() => EstimatorStdDevs(out _);

        /// <summary>
        /// Also returns a fresh field X/Y/heading covariance, or null when heading is undefined,
        /// so the serializer can publish both summaries without projecting every solution twice.
        /// </summary>
        internal double[] EstimatorStdDevs(out double[] planar)
        {
            double[] result = LocalStdDevs();
            if (TryToPose2d(out double[] pose, out planar))
            {
                double cosine = Math.Cos(pose[2]);
                double sine = Math.Sin(pose[2]);
                double forward =
                    cosine * cosine * planar[0]
                    + 2 * cosine * sine * planar[1]
                    + sine * sine * planar[4];
                double left =
                    sine * sine * planar[0]
                    - 2 * cosine * sine * planar[1]
                    + cosine * cosine * planar[4];
                result[0] = Math.Sqrt(Math.Max(0.0, forward));
                result[1] = Math.Sqrt(Math.Max(0.0, left));
                result[5] = Math.Sqrt(Math.Max(0.0, planar[8]));
            }
            return result;
        }

        /// <summary>The 21 upper-triangle covariance entries, row by row.</summary>
        public double[] UpperTriangle()
        {
            var values = new double[21];
            int index = 0;
            for (int r = 0; r < 6; r++)
            {
                for (int c = r; c < 6; c++)
                {
                    values[index++] = covariance[r * 6 + c];
                }
            }
            return values;
        }

        /// <summary>
        /// <c>this * transform</c>, as <see cref="Pose3d.TransformBy"/>: the pose of a frame rigidly
        /// attached to the child, such as the headset from the camera. The transform is exact.
        /// </summary>
        public PoseCovariance TransformBy(Transform3d transform)
        {
            if (transform == null)
            {
                throw new ArgumentNullException(nameof(transform));
            }
            double[] offsetRotation = Flatten(transform.Rotation.ToMatrix());
            double[] offsetTranslation = { transform.X, transform.Y, transform.Z };
            var mapped = new Component[components.Length];
            for (int m = 0; m < components.Length; m++)
            {
                var component = components[m];
                double[] lever = Multiply(component.Rotation, offsetTranslation);
                // Moving the child by r x lever: d(t') = dt - [lever]x r.
                var jacobian = Identity6();
                double[] skew = Skew(lever);
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        jacobian[r * 6 + 3 + c] = -skew[r * 3 + c];
                    }
                }
                mapped[m] = new Component(
                    MultiplyMatrices(component.Rotation, offsetRotation),
                    Add(component.Translation, lever),
                    Propagate(jacobian, component.Covariance),
                    component.Weight
                );
            }
            return new PoseCovariance(mapped);
        }

        /// <summary>The parent's pose in the child frame, with the covariance in the child frame.</summary>
        public PoseCovariance Inverse()
        {
            var mapped = new Component[components.Length];
            for (int m = 0; m < components.Length; m++)
            {
                var component = components[m];
                double[] transposed = Transpose(component.Rotation);
                double[] translation = Multiply(transposed, component.Translation);
                double[] skew = MultiplyMatrices(transposed, Skew(component.Translation));
                var jacobian = new double[36];
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        jacobian[r * 6 + c] = -transposed[r * 3 + c];
                        jacobian[(r + 3) * 6 + c + 3] = -transposed[r * 3 + c];
                        jacobian[r * 6 + c + 3] = -skew[r * 3 + c];
                    }
                }
                mapped[m] = new Component(
                    transposed,
                    new[] { -translation[0], -translation[1], -translation[2] },
                    Propagate(jacobian, component.Covariance),
                    component.Weight
                );
            }
            return new PoseCovariance(mapped);
        }

        /// <summary>
        /// <paramref name="other"/>'s offset from <paramref name="pose"/> in the covariance convention:
        /// [t_other - t, r] with R_other = exp([r]x) R.
        /// </summary>
        internal static double[] Difference(Component pose, Component other)
        {
            double[] rotation = Log(MultiplyMatrices(other.Rotation, Transpose(pose.Rotation)));
            return new[]
            {
                other.Translation[0] - pose.Translation[0],
                other.Translation[1] - pose.Translation[1],
                other.Translation[2] - pose.Translation[2],
                rotation[0],
                rotation[1],
                rotation[2],
            };
        }

        /// <summary><c>exp([v]x)</c> by Rodrigues' formula, with series terms for tiny angles.</summary>
        internal static double[] Exp(double[] v)
        {
            double angle = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            double first,
                second;
            if (angle < 1e-4)
            {
                first = 1.0 - angle * angle / 6.0;
                second = 0.5 - angle * angle / 24.0;
            }
            else
            {
                first = Math.Sin(angle) / angle;
                second = (1.0 - Math.Cos(angle)) / (angle * angle);
            }
            double[] skew = Skew(v);
            double[] squared = MultiplyMatrices(skew, skew);
            var result = new double[9];
            for (int i = 0; i < 9; i++)
            {
                result[i] = first * skew[i] + second * squared[i];
            }
            result[0] += 1;
            result[4] += 1;
            result[8] += 1;
            return result;
        }

        /// <summary>Rotation vector of a rotation matrix, accurate for tiny angles and near a half turn.</summary>
        internal static double[] Log(double[] m)
        {
            // The antisymmetric part's vector is the axis scaled by the sine.
            double[] sineAxis = { 0.5 * (m[7] - m[5]), 0.5 * (m[2] - m[6]), 0.5 * (m[3] - m[1]) };
            double sine = Math.Sqrt(Dot(sineAxis, sineAxis));
            double cosine = Math.Clamp((m[0] + m[4] + m[8] - 1.0) / 2.0, -1.0, 1.0);
            double angle = Math.Atan2(sine, cosine);
            if (cosine < 0 && sine < 1e-6)
            {
                // Near a half turn the antisymmetric part vanishes; (R + I) / 2 is about a a^T.
                int k =
                    m[0] >= m[4] && m[0] >= m[8] ? 0
                    : m[4] >= m[8] ? 1
                    : 2;
                var axis = new double[3];
                axis[k] = Math.Sqrt(Math.Max(0.0, (m[k * 4] + 1.0) / 2.0));
                for (int i = 0; i < 3; i++)
                {
                    if (i != k)
                    {
                        axis[i] = (m[i * 3 + k] + m[k * 3 + i]) / (4.0 * axis[k]);
                    }
                }
                double length = Math.Sqrt(Dot(axis, axis));
                double sign = Dot(axis, sineAxis) < 0 ? -1.0 : 1.0;
                return new[]
                {
                    sign * angle * axis[0] / length,
                    sign * angle * axis[1] / length,
                    sign * angle * axis[2] / length,
                };
            }
            double scale = sine > 0 ? angle / sine : 1.0;
            return new[] { sineAxis[0] * scale, sineAxis[1] * scale, sineAxis[2] * scale };
        }

        /// <summary><c>J C J^T</c> for 6x6 row-major matrices, symmetrized.</summary>
        internal static double[] Propagate(double[] jacobian, double[] covariance)
        {
            var product = new double[36];
            for (int r = 0; r < 6; r++)
            {
                for (int c = 0; c < 6; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        sum += jacobian[r * 6 + k] * covariance[k * 6 + c];
                    }
                    product[r * 6 + c] = sum;
                }
            }
            var result = new double[36];
            for (int r = 0; r < 6; r++)
            {
                for (int c = 0; c < 6; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        sum += product[r * 6 + k] * jacobian[c * 6 + k];
                    }
                    result[r * 6 + c] = sum;
                }
            }
            for (int r = 0; r < 6; r++)
            {
                for (int c = r + 1; c < 6; c++)
                {
                    double mean = 0.5 * (result[r * 6 + c] + result[c * 6 + r]);
                    result[r * 6 + c] = mean;
                    result[c * 6 + r] = mean;
                }
            }
            return result;
        }

        internal static double[] Skew(double[] v) =>
            new[] { 0, -v[2], v[1], v[2], 0, -v[0], -v[1], v[0], 0 };

        internal static double[] MultiplyMatrices(double[] a, double[] b)
        {
            var result = new double[9];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    result[r * 3 + c] =
                        a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
                }
            }
            return result;
        }

        internal static double[] Multiply(double[] m, double[] v) =>
            new[]
            {
                m[0] * v[0] + m[1] * v[1] + m[2] * v[2],
                m[3] * v[0] + m[4] * v[1] + m[5] * v[2],
                m[6] * v[0] + m[7] * v[1] + m[8] * v[2],
            };

        internal static double[] Transpose(double[] m) =>
            new[] { m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8] };

        private static double[] Add(double[] a, double[] b) =>
            new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };

        private static double Dot(double[] a, double[] b) =>
            a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        private static double[] Identity6()
        {
            var result = new double[36];
            for (int i = 0; i < 6; i++)
            {
                result[i * 7] = 1;
            }
            return result;
        }

        private static double[] BlockDiagonal(double[] rotation)
        {
            var result = new double[36];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    result[r * 6 + c] = rotation[r * 3 + c];
                    result[(r + 3) * 6 + c + 3] = rotation[r * 3 + c];
                }
            }
            return result;
        }

        private static double[] DiagonalRoots(double[] matrix)
        {
            var result = new double[6];
            for (int i = 0; i < 6; i++)
            {
                result[i] = Math.Sqrt(Math.Max(0.0, matrix[i * 7]));
            }
            return result;
        }

        private static double[] Flatten(double[,] m) =>
            new[]
            {
                m[0, 0],
                m[0, 1],
                m[0, 2],
                m[1, 0],
                m[1, 1],
                m[1, 2],
                m[2, 0],
                m[2, 1],
                m[2, 2],
            };
    }
}
