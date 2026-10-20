using System;

namespace QuestX.Camera
{
    /// <summary>
    /// How the headset is rolled about its forward axis, viewed from behind it (as worn).
    /// Images and image-space angles are rotated to undo the roll so they read upright.
    /// </summary>
    /// <remarks>
    /// Like Limelight's stream orientation, this is display only: it never changes a reported
    /// pose. The physical roll belongs in the robot-to-Quest transform, which is the only place
    /// poses learn how the headset is mounted (see <see cref="CameraOrientationMath.MountRollRadians"/>
    /// for the matching roll).
    /// </remarks>
    public enum CameraOrientation
    {
        /// <summary>Mounted upright. Nothing is rotated.</summary>
        NORMAL = 0,

        /// <summary>Rolled 180°. Images are rotated 180°.</summary>
        UPSIDE_DOWN = 1,

        /// <summary>Rolled 90° to the left (counter-clockwise). Images are rotated 90° counter-clockwise.</summary>
        LEFT = 2,

        /// <summary>Rolled 90° to the right (clockwise). Images are rotated 90° clockwise.</summary>
        RIGHT = 3,
    }

    /// <summary>
    /// Maps raw camera images to the upright frame for a <see cref="CameraOrientation"/>.
    /// Pixel coordinates are top-left origin with y down; angles are right- and up-positive.
    /// </summary>
    public static class CameraOrientationMath
    {
        public static bool IsValid(int value) =>
            value >= (int)CameraOrientation.NORMAL && value <= (int)CameraOrientation.RIGHT;

        /// <summary>
        /// The roll about the headset's forward (+X) axis that this orientation implies, in WPILib
        /// axes (rolled right is positive). It is the robot-to-Quest roll for a headset mounted
        /// level and facing forward; the web UI uses it to flag a mount that disagrees.
        /// </summary>
        public static double MountRollRadians(CameraOrientation orientation) =>
            orientation switch
            {
                CameraOrientation.UPSIDE_DOWN => Math.PI,
                CameraOrientation.LEFT => -Math.PI / 2,
                CameraOrientation.RIGHT => Math.PI / 2,
                _ => 0,
            };

        /// <summary>Whether the upright image swaps the raw width and height.</summary>
        public static bool SwapsAxes(CameraOrientation orientation) =>
            orientation == CameraOrientation.LEFT || orientation == CameraOrientation.RIGHT;

        /// <summary>Size of the upright image for a raw image of the given size.</summary>
        public static void RotateSize(
            CameraOrientation orientation,
            int width,
            int height,
            out int rotatedWidth,
            out int rotatedHeight
        )
        {
            bool swap = SwapsAxes(orientation);
            rotatedWidth = swap ? height : width;
            rotatedHeight = swap ? width : height;
        }

        /// <summary>
        /// Maps a continuous raw-image point (pixel edges at integers) into the upright image.
        /// </summary>
        public static void RotatePoint(
            CameraOrientation orientation,
            double x,
            double y,
            int width,
            int height,
            out double rotatedX,
            out double rotatedY
        )
        {
            switch (orientation)
            {
                case CameraOrientation.UPSIDE_DOWN:
                    rotatedX = width - x;
                    rotatedY = height - y;
                    break;
                case CameraOrientation.LEFT:
                    rotatedX = y;
                    rotatedY = width - x;
                    break;
                case CameraOrientation.RIGHT:
                    rotatedX = height - y;
                    rotatedY = x;
                    break;
                default:
                    rotatedX = x;
                    rotatedY = y;
                    break;
            }
        }

        /// <summary>
        /// Raw pixel that lands at upright pixel (<paramref name="rotatedX"/>, <paramref name="rotatedY"/>).
        /// </summary>
        public static void SourcePixel(
            CameraOrientation orientation,
            int rotatedX,
            int rotatedY,
            int width,
            int height,
            out int x,
            out int y
        )
        {
            switch (orientation)
            {
                case CameraOrientation.UPSIDE_DOWN:
                    x = width - 1 - rotatedX;
                    y = height - 1 - rotatedY;
                    break;
                case CameraOrientation.LEFT:
                    x = width - 1 - rotatedY;
                    y = rotatedX;
                    break;
                case CameraOrientation.RIGHT:
                    x = rotatedY;
                    y = height - 1 - rotatedX;
                    break;
                default:
                    x = rotatedX;
                    y = rotatedY;
                    break;
            }
        }

        /// <summary>
        /// Rotates horizontal (right-positive) and vertical (up-positive) angles from the
        /// principal point into the upright image, as Limelight's tx and ty do.
        /// </summary>
        public static void RotateAngles(
            CameraOrientation orientation,
            double tx,
            double ty,
            out double rotatedTx,
            out double rotatedTy
        )
        {
            switch (orientation)
            {
                case CameraOrientation.UPSIDE_DOWN:
                    rotatedTx = -tx;
                    rotatedTy = -ty;
                    break;
                case CameraOrientation.LEFT:
                    rotatedTx = -ty;
                    rotatedTy = tx;
                    break;
                case CameraOrientation.RIGHT:
                    rotatedTx = ty;
                    rotatedTy = -tx;
                    break;
                default:
                    rotatedTx = tx;
                    rotatedTy = ty;
                    break;
            }
        }
    }
}
