using System;
using System.Collections.Generic;
using QuestX.QuestX.AprilTag;
using QuestX.QuestX.Geometry;

namespace QuestX.QuestX.Native.PoseLib
{
    public unsafe class PoseLibResult
    {
        /// <summary>
        /// Represents the position of the camera in 3d field-relative FRC coordinate space
        /// </summary>
        public Pose3d CameraPose { get; }

        /// <summary>
        /// Native point-inlier count over all candidate corners, including corners from tags
        /// rejected by the later complete-tag selection. Retained for diagnostics and gating.
        /// </summary>
        public double AcceptedPoints { get; }

        /// <summary>
        /// Original candidate corner count fed to the solver, before complete-tag selection.
        /// It may exceed four times the published tag count; used with AcceptedPoints for gating.
        /// </summary>
        public int TotalPoints { get; }

        /// <summary>
        /// Planar ambiguity and reprojection error over accepted complete tags only.
        /// Per-tag ambiguities follow AcceptedTagIndices order.
        /// </summary>
        public AprilTagAmbiguity.Result Quality { get; }

        /// <summary>
        /// The camera's field pose (WPILib camera axes) with its covariance, or null when the
        /// corners don't constrain every axis. See <see cref="AprilTagCovariance"/>.
        /// </summary>
        public PoseCovariance Covariance { get; }

        /// <summary>
        /// Indices into the solver's input detection list of complete tags whose four residuals
        /// pass at the native pose. These same tags contribute quality, covariance and observations.
        /// </summary>
        public IReadOnlyList<int> AcceptedTagIndices { get; }

        /// <summary>
        /// Creates a new CPnPResult from the native struct returned by the estimator
        /// </summary>
        /// <param name="cameraPose">The native CameraPose to convert</param>
        /// <param name="numInliersOut">The inlier count to convert</param>
        /// <param name="totalPoints">Total number of point correspondences</param>
        /// <param name="quality">Ambiguity and fit error evaluated for this pose</param>
        /// <param name="covariance">The camera's field pose and covariance, or null</param>
        /// <param name="acceptedTagIndices">Contributing complete tags, in original input order</param>
        public PoseLibResult(
            PoseLibNatives.PoseLibCameraPoseNative cameraPose,
            ulong numInliersOut,
            int totalPoints,
            AprilTagAmbiguity.Result quality,
            PoseCovariance covariance,
            int[] acceptedTagIndices
        )
        {
            var q = new Quaternion(
                cameraPose.Q[0],
                cameraPose.Q[1],
                cameraPose.Q[2],
                cameraPose.Q[3]
            );
            var t = new Translation3d(cameraPose.T[0], cameraPose.T[1], cameraPose.T[2]);

            CameraPose = new Pose3d(t, new Rotation3d(q));

            AcceptedPoints = numInliersOut;
            TotalPoints = totalPoints;
            Quality = quality;
            Covariance = covariance;
            AcceptedTagIndices = Array.AsReadOnly((int[])acceptedTagIndices.Clone());
        }
    }
}
