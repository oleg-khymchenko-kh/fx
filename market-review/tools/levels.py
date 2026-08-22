import argparse
import datetime
import struct
import sys
from pathlib import Path

if sys.stdout.encoding and sys.stdout.encoding.lower() != "utf-8":
    sys.stdout.reconfigure(encoding="utf-8")

DATA_ROOT = Path(__file__).resolve().parents[2] / "FXViewer" / "bin" / "Debug" / "net10.0-windows" / "data"
HEADER_SIZE = 64
RECORD_SIZE = 16
DAY_BOUNDARY_UTC_HOUR = 21
MIN_MINUTES_PER_DAY = 300


def load_minutes(symbol, years):
    minutes = {}
    scale = 100000
    digits = 5
    for year in years:
        path = DATA_ROOT / symbol / f"{year}.m1"
        if not path.exists():
            continue
        data = path.read_bytes()
        magic, _ver, rec, y, mins, scale, digits = struct.unpack_from("<IiiiiiI", data, 0)
        if magic != 0x314D5846:
            raise ValueError(f"not a candle file: {path}")
        base = datetime.datetime(year, 1, 1, tzinfo=datetime.timezone.utc)
        for i in range(mins):
            off = HEADER_SIZE + i * RECORD_SIZE
            lo, hi, avg, flags = struct.unpack_from("<iiiI", data, off)
            if flags & 1:
                minutes[base + datetime.timedelta(minutes=i)] = (lo, hi, avg)
    return minutes, scale, digits


def trading_day_window(day):
    start = datetime.datetime(day.year, day.month, day.day, DAY_BOUNDARY_UTC_HOUR, tzinfo=datetime.timezone.utc) - datetime.timedelta(days=1)
    return start, start + datetime.timedelta(days=1)


def day_ohlc(minutes, day):
    start, end = trading_day_window(day)
    keys = sorted(k for k in minutes if start <= k < end)
    if len(keys) < MIN_MINUTES_PER_DAY:
        return None
    o = minutes[keys[0]][2]
    c = minutes[keys[-1]][2]
    h = max(minutes[k][1] for k in keys)
    l = min(minutes[k][0] for k in keys)
    return o, h, l, c, len(keys)


def previous_trading_days(minutes, before_day, count):
    days = []
    day = before_day
    for _ in range(30):
        day -= datetime.timedelta(days=1)
        ohlc = day_ohlc(minutes, day)
        if ohlc:
            days.append((day, ohlc))
            if len(days) == count:
                break
    return days


def week_ohlc(minutes, monday):
    start = datetime.datetime(monday.year, monday.month, monday.day, DAY_BOUNDARY_UTC_HOUR, tzinfo=datetime.timezone.utc) - datetime.timedelta(days=1)
    end = start + datetime.timedelta(days=5)
    keys = sorted(k for k in minutes if start <= k < end)
    if not keys:
        return None
    return minutes[keys[0]][2], max(minutes[k][1] for k in keys), min(minutes[k][0] for k in keys), minutes[keys[-1]][2]


def classic_pivots(h, l, c):
    p = (h + l + c) / 3
    return {
        "P": p,
        "R1": 2 * p - l,
        "S1": 2 * p - h,
        "R2": p + (h - l),
        "S2": p - (h - l),
        "R3": h + 2 * (p - l),
        "S3": l - 2 * (h - p),
    }


def fibonacci_pivots(h, l, c):
    p = (h + l + c) / 3
    r = h - l
    return {
        "P": p,
        "R1": p + 0.382 * r,
        "S1": p - 0.382 * r,
        "R2": p + 0.618 * r,
        "S2": p - 0.618 * r,
        "R3": p + 1.000 * r,
        "S3": p - 1.000 * r,
    }


def camarilla_pivots(h, l, c):
    r = h - l
    return {
        "R4": c + r * 1.1 / 2,
        "R3": c + r * 1.1 / 4,
        "R2": c + r * 1.1 / 6,
        "R1": c + r * 1.1 / 12,
        "S1": c - r * 1.1 / 12,
        "S2": c - r * 1.1 / 6,
        "S3": c - r * 1.1 / 4,
        "S4": c - r * 1.1 / 2,
    }


def round_levels(c, scale, step_pips=50, count=2):
    pip = scale / 10000
    step = step_pips * pip
    base = round(c / step) * step
    levels = sorted({base + i * step for i in range(-count, count + 1)})
    return levels


def fmt(v, scale, digits):
    return f"{v / scale:.{digits}f}"


