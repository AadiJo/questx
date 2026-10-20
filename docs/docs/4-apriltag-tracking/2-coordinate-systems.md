---
title: Coordinate Systems
---

# Coordinate Systems

QuestX crosses three coordinate systems:

| System | X | Y | Z | Use |
|---|---|---|---|---|
| FRC / WPILib | forward | left | up | field layouts, messages, robot estimator |
| Unity tracking | right | up | forward | local Quest VIO |
| Computer vision | right | down | forward | AprilTag/PoseLib solving |

## Local VIO

`/QuestX/vioData` contains the robot origin pose relative to the Oculus tracking space. The headset tracks its center-eye anchor and converts it to the robot origin with `headsetPose.transformBy(robotToQuest.inverse())`, using the robot-to-Quest mount set on the web interface's **3D view** page. The app converts its axes into WPILib conventions, but it does not turn the arbitrary tracking-space origin into a field origin.

The pose intentionally excludes the Unity camera-rig root. Legacy display-space pose resets therefore do not alter the measurement stream.

An Oculus recenter can redefine tracking space. Changing the robot-to-Quest mount also starts a new epoch. The `trackingEpoch` field identifies each continuous local frame.

## Field-relative AprilTag pose

The field layout stores known tag poses in WPILib field coordinates. PoseLib solves the passthrough camera pose in computer-vision coordinates, after which `CvToFrc` converts the result to a field-relative `Pose3d`.

The headset converts that result to the robot origin through the robot-to-Quest mount before publishing it. `/QuestX/aprilTagData` and the Limelight-compatible `botpose_wpiblue` therefore describe the robot, and robot code uses them directly as robot vision poses. Do not apply the mount again in robot code. `headsetpose_wpiblue` still reports the headset center eye.

## Frame alignment

Robot code maintains a rigid local-VIO-to-field transform per tracking epoch. Establish or update it using a trusted field reference—normally a temporally corresponding AprilTag observation—and never combine VIO samples from different epochs.

All message translations use meters and all rotations use radians.
