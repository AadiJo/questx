# PoseLib packing checks

Run from the repository root with .NET 8+:

```sh
dotnet run -c Release --project tests/poselib-packing-checks/PoseLibPackingChecks.csproj
```

Like `tests/measurement-checks`, this project needs the generated C# geometry protobuf
sources in `unity/Assets/QuestX/Generated` (see the localization CI workflow).

The project links the production field layout, solver, geometry, quality, covariance,
and result conversion. Small stand-ins replace Unity/Meta types, file locations, logging,
and the detector adapter. A managed PoseLib stand-in captures native-call arguments and
returns either an error or an identity pose. This checks packing and post-solve behavior;
it does not test native PoseLib, interop, Unity/IL2CPP, or headset frame rate.

Checks cover:

- Translated tags with nonzero roll, pitch, and yaw, checked against independent Euler
  rotation equations in BR/BL/UL/UR order; multiple and repeated IDs preserve input order.
- Exact native buffer lengths, point count, camera model, RANSAC threshold, and refreshed
  intrinsics; empty input skips native. Unknown IDs reject the solve before native can
  read incomplete correspondences (the manager normally filters these out).
- Real projected corners through the production quality/covariance/result path, including
  complete-tag rejection and all-rejected output.
- Warm cached corners allocate zero bytes when copied into an existing packed buffer.
  Public corner arrays and each solve's buffers retain independent array ownership.
- Pose/ID edits, entry replacement, reorder/removal/addition in the mutable tag list,
  list replacement, independent layouts/tag sizes, successful reload, and failed reloads
  retaining the previous geometry and existing validation behavior.

## Allocation measurement

```sh
DOTNET_TieredCompilation=0 dotnet run -c Release --project tests/poselib-packing-checks/PoseLibPackingChecks.csproj -- --measure
```

This warms the actual production layout/solver packing path for 2,000 solves, then
measures thread allocations and elapsed time for 20,000 solves at each tag count.
The stand-in returns a native error, isolating packing from quality/covariance work.
The constant production failure-message allocation is included; logging is discarded.

Measured on this Mac (ARM64, .NET 10.0.12, Release, tiered compilation disabled),
before and after the production change using the same harness:

| Tags | Before bytes/solve | After bytes/solve | Before ns/solve | After ns/solve |
| --- | ---: | ---: | ---: | ---: |
| 1 | 3,184 | 304 | 461 | 93 |
| 2 | 6,080 | 464 | 787 | 110 |
| 4 | 11,872 | 784 | 1,428 | 137 |

The allocation reduction is 90–93%. Timings are local microbenchmark observations,
not estimates of headset frame-rate improvement. Native solve time is excluded.
On this machine only .NET 10 is installed, so the runs also set
`DOTNET_ROLL_FORWARD=Major` and use `/Users/zacharywilson/.dotnet/dotnet`.

The layout remains a mutable model: as before, callers must not edit its public tag
list concurrently with a solve. Cache access follows the existing single-solver use.
