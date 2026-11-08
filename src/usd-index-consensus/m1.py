import calendar
import os
import time
import numpy as np

DEFAULT_ROOT = r"C:\Users\Oleg\Documents\Oleg\fx\FXViewer\bin\Debug\net10.0-windows\data"
HEADER = 64
RECORD = np.dtype([("lo", "<i4"), ("hi", "<i4"), ("avg", "<i4"), ("flags", "<u4")])
FLAG_FILLED = 1


def year_start_unix(year):
    return calendar.timegm((year, 1, 1, 0, 0, 0))


def load_year(root, symbol, year):
    path = os.path.join(root, symbol, f"{year}.m1")
    raw = np.fromfile(path, dtype=np.uint8, offset=HEADER)
    rec = raw.view(RECORD)
    idx = np.nonzero((rec["flags"] & FLAG_FILLED) != 0)[0]
    ts = year_start_unix(year) + idx.astype(np.int64) * 60
    return ts, rec["avg"][idx].astype(np.float64)


def load(root, symbol, years):
    parts = [load_year(root, symbol, y) for y in years if os.path.exists(os.path.join(root, symbol, f"{y}.m1"))]
    return np.concatenate([p[0] for p in parts]), np.concatenate([p[1] for p in parts])


def common_grid(symbols, t_from, t_to, root=DEFAULT_ROOT):
    years = range(time.gmtime(t_from).tm_year, time.gmtime(t_to).tm_year + 1)
    series = {}
    common = None
    for s in symbols:
        ts, avg = load(root, s, years)
        keep = (ts >= t_from) & (ts <= t_to)
        ts, avg = ts[keep], avg[keep]
        series[s] = (ts, avg)
        common = ts if common is None else np.intersect1d(common, ts)
    prices = {}
    for s in symbols:
        ts, avg = series[s]
        prices[s] = avg[np.searchsorted(ts, common)]
    return common, prices


def usd_up_moves(prices, pairs, unit="pips"):
    cols = []
    for p in pairs:
        v = prices[p]
        s = (v - v[0]) / 10.0 if unit == "pips" else np.log(v / v[0]) * 10000.0
        cols.append(s if p.startswith("USD") else -s)
    return np.stack(cols, axis=1)
