import csv
import json
import math
import re
import struct
import sys
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

import numpy as np

REPO = Path(r"C:\Users\Oleg\Documents\Oleg\fx")
DATA = REPO / r"FXViewer\bin\Debug\net10.0-windows\data"
HISTORY = DATA / "trading-central" / "api-history" / "forex.jsonl"
REPORTS = REPO / "reports"
PAIRS = ["EURUSD", "GBPUSD", "USDCHF", "USDJPY", "USDCAD", "AUDUSD", "NZDUSD", "EURGBP"]
HEADER = 64
RECORD = 16
FILLED = 1
NEW_YORK = ZoneInfo("America/New_York")
SPREAD_PIPS = float(sys.argv[1]) if len(sys.argv) > 1 else 1.0
NUMBER = re.compile(r"(?<![\w.])\d{1,3}(?:,\d{3})+(?:\.\d+)?|(?<![\w.])\d+(?:\.\d+)?")


def numbers(text):
    return [float(m.replace(",", "")) for m in NUMBER.findall(text or "")]


def pip_of(pair):
    return 0.01 if pair.endswith("JPY") else 0.0001


class Series:
    def __init__(self, pair):
        self.years = {}
        for path in (DATA / pair).glob("*.m1"):
            if not path.stem.isdigit():
                continue
            raw = np.fromfile(path, dtype=np.uint8)[HEADER:]
            count = len(raw) // RECORD
            rec = raw[: count * RECORD].view(np.int32).reshape(count, 4)
            filled = (rec[:, 3].view(np.uint32) & FILLED) != 0
            self.years[int(path.stem)] = (rec[:, 0].copy(), rec[:, 1].copy(), rec[:, 2].copy(), filled)
        self.scale = None

    def window(self, start_unix, end_unix):
        lows, highs, avgs, ok = [], [], [], []
        t = start_unix
        while t < end_unix:
            utc = datetime.fromtimestamp(t, timezone.utc)
            year_start = int(datetime(utc.year, 1, 1, tzinfo=timezone.utc).timestamp())
            year_end = int(datetime(utc.year + 1, 1, 1, tzinfo=timezone.utc).timestamp())
            stop = min(end_unix, year_end)
            data = self.years.get(utc.year)
            a, b = (t - year_start) // 60, (stop - year_start) // 60
            if data is None:
                n = b - a
                lows.append(np.zeros(n, np.int32)); highs.append(np.zeros(n, np.int32))
                avgs.append(np.zeros(n, np.int32)); ok.append(np.zeros(n, bool))
            else:
                lo, hi, av, fl = data
                lows.append(lo[a:b]); highs.append(hi[a:b]); avgs.append(av[a:b]); ok.append(fl[a:b])
            t = stop
        return np.concatenate(lows), np.concatenate(highs), np.concatenate(avgs), np.concatenate(ok)

    def price_at(self, unix, back_minutes=10):
        lo, hi, av, ok = self.window(unix - back_minutes * 60, unix + 60)
        idx = np.nonzero(ok)[0]
        return None if len(idx) == 0 else av[idx[-1]] / self.scale


def ny_close_after(unix):
    local = datetime.fromtimestamp(unix, timezone.utc).astimezone(NEW_YORK)
    close = local.replace(hour=17, minute=0, second=0, microsecond=0)
    if close <= local:
        close += timedelta(days=1)
    return int(close.timestamp())


def load_views():
    by_pair = defaultdict(list)
    for line in HISTORY.open(encoding="utf8"):
        d = json.loads(line)
        if d.get("symbol") in PAIRS and d.get("term") == "Intraday":
            by_pair[d["symbol"]].append(d)
    views = []
    skipped = defaultdict(int)
    for pair, items in by_pair.items():
        items.sort(key=lambda d: (d["time"], d["articleId"]))
        last_key = None
        for d in items:
            levels = d.get("levels") or {}
            try:
                raw_pivot = str(levels.get("pivot", "")).split()[0]
                if "." not in raw_pivot:
                    raw_pivot = raw_pivot.replace(",", ".")
                pivot = float(raw_pivot)
            except (ValueError, IndexError):
                skipped["no pivot"] += 1
                continue
            targets = [n for n in numbers(d.get("ourPreference")) if abs(n - pivot) > 1e-9]
            alternatives = [n for n in numbers(d.get("alternativeScenario")) if abs(n - pivot) > 1e-9]
            if not targets:
                skipped["no targets in text"] += 1
                continue
            key = (pivot, tuple(targets), tuple(alternatives))
            if key == last_key:
                continue
            last_key = key
            views.append({"pair": pair, "id": d["articleId"], "time": d["time"], "pivot": pivot,
                          "targets": targets, "alternatives": alternatives, "title": d.get("title", ""),
                          "opinion": (d.get("opinions") or {}).get("opinionForTerm")})
    views.sort(key=lambda v: (v["pair"], v["time"]))
    for i, v in enumerate(views):
        nxt = views[i + 1]["time"] if i + 1 < len(views) and views[i + 1]["pair"] == v["pair"] else None
        close = ny_close_after(v["time"])
        v["end"] = min(close, nxt) if nxt else close
        v["end_close"] = close
    return views, skipped


