using System;
using QuestX.QuestX.Geometry;
using QuestX.Utils;
using UnityEngine;

namespace QuestX.QuestX.AprilTag
{
    /// <summary>Keep the sensor-frame conversion and physical acceptance checks in one place.</summary>
    public static class AprilTagPoseValidation
    {
        /// <summary>
        /// PoseLib locates the lens. VIO locates the headset center eye. Undo the SDK's
        /// calibrated headset-to-lens transform before combining these measurements.
        /// The robot's separate mounting transform is deliberately not applied here.
        /// </summary>
        public static Pose3d CameraToHeadset(Pose3d fieldCamera, Pose lensOffset)
        {
            var headsetToCamera = new Pose3d(
                Conversions.UnityToFrc3d(lensOffset.position, lensOffset.rotation)
            );
            return fieldCamera.TransformBy(
                new Transform3d(headsetToCamera.Translation, headsetToCamera.Rotation).Inverse()
            );
        }

        public static bool TryValidate(
            Pose3d camera,
            Field2d field,
            double inliers,
            int corners,
            out string reason
        )
        {
            var q = camera.Rotation.Quaternion;
            double[] values = { camera.X, camera.Y, camera.Z, q.W, q.X, q.Y, q.Z, inliers };
            foreach (double value in values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    reason = "Pose solve contains a non-finite value";
                    return false;
                }
            }
            if (corners < 4 || inliers < 4 || inliers / corners < 0.75)
            {
                reason =
                    $"Insufficient inliers: {inliers}/{corners}, require four and at least 75%";
                return false;
            }
            // Allow practice targets and a robot just outside the carpet, but reject
            // mirrored solutions below the floor or far beyond the selected field.
            if (
                camera.Z < -0.25
                || camera.Z > 3.5
                || field == null
                || field.X <= 0
                || field.Y <= 0
                || camera.X < -2
                || camera.X > field.X + 2
                || camera.Y < -2
                || camera.Y > field.Y + 2
            )
            {
                reason = "Solved camera position is outside the physical field/height limits";
                return false;
            }
            reason = "";
            return true;
        }

        /// <summary>
        /// WPILib tag +X is the printed face normal. A detected tag must be in front
        /// of the camera, and the camera must be on the printed side of the tag.
        /// This rejects physically impossible mirrored/back-facing PnP solutions.
        /// It is not a numeric ambiguity score for the remaining planar solutions.
        /// </summary>
        public static bool IsFacingCamera(Pose3d camera, Pose3d tag)
        {
            var cameraToTag = tag.Translation.Minus(camera.Translation);
            var inCamera = cameraToTag.RotateBy(camera.Rotation.UnaryMinus());
            var tagToCamera = camera
                .Translation.Minus(tag.Translation)
                .RotateBy(tag.Rotation.UnaryMinus());
            return inCamera.X > 0.05 && tagToCamera.X > 0.02;
        }
    }
}
