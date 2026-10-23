using System.Collections;
using System.Reflection;
using Meta.XR;
using QuestX.Core;
using QuestX.VioQuality;
using UnityEngine;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

// Compare every stored field, including IEEE bits (NaN and signed zero), without
// serializing computed Vector3.normalized recursively or skipping read-only fields.
bool Equal(object a, object b)
{
    if (a == null || b == null) return a == b;
    if (a.GetType() != b.GetType()) return false;
    if (a is double d) return BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits((double)b);
    if (a is float f) return BitConverter.SingleToInt32Bits(f) == BitConverter.SingleToInt32Bits((float)b);
    if (a is IEnumerable items)
        return items.Cast<object>().Zip(((IEnumerable)b).Cast<object>(), Equal).All(x => x)
            && items.Cast<object>().Count() == ((IEnumerable)b).Cast<object>().Count();
    if (a.GetType().IsPrimitive || a.GetType().IsEnum) return a.Equals(b);
    return a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
        .All(field => Equal(field.GetValue(a), field.GetValue(b)));
}
object Points(object monitor) => monitor.GetType().GetField("points", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(monitor);

Check(QuestXConstants.VioQuality.UPDATE_HZ == 15, "Keep the 15 Hz schedule constant");
Check(QuestXConstants.VioQuality.RAY_COLUMNS * QuestXConstants.VioQuality.RAY_ROWS == 160, "Keep all 160 rays");
foreach (string scenario in new[] { "all", "mixed", "not-ready", "inactive", "unsupported", "tracking-loss", "settling", "miss", "tracking-flags" })
{
    EnvironmentRaycastManager.IsSupported = scenario != "unsupported";
    var oldRig = new OVRCameraRig();
    var newRig = new OVRCameraRig();
    var oldHost = new GameObject();
    var newHost = new GameObject();
    oldHost.Manager.Mode = newHost.Manager.Mode = scenario switch
    {
        "mixed" => HitMode.Mixed, "not-ready" => HitMode.NotReady, "miss" => HitMode.Miss, _ => HitMode.All
    };
    oldHost.Manager.isActiveAndEnabled = newHost.Manager.isActiveAndEnabled = scenario != "inactive";
    var original = new UncachedMonitor(oldRig, oldHost);
    var optimized = new VioQualityMonitor(newRig, newHost);
    var frame = new VioReferenceFrame();
    for (int step = 0; step < 6; step++)
    {
        // Refresh position AND rotation each call; also exercise a rotated, nonuniformly
        // scaled tracking space and the published-frame yaw/offset conversion.
        oldRig.centerEyeAnchor.Position = newRig.centerEyeAnchor.Position = new Vector3(1 + step * .01f, 2, 3);
        oldRig.centerEyeAnchor.Rotation = newRig.centerEyeAnchor.Rotation = Quaternion.Euler(10 + step, 20 - step, 30 + step);
        if (step == 4) { frame.Epoch++; frame.YawCorrectionRadians = -.3; frame.Offset = new(-.2, .5, .1); }
        OVRPlugin.Tracking = scenario != "tracking-loss" || step != 2;
        OVRPlugin.Valid = scenario != "tracking-flags" || step != 2;
        OVRPlugin.Orientation = scenario != "tracking-flags" || step != 3;
        frame.PublishedTracking = scenario != "settling" || step != 2;
        oldHost.Manager.Rays.Clear();
        newHost.Manager.Rays.Clear();
        int oldReads = oldRig.centerEyeAnchor.Positions + oldRig.centerEyeAnchor.Rotations;
        int positions = newRig.centerEyeAnchor.Positions, rotations = newRig.centerEyeAnchor.Rotations;
        int inversePoints = newRig.trackingSpace.InversePoints, inverseDirections = newRig.trackingSpace.InverseDirections;
        var a = original.Sample(step / 15.0, frame);
        var b = optimized.Sample(step / 15.0, frame);
        string label = $"{scenario} step {step}";
        Check(Equal(a, b), $"{label}: full quality sample differs");
        Check(Equal(oldHost.Manager.Rays, newHost.Manager.Rays), $"{label}: ray origins/directions differ");
        Check(Equal(Points(original), Points(optimized)), $"{label}: estimator point clouds differ");
        bool active = scenario != "unsupported" && scenario != "inactive";
        Check(newRig.centerEyeAnchor.Positions - positions == (active ? 1 : 0), $"{label}: eye position read once after readiness");
        Check(newRig.centerEyeAnchor.Rotations - rotations == (active ? 1 : 0), $"{label}: eye rotation read once after readiness");
        Check(newHost.Manager.Rays.Count == (active ? 160 : 0), $"{label}: ray count");
        int hits = !active || scenario == "not-ready" || scenario == "miss" ? 0 : scenario == "mixed" ? 40 : 160;
        Check(newRig.trackingSpace.InversePoints - inversePoints == hits, $"{label}: preserve every inverse point transform");
        Check(newRig.trackingSpace.InverseDirections - inverseDirections == hits, $"{label}: preserve every inverse direction transform");
        Check(b.NearFraction == (scenario == "mixed" ? .25 : 0), $"{label}: near-hit fraction");
        if (scenario == "unsupported") Check(b.Drift.Status == DriftStatus.NotSupported, label);
        else if (scenario == "inactive" || scenario == "not-ready") Check(b.Drift.Status == DriftStatus.NotReady, label);
        else if (!OVRPlugin.Tracking || !frame.PublishedTracking) Check(b.Drift.Status == DriftStatus.NotTracking, label);
        else if (scenario == "miss") Check(b.Drift.Status == DriftStatus.TooFewHits, label);
        else if (step == 0 || step == 4) Check(b.Drift.Status == DriftStatus.NewKeyframe, label);
        else if (scenario == "all") Check(b.Drift.Status == DriftStatus.Ok, label);
        if (step == 0)
            Console.WriteLine($"{scenario}: rays={newHost.Manager.Rays.Count}, eye reads={oldRig.centerEyeAnchor.Positions + oldRig.centerEyeAnchor.Rotations - oldReads} -> {newRig.centerEyeAnchor.Positions + newRig.centerEyeAnchor.Rotations - positions - rotations}");
    }
}
Console.WriteLine($"PASS: {checks} monitor checks; exact rays, clouds and complete samples versus the uncached reference. Unity/OpenXR are stand-ins; no device timing claims.");
