"""Synthetic checks for the VIO depth-drift estimator (Python mirror of DepthDriftEstimator.cs).

A headset ~0.55 m above the floor drives through a simple field (floor, walls, boxes). Rays are cast like
VioQualityMonitor (16x10 fan, +-45 deg yaw, -35..+20 deg pitch, 0.3-5 m). Hits are placed in the VIO frame using
the *VIO* pose, so any VIO error moves the whole static scene. Run: python check_depth_drift.py
"""
import math, random, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from depth_drift_mirror import DepthDriftEstimator, OK

random.seed(4)


def norm(v):
    l = math.sqrt(sum(x * x for x in v)); return tuple(x / l for x in v)


def plane(point, normal):
    return ('plane', point, norm(normal))


def box(lo, hi):
    return ('box', lo, hi)


def ray_hit(o, d, scene, moving=()):
    best, bn = None, None
    for obj in list(scene) + list(moving):
        if obj[0] == 'plane':
            _, p0, n = obj
            den = sum(d[i] * n[i] for i in range(3))
            if abs(den) < 1e-9: continue
            t = sum((p0[i] - o[i]) * n[i] for i in range(3)) / den
            if t > 1e-6 and (best is None or t < best): best, bn = t, n
        else:
            _, lo, hi = obj; tmin, tmax, nrm = -1e9, 1e9, None
            ok = True
            for i in range(3):
                if abs(d[i]) < 1e-12:
                    if not lo[i] <= o[i] <= hi[i]: ok = False
                    continue
                t1, t2 = (lo[i] - o[i]) / d[i], (hi[i] - o[i]) / d[i]
                n1 = tuple((-1.0 if j == i else 0.0) for j in range(3)); n2 = tuple((1.0 if j == i else 0.0) for j in range(3))
                if t1 > t2: t1, t2, n1, n2 = t2, t1, n2, n1
                if t1 > tmin: tmin, nrm = t1, n1
                tmax = min(tmax, t2)
            if ok and tmin <= tmax and tmin > 1e-6 and (best is None or tmin < best): best, bn = tmin, nrm
    return best, bn


FIELD = [plane((0, 0, 0), (0, 0, 1)), plane((-1.0, 0, 0), (1, 0, 0)), plane((8.0, 0, 0), (-1, 0, 0)),
         plane((0, -4.0, 0), (0, 1, 0)), plane((0, 4.0, 0), (0, -1, 0)),
         box((2.5, -1.2, 0), (3.1, -0.4, 1.0)), box((4.0, 0.8, 0), (4.6, 2.0, 1.2)), box((1.0, 1.6, 0), (1.4, 2.2, 0.6))]
FLOOR_ONLY = [plane((0, 0, 0), (0, 0, 1))]


def cloud(true_pos, yaw, vio_error, scene, noise=0.01, moving=()):
    """Cast the 16x10 fan from the TRUE headset pose; place hits using the VIO pose (true + vio_error)."""
    pts = []
    for i in range(10):
        pitch = math.radians(-35 + 55 * i / 9)
        for j in range(16):
            yw = yaw + math.radians(-45 + 90 * j / 15)
            d = (math.cos(pitch) * math.cos(yw), math.cos(pitch) * math.sin(yw), math.sin(pitch))
            t, n = ray_hit(true_pos, d, scene, moving)
            if t is None or t < 0.3 or t > 5.0: continue
            t += random.gauss(0, noise * max(1.0, t))
            p = tuple(true_pos[k] + t * d[k] + vio_error[k] for k in range(3))
            nn = norm(tuple(n[k] + random.gauss(0, 0.02) for k in range(3)))
            pts.append((p, nn, t))
    return pts


RICH = [plane((0, 0, 0), (0, 0, 1)), plane((-1.0, 0, 0), (1, 0, 0)), plane((5.0, 0, 0), (-1, 0, 0)),
        plane((0, -4.0, 0), (0, 1, 0)), plane((0, 2.5, 0), (0, -1, 0)),
        box((2.5, -1.2, 0), (3.1, -0.4, 1.0)), box((3.4, 0.8, 0), (4.0, 2.0, 1.2)), box((1.0, 1.6, 0), (1.4, 2.2, 0.6)),
        box((3.6, -2.6, 0), (4.2, -1.8, 0.9))]


def run(name, scene, error_rate, steps=16, dt=1 / 15, speed=1.5, moving_robot=False, noise=0.01, max_std=None):
    """Returns True when every report is consistent (|error| <= 3 std per axis) and, if max_std is given,
    the X/Y standard deviations of the last report are below it (the scene constrains both axes)."""
    est = DepthDriftEstimator(); pos = [1.0, -2.5, 0.55]; yaw = math.radians(30); err = [0.0, 0.0, 0.0]
    key_errs = {}; worst_sigma = 0.0; worst_cm = 0.0; oks = 0; last = None
    for k in range(steps):
        t = k * dt
        pos[0] += speed * dt * math.cos(yaw); pos[1] += speed * dt * math.sin(yaw)
        err = [err[i] + error_rate[i] * dt for i in range(3)]
        mov = [box((2.0 + 0.8 * t, -0.2, 0), (2.6 + 0.8 * t, 0.5, 0.7))] if moving_robot else []
        pts = cloud(tuple(pos), yaw, tuple(err), scene, noise, mov)
        key_time = est.keyframe_time if est.keyframe is not None else None
        r = est.update(pts, t, True, 0)
        if r['status'] == OK:
            truth = [err[i] - key_errs[key_time][i] for i in range(3)]
            for i in range(2):
                e = abs(r['drift'][i] - truth[i])
                worst_sigma = max(worst_sigma, e / r['std'][i])
                if r['std'][i] < 1.0: worst_cm = max(worst_cm, e)
            oks += 1; last = (r, truth)
        if est.keyframe is not None and est.keyframe_time == t: key_errs[t] = list(err)
    r, truth = last
    passed = oks > 0 and worst_sigma <= 3.0 and (max_std is None or (r['std'][0] < max_std and r['std'][1] < max_std))
    print(f'{"PASS" if passed else "FAIL"} {name:36s} reports={oks:2d} worst |err|/std={worst_sigma:4.1f}'
          f' worst observable err={worst_cm * 100:4.1f} cm | last drift=({r["drift"][0]*100:6.1f},{r["drift"][1]*100:6.1f})'
          f' truth=({truth[0]*100:5.1f},{truth[1]*100:5.1f}) std=({r["std"][0]*100:7.1f},{r["std"][1]*100:7.1f}) cm'
          f' inliers={r["inlier_ratio"]:.2f}')
    return passed


ok = True
ok &= run('rich field, perfect VIO', RICH, (0, 0, 0), max_std=0.05)
ok &= run('rich field, VIO drift 10 cm/s X', RICH, (0.10, 0, 0), max_std=0.05)
ok &= run('rich field, VIO drift 8 cm/s Y', RICH, (0, 0.08, 0), max_std=0.05)
ok &= run('rich field + moving robot', RICH, (0.06, -0.04, 0), moving_robot=True, max_std=0.05)
ok &= run('rich field, noisy depth (3 %)', RICH, (0.05, 0, 0), noise=0.03)
ok &= run('sparse field (X weakly constrained)', FIELD, (0.10, 0, 0))
ok &= run('floor only (horizontal unobservable)', FLOOR_ONLY, (0.10, 0.0, 0))
print('PASS' if ok else 'FAIL')
sys.exit(0 if ok else 1)
