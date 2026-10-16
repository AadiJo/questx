# VIO quality (depth drift) checks

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
