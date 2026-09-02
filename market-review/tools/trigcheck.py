import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dayfact import Year
from datetime import datetime, timedelta, timezone

d = datetime(2026,9,1,tzinfo=timezone.utc)
start = d - timedelta(days=1); start = start.replace(hour=21, minute=0)
end = d.replace(hour=20, minute=59)

def check(sym, level, side, digits=5):
    y = Year(sym, 2026)
    s = y.series(start, end)
    run = 0; best = 0; best_t = None; cur_start=None
    for (t, lo, hi, avg) in s:
        out = (avg < level) if side=='below' else (avg > level)
        if out:
            if run==0: cur_start=t
            run += 1
            if run > best: best = run; best_t = cur_start
        else:
            run = 0
    print("%s %s %s: longest streak %d min, started %s" % (sym, side, level, best, best_t.strftime("%H:%M") if best_t else "-"))

check('EURUSD', 1.1588, 'below')
check('EURUSD', 1.1641, 'above')
check('GBPUSD', 1.3511, 'below')
check('GBPUSD', 1.3572, 'above')
check('USDCHF', 0.8111, 'above')
check('USDCHF', 0.8054, 'below')
check('EURGBP', 0.8558, 'below')
check('EURGBP', 0.8581, 'above')
