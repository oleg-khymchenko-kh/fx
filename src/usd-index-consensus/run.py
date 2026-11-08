import calendar
import datetime as dt
import sys
import time
import numpy as np
from m1 import common_grid, usd_up_moves
from consensus import (scale_list, build_frame, consensus_index, per_minute_index, DifferenceOperator,
                       interval_consensus)

PAIRS = ["EURUSD", "GBPUSD", "USDCHF", "USDCAD", "NZDUSD", "AUDUSD"]
SHORT = ["EUR", "GBP", "CHF", "CAD", "NZD", "AUD"]
WEIGHTS = {"ranked": np.array([6, 5, 4, 3, 2, 1.0]), "heavy": np.array([10, 8, 4, 3, 2, 1.0]), "equal": np.ones(6)}
DEFAULT = dict(unit="pips", weights="ranked", kappa=0.5, rel=0.15, kind="sharp", beta=0.0, strength="margin",
               max_scale=65536, avg_div=256, avg_from=256, overlap_long=16, base_short=2.0, base_long=1.4142,
               recent=72000, recency_floor=0.1, local_floor=1,
               start="2026-06-01", end="2026-10-17", example="2026-08-21T09:10,2026-10-01T15:54", out="")
BUCKETS = [("1h-4h", 60, 240), ("4h-1d", 240, 1440), ("1d-5d", 1440, 7200), ("5d-2w", 7200, 20000),
           ("2w-6w", 20000, 70000)]


def parse_time(text):
    fmt = "%Y-%m-%dT%H:%M" if "T" in text else "%Y-%m-%d"
    return calendar.timegm(dt.datetime.strptime(text, fmt).timetuple())


def parse_args(argv):
    o = dict(DEFAULT)
    for a in argv:
        k, v = a.split("=", 1)
        o[k] = type(o[k])(v) if not isinstance(o[k], str) else v
    return o


def main():
    o = parse_args(sys.argv[1:])
    ts, prices = common_grid(PAIRS, parse_time(o["start"]), parse_time(o["end"]))
    x = usd_up_moves(prices, PAIRS, o["unit"])
    T = len(ts)
    W = WEIGHTS[o["weights"]] / WEIGHTS[o["weights"]].sum()
    print(f"{T} common minutes {o['start']} .. {o['end']}, unit {o['unit']}, weights {o['weights']}")

    t = time.time()
    scales = scale_list(T, o["max_scale"], o["base_short"], o["base_long"])
    A, B, K, HALF = build_frame(T, scales, overlap_long=o["overlap_long"], avg_div=o["avg_div"], avg_from=o["avg_from"])
    operator = DifferenceOperator(T, A, B, K, HALF)
    u_mean = per_minute_index(x, W, "mean")
    u_med = per_minute_index(x, np.ones(6), "median")
    u_new, info = consensus_index(x, scales, A, B, K, HALF, W, operator, kappa=o["kappa"], rel=o["rel"],
                                  kind=o["kind"], beta=o["beta"], strength=o["strength"], u_init=u_med,
                                  recent=o["recent"], recency_floor=o["recency_floor"],
                                  local_floor=bool(o["local_floor"]))
    print(f"{len(scales)} scales, {len(A)} intervals, cg status {info['cg']}, {time.time() - t:.0f}s")

    pr = lambda mv: " ".join(f"{s} {v:+.0f}" for s, v in zip(SHORT, mv))
    t0, t1 = (np.searchsorted(ts, parse_time(p)) for p in o["example"].split(","))
    print(f"\nexample interval {o['example']}:")
    print(f"  pairs usd-up: {pr(x[t1] - x[t0])}")
    for name, u in [("mean of steps", u_mean), ("median of steps (v1)", u_med), ("consensus", u_new)]:
        print(f"  {name:22s} {u[t1] - u[t0]:+.0f}")

    d1 = np.diff(u_new)
    quiet = np.all(np.abs(np.diff(x, axis=0)) <= 2, axis=1)
    print(f"\nper-minute: std {d1.std():.2f} (v1 {np.diff(u_med).std():.2f}), "
          f"jumps >6 while every pair moved <=2: {np.sum((np.abs(d1) > 6) & quiet)}, max |1 min| {np.abs(d1).max():.1f}")

    rng = np.random.default_rng(1)
    n = 20000
    lengths = np.exp(rng.uniform(np.log(60), np.log(70000), n)).astype(np.int64)
    starts = rng.integers(0, T - lengths)
    moves = x[starts + lengths] - x[starts]
    k_near = np.argmin(np.abs(scales[None, :] - lengths[:, None]), axis=1)
    ends = starts + lengths
    if info["local"] is not None:
        j_near = np.clip((ends - scales[k_near]) // info["steps"][k_near], 0, None)
        floor_r = np.array([info["local"].at(k, j) for k, j in zip(k_near, j_near)])
    else:
        per_scale = np.array([info["floor_e"][K == k][0] if np.any(K == k) else 0.5 for k in range(len(scales))])
        floor_r = per_scale[k_near]
    m_r, H_r, _, _ = interval_consensus(moves, floor_r, W, rel=o["rel"], kind=o["kind"], strength=o["strength"])
    age = (T - 1) - ends
    zones = [("ending in the last 10 weeks", age <= o["recent"]),
             ("ending 10 to 19 weeks ago", (age > o["recent"]) & (age <= o["recent"] + 65536)),
             ("all", np.ones(n, dtype=bool))]
    for zone, zsel in zones:
        print(f"\nrandom intervals {zone}: strength-weighted mean |index move - interval consensus|")
        print("  " + " ".join(f"{name:>8s}" for name, *_ in BUCKETS))
        for name, u in [("mean", u_mean), ("median v1", u_med), ("consensus", u_new)]:
            err = np.abs(u[ends] - u[starts] - m_r)
            cells = []
            for _, lo, hi in BUCKETS:
                sel = zsel & (lengths >= lo) & (lengths < hi)
                cells.append(np.sum(err[sel] * H_r[sel]) / max(np.sum(H_r[sel]), 1e-9))
            print("  " + " ".join(f"{c:8.1f}" for c in cells) + f"  {name}")
    strong = [np.mean(H_r[(lengths >= lo) & (lengths < hi)] > 0.3) * 100 for _, lo, hi in BUCKETS]
    print("  " + " ".join(f"{s:7.0f}%" for s in strong) + "  share of intervals with strength > 0.3")

    if o["out"]:
        np.save(o["out"], np.stack([ts, u_mean, u_med, u_new]))
        print(f"\nsaved {o['out']}")


if __name__ == "__main__":
    main()
