using System;
using QuestX.Core;
using QuestX.QuestX.Geometry;

/// <summary>Checks pose-reset re-basing and recenter epoch handling.</summary>
static class VioFrameChecks
{
    public static void Run(Action<bool, string> check)
    {
        const double Tolerance = 1e-9;
        bool Near(double a, double b) => Math.Abs(a - b) < Tolerance;
        bool SameAngle(double a, double b) => Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI)) < Tolerance;

        var frame = new VioReferenceFrame(0.1);

        // Before any reset, published VIO is the local pose.
        check(!frame.TryReset(Pose3d.Zero, out _), "Reset needs a VIO sample");
        var local = new Pose3d(1, 2, 0.5, new Rotation3d(-0.1, 0.2, 1.0));
        frame.Sample(local, true, 1.0);
        check(frame.PublishedPose.Equals(local), "Published VIO starts in the local frame");
        check(frame.Epoch == 0, "Epoch starts at zero");

        // Reset: position and yaw match the target; roll and pitch remain measured.
        var target = new Pose3d(3, 4, 0.7, new Rotation3d(0, 0, -0.5));
        check(frame.TryReset(target, out string reason) && reason == "", "Tracking headset accepts a reset");
        check(frame.Epoch == 1, "Reset starts a new epoch");
        var published = frame.PublishedPose;
        check(Near(published.X, 3) && Near(published.Y, 4) && Near(published.Z, 0.7), "Reset position matches target");
        check(SameAngle(published.Rotation.Z, -0.5), "Reset yaw matches target");
        check(SameAngle(published.Rotation.X, -0.1) && SameAngle(published.Rotation.Y, 0.2),
            "Reset keeps measured roll and pitch (gravity-aligned frame)");

        // Motion after the reset is unchanged: the published frame is a rigid transform of the local one.
        var motion = new Transform3d(0.4, -0.2, 0.05, new Rotation3d(0.02, -0.01, 0.3));
        var moved = local.TransformBy(motion);
        frame.Sample(moved, true, 1.01);
        var delta = frame.PublishedPose.RelativeTo(published);
        check(Near(delta.X, 0.4) && Near(delta.Y, -0.2) && Near(delta.Z, 0.05), "Body-frame motion is preserved");
        check(SameAngle(delta.Rotation.Z, 0.3), "Heading change is preserved");

        // A second reset replaces the first rather than compounding it.
        check(frame.TryReset(new Pose3d(5, 1, 0.7, new Rotation3d(0, 0, 2.0)), out _), "Second reset");
        check(frame.Epoch == 2, "Each reset starts a new epoch");
        check(Near(frame.PublishedPose.X, 5) && SameAngle(frame.PublishedPose.Rotation.Z, 2.0), "Second reset target");

        // A headset that is not tracking cannot anchor a reset; nothing changes.
        frame.Sample(moved, false, 1.02);
        var beforeRejected = frame.PublishedPose;
        check(!frame.TryReset(target, out reason) && reason.Contains("not tracking"), "Untracked reset rejected");
        check(frame.Epoch == 2 && frame.PublishedPose.Equals(beforeRejected), "Rejected reset leaves the frame alone");

        // Recenter: the announcement precedes the origin move. Held samples get their own epoch,
        // repeat the last pre-recenter pose, and read as not tracking; live samples resume in
        // another epoch. No epoch ever mixes the two origins.
        frame.Sample(moved, true, 2.0);
        var beforeRecenter = frame.PublishedPose;
        check(frame.PublishedTracking, "Tracking sample before the recenter");
        frame.BeginOriginChange(2.0);
        check(frame.Epoch == 3 && frame.IsSettling, "Recenter starts an epoch");
        check(!frame.PublishedTracking && frame.PublishedPose.Equals(beforeRecenter), "Held sample before the move");
        var recentered = new Pose3d(0, 0, 0, new Rotation3d(0.01, -0.02, 0)); // Origin moved to the headset.
        frame.Sample(recentered, true, 2.05);
        check(frame.PublishedPose.Equals(beforeRecenter) && !frame.PublishedTracking, "Moved origin stays hidden while settling");
        frame.BeginOriginChange(2.02); // Same recenter reported through another SDK path.
        check(frame.Epoch == 3, "Duplicate announcement does not start another epoch");
        frame.Sample(recentered, true, 2.11);
        check(frame.IsSettling, "Duplicate announcement extends the settle window");
        check(!frame.TryReset(target, out reason) && reason.Contains("changing"), "Reset waits for the recenter to settle");
        frame.Sample(recentered, true, 2.13);
        check(!frame.IsSettling && frame.PublishedTracking, "Live samples resume after the settle window");
        check(frame.Epoch == 4, "Live samples resume in a fresh epoch");
        check(frame.PublishedPose.Equals(frame.Apply(recentered)), "Live samples use the new origin");
        check(frame.TryReset(target, out _) && frame.Epoch == 5, "Reset after settling");
        frame.BeginOriginChange(3.0);
        check(frame.Epoch == 6, "A later recenter starts another epoch");

        // Tracking loss outside a recenter publishes the live pose, marked not tracking.
        frame.Sample(recentered, true, 3.2);
        frame.Sample(moved, false, 3.21);
        check(!frame.PublishedTracking && frame.PublishedPose.Equals(frame.Apply(moved)), "Tracking loss is reported live");
    }
}
