# VIO quality (depth drift) checks

## Portable monitor regression checks

From the repository root, run:

```sh
dotnet run --project tests/vio-quality/VioQualityChecks.csproj -c Release
```

This .NET 8+ suite compiles the production monitor, estimator, sample, and constants.
Counted Unity/OpenXR stand-ins compare the monitor against `UncachedMonitor.cs`, a frozen
pre-optimization reference from `4e60c338e5253d689b272cb960562e69a909cfb5` (only renamed).
Do not update the reference to mirror the optimized implementation.

The checks compare IEEE bits of every ray, depth point, and complete sample across all hits,
mixed misses/low confidence/near hits, no ready depth, inactive/unsupported depth, no hits,
tracking loss/recovery, settling, and tracking flags. Position and rotation move between calls;
tracking-space rotation/nonuniform scale, yaw/offset changes, and epoch changes exercise the
unchanged inverse transforms and actual estimator. Assertions require one position and one
rotation read per active sample, zero on early exits, all 160 rays, and the 15 Hz constant.
The application scheduler itself is outside this portable suite.

These are stand-in behavioral and operation-count checks, not Unity/OpenXR integration,
IL2CPP performance, headset timing, or native transform precision tests. No new packages are required.

## Synthetic depth drift checks

`depth_drift_mirror.py` is a line-for-line Python mirror of
`unity/Assets/QuestX/VioQuality/DepthDriftEstimator.cs`. `check_depth_drift.py` raycasts simple synthetic
scenes (floor, walls, boxes, an optional moving robot) the way `VioQualityMonitor` does and injects known VIO drift and
depth noise.

```
python check_depth_drift.py
```

Each scenario passes when every report is consistent (|estimate - truth| <= 3 standard deviations per axis). The
structure-rich scenes must also constrain X and Y to under 5 cm. Results at the time of writing:

| Scenario | Worst error / std | Worst error |
| --- | --- | --- |
| rich field, perfect VIO | 1.8 | 2.1 cm |
| rich field, 10 cm/s X drift | 1.1 | 1.2 cm |
| rich field, 8 cm/s Y drift | 0.9 | 0.9 cm |
| rich field + moving robot | 1.4 | 1.6 cm |
| rich field, 3 % depth noise | 2.0 | 3.0 cm |
| sparse field (X barely constrained) | 0.8 | X reported unobservable (2 m std) |
| floor only | 0.0 | X and Y reported unobservable (2 m std) |

When the C# estimator was ported, it was compiled with the .NET 8 SDK (C# 9) and run on the same frames as this mirror:
all 672 compared values (status, counts, drift, standard deviations, RMS) were bit-identical.

Not covered here: real Quest depth noise, latency between depth capture and the raycast call, and on-headset CPU cost.
