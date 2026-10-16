using System.Collections.Generic;
using Meta.XR;
using QuestX.Core;
using QuestX.Utils;
using UnityEngine;

namespace QuestX.VioQuality
{
    /// <summary>
    /// Samples Meta environment depth with a fixed fan of raycasts from the headset and feeds the hits to a
    /// <see cref="DepthDriftEstimator"/>. Hits come back in Unity world space; they are converted into the rig's tracking
    /// space (the local frame of the VIO pose), into FRC axes, and then into the published VIO frame of
    /// <see cref="VioReferenceFrame"/>, so drift is reported in the same frame as /v2/vio.
    /// </summary>
    public sealed class VioQualityMonitor
    {
        private readonly OVRCameraRig cameraRig;
        private readonly EnvironmentRaycastManager raycastManager;
        private readonly DepthDriftEstimator estimator = new DepthDriftEstimator();
        private readonly List<Vector3> rayDirections = new List<Vector3>();
        private readonly List<DepthPoint> points = new List<DepthPoint>();
        private readonly bool supported;

        public VioQualityMonitor(OVRCameraRig cameraRig, GameObject host)
        {
            this.cameraRig = cameraRig;
            var c = QuestXConstants.VioQuality.RAY_COLUMNS;
            var r = QuestXConstants.VioQuality.RAY_ROWS;
            for (int i = 0; i < r; i++)
            {
                float pitch = Mathf.Lerp(
                    QuestXConstants.VioQuality.RAY_PITCH_MIN_DEG,
                    QuestXConstants.VioQuality.RAY_PITCH_MAX_DEG,
                    r == 1 ? 0.5f : (float)i / (r - 1)
                );
                for (int j = 0; j < c; j++)
                {
                    float yaw = Mathf.Lerp(
                        -QuestXConstants.VioQuality.RAY_HALF_YAW_DEG,
                        QuestXConstants.VioQuality.RAY_HALF_YAW_DEG,
                        c == 1 ? 0.5f : (float)j / (c - 1)
                    );
                    // Unity: +pitch about X looks down, so negate to make positive pitch look up.
                    rayDirections.Add(Quaternion.Euler(-pitch, yaw, 0) * Vector3.forward);
                }
            }

            supported = QuestXConstants.VioQuality.ENABLED && EnvironmentRaycastManager.IsSupported;
            if (supported)
            {
                raycastManager = host.GetComponent<EnvironmentRaycastManager>();
                if (raycastManager == null) // Unity null check (not ??) so destroyed components count as missing
                    raycastManager = host.AddComponent<EnvironmentRaycastManager>();
            }
            else
            {
                QueuedLogger.LogWarning(
                    "VIO quality: environment raycast not supported or disabled; publishing tracking state only"
                );
            }
        }

        /// <summary>Takes one depth sample and compares it with the keyframe. Call from the main thread.</summary>
        /// <param name="appTime">App time in seconds</param>
        /// <param name="vioFrame">Published VIO frame; a new epoch or a settling recenter restarts the keyframe</param>
        public VioQualitySample Sample(double appTime, VioReferenceFrame vioFrame)
        {
            var sample = new VioQualitySample
            {
                TrackingEpoch = vioFrame.Epoch,
                PositionTracked = OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.Head),
                PositionValid = OVRPlugin.GetNodePositionValid(OVRPlugin.Node.Head),
                OrientationTracked = OVRPlugin.GetNodeOrientationTracked(OVRPlugin.Node.Head),
                Rays = rayDirections.Count,
            };
            var w = OVRPlugin.GetNodeAngularVelocity(OVRPlugin.Node.Head, OVRPlugin.Step.Render);
            var v = OVRPlugin.GetNodeVelocity(OVRPlugin.Node.Head, OVRPlugin.Step.Render);
            sample.AngularSpeedRadPerSec = Mathf.Sqrt(w.x * w.x + w.y * w.y + w.z * w.z);
            sample.LinearSpeedMetersPerSec = Mathf.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

            if (!supported)
            {
                sample.Drift.Status = DriftStatus.NotSupported;
                return sample;
            }
            if (!raycastManager.isActiveAndEnabled)
            {
                sample.Drift.Status = DriftStatus.NotReady;
                return sample;
            }

            Transform eye = cameraRig.centerEyeAnchor;
            Transform space = cameraRig.trackingSpace;
            double yaw = vioFrame.YawCorrectionRadians;
            double cos = System.Math.Cos(yaw);
            double sin = System.Math.Sin(yaw);
            var offset = vioFrame.Offset;
            points.Clear();
            int near = 0;
            bool anyReady = false;
            foreach (var dir in rayDirections)
            {
                var ray = new Ray(eye.position, eye.rotation * dir);
                bool hit = raycastManager.Raycast(
                    ray,
                    out var result,
                    QuestXConstants.VioQuality.MAX_RANGE_METERS
                );
                if (result.status != EnvironmentRaycastHitStatus.NotReady)
                    anyReady = true;
                if (
                    !hit
                    || result.normalConfidence < QuestXConstants.VioQuality.MIN_NORMAL_CONFIDENCE
                )
                    continue;
                float range = Vector3.Distance(eye.position, result.point);
                if (range < QuestXConstants.VioQuality.MIN_RANGE_METERS)
                {
                    near++;
                    continue;
                }
                Vector3 p = space.InverseTransformPoint(result.point);
                Vector3 n = space.InverseTransformDirection(result.normal).normalized;
                var lp = ToFrc(p);
                var ln = ToFrc(n);
                points.Add(
                    new DepthPoint(
                        new Vec3d(
                            cos * lp.X - sin * lp.Y + offset.X,
                            sin * lp.X + cos * lp.Y + offset.Y,
                            lp.Z + offset.Z
                        ),
                        new Vec3d(cos * ln.X - sin * ln.Y, sin * ln.X + cos * ln.Y, ln.Z),
                        range
                    )
                );
            }
            sample.NearFraction = (double)near / rayDirections.Count;
            if (!anyReady)
            {
                sample.Drift.Status = DriftStatus.NotReady;
                return sample;
            }

            // While a recenter settles the local origin may still move, so report not tracking (drops the keyframe).
            bool tracking = sample.PositionTracked && vioFrame.PublishedTracking;
            sample.Drift = estimator.Update(points, appTime, tracking, vioFrame.Epoch);
            return sample;
        }

        /// <summary>Unity tracking-space vector to FRC axes (X forward = Unity Z, Y left = -Unity X, Z up = Unity Y).</summary>
        private static Vec3d ToFrc(Vector3 u) => new Vec3d(u.z, -u.x, u.y);
    }
}
