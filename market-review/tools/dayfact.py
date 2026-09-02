import struct
import sys
import os
from datetime import datetime, timedelta, timezone

DATA = r"C:\Users\Oleg\Documents\Oleg\fx\FXViewer\bin\Debug\net10.0-windows\data"
HEADER = 64
REC = 16
FLAG_FILLED = 1


class Year:
    def __init__(self, symbol, year):
        path = os.path.join(DATA, symbol, "%d.m1" % year)
        with open(path, "rb") as f:
            self.buf = f.read()
        (magic, ver, recsize, yr, mins, scale, digits) = struct.unpack_from("<IiiiiiI", self.buf, 0)
        self.scale = scale
        self.digits = digits
        self.year = year
        self.minutes = mins

    def moy(self, dt):
        start = datetime(self.year, 1, 1, tzinfo=timezone.utc)
        return int((dt - start).total_seconds() // 60)

    def get(self, dt):
        i = self.moy(dt)
        if i < 0 or i >= self.minutes:
            return None
        off = HEADER + i * REC
        lo, hi, avg, flags = struct.unpack_from("<iiiI", self.buf, off)
        if not (flags & FLAG_FILLED):
            return None
        return (lo / float(self.scale), hi / float(self.scale), avg / float(self.scale), flags)

    def series(self, dt_from, dt_to):
        out = []
        cur = dt_from
        while cur <= dt_to:
            c = self.get(cur)
            if c:
                out.append((cur, c[0], c[1], c[2]))
            cur += timedelta(minutes=1)
        return out


def fmt(x, d=5):
    return ("%." + str(d) + "f") % x


def main():
    day = sys.argv[1] if len(sys.argv) > 1 else "2026-09-01"
    d = datetime.strptime(day, "%Y-%m-%d").replace(tzinfo=timezone.utc)
    start = d - timedelta(days=1)
    start = start.replace(hour=21, minute=0)
    end = d.replace(hour=20, minute=59)
    symbols = sys.argv[2].split(",") if len(sys.argv) > 2 else ["EURUSD", "GBPUSD", "USDCHF", "EURGBP"]
    for sym in symbols:
        y = Year(sym, d.year)
        s = y.series(start, end)
        if not s:
            print("%s: no data" % sym)
            continue
        lo = min(x[1] for x in s)
        hi = max(x[2] for x in s)
        lo_t = [x[0] for x in s if x[1] == lo][0]
        hi_t = [x[0] for x in s if x[2] == hi][0]
        first = s[0]
        last = s[-1]
        print("=== %s  minutes=%d" % (sym, len(s)))
        print("  open  %s at %s" % (fmt(first[3]), first[0].strftime("%H:%M")))
        print("  close %s at %s" % (fmt(last[3]), last[0].strftime("%H:%M")))
        print("  high  %s at %s" % (fmt(hi), hi_t.strftime("%m-%d %H:%M")))
        print("  low   %s at %s" % (fmt(lo), lo_t.strftime("%m-%d %H:%M")))
        print("  range %.1f pips" % ((hi - lo) * 10000))
        prev = None
        rows = []
        cur = start
        while cur <= end:
            h_end = min(cur + timedelta(minutes=59), end)
            seg = [x for x in s if cur <= x[0] <= h_end]
            if seg:
                o = seg[0][3]
                c = seg[-1][3]
                hh = max(x[2] for x in seg)
                ll = min(x[1] for x in seg)
                rows.append((cur, o, c, hh, ll))
            cur += timedelta(hours=1)
        print("  hourly (UTC): time  open close  high  low  chg_pips  range_pips")
        for r in rows:
            chg = (r[2] - r[1]) * 10000
            rng = (r[3] - r[4]) * 10000
            mark = " <<<" if abs(chg) >= 40 or rng >= 55 else ""
            print("   %s  %s %s  %s %s  %+7.1f  %6.1f%s" % (
                r[0].strftime("%m-%d %H:%M"), fmt(r[1]), fmt(r[2]), fmt(r[3]), fmt(r[4]), chg, rng, mark))


if __name__ == "__main__":
    main()
