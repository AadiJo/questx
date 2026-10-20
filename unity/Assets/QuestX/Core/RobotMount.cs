using System;
using QuestX.QuestX.Geometry;

namespace QuestX.Core
{
    /// <summary>
    /// Where the headset's center eye sits on the robot. Every pose QuestX publishes is the
    /// robot's pose, derived from the measured headset pose through this transform, so robot
    /// code must not apply a robot-to-Quest offset of its own.
    /// </summary>
    /// <remarks>
    /// The transform and its inverse are swapped together as one object, so readers on other
    /// threads (the web server) always see a consistent pair.
    /// </remarks>
    public sealed class RobotMount
    {
        private sealed class State
        {
            public readonly Transform3d RobotToHeadset;
            public readonly Transform3d HeadsetToRobot;

            public State(Transform3d robotToHeadset)
            {
                RobotToHeadset = robotToHeadset;
                HeadsetToRobot = robotToHeadset.Inverse();
            }
        }

        private State state = new State(Transform3d.Zero);

        /// <summary>The headset center-eye pose in the robot frame.</summary>
        public Transform3d RobotToHeadset => state.RobotToHeadset;

        /// <summary>The robot origin's pose in the headset center-eye frame.</summary>
        public Transform3d HeadsetToRobot => state.HeadsetToRobot;

        /// <summary>Replaces the transform. Angles are in degrees, translation in meters.</summary>
        /// <returns>True if the transform changed.</returns>
        public bool Set(
            double xMeters,
            double yMeters,
            double zMeters,
            double rollDegrees,
            double pitchDegrees,
            double yawDegrees
        )
        {
            const double radiansPerDegree = Math.PI / 180.0;
            var next = new Transform3d(
                new Translation3d(xMeters, yMeters, zMeters),
                new Rotation3d(
                    rollDegrees * radiansPerDegree,
                    pitchDegrees * radiansPerDegree,
                    yawDegrees * radiansPerDegree
                )
            );
            if (next == state.RobotToHeadset)
            {
                return false;
            }
            state = new State(next);
            return true;
        }

        /// <summary>Robot pose for a measured headset pose, in the same frame.</summary>
        public Pose3d HeadsetToRobotPose(Pose3d headsetPose) =>
            headsetPose.TransformBy(state.HeadsetToRobot);

        /// <summary>Headset pose for a robot pose, in the same frame.</summary>
        public Pose3d RobotToHeadsetPose(Pose3d robotPose) =>
            robotPose.TransformBy(state.RobotToHeadset);
    }
}
