"""Line-for-line Python mirror of unity/Assets/QuestX/VioQuality/DepthDriftEstimator.cs (keep in sync)."""
import math

OK, NOT_SUPPORTED, NOT_READY, NOT_TRACKING, TOO_FEW_HITS, NEW_KEYFRAME, TOO_FEW_MATCHES = range(7)


class Settings:
    min_hits = 40
    min_matches = 30
    match_radius = 0.30
    min_normal_agreement = 0.7
    depth_noise_base = 0.005
    depth_noise_per_meter = 0.01
    normal_noise_rad = 0.03
    inlier_sigmas = 3.0
    initial_inlier_threshold = 0.10
    reference_sigma = 0.03
    min_observable_count = 3.0
    unobservable_std = 2.0
    min_std = 0.01
    match_rounds = 3
    iterations = 4
    keyframe_max_age = 1.0
    keyframe_min_match_ratio = 0.5


def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def scale(s, a): return (s * a[0], s * a[1], s * a[2])


def eigen_sym3(h):
    """Cyclic Jacobi on a symmetric 3x3 (row-major list). Returns (values[3], vectors as columns: v[row][col])."""
    a = [h[0:3], h[3:6], h[6:9]]
    a = [list(r) for r in a]
    v = [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]]
    for _ in range(50):
        off = a[0][1] ** 2 + a[0][2] ** 2 + a[1][2] ** 2
        if off < 1e-30:
            break
        for p, q in ((0, 1), (0, 2), (1, 2)):
            if abs(a[p][q]) < 1e-300:
                continue
            theta = (a[q][q] - a[p][p]) / (2 * a[p][q])
            t = (1.0 if theta >= 0 else -1.0) / (abs(theta) + math.sqrt(theta * theta + 1))
            c = 1 / math.sqrt(t * t + 1); s = t * c
            for k in range(3):
                akp, akq = a[k][p], a[k][q]
                a[k][p] = c * akp - s * akq; a[k][q] = s * akp + c * akq
            for k in range(3):
                apk, aqk = a[p][k], a[q][k]
                a[p][k] = c * apk - s * aqk; a[q][k] = s * apk + c * aqk
            for k in range(3):
                vkp, vkq = v[k][p], v[k][q]
                v[k][p] = c * vkp - s * vkq; v[k][q] = s * vkp + c * vkq
    return [a[0][0], a[1][1], a[2][2]], v


