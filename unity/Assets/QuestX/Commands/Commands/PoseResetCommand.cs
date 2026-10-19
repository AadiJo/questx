using System;
using QuestX.Core;
using QuestX.Protos.Generated;
using QuestX.QuestX.Geometry;
using QuestX.Utils;
using UnityEngine;
using Wpi.Proto;
using Quaternion = UnityEngine.Quaternion;

namespace QuestX.Commands.Commands
{
    /// <summary>
    /// Re-bases the published VIO frame on a target pose and applies the matching display-space
    /// offset to the VR camera rig.
    /// </summary>
    public class PoseResetCommand : ICommand
    {
        private readonly ICommandContext commandContext;
        private readonly VioReferenceFrame vioFrame;
        private readonly Transform vrCamera;
        private readonly Transform vrCameraRoot;
        private readonly Transform resetTransform;

        /// <summary>
        /// Initializes a new instance of the PoseResetCommand.
        /// </summary>
        public PoseResetCommand(
            ICommandContext commandContext,
            VioReferenceFrame vioFrame,
            Transform vrCamera,
            Transform vrCameraRoot,
            Transform resetTransform
        )
        {
            this.commandContext = commandContext;
            this.vioFrame = vioFrame;
            this.vrCamera = vrCamera;
            this.vrCameraRoot = vrCameraRoot;
            this.resetTransform = resetTransform;
        }

        /// <summary>
        /// The formatted name for PoseResetCommand.
        /// </summary>
        public string CommandNiceName => "PoseReset";

        /// <summary>
        /// Executes the pose reset. The next published VIO sample reports the target's position
        /// and yaw (roll and pitch stay measured) and carries a new tracking epoch. Success is
        /// reported only after the VIO frame has been re-based.
        /// </summary>
        public void Execute(ProtobufQuestXCommand receivedCommand)
        {
            QueuedLogger.Log("Received pose reset request, initiating reset...");

            // Read pose data from network tables
            ProtobufPose3d resetPose = receivedCommand.PoseResetPayload?.TargetPose;
            if (resetPose?.Translation == null || resetPose.Rotation?.Q == null)
            {
                Fail(receivedCommand.CommandId, "Pose reset command has no target pose");
                return;
            }
            double poseX = resetPose.Translation.X;
            double poseY = resetPose.Translation.Y;
            double poseZ = resetPose.Translation.Z;
            double poseQx = resetPose.Rotation.Q.X;
            double poseQy = resetPose.Rotation.Q.Y;
            double poseQz = resetPose.Rotation.Q.Z;
            double poseQW = resetPose.Rotation.Q.W;

            // Validate pose data
            double[] values = { poseX, poseY, poseZ, poseQx, poseQy, poseQz, poseQW };
            foreach (double value in values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    Fail(receivedCommand.CommandId, "Failed to get valid pose data (non-finite)");
                    return;
                }
            }
            double quaternionNorm = Math.Sqrt(
                poseQx * poseQx + poseQy * poseQy + poseQz * poseQz + poseQW * poseQW
            );
            if (quaternionNorm < 1e-6)
            {
                Fail(receivedCommand.CommandId, "Failed to get valid pose data (zero rotation)");
                return;
            }

            // Additional validation for field boundaries
            bool inBounds =
                poseX >= 0
                && poseX <= QuestXConstants.Field.FIELD_LENGTH
                && poseY >= 0
                && poseY <= QuestXConstants.Field.FIELD_WIDTH;

            if (!inBounds)
            {
                QueuedLogger.LogWarning($"Pose out of field bounds: ({poseX}, {poseY})");
            }

            // Re-base VIO first: if the headset cannot anchor the reset, nothing changes.
            var target = new Pose3d(
                new Translation3d(poseX, poseY, poseZ),
                new Rotation3d(
                    new global::QuestX.QuestX.Geometry.Quaternion(
                        poseQW / quaternionNorm,
                        poseQx / quaternionNorm,
                        poseQy / quaternionNorm,
                        poseQz / quaternionNorm
                    )
                )
            );
            if (!vioFrame.TryReset(target, out string reason))
            {
                Fail(receivedCommand.CommandId, reason);
                return;
            }

            // Display-space offset for the rig (does not affect published VIO).
            // Step 1: Convert FRC field coordinates to Unity coordinates
            var (targetCameraPosition, targetCameraRotation) = Conversions.FrcToUnity3d(resetPose);

            // Step 2: Calculate rotation difference between current camera and target
            Quaternion newRotation =
                targetCameraRotation * Quaternion.Inverse(vrCamera.localRotation);

            // Step 3: Apply rotation to root
            vrCameraRoot.rotation = newRotation;

            // Step 4: Recalculate position after rotation
            Vector3 newRootPosition = targetCameraPosition - (newRotation * vrCamera.localPosition);

            // Step 5: Apply the new position to vrCameraRoot
            vrCameraRoot.position = newRootPosition;

            QueuedLogger.Log(
                $"Pose reset applied: X={poseX}, Y={poseY}, Z={poseZ} Rotation W={poseQW}, X={poseQx}, "
                    + $"Y={poseQy}, Z={poseQz}; VIO tracking epoch is now {vioFrame.Epoch}"
            );

            commandContext.SendSuccessResponse(receivedCommand.CommandId);
            QueuedLogger.Log("Pose reset completed successfully");
        }

        private void Fail(uint commandId, string reason)
        {
            commandContext.SendErrorResponse(commandId, reason);
            QueuedLogger.LogError($"Pose reset rejected: {reason}");
        }
    }
}
