import math
import random
from collections import defaultdict
from datetime import datetime, timedelta, timezone

import numpy as np

from tc_accuracy import PAIRS, REPORTS, Series, load_views, pip_of

TOUCH_ADR = 0.10
BOUNCE_ADR = 0.25
ADR_DAYS = 14
RANDOM_PER_VIEW = 50
SEED = 20261002


def adr(series, unix):
    day = datetime.fromtimestamp(unix, timezone.utc).date()
    ranges = []
    d = day - timedelta(days=1)
    while len(ranges) < ADR_DAYS and (day - d).days < 40:
        if d.weekday() < 5:
            start = int(datetime(d.year, d.month, d.day, tzinfo=timezone.utc).timestamp())
            lo, hi, av, ok = series.window(start, start + 86400)
            if ok.sum() > 600:
                ranges.append((hi[ok].max() - lo[ok].min()) / series.scale)
        d -= timedelta(days=1)
    return float(np.mean(ranges)) if len(ranges) >= 5 else None


def outcome(lo, hi, ok, level, above, touch, bounce):
    if above:
        touched = np.nonzero(ok & (lo <= level + touch))[0]
    else:
        touched = np.nonzero(ok & (hi >= level - touch))[0]
    if len(touched) == 0:
        return "not tested"
    i = touched[0]
    lo, hi, ok = lo[i:], hi[i:], ok[i:]
    if above:
        away = np.nonzero(ok & (hi >= level + bounce))[0]
        through = np.nonzero(ok & (lo < level - touch))[0]
        broke = np.nonzero(ok & (lo <= level - bounce))[0]
    else:
        away = np.nonzero(ok & (lo <= level - bounce))[0]
        through = np.nonzero(ok & (hi > level + touch))[0]
        broke = np.nonzero(ok & (hi >= level + bounce))[0]
    a = away[0] if len(away) else None
    t = through[0] if len(through) else None
    if a is not None and (t is None or a < t):
        return "bounce"
    if len(broke):
        return "break"
    return "touched, no result" if t is None else "pierced"


def wilson(k, n, z=1.96):
    if n == 0:
        return 0.0, 0.0
    p = k / n
    centre = (p + z * z / (2 * n)) / (1 + z * z / n)
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / (1 + z * z / n)
    return centre - half, centre + half


def line(name, counts):
    touched = sum(n for k, n in counts.items() if k != "not tested")
    total = touched + counts.get("not tested", 0)
    b = counts.get("bounce", 0)
    lo, hi = wilson(b, touched)
    rate = b / touched if touched else 0
    return (f"| {name} | {total} | {touched} ({touched / max(1, total):.0%}) | {b} | **{rate:.1%}** "
            f"| {lo:.0%}-{hi:.0%} | {counts.get('break', 0)} | {counts.get('pierced', 0)} "
            f"| {counts.get('touched, no result', 0)} |")


def main():
    views, _ = load_views()
    series = {}
    for pair in PAIRS:
        s = Series(pair)
        sample = next(v for v in views if v["pair"] == pair)
        lo, hi, av, ok = s.window(sample["time"] - 600, sample["time"] + 60)
        s.scale = 10 ** round(math.log10(av[np.nonzero(ok)[0][-1]] / sample["pivot"]))
        series[pair] = s

    prepared = []
    for v in views:
        s = series[v["pair"]]
        p0 = s.price_at(v["time"])
        a = adr(s, v["time"])
        if p0 is None or a is None:
            continue
        touch = a * TOUCH_ADR
        if abs(p0 - v["pivot"]) <= touch:
            continue
        prepared.append(dict(v, p0=p0, adr=a, touch=touch, bounce=a * BOUNCE_ADR,
                             dist=abs(p0 - v["pivot"]) / a))

    rng = random.Random(SEED)
    pool = [p["dist"] for p in prepared]
    results = {}
    for horizon_key, label in (("end", "until the next view or the New York close"),
                               ("end_close", "until the New York close")):
        real = defaultdict(lambda: defaultdict(int))
        control = defaultdict(lambda: defaultdict(int))
        mirror = defaultdict(lambda: defaultdict(int))
        for p in prepared:
            s = series[p["pair"]]
            start = (p["time"] // 60 + 1) * 60
            lo, hi, av, ok = s.window(start, p[horizon_key])
            if not ok.any():
                continue
            lo = lo / s.scale
            hi = hi / s.scale
            above = p["p0"] > p["pivot"]
            for key in ("All pairs", p["pair"], "pivot below price (support)" if above
                        else "pivot above price (resistance)"):
                real[key][outcome(lo, hi, ok, p["pivot"], above, p["touch"], p["bounce"])] += 1
            m_level = p["p0"] + (p["p0"] - p["pivot"])
            m = outcome(lo, hi, ok, m_level, not above, p["touch"], p["bounce"])
            mirror["All pairs"][m] += 1
            mirror[p["pair"]][m] += 1
            for _ in range(RANDOM_PER_VIEW):
                d = rng.choice(pool) * p["adr"]
                if d <= p["touch"]:
                    continue
                side_above = rng.random() < 0.5
                level = p["p0"] - d if side_above else p["p0"] + d
                r = outcome(lo, hi, ok, level, side_above, p["touch"], p["bounce"])
                control["All pairs"][r] += 1
                control[p["pair"]][r] += 1
        results[label] = (real, control, mirror)

    out = ["# Trading Central pivot as a bounce level", ""]
    out.append(f"Intraday views of {', '.join(PAIRS)}, 2026-08-16 .. 2026-10-01, one distinct level set = one "
               f"pivot ({len(prepared)} pivots with price data and ADR). Rules of `market-review/levels.md`: "
               f"touch = the minute high/low comes within {TOUCH_ADR:.0%} ADR({ADR_DAYS}) of the level; "
               f"bounce = after the first touch the price moves {BOUNCE_ADR:.0%} ADR away before it goes more "
               f"than {TOUCH_ADR:.0%} ADR through; break = {BOUNCE_ADR:.0%} ADR through; pierced = more than "
               f"{TOUCH_ADR:.0%} through but less than {BOUNCE_ADR:.0%}. Bounce rate = bounce / touched.")
    out.append("")
    out.append(f"Control: {RANDOM_PER_VIEW} random levels per view, distance from the price taken from the "
               f"real pivot distances (in ADR), random side, same time window and rules. Mirror: one level at "
               f"the same distance on the other side of the price.")
    out.append("")
    header = ("| Set | Levels | Touched | Bounce | Bounce rate | 95% interval | Break | Pierced | "
              "No result |\n|---|---|---|---|---|---|---|---|---|")
    for label, (real, control, mirror) in results.items():
        out.append(f"## Horizon: {label}")
        out.append("")
        out.append(header)
        for key in ["All pairs", "pivot below price (support)", "pivot above price (resistance)"] + PAIRS:
            if key in real:
                out.append(line(f"TC {key}", real[key]))
        out.append(line("Random levels, all pairs", control["All pairs"]))
        out.append(line("Mirror levels, all pairs", mirror["All pairs"]))
        out.append("")
        out.append("Random levels by pair:")
        out.append("")
        out.append(header)
        for pair in PAIRS:
            out.append(line(pair, control[pair]))
        out.append("")
    text = "\n".join(out)
    (REPORTS / "tc-pivot-bounce.md").write_text(text, encoding="utf8")
    print(text)


if __name__ == "__main__":
    main()
