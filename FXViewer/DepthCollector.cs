using System.Globalization;
using System.IO;
using FXViewer.OrderBook;

namespace FXViewer;

public sealed class DepthCollector
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public const string DepthFolderName = "MarketDepthData";
    private const int PriceScale = 100000;
    private const int PipPoints = 10;

    public readonly record struct Write(string Symbol, IReadOnlyList<DepthSnapshot> Snapshots);

    private sealed class PairState
    {
        public string File = "";
        public long Offset;
        public bool Missing;
        public long LastStored = -1;
        public readonly DepthBook Book = new();
        public readonly List<DepthSnapshot> Pending = new();
    }

    private readonly Dictionary<string, PairState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;
    private readonly Func<bool> _canWrite;
    private readonly Func<string, string> _symbolDirectory;
    private readonly string _folder;
    private bool _firstPass = true;

    public string Folder => _folder;

    public DepthCollector(string? sierraDataFolder, Func<string, string> symbolDirectory,
        Func<bool> canWrite, Action<string> log)
    {
        _log = log;
        _canWrite = canWrite;
        _symbolDirectory = symbolDirectory;
        _folder = Path.Combine(VolumeCollector.ResolveFolder(sierraDataFolder), DepthFolderName);
    }

    public IReadOnlyList<Write> RunOnce(DateTime utcNow)
    {
        var writes = new List<Write>();
        if (!Directory.Exists(_folder))
        {
            if (_firstPass) _log($"depth: folder not found: {_folder}");
            _firstPass = false;
            return writes;
        }
        if (_firstPass) _log($"depth: reading Sierra depth from {_folder}");

        foreach (var (pair, root) in VolumeCollector.Futures)
        {
            var contract = VolumeCollector.FrontContract(root, utcNow);
            var name = $"{contract}-CME.{utcNow:yyyy-MM-dd}.depth";
            var path = Path.Combine(_folder, name);
            if (!_state.TryGetValue(pair, out var st))
            {
                st = new PairState();
                _state[pair] = st;
            }
            if (!File.Exists(path))
            {
                if (!st.Missing && _firstPass) _log($"depth {pair}: no file {name}, skipped");
                st.Missing = true;
                continue;
            }
            st.Missing = false;
            if (!string.Equals(st.File, name, StringComparison.OrdinalIgnoreCase))
            {
                if (st.File.Length > 0) _log($"depth {pair}: {st.File} -> {name}");
                else _log($"depth {pair}: reading {name}");
                st.File = name;
                st.Offset = 0;
                st.Book.Reset();
                st.Pending.Clear();
            }
            if (st.LastStored < 0)
                st.LastStored = DepthStore.LastMinute(_symbolDirectory(pair), utcNow.Year);

            var pending = st.Pending;
            var res = DepthReader.ReadTail(path, st.Offset, PriceScale, st.Book,
                (minute, book) =>
                {
                    if (minute <= st.LastStored) return;
                    if (book.TryBuild(minute, PipPoints, out var snapshot)) pending.Add(snapshot);
                });
            if (!res.Ok)
            {
                _log($"depth {pair}: {res.Error}");
                continue;
            }
            if (res.Restarted)
                _log($"depth {pair}: {name} was rewritten, rereading from the start");
            st.Offset = res.Offset;
            if (pending.Count == 0) continue;
            if (!_canWrite()) continue;

            int written = 0;
            foreach (var group in pending.GroupBy(s =>
                DateTimeOffset.FromUnixTimeSeconds(s.MinuteUnix).UtcDateTime.Year))
            {
                var list = group.OrderBy(s => s.MinuteUnix).ToList();
                written += DepthStore.Append(
                    _symbolDirectory(pair), group.Key, PipPoints, list, _log);
            }
            if (written > 0)
            {
                long newest = pending.Max(s => s.MinuteUnix);
                st.LastStored = newest;
                writes.Add(new Write(pair, pending.OrderBy(s => s.MinuteUnix).ToList()));
                _log($"depth {pair}: {written} minute(s) stored up to " +
                    $"{DateTimeOffset.FromUnixTimeSeconds(newest).UtcDateTime:yyyy-MM-dd HH:mm} UTC");
            }
            pending.Clear();
        }

        _firstPass = false;
        return writes;
    }
}
