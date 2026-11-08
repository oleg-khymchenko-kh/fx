import argparse
import json
import os
import statistics
import subprocess
import sys
import time
from datetime import datetime, timezone

CURL = r"C:\Windows\System32\curl.exe"
USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36"
LOGIN_URL = "https://www.myfxbook.com/api/login.json"
OUTLOOK_URL = "https://www.myfxbook.com/api/get-community-outlook.json"
SNAPSHOT_FIELDS = (
    "shortPercentage", "longPercentage", "shortVolume", "longVolume",
    "longPositions", "shortPositions", "avgShortPrice", "avgLongPrice",
)


def curl_json(url, query):
    args = [CURL, "-s", "-G", "-A", USER_AGENT, "-H", "Accept: application/json", url]
    for key, value in query.items():
        args += ["--data-urlencode", "%s=%s" % (key, value)]
    completed = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace")
    body = completed.stdout.strip()
    if completed.returncode != 0 or not body:
        raise RuntimeError("curl failed, exit %d: %s" % (completed.returncode, completed.stderr.strip()[:200]))
    try:
        return json.loads(body)
    except ValueError:
        raise RuntimeError("not JSON (Cloudflare page?): " + body[:200])


def login():
    session = os.environ.get("MYFXBOOK_SESSION")
    if session:
        return session
    email = os.environ.get("MYFXBOOK_EMAIL")
    password = os.environ.get("MYFXBOOK_PASSWORD")
    if not email or not password:
        sys.exit("set MYFXBOOK_SESSION, or MYFXBOOK_EMAIL and MYFXBOOK_PASSWORD, in the environment")
    answer = curl_json(LOGIN_URL, {"email": email, "password": password})
    if answer.get("error"):
        sys.exit("login failed: " + str(answer.get("message")))
    print("session:", answer["session"], "(reuse it via MYFXBOOK_SESSION, it is bound to this IP for a month)")
    return answer["session"]


def snapshot_of(symbol_row):
    return tuple(symbol_row.get(field) for field in SNAPSHOT_FIELDS)


def main():
    parser = argparse.ArgumentParser(description="Poll the Myfxbook community outlook snapshot and measure how often it changes")
    parser.add_argument("--interval", type=float, default=15, help="seconds between requests")
    parser.add_argument("--count", type=int, default=90, help="number of requests (free plan: 100 per day)")
    parser.add_argument("--symbols", default="EURUSD,GBPUSD,USDCHF")
    parser.add_argument("--out", default="", help="jsonl file for the raw answers")
    args = parser.parse_args()
    symbols = [s.strip().upper() for s in args.symbols.split(",") if s.strip()]
    session = login()
    out = open(args.out, "a", encoding="utf-8") if args.out else None
    last = {}
    change_times = {s: [] for s in symbols}
    started = time.time()
    for i in range(args.count):
        tick = time.time()
        stamp = datetime.now(timezone.utc).strftime("%H:%M:%S")
        try:
            answer = curl_json(OUTLOOK_URL, {"session": session})
        except RuntimeError as ex:
            print(stamp, "request failed:", ex)
            time.sleep(args.interval)
            continue
        if answer.get("error"):
            print(stamp, "API error:", answer.get("message"))
            break
        if out:
            out.write(json.dumps({"t": tick, "answer": answer}) + "\n")
            out.flush()
        rows = {row.get("name", "").upper(): row for row in answer.get("symbols", [])}
        changed = []
        for symbol in symbols:
            row = rows.get(symbol)
            if row is None:
                continue
            snap = snapshot_of(row)
            if symbol in last and snap != last[symbol]:
                change_times[symbol].append(tick)
                changed.append("%s %s%%/%s%% %s/%s lots %s/%s pos" % (
                    symbol, row.get("longPercentage"), row.get("shortPercentage"),
                    row.get("longVolume"), row.get("shortVolume"),
                    row.get("longPositions"), row.get("shortPositions")))
            last[symbol] = snap
        print(stamp, "#%d" % (i + 1), "changed: " + "; ".join(changed) if changed else "same")
        wait = args.interval - (time.time() - tick)
        if wait > 0 and i + 1 < args.count:
            time.sleep(wait)
    if out:
        out.close()
    elapsed = time.time() - started
    print()
    print("elapsed %.0f s, interval %.0f s" % (elapsed, args.interval))
    for symbol in symbols:
        times = change_times[symbol]
        if len(times) < 2:
            print(symbol, "changes:", len(times))
            continue
        gaps = [b - a for a, b in zip(times, times[1:])]
        print("%s changes: %d, gap between changes: min %.0f s, median %.0f s, max %.0f s" % (
            symbol, len(times), min(gaps), statistics.median(gaps), max(gaps)))


if __name__ == "__main__":
    main()