class DepthDriftEstimator:
    def __init__(self, settings=None):
        self.s = settings or Settings()
        self.keyframe = None
        self.keyframe_time = 0.0
        self.keyframe_epoch = 0

    def update(self, points, time, tracking, epoch):
        """points: list of (position, normal, range) in the local VIO frame (FRC axes)."""
        r = dict(status=None, baseline=0.0, hits=len(points or []), matched=0, inliers=0, inlier_ratio=0.0,
                 drift=(0.0, 0.0, 0.0), std=(0.0, 0.0, 0.0), rms=0.0)
        if not tracking:
            self.keyframe = None; r['status'] = NOT_TRACKING; return r
        if r['hits'] < self.s.min_hits:
            r['status'] = TOO_FEW_HITS; return r
        if self.keyframe is None or epoch != self.keyframe_epoch or time <= self.keyframe_time:
            self._set(points, time, epoch); r['status'] = NEW_KEYFRAME; return r
        r['baseline'] = time - self.keyframe_time
        self._compare(points, r)
        view_changed = r['matched'] < self.s.keyframe_min_match_ratio * r['hits']
        if r['baseline'] >= self.s.keyframe_max_age or view_changed:
            self._set(points, time, epoch)
        return r

    def _set(self, points, time, epoch):
        self.keyframe = list(points); self.keyframe_time = time; self.keyframe_epoch = epoch

    def _match(self, points, d):
        s = self.s; radius2 = s.match_radius ** 2
        normals, residuals, sigmas = [], [], []
        for p, pn, rng in points:
            ps = sub(p, d)
            best, best_d2 = -1, radius2
            for k, (q, qn, qr) in enumerate(self.keyframe):
                d2 = sum(x * x for x in sub(ps, q))
                if d2 <= best_d2:
                    best_d2, best = d2, k
            if best < 0:
                continue
            q, qn, qr = self.keyframe[best]
            if abs(dot(pn, qn)) < s.min_normal_agreement:
                continue
            off = sub(ps, q)
            along = dot(qn, off)
            tangential2 = max(0.0, dot(off, off) - along * along)
            depth_sigma = s.depth_noise_base + s.depth_noise_per_meter * max(rng, qr)
            sigma2 = depth_sigma * depth_sigma + (s.normal_noise_rad ** 2) * tangential2
            normals.append(qn); residuals.append(dot(qn, sub(p, q))); sigmas.append(sigma2)
        return normals, residuals, sigmas

    def _compare(self, points, r):
        s = self.s
        d = (0.0, 0.0, 0.0)
        chi2 = 1.0; rms = 0.0; inliers = 0; vals = [0.0, 0.0, 0.0]; vecs = None; observable = [False] * 3; matched = 0
        for _ in range(s.match_rounds):
            normals, residuals, sigma2 = self._match(points, d)
            matched = len(normals)
            if matched < s.min_matches:
                break
            first = True
            for _ in range(s.iterations):
                h = [0.0] * 9; b = [0.0, 0.0, 0.0]; inliers = 0
                for n, res, s2 in zip(normals, residuals, sigma2):
                    e = res - dot(n, d)
                    gate = max(s.initial_inlier_threshold, s.inlier_sigmas * math.sqrt(s2)) if first else s.inlier_sigmas * math.sqrt(s2 * chi2)
                    if abs(e) > gate:
                        continue
                    inliers += 1; w = 1.0 / s2
                    h[0] += w * n[0] * n[0]; h[1] += w * n[0] * n[1]; h[2] += w * n[0] * n[2]
                    h[4] += w * n[1] * n[1]; h[5] += w * n[1] * n[2]; h[8] += w * n[2] * n[2]
                    h[3], h[6], h[7] = h[1], h[2], h[5]
                    b = [b[i] + w * res * n[i] for i in range(3)]
                first = False
                if inliers < s.min_matches:
                    break
                vals, vecs = eigen_sym3(h)
                threshold = s.min_observable_count / (s.reference_sigma ** 2)
                observable = [vals[i] > threshold for i in range(3)]
                dn = [0.0, 0.0, 0.0]
                for i in range(3):
                    if not observable[i]:
                        continue
                    vi = (vecs[0][i], vecs[1][i], vecs[2][i])
                    coef = dot(vi, b) / vals[i]
                    dn = [dn[k] + coef * vi[k] for k in range(3)]
                d = tuple(dn)
                sum_w, sum_e2, count = 0.0, 0.0, 0
                for n, res, s2 in zip(normals, residuals, sigma2):
                    e = res - dot(n, d)
                    if abs(e) <= s.inlier_sigmas * math.sqrt(s2 * chi2):
                        sum_w += e * e / s2; sum_e2 += e * e; count += 1
                chi2 = max(1.0, sum_w / (count - 3)) if count > 3 else 1.0
                rms = math.sqrt(sum_e2 / count) if count else 0.0
        r['matched'] = matched
        if matched < s.min_matches:
            r['status'] = TOO_FEW_MATCHES; return
        r['inliers'] = inliers
        r['inlier_ratio'] = inliers / matched
        if inliers < s.min_matches or vecs is None:
            r['status'] = TOO_FEW_MATCHES; return
        cov = [0.0] * 9
        for i in range(3):
            var = chi2 / vals[i] if observable[i] else s.unobservable_std ** 2
            for row in range(3):
                for col in range(3):
                    cov[row * 3 + col] += var * vecs[row][i] * vecs[col][i]
        r['drift'] = d
        r['std'] = tuple(math.sqrt(cov[i] + s.min_std ** 2) for i in (0, 4, 8))
        r['rms'] = rms
        r['status'] = OK
