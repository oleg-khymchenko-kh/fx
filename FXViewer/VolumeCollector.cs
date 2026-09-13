using System.Collections.Concurrent;
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
    private const int BackfillHours = 48;
    private const int WaitingLogSeconds = 300;
    private const int ProfilePipPoints = 10;
    private const int RollCheckSeconds = 300;
    private const int RecentHours = 24;
    private const int WideRecentHours = 96;
    private const int FrozenLagSeconds = 3600;
    private const int StaleLogSeconds = 3600;
    private const int DeadAfterHours = 96;
    private const int DeadLogSeconds = 6 * 3600;
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
        public long LastRollCheck;
        public long LastStaleLog;
        public long LastDeadLog;
        public long NewestSeen;
        public long LastWritten;
        public long Floor;
        public int LastNoCandle = -1;
        public long LastNoCandleLog;
        public long ProfileFloor = -1;
        public readonly Dictionary<long, long> Pending = new();
        public readonly Dictionary<long, int> Written = new();
        public readonly Dictionary<long, TickMinute> PendingTicks = new();
        public readonly ShiftEstimator Shift = new();
    }

    private readonly Dictionary<string, PairState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _chosen = new(StringComparer.OrdinalIgnoreCase);
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

    public string? ContractFor(string pair) =>
        _chosen.TryGetValue(pair, out var contract) ? contract : null;

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
        if (_firstPass) _log($"volume: reading Sierra data from {_folder}");

        foreach (var (pair, root) in Futures)
        {
            if (!_state.TryGetValue(pair, out var st))
            {
                st = new PairState();
                _state[pair] = st;
            }
            if (!ChooseContract(pair, root, st, utcNow, nowUnix)) continue;
            var contract = st.Contract;
            var path = ContractPath(contract);
            if (!File.Exists(path))
            {
                if (!st.Missing) _log($"volume {pair}: no file {Path.GetFileName(path)}, skipped");
                st.Missing = true;
                continue;
            }
            st.Missing = false;

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
            if (res.NewestMinute > st.NewestSeen) st.NewestSeen = res.NewestMinute;
            WarnIfDead(pair, st, nowUnix);
            if (st.Pending.Count == 0) continue;

            long newest = 0;
            foreach (var minute in st.Pending.Keys)
                if (minute > newest) newest = minute;
            long horizon = Math.Max(nowUnix, newest) - BackfillHours * 3600L;

            int written = 0;
            int noCandle = 0;
            bool aborted = false;
            foreach (var (minute, volume) in st.Pending)
            {
                if (minute < horizon || minute <= st.Floor) continue;
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
                if (minute > st.LastWritten) st.LastWritten = minute;
                writes.Add(new Write(pair, minute, value));
                written++;
            }
            if (written > 0) db.FlushSymbol(pair);

            if (ticksInto != null && !aborted && st.PendingTicks.Count > 0 && _canWrite())
            {
                var writer = db.ProfileWriter(pair, ProfilePipPoints, _log);
                if (st.ProfileFloor < 0) st.ProfileFloor = writer.LastStoredMinute();
                long skipBelow = Math.Max(st.ProfileFloor, horizon - 60);
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
                var expired = st.PendingTicks.Keys.Where(m => m < horizon).ToList();
                foreach (var m in expired) st.PendingTicks.Remove(m);
            }

            if (!aborted)
            {
                long settled = newest - SettleMinutes * 60L;
                var stale = st.Pending.Keys
                    .Where(m => m < settled && (st.Written.ContainsKey(m) || m < horizon || m <= st.Floor))
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

    private bool ChooseContract(string pair, string root, PairState st, DateTime utcNow, long nowUnix)
    {
        var (previous, front) = QuarterContracts(root, utcNow);
        if (st.Contract.Length > 0 && nowUnix - st.LastRollCheck < RollCheckSeconds) return true;
        st.LastRollCheck = nowUnix;

        var candidates = new List<string> { front, previous };
        if (st.Contract.Length > 0 && !candidates.Any(c => Same(c, st.Contract)))
            candidates.Add(st.Contract);
        long since = nowUnix - RecentHours * 3600L;
        long wideSince = nowUnix - WideRecentHours * 3600L;
        var recent = new Dictionary<string, ScidReader.RecentResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            var r = ScidReader.ReadRecent(ContractPath(c), since, wideSince);
            if (r.Exists && !r.Ok) _log($"volume {pair}: {c}-CME.scid: {r.Error}");
            recent[c] = r;
        }
        var best = PickContract(candidates, recent, st.Contract, front);
        if (best == null)
        {
            if (!st.Missing)
                _log($"volume {pair}: no file {front}-CME.scid or {previous}-CME.scid, skipped");
            st.Missing = true;
            return false;
        }
        long freshest = Freshest(candidates, recent);

        if (!Same(best, st.Contract))
        {
            string detail = Describe(best, recent[best], freshest);
            if (!Same(best, front))
                detail += ", calendar " + Describe(front, recent[front], freshest)
                    + Advice(front, recent[front], freshest);
            if (st.Contract.Length > 0)
                _log($"volume {pair}: contract {st.Contract} -> {best} ({detail})");
            else
                _log($"volume {pair}: front contract {best} ({detail})");
            SwitchTo(st, best);
            st.LastStaleLog = nowUnix;
        }
        else if (!Same(best, front) && recent[best].WideVolume > 0
            && nowUnix - st.LastStaleLog >= StaleLogSeconds)
        {
            st.LastStaleLog = nowUnix;
            _log($"volume {pair}: staying on {Describe(best, recent[best], freshest)}, calendar " +
                Describe(front, recent[front], freshest) + Advice(front, recent[front], freshest));
        }
        _chosen[pair] = st.Contract;
        return true;
    }

    public static string? PickContract(IReadOnlyList<string> candidates,
        IReadOnlyDictionary<string, ScidReader.RecentResult> recent, string current, string front)
    {
        var existing = candidates.Where(c => recent[c].Exists).ToList();
        if (existing.Count == 0) return null;
        long freshest = Freshest(existing, recent);
        return existing
            .Where(c => !Frozen(recent[c], freshest))
            .OrderByDescending(c => recent[c].Volume)
            .ThenByDescending(c => recent[c].WideVolume)
            .ThenByDescending(c => recent[c].NewestUnix)
            .ThenBy(c => Same(c, current) ? 0 : Same(c, front) ? 1 : 2)
            .First();
    }

    private static long Freshest(IEnumerable<string> contracts,
        IReadOnlyDictionary<string, ScidReader.RecentResult> recent)
    {
        long freshest = 0;
        foreach (var c in contracts)
            if (recent[c].Exists && recent[c].NewestUnix > freshest) freshest = recent[c].NewestUnix;
        return freshest;
    }

    private static bool Frozen(ScidReader.RecentResult r, long freshest) =>
        r.Exists && r.NewestUnix + FrozenLagSeconds < freshest;

    private static void SwitchTo(PairState st, string contract)
    {
        st.Contract = contract;
        st.Offset = 0;
        st.Floor = st.LastWritten;
        st.NewestSeen = 0;
        st.ProfileFloor = -1;
        st.Pending.Clear();
        st.Written.Clear();
        st.PendingTicks.Clear();
        st.Shift.Reset();
    }

    private void WarnIfDead(string pair, PairState st, long nowUnix)
    {
        if (st.NewestSeen <= 0 || nowUnix - st.NewestSeen < DeadAfterHours * 3600L) return;
        if (nowUnix - st.LastDeadLog < DeadLogSeconds) return;
        st.LastDeadLog = nowUnix;
        _log($"volume {pair}: no trades in {st.Contract}-CME.scid since " +
            $"{Utc(st.NewestSeen):yyyy-MM-dd HH:mm} UTC, is a chart for it open in Sierra?");
    }

    private static string Describe(string contract, ScidReader.RecentResult r, long freshest)
    {
        if (!r.Exists) return $"{contract} missing";
        if (r.NewestUnix <= 0) return $"{contract} empty";
        if (Frozen(r, freshest) || (r.Volume == 0 && r.WideVolume == 0))
            return $"{contract} no trades since {Utc(r.NewestUnix):yyyy-MM-dd HH:mm} UTC";
        if (r.Volume > 0) return $"{contract} {r.Volume:N0} in 24h";
        return $"{contract} {r.WideVolume:N0} in 96h";
    }

    private static string Advice(string front, ScidReader.RecentResult frontRecent, long freshest) =>
        !frontRecent.Exists || Frozen(frontRecent, freshest)
            ? $", open a chart for {front}-CME in Sierra"
            : "";

    private string ContractPath(string contract) => Path.Combine(_folder, contract + "-CME.scid");

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static DateTime Utc(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

    public static string FrontContract(string root, DateTime utcNow) => QuarterContracts(root, utcNow).Front;

    public static (string Previous, string Front) QuarterContracts(string root, DateTime utcNow)
    {
        var day = utcNow.Date;
        int quarter = utcNow.Year * 4 + (utcNow.Month - 1) / 3;
        for (int i = 0; i < 8; i++, quarter++)
        {
            if (day < RollDate(quarter / 4, quarter % 4 * 3 + 3))
                break;
        }
        return (ContractCode(root, quarter - 1), ContractCode(root, quarter));
    }

    public static DateTime RollDate(int year, int month)
    {
        var first = new DateTime(year, month, 1);
        int offset = ((int)DayOfWeek.Wednesday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 14).AddDays(-8);
    }

    private static string ContractCode(string root, int quarter) =>
        root + MonthCode(quarter % 4 * 3 + 3) + (quarter / 4 % 100).ToString("00");

    private static char MonthCode(int month) => month switch
    {
        3 => 'H',
        6 => 'M',
        9 => 'U',
        _ => 'Z',
    };
}
