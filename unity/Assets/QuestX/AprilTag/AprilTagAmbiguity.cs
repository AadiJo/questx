using System;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>
    /// Planar pose ambiguity and fit error for an AprilTag solve.
    /// </summary>
    /// <remarks>
    /// A planar target seen by a pinhole camera usually has two poses that explain its corners
    /// (the IPPE solutions of Collins and Bartoli, 2014). Ambiguity is the lower RMS reprojection
    /// error divided by the higher one: near 0 means one pose clearly fits better, and 1 means the
    /// image cannot distinguish them. Per tag, this is the same quantity as PhotonVision's
    /// <c>getPoseAmbiguity()</c> and Limelight's per-fiducial ambiguity.
    ///
    /// This class has no Unity dependencies so it can be checked outside the editor.
    /// </remarks>
    public static class AprilTagAmbiguity
    {
        /// <summary>
        /// Corners farther than this from the first tag's plane make a multi-tag solve non-coplanar.
        /// Field layouts place tags on a shared face exactly in one plane, so this only absorbs rounding.
        /// </summary>
        public const double CoplanarToleranceMeters = 0.02;

        /// <summary>Ambiguity and fit error of one solve.</summary>
        public sealed class Result
        {
            /// <summary>
            /// Measurement ambiguity from 0 to 1. A single tag uses that tag's ambiguity. Several
            /// coplanar tags are solved jointly as one plane. Tags on different planes do not share
            /// the planar two-pose ambiguity, so the value is 0 (PhotonVision's multi-tag convention).
            /// It is 1 when the planar solve is degenerate.
            /// </summary>
            public double Ambiguity { get; }

            /// <summary>RMS pixel distance between every observed corner and its reprojection.</summary>
            public double ReprojectionErrorPixels { get; }

            /// <summary>Single-tag ambiguity for each tag, in input order.</summary>
            public double[] TagAmbiguities { get; }

            public Result(double ambiguity, double reprojectionErrorPixels, double[] tagAmbiguities)
            {
                Ambiguity = ambiguity;
                ReprojectionErrorPixels = reprojectionErrorPixels;
                TagAmbiguities = tagAmbiguities;
            }
        }

        /// <summary>
        /// Evaluates a solve over the same correspondences that were given to PoseLib.
        /// </summary>
        /// <param name="points2d">Pixel corners [x0, y0, x1, y1, ...], four per tag</param>
        /// <param name="points3d">Field corners [x0, y0, z0, ...], matching <paramref name="points2d"/></param>
        /// <param name="tagCount">Number of tags; corners 4i through 4i+3 belong to tag i</param>
        /// <param name="intrinsics">Pinhole intrinsics [fx, fy, cx, cy] in pixels</param>
        /// <param name="rotationWxyz">Solved world-to-camera rotation quaternion [w, x, y, z]</param>
        /// <param name="translation">Solved world-to-camera translation [x, y, z]</param>
        public static Result Evaluate(
            double[] points2d,
            double[] points3d,
            int tagCount,
            double[] intrinsics,
            double[] rotationWxyz,
            double[] translation
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

            double[] rotation = QuaternionToMatrix(rotationWxyz);
            double reprojectionError = RmsError(
                rotation,
                translation,
                points2d,
                points3d,
                0,
                pointCount,
                intrinsics
            );

            var tagAmbiguities = new double[tagCount];
            for (int tag = 0; tag < tagCount; tag++)
            {
                tagAmbiguities[tag] = PlanarAmbiguity(points2d, points3d, tag * 4, 4, intrinsics);
            }

            double ambiguity;
            if (tagCount == 1)
            {
                ambiguity = tagAmbiguities[0];
            }
            else if (IsCoplanar(points3d, pointCount))
            {
                ambiguity = PlanarAmbiguity(points2d, points3d, 0, pointCount, intrinsics);
            }
            else
            {
                ambiguity = 0.0;
            }

            return new Result(ambiguity, reprojectionError, tagAmbiguities);
        }

        /// <summary>
        /// Solves both IPPE poses for a set of coplanar corners and returns the ratio of their
        /// reprojection errors. The plane's orientation is taken from the first four corners.
        /// </summary>
        internal static double PlanarAmbiguity(
            double[] points2d,
            double[] points3d,
            int first,
            int count,
            double[] intrinsics
        )
        {
            if (!TrySolvePlanar(points2d, points3d, first, count, intrinsics, out var solutions))
            {
                return 1.0;
            }

            double errorA = RmsError(
                solutions[0].Rotation,
                solutions[0].Translation,
                points2d,
                points3d,
                first,
                count,
                intrinsics
            );
            double errorB = RmsError(
                solutions[1].Rotation,
                solutions[1].Translation,
                points2d,
                points3d,
                first,
                count,
                intrinsics
            );
            return Ratio(Math.Min(errorA, errorB), Math.Max(errorA, errorB));
        }

        /// <summary>A world-to-camera pose. Rotation is a row-major 3x3 matrix.</summary>
        internal readonly struct CameraPose
        {
            public double[] Rotation { get; }
            public double[] Translation { get; }

            public CameraPose(double[] rotation, double[] translation)
            {
                Rotation = rotation;
                Translation = translation;
            }
        }

        /// <summary>
        /// Computes the two IPPE world-to-camera poses for coplanar corners.
        /// </summary>
        internal static bool TrySolvePlanar(
            double[] points2d,
            double[] points3d,
            int first,
            int count,
            double[] intrinsics,
            out CameraPose[] solutions
        )
        {
            solutions = null;
            if (count < 4)
            {
                return false;
            }

            // Plane frame: origin at the centroid, normal from the first tag's diagonals.
            double[] origin = new double[3];
            for (int i = first; i < first + count; i++)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    origin[axis] += points3d[i * 3 + axis] / count;
                }
            }
            double[] normal = Cross(
                Subtract(Point(points3d, first + 2), Point(points3d, first)),
                Subtract(Point(points3d, first + 3), Point(points3d, first + 1))
            );
            if (!TryNormalize(normal))
            {
                return false;
            }

            double[] axisX = null;
            double farthest = 0;
            for (int i = first; i < first + count; i++)
            {
                double[] offset = Subtract(Point(points3d, i), origin);
                double[] inPlane = Subtract(offset, Scale(normal, Dot(offset, normal)));
                double length = Dot(inPlane, inPlane);
                if (length > farthest)
                {
                    farthest = length;
                    axisX = inPlane;
                }
            }
            if (axisX == null || !TryNormalize(axisX))
            {
                return false;
            }
            double[] axisY = Cross(normal, axisX);

            // Plane coordinates, scaled to unit RMS radius to condition the homography fit.
            var planeX = new double[count];
            var planeY = new double[count];
            double sumSquares = 0;
            for (int i = 0; i < count; i++)
            {
                double[] offset = Subtract(Point(points3d, first + i), origin);
                planeX[i] = Dot(offset, axisX);
                planeY[i] = Dot(offset, axisY);
                sumSquares += planeX[i] * planeX[i] + planeY[i] * planeY[i];
            }
            double scale = Math.Sqrt(sumSquares / count);
            if (!(scale > 1e-9))
            {
                return false;
            }

            double fx = intrinsics[0];
            double fy = intrinsics[1];
            double cx = intrinsics[2];
            double cy = intrinsics[3];
            var imageU = new double[count];
            var imageV = new double[count];
            for (int i = 0; i < count; i++)
            {
                imageU[i] = (points2d[(first + i) * 2] - cx) / fx;
                imageV[i] = (points2d[(first + i) * 2 + 1] - cy) / fy;
            }

            // Homography from scaled plane coordinates to normalized image coordinates, H22 = 1.
            var normalMatrix = new double[8, 8];
            var normalVector = new double[8];
            var row = new double[8];
            for (int i = 0; i < count; i++)
            {
                double sx = planeX[i] / scale;
                double sy = planeY[i] / scale;
                for (int pass = 0; pass < 2; pass++)
                {
                    double target = pass == 0 ? imageU[i] : imageV[i];
                    Array.Clear(row, 0, row.Length);
                    int offset = pass == 0 ? 0 : 3;
                    row[offset] = sx;
                    row[offset + 1] = sy;
                    row[offset + 2] = 1;
                    row[6] = -target * sx;
                    row[7] = -target * sy;
                    for (int r = 0; r < 8; r++)
                    {
                        normalVector[r] += row[r] * target;
                        for (int c = 0; c < 8; c++)
                        {
                            normalMatrix[r, c] += row[r] * row[c];
                        }
                    }
                }
            }
            if (!TrySolve(normalMatrix, normalVector, 8))
            {
                return false;
            }
            double[] h = normalVector;
            // Undo the plane-coordinate scale: columns 0 and 1 multiply x / scale and y / scale.
            double h00 = h[0] / scale,
                h01 = h[1] / scale,
                h02 = h[2];
            double h10 = h[3] / scale,
                h11 = h[4] / scale,
                h12 = h[5];
            double h20 = h[6] / scale,
                h21 = h[7] / scale;

            // IPPE: the plane origin projects to (p, q). J is the homography's Jacobian there.
            double p = h02;
            double q = h12;
            double j00 = h00 - h20 * p;
            double j01 = h01 - h21 * p;
            double j10 = h10 - h20 * q;
            double j11 = h11 - h21 * q;

            // Rv rotates the z axis onto the line of sight through (p, q, 1).
            double[] rv = ZAxisToDirection(p, q);
            double b00 = rv[0] - p * rv[6];
            double b01 = rv[1] - p * rv[7];
            double b10 = rv[3] - q * rv[6];
            double b11 = rv[4] - q * rv[7];
            double determinant = b00 * b11 - b01 * b10;
            if (Math.Abs(determinant) < 1e-12)
            {
                return false;
            }
            double a00 = (b11 * j00 - b01 * j10) / determinant;
            double a01 = (b11 * j01 - b01 * j11) / determinant;
            double a10 = (-b10 * j00 + b00 * j10) / determinant;
            double a11 = (-b10 * j01 + b00 * j11) / determinant;

            // The largest singular value of A is 1 / depth; dividing by it leaves the top 2x2
            // block of a rotation whose third row is known only up to sign.
            double aat00 = a00 * a00 + a01 * a01;
            double aat01 = a00 * a10 + a01 * a11;
            double aat11 = a10 * a10 + a11 * a11;
            double gamma = Math.Sqrt(
                0.5
                    * (
                        aat00
                        + aat11
                        + Math.Sqrt((aat00 - aat11) * (aat00 - aat11) + 4.0 * aat01 * aat01)
                    )
            );
            if (!(gamma > 1e-12))
            {
                return false;
            }
            double r00 = a00 / gamma;
            double r01 = a01 / gamma;
            double r10 = a10 / gamma;
            double r11 = a11 / gamma;
            double c0 = Math.Sqrt(Math.Max(0.0, 1.0 - r00 * r00 - r10 * r10));
            double c1 = Math.Sqrt(Math.Max(0.0, 1.0 - r01 * r01 - r11 * r11));
            if (-(r00 * r01 + r10 * r11) < 0)
            {
                c1 = -c1;
            }

            // planeToWorld has columns axisX, axisY, normal. World-to-camera rotation is
            // planeToCamera * planeToWorld^T, and translation moves the plane origin.
            solutions = new CameraPose[2];
            for (int solution = 0; solution < 2; solution++)
            {
                double sign = solution == 0 ? 1.0 : -1.0;
                double[] column0 = { r00, r10, sign * c0 };
                double[] column1 = { r01, r11, sign * c1 };
                double[] column2 = Cross(column0, column1);
                var planeToCamera = new double[9];
                for (int r = 0; r < 3; r++)
                {
                    planeToCamera[r * 3] =
                        rv[r * 3] * column0[0]
                        + rv[r * 3 + 1] * column0[1]
                        + rv[r * 3 + 2] * column0[2];
                    planeToCamera[r * 3 + 1] =
                        rv[r * 3] * column1[0]
                        + rv[r * 3 + 1] * column1[1]
                        + rv[r * 3 + 2] * column1[2];
                    planeToCamera[r * 3 + 2] =
                        rv[r * 3] * column2[0]
                        + rv[r * 3 + 1] * column2[1]
                        + rv[r * 3 + 2] * column2[2];
                }

                if (
                    !TrySolveTranslation(
                        planeToCamera,
                        planeX,
                        planeY,
                        imageU,
                        imageV,
                        out double[] planeTranslation
                    )
                )
                {
                    return false;
                }

                var worldToCamera = new double[9];
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        worldToCamera[r * 3 + c] =
                            planeToCamera[r * 3] * axisX[c]
                            + planeToCamera[r * 3 + 1] * axisY[c]
                            + planeToCamera[r * 3 + 2] * normal[c];
                    }
                }
                var worldTranslation = new double[3];
                for (int r = 0; r < 3; r++)
                {
                    worldTranslation[r] =
                        planeTranslation[r]
                        - (
                            worldToCamera[r * 3] * origin[0]
                            + worldToCamera[r * 3 + 1] * origin[1]
                            + worldToCamera[r * 3 + 2] * origin[2]
                        );
                }
                solutions[solution] = new CameraPose(worldToCamera, worldTranslation);
            }
            return true;
        }

        /// <summary>
        /// Least-squares translation for a known plane-to-camera rotation. Each corner contributes
        /// the two linear constraints that its camera-frame point lies on its image ray.
        /// </summary>
        private static bool TrySolveTranslation(
            double[] rotation,
            double[] planeX,
            double[] planeY,
            double[] imageU,
            double[] imageV,
            out double[] translation
        )
        {
            var normalMatrix = new double[3, 3];
            var normalVector = new double[3];
            for (int i = 0; i < planeX.Length; i++)
            {
                double qx = rotation[0] * planeX[i] + rotation[1] * planeY[i];
                double qy = rotation[3] * planeX[i] + rotation[4] * planeY[i];
                double qz = rotation[6] * planeX[i] + rotation[7] * planeY[i];
                double u = imageU[i];
                double v = imageV[i];

                // [1, 0, -u] t = u qz - qx and [0, 1, -v] t = v qz - qy
                double ru = u * qz - qx;
                double rvValue = v * qz - qy;
                normalMatrix[0, 0] += 1;
                normalMatrix[0, 2] += -u;
                normalMatrix[1, 1] += 1;
                normalMatrix[1, 2] += -v;
                normalMatrix[2, 0] += -u;
                normalMatrix[2, 1] += -v;
                normalMatrix[2, 2] += u * u + v * v;
                normalVector[0] += ru;
                normalVector[1] += rvValue;
                normalVector[2] += -u * ru - v * rvValue;
            }
            translation = normalVector;
            return TrySolve(normalMatrix, normalVector, 3);
        }

        private static bool IsCoplanar(double[] points3d, int pointCount)
        {
            double[] anchor = Point(points3d, 0);
            double[] normal = Cross(
                Subtract(Point(points3d, 2), anchor),
                Subtract(Point(points3d, 3), Point(points3d, 1))
            );
            if (!TryNormalize(normal))
            {
                return false;
            }
            for (int i = 4; i < pointCount; i++)
            {
                if (
                    Math.Abs(Dot(Subtract(Point(points3d, i), anchor), normal))
                    > CoplanarToleranceMeters
                )
                {
                    return false;
                }
            }
            return true;
        }

        private static double Ratio(double lower, double higher)
        {
            if (double.IsNaN(lower) || double.IsNaN(higher) || double.IsPositiveInfinity(lower))
            {
                return 1.0;
            }
            if (!(higher > 0))
            {
                // Both poses reproject exactly: the image cannot tell them apart.
                return 1.0;
            }
            return Math.Clamp(lower / higher, 0.0, 1.0);
        }

        /// <summary>RMS reprojection error in pixels; infinite if a corner is behind the camera.</summary>
        internal static double RmsError(
            double[] rotation,
            double[] translation,
            double[] points2d,
            double[] points3d,
            int first,
            int count,
            double[] intrinsics
        )
        {
            double sum = 0;
            for (int i = first; i < first + count; i++)
            {
                double x = points3d[i * 3];
                double y = points3d[i * 3 + 1];
                double z = points3d[i * 3 + 2];
                double cameraX =
                    rotation[0] * x + rotation[1] * y + rotation[2] * z + translation[0];
                double cameraY =
                    rotation[3] * x + rotation[4] * y + rotation[5] * z + translation[1];
                double cameraZ =
                    rotation[6] * x + rotation[7] * y + rotation[8] * z + translation[2];
                if (!(cameraZ > 1e-9))
                {
                    return double.PositiveInfinity;
                }
                double dx = intrinsics[0] * cameraX / cameraZ + intrinsics[2] - points2d[i * 2];
                double dy = intrinsics[1] * cameraY / cameraZ + intrinsics[3] - points2d[i * 2 + 1];
                sum += dx * dx + dy * dy;
            }
            return Math.Sqrt(sum / count);
        }

        /// <summary>Row-major rotation matrix of a [w, x, y, z] quaternion.</summary>
        internal static double[] QuaternionToMatrix(double[] wxyz)
        {
            double norm = Math.Sqrt(
                wxyz[0] * wxyz[0] + wxyz[1] * wxyz[1] + wxyz[2] * wxyz[2] + wxyz[3] * wxyz[3]
            );
            double w = wxyz[0] / norm;
            double x = wxyz[1] / norm;
            double y = wxyz[2] / norm;
            double z = wxyz[3] / norm;
            return new[]
            {
                1 - 2 * (y * y + z * z),
                2 * (x * y - w * z),
                2 * (x * z + w * y),
                2 * (x * y + w * z),
                1 - 2 * (x * x + z * z),
                2 * (y * z - w * x),
                2 * (x * z - w * y),
                2 * (y * z + w * x),
                1 - 2 * (x * x + y * y),
            };
        }

        /// <summary>
        /// Row-major rotation that maps the z axis onto the unit direction of (p, q, 1), so its
        /// transpose maps that direction onto the z axis.
        /// </summary>
        private static double[] ZAxisToDirection(double p, double q)
        {
            double length = Math.Sqrt(p * p + q * q + 1);
            double ax = p / length;
            double ay = q / length;
            double az = 1 / length;
            // az > 0 always, so 1 + az never vanishes.
            double d = 1 / (1 + az);
            return new[]
            {
                1 - ax * ax * d,
                -ax * ay * d,
                ax,
                -ax * ay * d,
                1 - ay * ay * d,
                ay,
                -ax,
                -ay,
                1 - (ax * ax + ay * ay) * d,
            };
        }

        /// <summary>Gaussian elimination with partial pivoting. The solution replaces b.</summary>
        private static bool TrySolve(double[,] a, double[] b, int n)
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
                if (!(Math.Abs(a[pivot, column]) > 1e-15))
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

        private static double[] Point(double[] points3d, int index) =>
            new[] { points3d[index * 3], points3d[index * 3 + 1], points3d[index * 3 + 2] };

        private static double[] Subtract(double[] a, double[] b) =>
            new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

        private static double[] Scale(double[] a, double s) =>
            new[] { a[0] * s, a[1] * s, a[2] * s };

        private static double Dot(double[] a, double[] b) =>
            a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        private static double[] Cross(double[] a, double[] b) =>
            new[]
            {
                a[1] * b[2] - a[2] * b[1],
                a[2] * b[0] - a[0] * b[2],
                a[0] * b[1] - a[1] * b[0],
            };

        private static bool TryNormalize(double[] v)
        {
            double length = Math.Sqrt(Dot(v, v));
            if (!(length > 1e-12))
            {
                return false;
            }
            v[0] /= length;
            v[1] /= length;
            v[2] /= length;
            return true;
        }
    }
}