def pips(v, scale):
    return v / (scale / 10000)


def next_trading_day(minutes):
    last = max(minutes)
    day = last.date()
    if last.hour >= DAY_BOUNDARY_UTC_HOUR:
        day += datetime.timedelta(days=1)
    while day.weekday() >= 5:
        day += datetime.timedelta(days=1)
    return day


def report(symbol, target_day):
    years = {target_day.year, target_day.year - 1}
    minutes, scale, digits = load_minutes(symbol, sorted(years))
    if not minutes:
        print(f"## {symbol}: no data")
        return
    if target_day is None:
        target_day = next_trading_day(minutes)

    days = previous_trading_days(minutes, target_day, 21)
    if not days:
        print(f"## {symbol}: no completed trading days before {target_day}")
        return
    prev_day, (o, h, l, c, filled) = days[0]

    monday = target_day - datetime.timedelta(days=target_day.weekday())
    prev_monday = monday - datetime.timedelta(days=7)
    wk = week_ohlc(minutes, prev_monday)

    ranges = [ph - pl for _, (_po, ph, pl, _pc, _n) in days]
    adr = {n: sum(ranges[:n]) / n for n in (5, 10, 20) if len(ranges) >= n}

    print(f"## {symbol} - уровни на {target_day} (якорь: день {prev_day}, граница {DAY_BOUNDARY_UTC_HOUR}:00 UTC)")
    print()
    print(f"Вчерашний день: O {fmt(o, scale, digits)}  H {fmt(h, scale, digits)}  L {fmt(l, scale, digits)}  C {fmt(c, scale, digits)}  (минут: {filled})")
    if wk:
        wo, wh, wl, wc = wk
        print(f"Прошлая неделя: O {fmt(wo, scale, digits)}  H {fmt(wh, scale, digits)}  L {fmt(wl, scale, digits)}  C {fmt(wc, scale, digits)}")
    print()

    cp = classic_pivots(h, l, c)
    fp = fibonacci_pivots(h, l, c)
    cam = camarilla_pivots(h, l, c)
    print("| Уровень | Classic | Fibonacci | Camarilla |")
    print("|---|---|---|---|")
    for name in ("R4", "R3", "R2", "R1", "P", "S1", "S2", "S3", "S4"):
        cl = fmt(cp[name], scale, digits) if name in cp else "-"
        fb = fmt(fp[name], scale, digits) if name in fp else "-"
        cm = fmt(cam[name], scale, digits) if name in cam else "-"
        print(f"| {name} | {cl} | {fb} | {cm} |")
    print()

    if wk:
        wcp = classic_pivots(wh, wl, wc)
        wk_line = ", ".join(f"{k} {fmt(v, scale, digits)}" for k, v in wcp.items())
        print(f"Недельные classic-пивоты: {wk_line}")
    adr_line = ", ".join(f"ADR({n}) {pips(v, scale):.0f} п" for n, v in adr.items())
    print(f"Средний дневной размах: {adr_line}")
    if 5 in adr:
        half = adr[5] / 2
        print(f"Проекция диапазона от закрытия {fmt(c, scale, digits)}: ±ADR(5)/2 = {fmt(c - half, scale, digits)} - {fmt(c + half, scale, digits)}; полный ADR(5) вниз {fmt(c - adr[5], scale, digits)} / вверх {fmt(c + adr[5], scale, digits)}")
    rounds = ", ".join(fmt(v, scale, digits) for v in round_levels(c, scale))
    print(f"Круглые уровни рядом: {rounds}")
    print()


def main():
    parser = argparse.ArgumentParser(description="Time-invariant FX levels from the FXViewer minute database")
    parser.add_argument("symbols", nargs="*", default=["EURUSD", "GBPUSD", "USDCHF", "EURGBP"])
    parser.add_argument("--date", help="trading day being forecast, YYYY-MM-DD; default: next trading day in data")
    args = parser.parse_args()
    symbols = args.symbols or ["EURUSD", "GBPUSD", "USDCHF", "EURGBP"]
    target = datetime.date.fromisoformat(args.date) if args.date else None
    for symbol in symbols:
        if target is None:
            minutes, _s, _d = load_minutes(symbol, [datetime.date.today().year])
            day = next_trading_day(minutes) if minutes else None
            if day is None:
                print(f"## {symbol}: no data")
                continue
            report(symbol, day)
        else:
            report(symbol, target)


if __name__ == "__main__":
    main()
