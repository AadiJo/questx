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

`/QuestX/vioData` contains the center-eye anchor pose relative to the Oculus tracking space. The app converts its axes into WPILib conventions, but it does not turn the arbitrary tracking-space origin into a field origin.

The pose intentionally excludes the Unity camera-rig root. Legacy display-space pose resets therefore do not alter the measurement stream.

An Oculus recenter can redefine tracking space. The `trackingEpoch` field identifies each continuous local frame.

## Field-relative AprilTag pose

The field layout stores known tag poses in WPILib field coordinates. PoseLib solves the passthrough camera pose in computer-vision coordinates, after which `CvToFrc` converts the result to a field-relative `Pose3d`.

That result describes the Quest camera, not the robot center. Robot code must apply the measured `ROBOT_TO_QUEST` mounting transform before using it as a robot vision pose.

## Frame alignment

Robot code maintains a rigid local-VIO-to-field transform per tracking epoch. Establish or update it using a trusted field reference—normally a temporally corresponding AprilTag observation—and never combine VIO samples from different epochs.

All message translations use meters and all rotations use radians.