def first_touch(lo, hi, ok, up_level, down_level):
    up = np.nonzero(ok & (hi >= up_level))[0]
    down = np.nonzero(ok & (lo <= down_level))[0]
    u = up[0] if len(up) else None
    w = down[0] if len(down) else None
    if u is None and w is None:
        return "open", None
    if w is None or (u is not None and u < w):
        return "up", u
    if u is None or w < u:
        return "down", w
    return "both", u


def evaluate(v, series, horizon_key):
    pair = v["pair"]
    s = series[pair]
    pip = pip_of(pair)
    p0 = s.price_at(v["time"])
    if p0 is None:
        return {"status": "no price"}
    pivot, t1 = v["pivot"], v["targets"][0]
    bull = t1 > pivot
    if bull and not (pivot < p0 < t1) or (not bull) and not (t1 < p0 < pivot):
        return {"status": "price outside pivot..target", "p0": p0}
    d_target = abs(t1 - p0)
    d_stop = abs(p0 - pivot)
    start = (v["time"] // 60 + 1) * 60
    end = v[horizon_key]
    if end <= start:
        return {"status": "no time"}
    lo, hi, av, ok = s.window(start, end)
    if not ok.any():
        return {"status": "no candles"}
    lo = lo / s.scale
    hi = hi / s.scale
    av = av / s.scale

    def run(target, stop, long):
        up_level, down_level = (target, stop) if long else (stop, target)
        side, _ = first_touch(lo, hi, ok, up_level, down_level)
        if side == "open":
            last = av[np.nonzero(ok)[0][-1]]
            pnl = (last - p0) if long else (p0 - last)
            return "open", pnl / pip
        win = (side == "up") == long and side != "both"
        return ("target" if win else "stop"), (abs(target - p0) if win else -abs(stop - p0)) / pip

    real = run(t1, pivot, bull)
    mirror_t = p0 - d_target if bull else p0 + d_target
    mirror_s = p0 + d_stop if bull else p0 - d_stop
    mirror = run(mirror_t, mirror_s, not bull)
    before = s.price_at(v["time"] - 24 * 3600, back_minutes=120)
    momentum = None
    if before is not None and before != p0:
        momentum_long = p0 > before
        same = momentum_long == bull
        momentum = real if same else mirror
    t2 = None
    if len(v["targets"]) > 1:
        t2 = run(v["targets"][1], pivot, bull)[0]
    return {"status": "ok", "p0": p0, "bull": bull, "d_target": d_target / pip, "d_stop": d_stop / pip,
            "p_random": d_stop / (d_target + d_stop), "real": real[0], "real_pips": real[1],
            "mirror": mirror[0], "mirror_pips": mirror[1], "t2": t2,
            "momentum": momentum[0] if momentum else None, "momentum_pips": momentum[1] if momentum else None,
            "with_momentum": None if momentum is None else momentum is real}


def summarize(name, rows, out):
    ok = [r for r in rows if r["status"] == "ok"]
    resolved = [r for r in ok if r["real"] != "open"]
    hits = sum(r["real"] == "target" for r in resolved)
    expected = sum(r["p_random"] for r in resolved)
    var = sum(r["p_random"] * (1 - r["p_random"]) for r in resolved)
    z = (hits - expected) / math.sqrt(var) if var else 0
    m_resolved = [r for r in ok if r["mirror"] != "open"]
    m_hits = sum(r["mirror"] == "target" for r in m_resolved)
    pnl = np.array([r["real_pips"] for r in ok]) if ok else np.zeros(1)
    m_pnl = np.array([r["mirror_pips"] for r in ok]) if ok else np.zeros(1)
    t2 = [r for r in ok if r["t2"] in ("target", "stop")]
    out.append(f"### {name}")
    out.append("")
    out.append(f"- views checked: {len(ok)}, resolved (target or pivot reached): {len(resolved)}, "
               f"open at the end: {len(ok) - len(resolved)}")
    if resolved:
        out.append(f"- first target before pivot: {hits} of {len(resolved)} = {hits / len(resolved):.1%}; "
                   f"random walk expects {expected / len(resolved):.1%} ({expected:.1f}); z = {z:+.2f}")
    if m_resolved:
        out.append(f"- mirrored trade (same distances, other way): {m_hits} of {len(m_resolved)} = "
                   f"{m_hits / len(m_resolved):.1%}")
    if t2:
        t2_hits = sum(r["t2"] == "target" for r in t2)
        out.append(f"- second target before pivot: {t2_hits} of {len(t2)} = {t2_hits / len(t2):.1%}")
    out.append(f"- trade P0 -> T1 / stop at pivot, avg pips per view: {pnl.mean():+.1f} "
               f"(after {SPREAD_PIPS:g} pip spread {pnl.mean() - SPREAD_PIPS:+.1f}); "
               f"mirrored {m_pnl.mean():+.1f}; total {pnl.sum():+.0f} vs mirrored {m_pnl.sum():+.0f} pips")
    mom = [r for r in ok if r["momentum"] is not None]
    if mom:
        mom_resolved = [r for r in mom if r["momentum"] != "open"]
        mom_hits = sum(r["momentum"] == "target" for r in mom_resolved)
        agree = sum(r["with_momentum"] for r in mom)
        out.append(f"- TC direction equals the last 24 h move in {agree} of {len(mom)} = {agree / len(mom):.0%}; "
                   f"'follow the last 24 h' with the same distances: {mom_hits} of {len(mom_resolved)} = "
                   f"{mom_hits / max(1, len(mom_resolved)):.1%}, avg {np.mean([r['momentum_pips'] for r in mom]):+.1f} pips")
    if ok:
        out.append(f"- median distance to T1 {np.median([r['d_target'] for r in ok]):.1f} pips, "
                   f"to pivot {np.median([r['d_stop'] for r in ok]):.1f} pips")
    out.append("")


def main():
    views, skipped = load_views()
    series = {}
    for pair in PAIRS:
        s = Series(pair)
        sample = next(v for v in views if v["pair"] == pair)
        lo, hi, av, ok = s.window(sample["time"] - 600, sample["time"] + 60)
        raw = av[np.nonzero(ok)[0][-1]]
        s.scale = 10 ** round(math.log10(raw / sample["pivot"]))
        series[pair] = s

    out = ["# Trading Central accuracy check", ""]
    first = datetime.fromtimestamp(min(v["time"] for v in views), timezone.utc)
    last = datetime.fromtimestamp(max(v["time"] for v in views), timezone.utc)
    out.append(f"Intraday views, {first:%Y-%m-%d} .. {last:%Y-%m-%d}, pairs {', '.join(PAIRS)}. "
               f"One view = one distinct set of pivot and targets; repeats with the same levels are merged. "
               f"Prices are FxPro bid minutes. Spread used for the pip result: {SPREAD_PIPS:g} pip.")
    out.append("")
    out.append(f"Distinct views: {len(views)}. Dropped before the check: "
               + ", ".join(f"{k} {n}" for k, n in skipped.items()))
    out.append("")

    csv_rows = []
    for horizon_key, label in (("end", "until the next view or the New York close"),
                               ("end_close", "until the New York close, ignoring newer views")):
        rows = [dict(v, **evaluate(v, series, horizon_key)) for v in views]
        statuses = defaultdict(int)
        for r in rows:
            statuses[r["status"]] += 1
        out.append(f"## Horizon: {label}")
        out.append("")
        out.append("Status: " + ", ".join(f"{k} {n}" for k, n in statuses.items()))
        out.append("")
        summarize("All pairs", rows, out)
        for pair in PAIRS:
            summarize(pair, [r for r in rows if r["pair"] == pair], out)
        summarize("Bullish views", [r for r in rows if r.get("bull") is True], out)
        summarize("Bearish views", [r for r in rows if r.get("bull") is False], out)
        if horizon_key == "end":
            csv_rows = rows

    REPORTS.mkdir(exist_ok=True)
    (REPORTS / "tc-accuracy.md").write_text("\n".join(out), encoding="utf8")
    fields = ["pair", "id", "time", "title", "pivot", "targets", "alternatives", "status", "p0", "bull",
              "d_target", "d_stop", "p_random", "real", "real_pips", "mirror", "mirror_pips", "t2"]
    with (REPORTS / "tc-accuracy-views.csv").open("w", newline="", encoding="utf8") as f:
        w = csv.DictWriter(f, fieldnames=fields, extrasaction="ignore", delimiter=";")
        w.writeheader()
        for r in csv_rows:
            r = dict(r)
            r["time"] = datetime.fromtimestamp(r["time"], timezone.utc).strftime("%Y-%m-%d %H:%M")
            r["targets"] = " ".join(map(str, r["targets"]))
            r["alternatives"] = " ".join(map(str, r["alternatives"]))
            w.writerow(r)
    print("\n".join(out))


if __name__ == "__main__":
    main()
