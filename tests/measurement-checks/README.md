# Measurement checks

Run `dotnet run --project tests/measurement-checks/MeasurementChecks.csproj` from the repository root (.NET 8+).

These checks compile the production C# sources directly, without Unity:

- **AprilTag ambiguity**: synthetic tags projected through a 1280x1280 pinhole camera with seeded pixel noise. IPPE must recover the true pose, a distant face-on tag must read as ambiguous, a near oblique tag must not, coplanar tags are solved jointly, and tags on different planes report 0.
- **VIO reference frame**: pose resets match the target's position and yaw while keeping measured roll and pitch, preserve motion within an epoch, and start one epoch each; recenters hold the last pose as not tracking in their own epoch while the origin settles, then resume live poses in a fresh epoch.
- **Wire formats**: the v3 AprilTag array keeps the v2 header and appends ambiguity, reprojection error, and processing latency, and the Limelight botpose length lets `LimelightHelpers` parse raw fiducials.
- **Sample frames**: realistic single-tag and two-tag v3 arrays built by the production solver and encoder; the run prints both.

Add `--export-fixtures` to regenerate the fixtures in `questx-lib/src/test/resources/measurement-v3/` for the Java decoder tests.

The geometry types depend on the C# protobuf classes in `unity/Assets/QuestX/Generated`, which are generated from `protos/` and not committed. Generate them first, as CI does (`.github/workflows/reusable-build-proto-csharp.yml`).
