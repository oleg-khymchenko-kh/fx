using System.IO;
using FXViewer.Storage;

namespace FXViewer;

public sealed class VolumeCollector
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public static readonly (string Pair, string Root)[] Futures =
    {
        ("EURUSD", "6E"),
        ("GBPUSD", "6B"),
        ("USDCHF", "6S"),
        ("USDJPY", "6J"),
        ("AUDUSD", "6A"),
        ("NZDUSD", "6N"),
        ("USDCAD", "6C"),
    };

    private const int SettleMinutes = 3;
    private const int StartupBackfillHours = 48;
    private const int RetryHours = StartupBackfillHours;
    private const int WaitingLogSeconds = 300;
    private const int ProfilePipPoints = 10;
    private const string DefaultInstall = @"C:\SierraChart";

    public readonly record struct Write(string Symbol, long MinuteUnix, int Volume);

    public readonly record struct ProfileWrite(string Symbol, ProfileRecord Record);

    public readonly record struct RunResult(
        IReadOnlyList<Write> Volumes, IReadOnlyList<ProfileWrite> Profiles);

    private sealed class PairState
    {
        public string Contract = "";
        public long Offset;
        public bool Missing;
        public int LastNoCandle = -1;
        public long LastNoCandleLog;
        public long ProfileFloor = -1;
        public readonly Dictionary<long, long> Pending = new();
        public readonly Dictionary<long, int> Written = new();
        public readonly Dictionary<long, TickMinute> PendingTicks = new();
        public readonly ShiftEstimator Shift = new();
    }

    private readonly Dictionary<string, PairState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;
    private readonly Func<bool> _canWrite;
    private readonly string _folder;
    private bool _firstPass = true;

    public string Folder => _folder;

    public VolumeCollector(string? configuredFolder, Func<bool> canWrite, Action<string> log)
    {
        _log = log;
        _canWrite = canWrite;
        _folder = ResolveFolder(configuredFolder);
    }

    public static string ResolveFolder(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        try
        {
            var pointer = Path.Combine(DefaultInstall, "DataFilesFolder.txt");
            if (File.Exists(pointer))
            {
                var text = File.ReadAllText(pointer).Trim();
                if (text.Length > 0) return text;
            }
        }
        catch
        {
        }
        return Path.Combine(DefaultInstall, "Data");
    }

    public RunResult RunOnce(CandleDatabase db, DateTime utcNow)
    {
        var writes = new List<Write>();
        var profileWrites = new List<ProfileWrite>();
        if (!Directory.Exists(_folder))
        {
            if (_firstPass) _log($"volume: Sierra data folder not found: {_folder}");
            _firstPass = false;
            return new RunResult(writes, profileWrites);
        }

        long nowUnix = ((DateTimeOffset)DateTime.SpecifyKind(utcNow, DateTimeKind.Utc))
            .ToUnixTimeSeconds();
        long cutoff = 0;
        if (_firstPass)
        {
            var from = utcNow.AddHours(-StartupBackfillHours);
            cutoff = ((DateTimeOffset)DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeSeconds();
            _log($"volume: reading Sierra data from {_folder}");
        }

        foreach (var (pair, root) in Futures)
        {
            if (!_state.TryGetValue(pair, out var st))
            {
                st = new PairState();
                _state[pair] = st;
            }
            var contract = FrontContract(root, utcNow);
            var path = Path.Combine(_folder, contract + "-CME.scid");
            if (!File.Exists(path))
            {
                if (!st.Missing) _log($"volume {pair}: no file {Path.GetFileName(path)}, skipped");
                st.Missing = true;
                continue;
            }
            st.Missing = false;
            if (!string.Equals(st.Contract, contract, StringComparison.OrdinalIgnoreCase))
            {
                if (st.Contract.Length > 0)
                    _log($"volume {pair}: contract {st.Contract} -> {contract}");
                else
                    _log($"volume {pair}: front contract {contract}");
                st.Contract = contract;
                st.Offset = 0;
                st.Pending.Clear();
                st.Written.Clear();
                st.PendingTicks.Clear();
                st.Shift.Reset();
            }

            var ticksInto = VolumeProfileStore.SupportedPairs.Contains(pair) ? st.PendingTicks : null;
            var res = ScidReader.ReadMinuteVolumes(path, st.Offset, st.Pending, ticksInto,
                VolumeProfileStore.IsReciprocal(pair));
            if (!res.Ok)
            {
                _log($"volume {pair}: {res.Error}");
                continue;
            }
            if (res.Restarted)
            {
                _log($"volume {pair}: {Path.GetFileName(path)} was rewritten, rereading from the start");
                st.Written.Clear();
            }
            st.Offset = res.Offset;
            if (st.Pending.Count == 0) continue;

            long newest = 0;
            foreach (var minute in st.Pending.Keys)
                if (minute > newest) newest = minute;

            int written = 0;
            int noCandle = 0;
            bool aborted = false;
            foreach (var (minute, volume) in st.Pending)
            {
                if (minute < cutoff) continue;
                if (!_canWrite()) { aborted = true; break; }
                int value = (int)Math.Min(volume, VolumeCodes.Max);
                if (st.Written.TryGetValue(minute, out var had) && had == value) continue;
                var utc = DateTimeOffset.FromUnixTimeSeconds(minute).UtcDateTime;
                if (!db.WriteVolume(pair, utc, value))
                {
                    noCandle++;
                    continue;
                }
                st.Written[minute] = value;
                writes.Add(new Write(pair, minute, value));
                written++;
            }
            if (written > 0) db.FlushSymbol(pair);

            if (ticksInto != null && !aborted && st.PendingTicks.Count > 0 && _canWrite())
            {
                var writer = db.ProfileWriter(pair, ProfilePipPoints, _log);
                if (st.ProfileFloor < 0) st.ProfileFloor = writer.LastStoredMinute();
                long skipBelow = Math.Max(st.ProfileFloor,
                    Math.Max(nowUnix, newest) - StartupBackfillHours * 3600L - 60);
                if (skipBelow > 0)
                {
                    var old = st.PendingTicks.Keys.Where(m => m <= skipBelow).ToList();
                    foreach (var m in old) st.PendingTicks.Remove(m);
                }
                long settled = newest - SettleMinutes * 60L;
                var ready = st.PendingTicks.Keys.Where(m => m <= settled).OrderBy(m => m).ToList();
                if (ready.Count > 0)
                {
                    var candles = db.ReadRange(pair,
                        DateTimeOffset.FromUnixTimeSeconds(ready[0]).UtcDateTime,
                        DateTimeOffset.FromUnixTimeSeconds(ready[^1]).UtcDateTime);
                    var avgByMinute = new Dictionary<long, int>(candles.Count);
                    foreach (var c in candles) avgByMinute[c.MinuteUnixSeconds] = c.Avg;
                    var records = new List<ProfileRecord>();
                    int overflow = 0;
                    foreach (var minute in ready)
                    {
                        var accum = st.PendingTicks[minute];
                        if (!accum.HasTrades)
                        {
                            st.PendingTicks.Remove(minute);
                            continue;
                        }
                        if (!avgByMinute.TryGetValue(minute, out var avg)) continue;
                        int shift = st.Shift.Push(minute, avg, accum.VwapPoints);
                        var record = VolumeProfileStore.Build(minute, accum, shift, ProfilePipPoints);
                        if (record == null)
                        {
                            overflow++;
                            st.PendingTicks.Remove(minute);
                            continue;
                        }
                        records.Add(record.Value);
                    }
                    if (overflow > 0)
                        _log($"volume {pair}: {overflow} minute(s) wider than " +
                            $"{VolumeProfileStore.MaxSpan} pips, profile dropped");
                    if (records.Count > 0)
                    {
                        var stored = writer.Append(records);
                        writer.Flush();
                        foreach (var r in stored)
                        {
                            st.PendingTicks.Remove(r.MinuteUnix);
                            profileWrites.Add(new ProfileWrite(pair, r));
                        }
                    }
                }
                long tickGiveUp = newest - RetryHours * 3600L;
                var expired = st.PendingTicks.Keys.Where(m => m < tickGiveUp).ToList();
                foreach (var m in expired) st.PendingTicks.Remove(m);
            }

            if (!aborted)
            {
                long settled = newest - SettleMinutes * 60L;
                long giveUp = newest - RetryHours * 3600L;
                var stale = st.Pending.Keys
                    .Where(m => m < settled && (st.Written.ContainsKey(m) || m < giveUp))
                    .ToList();
                foreach (var minute in stale)
                {
                    st.Pending.Remove(minute);
                    st.Written.Remove(minute);
                }
            }

            var upTo = DateTimeOffset.FromUnixTimeSeconds(newest).UtcDateTime;
            if (written > 0)
                _log($"volume {pair}: {contract}, {written} minutes written up to " +
                    $"{upTo:yyyy-MM-dd HH:mm} UTC" +
                    (noCandle > 0 ? $", {noCandle} waiting for a candle" : ""));
            else if (noCandle > 0 && noCandle != st.LastNoCandle
                && nowUnix - st.LastNoCandleLog >= WaitingLogSeconds)
            {
                _log($"volume {pair}: {contract}, {noCandle} minutes waiting for a candle up to " +
                    $"{upTo:yyyy-MM-dd HH:mm} UTC");
                st.LastNoCandleLog = nowUnix;
            }
            if (!aborted) st.LastNoCandle = noCandle;
        }

        _firstPass = false;
        return new RunResult(writes, profileWrites);
    }

    public static string FrontContract(string root, DateTime utcNow)
    {
        var day = utcNow.Date;
        for (int i = 0; i < 8; i++)
        {
            int quarter = (utcNow.Month - 1) / 3 + i;
            int year = utcNow.Year + quarter / 4;
            int month = quarter % 4 * 3 + 3;
            if (day < RollDate(year, month))
                return root + MonthCode(month) + (year % 100).ToString("00");
        }
        return root + MonthCode(12) + (utcNow.Year % 100).ToString("00");
    }

    public static DateTime RollDate(int year, int month)
    {
        var first = new DateTime(year, month, 1);
        int offset = ((int)DayOfWeek.Wednesday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 14).AddDays(-8);
    }

    private static char MonthCode(int month) => month switch
    {
        3 => 'H',
        6 => 'M',
        9 => 'U',
        _ => 'Z',
    };
}
