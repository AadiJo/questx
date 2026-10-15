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
        /// How many points of the points inputted were fit to the model (inliers)
        /// </summary>
        public double AcceptedPoints { get; }

        /// <summary>
        /// Total number of 2D-3D point correspondences fed to the solver
        /// </summary>
        public int TotalPoints { get; }

        /// <summary>
        /// Planar pose ambiguity and reprojection error of this solve over every correspondence
        /// </summary>
        public AprilTagAmbiguity.Result Quality { get; }

        /// <summary>
        /// Creates a new CPnPResult from the native struct returned by the estimator
        /// </summary>
        /// <param name="cameraPose">The native CameraPose to convert</param>
        /// <param name="numInliersOut">The inlier count to convert</param>
        /// <param name="totalPoints">Total number of point correspondences</param>
        /// <param name="quality">Ambiguity and fit error evaluated for this pose</param>
        public PoseLibResult(
            PoseLibNatives.PoseLibCameraPoseNative cameraPose,
            ulong numInliersOut,
            int totalPoints,
            AprilTagAmbiguity.Result quality
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
        }
    }
}
