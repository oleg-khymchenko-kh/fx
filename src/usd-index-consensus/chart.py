import calendar
import datetime as dt
import sys
import numpy as np
from m1 import common_grid, usd_up_moves

PAIRS = ["EURUSD", "GBPUSD", "USDCHF", "USDCAD", "NZDUSD", "AUDUSD"]
COLORS = ["#1f77b4", "#d62728", "#2ca02c", "#ff7f0e", "#9467bd", "#8c564b"]


def parse_time(text):
    fmt = "%Y-%m-%dT%H:%M" if "T" in text else "%Y-%m-%d"
    return calendar.timegm(dt.datetime.strptime(text, fmt).timetuple())


def main():
    src, out, start, end = sys.argv[1:5]
    marks = [parse_time(m) for m in sys.argv[5].split(",")] if len(sys.argv) > 5 else []
    ts, u_mean, u_med, u_new = np.load(src)
    t_from, t_to = parse_time(start), parse_time(end)
    grid_ts, prices = common_grid(PAIRS, int(ts[0]), int(ts[-1]))
    x = usd_up_moves(prices, PAIRS, "pips")[np.searchsorted(grid_ts, ts)]
    idx = np.nonzero((ts >= t_from) & (ts <= t_to))[0][::30]
    ts, u_mean, u_med, u_new, x = ts[idx], u_mean[idx], u_med[idx], u_new[idx], x[idx]
    u_mean, u_med, u_new, x = u_mean - u_mean[0], u_med - u_med[0], u_new - u_new[0], x - x[0]

    width, height, ml, mr, mt, mb = 1400, 760, 60, 20, 20, 50
    ymin = min(x.min(), u_mean.min(), u_med.min(), u_new.min()) - 20
    ymax = max(x.max(), u_mean.max(), u_med.max(), u_new.max()) + 20
    X = lambda t: ml + (t - ts[0]) / (ts[-1] - ts[0]) * (width - ml - mr)
    Y = lambda v: mt + (ymax - v) / (ymax - ymin) * (height - mt - mb)

    def line(vals, color, w, dash=""):
        pts = " ".join(f"{X(t):.1f},{Y(v):.1f}" for t, v in zip(ts, vals))
        d = f' stroke-dasharray="{dash}"' if dash else ""
        return f'<polyline fill="none" stroke="{color}" stroke-width="{w}"{d} points="{pts}"/>'

    svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" '
           f'style="background:#fff;font-family:sans-serif;font-size:12px">']
    for lvl in range(int(ymin // 100) * 100, int(ymax) + 100, 100):
        if ymin <= lvl <= ymax:
            svg.append(f'<line x1="{ml}" x2="{width - mr}" y1="{Y(lvl):.1f}" y2="{Y(lvl):.1f}" stroke="#eee"/>')
            svg.append(f'<text x="5" y="{Y(lvl) + 4:.1f}" fill="#666">{lvl:+d}</text>')
    day = dt.datetime.fromtimestamp(int(ts[0]), dt.UTC).replace(hour=0, minute=0)
    while day.timestamp() < ts[-1]:
        t = calendar.timegm(day.timetuple())
        if day.weekday() == 0 and t >= ts[0]:
            svg.append(f'<line x1="{X(t):.1f}" x2="{X(t):.1f}" y1="{mt}" y2="{height - mb}" stroke="#ddd"/>')
            svg.append(f'<text x="{X(t) + 3:.1f}" y="{height - mb + 15}" fill="#666">{day:%d %b}</text>')
        day += dt.timedelta(days=1)
    for col, series in zip(COLORS, x.T):
        svg.append(line(series, col, 1))
    svg.append(line(u_mean, "#999", 2, "6,4"))
    svg.append(line(u_med, "#000", 2, "2,3"))
    svg.append(line(u_new, "#000", 3))
    for t in marks:
        svg.append(f'<line x1="{X(t):.1f}" x2="{X(t):.1f}" y1="{mt}" y2="{height - mb}" stroke="#f0a" '
                   f'stroke-width="1.5" stroke-dasharray="4,3"/>')
    lx, ly = ml + 10, mt + 15
    legend = [(p, c, 1, "") for p, c in zip(PAIRS, COLORS)] + [
        ("USD index: mean of steps", "#999", 2, "6,4"), ("USD index: v1 (median of steps)", "#000", 2, "2,3"),
        ("USD index: consensus", "#000", 3, "")]
    for name, col, w, dash in legend:
        d = f' stroke-dasharray="{dash}"' if dash else ""
        svg.append(f'<line x1="{lx}" x2="{lx + 30}" y1="{ly}" y2="{ly}" stroke="{col}" stroke-width="{w}"{d}/>')
        svg.append(f'<text x="{lx + 36}" y="{ly + 4}">{name}</text>')
        ly += 16
    svg.append(f'<text x="700" y="15" fill="#444">all lines in the "USD up" direction, pips '
               f'(USDCHF and USDCAD as is, the other four mirrored), zero at {start}</text>')
    svg.append("</svg>")
    html = ("<!doctype html><html><head><meta charset='utf-8'><title>USD index consensus</title></head>"
            "<body style='margin:0'>" + "\n".join(svg) + "</body></html>")
    with open(out, "w", encoding="utf-8") as f:
        f.write(html)
    print("written", out)


if __name__ == "__main__":
    main()
