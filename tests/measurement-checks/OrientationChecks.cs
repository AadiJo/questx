using System;
using QuestX.Camera;
using QuestX.QuestX.Geometry;

/// <summary>Checks that rotated images, overlay points, tag angles, and poses agree for every orientation.</summary>
static class OrientationChecks
{
    static readonly CameraOrientation[] All =
    {
        CameraOrientation.NORMAL,
        CameraOrientation.UPSIDE_DOWN,
        CameraOrientation.LEFT,
        CameraOrientation.RIGHT,
    };

    public static void Run(Action<bool, string> check)
    {
        const int width = 6;
        const int height = 4;
        foreach (var orientation in All)
        {
            CameraOrientationMath.RotateSize(orientation, width, height, out int rotatedWidth, out int rotatedHeight);
            check(rotatedWidth * rotatedHeight == width * height, $"{orientation}: rotation keeps the pixel count");

            // The image job reads SourcePixel; overlay corners use RotatePoint. They must agree.
            var covered = new bool[width * height];
            for (int ry = 0; ry < rotatedHeight; ry++)
                for (int rx = 0; rx < rotatedWidth; rx++)
                {
                    CameraOrientationMath.SourcePixel(orientation, rx, ry, width, height, out int x, out int y);
                    check(x >= 0 && x < width && y >= 0 && y < height, $"{orientation}: source pixel is in bounds");
                    covered[y * width + x] = true;
                    CameraOrientationMath.RotatePoint(orientation, x + 0.5, y + 0.5, width, height, out double px, out double py);
                    check(px == rx + 0.5 && py == ry + 0.5, $"{orientation}: pixel centers map like the image");
                }
            check(Array.TrueForAll(covered, c => c), $"{orientation}: every source pixel is used once");

            // With a centered principal point and square pixels, tx/ty point at the rotated pixel.
            const double f = 10;
            double rawX = 4.5, rawY = 1.0;
            double tx = Math.Atan2(rawX - width / 2.0, f), ty = Math.Atan2(height / 2.0 - rawY, f);
            CameraOrientationMath.RotateAngles(orientation, tx, ty, out double rtx, out double rty);
            CameraOrientationMath.RotatePoint(orientation, rawX, rawY, width, height, out double rx2, out double ry2);
            check(Math.Abs(rtx - Math.Atan2(rx2 - rotatedWidth / 2.0, f)) < 1e-12, $"{orientation}: tx follows the image");
            check(Math.Abs(rty - Math.Atan2(rotatedHeight / 2.0 - ry2, f)) < 1e-12, $"{orientation}: ty follows the image");
        }

        // Rolled left (counter-clockwise from behind), the camera's right axis points up, so
        // something above the headset appears right of center. Upright, it must read as up.
        CameraOrientationMath.RotateAngles(CameraOrientation.LEFT, 10, 0, out double leftTx, out double leftTy);
        check(leftTx == 0 && leftTy == 10, "Left: a target above the headset reads as up");
        CameraOrientationMath.RotateAngles(CameraOrientation.RIGHT, -10, 0, out double rightTx, out double rightTy);
        check(rightTx == 0 && rightTy == 10, "Right: a target above the headset reads as up");
        CameraOrientationMath.RotateAngles(CameraOrientation.UPSIDE_DOWN, 3, -4, out double flipTx, out double flipTy);
        check(flipTx == -3 && flipTy == 4, "Upside-down: tx and ty are negated");

        check(CameraOrientationMath.IsValid(0) && CameraOrientationMath.IsValid(3), "Stored orientations 0-3 are valid");
        check(!CameraOrientationMath.IsValid(-1) && !CameraOrientationMath.IsValid(4), "Out-of-range orientations are rejected");

        // Rolled left, the headset's top (+Z) points to the robot's left (+Y).
        var top = new Translation3d(0, 0, 1).RotateBy(
            new Rotation3d(CameraOrientationMath.MountRollRadians(CameraOrientation.LEFT), 0, 0));
        check(Math.Abs(top.Y - 1) < 1e-12, "Left: the headset top points to the robot's left");

        // A headset rolled on a robot on a ramp: the upright pose is the robot's own attitude at the
        // headset's position, and image angles match what an upright headset would see.
        var robot = new Rotation3d(0, 0.2, 2.5);
        var position = new Translation3d(3, 4, 0.5);
        var target = new Pose3d(new Translation3d(5, 6, 1.5), new Rotation3d());
        foreach (var orientation in All)
        {
            var mount = new Rotation3d(CameraOrientationMath.MountRollRadians(orientation), 0, 0);
            var mounted = new Pose3d(position, mount.RotateBy(robot));
            var upright = CameraOrientationMath.UprightPose(orientation, mounted);
            check(upright.Translation.GetDistance(position) < 1e-12, $"{orientation}: upright pose keeps position");
            check(SameRotation(upright.Rotation, robot), $"{orientation}: upright pose has the robot's attitude");

            var (rawTx, rawTy) = Angles(target.RelativeTo(mounted).Translation);
            var (uprightTx, uprightTy) = Angles(target.RelativeTo(upright).Translation);
            CameraOrientationMath.RotateAngles(orientation, rawTx, rawTy, out double tx, out double ty);
            check(Math.Abs(tx - uprightTx) < 1e-9 && Math.Abs(ty - uprightTy) < 1e-9,
                $"{orientation}: rotated tx/ty match an upright headset");
        }
    }

    /// <summary>Pinhole angles of a point in WPILib camera axes (x forward, y left, z up).</summary>
    static (double Tx, double Ty) Angles(Translation3d p) =>
        (Math.Atan2(-p.Y, p.X), Math.Atan2(p.Z, p.X));

    static bool SameRotation(Rotation3d a, Rotation3d b)
    {
        var p = a.Quaternion;
        var q = b.Quaternion;
        return Math.Abs(Math.Abs(p.W * q.W + p.X * q.X + p.Y * q.Y + p.Z * q.Z) - 1) < 1e-12;
    }
}
