import numpy as np
import scipy.sparse as sp
import scipy.sparse.linalg as spl


def scale_list(T, max_scale=131072, base_short=2.0, base_long=1.4142, long_from=1440):
    scales = [1]
    while True:
        s = scales[-1]
        base = base_long if s >= long_from else base_short
        nxt = max(s + 1, int(round(s * base)))
        if nxt > max_scale or nxt >= T:
            break
        scales.append(nxt)
    return np.array(scales)


def build_frame(T, scales, overlap_short=4, overlap_long=16, long_from=1440, avg_div=256, avg_from=256):
    A, B, K, HALF = [], [], [], []
    for k, s in enumerate(scales):
        overlap = overlap_long if s >= long_from else overlap_short
        step = max(1, s // overlap)
        half = s // avg_div if s >= avg_from else 0
        a = np.arange(half, T - s - half, step, dtype=np.int64)
        A.append(a)
        B.append(a + s)
        K.append(np.full(len(a), k, dtype=np.int32))
        HALF.append(half)
    return np.concatenate(A), np.concatenate(B), np.concatenate(K), np.array(HALF)


def box_mean(x, idx, half):
    if half == 0:
        return x[idx]
    cs = np.concatenate([np.zeros((1,) + x.shape[1:]), np.cumsum(x, axis=0)])
    return (cs[idx + half + 1] - cs[idx - half]) / (2 * half + 1)


def interval_moves(x, A, B, K, HALF):
    out = np.empty((len(A),) + x.shape[1:])
    for k, half in enumerate(HALF):
        sel = K == k
        out[sel] = box_mean(x, B[sel], half) - box_mean(x, A[sel], half)
    return out


def noise_per_scale(moves, K, nscales):
    noise = np.zeros(nscales)
    for k in range(nscales):
        m = moves[K == k]
        med = np.median(m, axis=1, keepdims=True)
        noise[k] = np.median(np.median(np.abs(m - med), axis=1))
    return noise


class LocalFloor:
    def __init__(self, moves, K, scales, steps, kappa, min_floor, recent, min_block=8):
        self.centers = []
        self.values = []
        self.floor_e = np.empty(len(K))
        for k in range(len(scales)):
            idx = np.nonzero(K == k)[0]
            m = moves[idx]
            med = np.median(m, axis=1, keepdims=True)
            mad = np.median(np.abs(m - med), axis=1)
            count = len(idx)
            block = max(min_block, recent // steps[k])
            nb = max(1, count // block)
            edges = [round(count * b / nb) for b in range(nb + 1)]
            centers = np.array([(edges[b] + edges[b + 1] - 1) / 2.0 for b in range(nb)])
            values = np.array([max(min_floor, kappa * np.median(mad[edges[b]:edges[b + 1]])) for b in range(nb)])
            self.centers.append(centers)
            self.values.append(values)
            self.floor_e[idx] = np.interp(np.arange(count), centers, values)

    def at(self, k, j):
        return np.interp(j, self.centers[k], self.values[k])


def kernel(z, kind="sharp"):
    az = np.abs(z)
    if kind == "sharp":
        t = np.maximum(0.0, az - 1.0)
        return np.exp(-8.0 * t * t)
    if kind == "hard":
        return (az <= 1.0).astype(float)
    return np.exp(-0.5 * z * z)


def interval_consensus(moves, floor, W, rel=0.15, kind="sharp", iters=12, strength="margin"):
    E, n = moves.shape
    modes = np.empty((E, n))
    weights = np.empty((E, n))
    for j in range(n):
        m = moves[:, j].copy()
        for _ in range(iters):
            c = np.maximum(floor, rel * np.abs(m))
            w = kernel((moves - m[:, None]) / c[:, None], kind) * W[None, :]
            m = (w * moves).sum(1) / w.sum(1)
        c = np.maximum(floor, rel * np.abs(m))
        w = kernel((moves - m[:, None]) / c[:, None], kind) * W[None, :]
        modes[:, j] = m
        weights[:, j] = w.sum(1)
    best = np.argmax(weights, axis=1)
    rows = np.arange(E)
    m1 = modes[rows, best]
    w1 = weights[rows, best]
    c1 = np.maximum(floor, rel * np.abs(m1))
    distinct = np.abs(modes - m1[:, None]) > c1[:, None]
    w2 = np.where(distinct, weights, 0.0).max(1)
    if strength == "margin":
        H = np.maximum(0.0, w1 - w2)
    elif strength == "top":
        H = w1
    else:
        H = w1 * w1
    return m1, H, w1, w2


class DifferenceOperator:
    def __init__(self, T, A, B, K, HALF):
        rows, cols, vals = [], [], []
        E = len(A)
        for k, half in enumerate(HALF):
            sel = np.nonzero(K == k)[0]
            n = 2 * half + 1
            for off in range(-half, half + 1):
                rows.append(sel)
                cols.append(B[sel] + off)
                vals.append(np.full(len(sel), 1.0 / n))
                rows.append(sel)
                cols.append(A[sel] + off)
                vals.append(np.full(len(sel), -1.0 / n))
        self.D = sp.csr_matrix((np.concatenate(vals), (np.concatenate(rows), np.concatenate(cols))), shape=(E, T))
        self.Dt = self.D.T.tocsr()

    def solve(self, H, m, u0, rtol=1e-10):
        L = (self.Dt @ sp.diags(H) @ self.D).tocsr()
        f = self.Dt @ (H * m)
        L[0, 0] += 1e3
        M = sp.diags(1.0 / L.diagonal())
        u, info = spl.cg(L, f, x0=u0, M=M, rtol=rtol, maxiter=20000)
        return u - u[0], info


def recency_weight(B, minutes, recent, decay, floor):
    age = (minutes - 1) - B
    w = 1.0 - (age - recent) / float(decay) * (1.0 - floor)
    return np.clip(w, floor, 1.0)


def consensus_index(x, scales, A, B, K, HALF, W, operator, kappa=0.5, rel=0.15, kind="sharp", beta=0.0,
                    strength="margin", u_init=None, min_floor=0.5, recent=72000, recency_floor=1.0,
                    local_floor=True):
    moves = interval_moves(x, A, B, K, HALF)
    steps = np.array([max(1, s // (16 if s >= 1440 else 4)) for s in scales])
    if local_floor:
        lf = LocalFloor(moves, K, scales, steps, kappa, min_floor, recent)
        floor_e = lf.floor_e
    else:
        noise = noise_per_scale(moves, K, len(scales))
        per_scale = np.maximum(min_floor, kappa * noise)
        lf = None
        floor_e = per_scale[K]
    m, H, w1, w2 = interval_consensus(moves, floor_e, W, rel=rel, kind=kind, strength=strength)
    omega = (scales / float(scales[-1])) ** beta
    recency = recency_weight(B, x.shape[0], recent, scales[-1], recency_floor)
    u0 = u_init if u_init is not None else (x * W[None, :]).sum(1) / W.sum()
    u, info = operator.solve((H + 1e-6) * omega[K] * recency, m, u0)
    return u, dict(m=m, H=H, w1=w1, w2=w2, floor_e=floor_e, local=lf, steps=steps, moves=moves, cg=info,
                   recency=recency)


def per_minute_index(x, W, method):
    steps = np.diff(x, axis=0)
    s = (steps * W[None, :]).sum(1) / W.sum() if method == "mean" else np.median(steps, axis=1)
    return np.concatenate([[0.0], np.cumsum(s)])
