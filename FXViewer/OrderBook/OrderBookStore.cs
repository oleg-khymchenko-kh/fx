using System.Globalization;
using System.IO;

namespace FXViewer.OrderBook;

public static class OrderBookStore
{
    public const int Version = 2;

    private const int HeaderBytes = 8;
    private const int RowBytes = OrderBookSnapshot.Rows * 4;
    private const int FixedBytesV1 = 60;
    private const int FixedBytesV2 = 64;
    private const int RecordBytesV1 = FixedBytesV1 + RowBytes;
    private const int RecordBytesV2 = FixedBytesV2 + RowBytes;

    private static readonly byte[] Magic = { (byte)'F', (byte)'X', (byte)'O', (byte)'B' };

    public static string Directory(string symbolDirectory) =>
        Path.Combine(symbolDirectory, "orderbook");

    public static string RawDirectory(string symbolDirectory) =>
        Path.Combine(Directory(symbolDirectory), "raw");

    public static string YearPath(string symbolDirectory, int year) =>
        Path.Combine(Directory(symbolDirectory), year.ToString(CultureInfo.InvariantCulture) + ".obk");

    public static string RawPath(string symbolDirectory, long imageTimeUnix) =>
        Path.Combine(RawDirectory(symbolDirectory),
            imageTimeUnix.ToString(CultureInfo.InvariantCulture) + ".png");

    private static int RecordBytes(int version) => version == 1 ? RecordBytesV1 : RecordBytesV2;

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private static int FileVersion(string path)
    {
        using var stream = OpenShared(path);
        if (stream.Length < HeaderBytes) return 0;
        var header = new byte[HeaderBytes];
        if (stream.Read(header, 0, HeaderBytes) < HeaderBytes) return 0;
        for (int i = 0; i < Magic.Length; i++)
            if (header[i] != Magic[i]) return 0;
        int version = BitConverter.ToInt32(header, 4);
        return version is 1 or 2 ? version : 0;
    }

    private static void Repair(string path, Action<string>? log)
    {
        long length = new FileInfo(path).Length;
        if (length < HeaderBytes)
        {
            if (length > 0)
            {
                File.Delete(path);
                log?.Invoke($"order book: {Path.GetFileName(path)} was shorter than its header, removed");
            }
            return;
        }
        int version = FileVersion(path);
        if (version == 0)
        {
            var bad = path + ".bad";
            if (File.Exists(bad)) File.Delete(bad);
            File.Move(path, bad);
            log?.Invoke($"order book: {Path.GetFileName(path)} has a broken header, " +
                $"moved aside to {Path.GetFileName(bad)}");
            return;
        }
        int recordBytes = RecordBytes(version);
        long expected = HeaderBytes + (length - HeaderBytes) / recordBytes * recordBytes;
        if (expected == length) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.SetLength(expected);
        log?.Invoke($"order book: {Path.GetFileName(path)} had a torn tail, " +
            $"truncated {length - expected} stray bytes");
    }

    public static long LastTimeUnix(string symbolDirectory)
    {
        var dir = Directory(symbolDirectory);
        if (!System.IO.Directory.Exists(dir)) return 0;
        long best = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "*.obk"))
        {
            int version = FileVersion(path);
            if (version == 0) continue;
            int recordBytes = RecordBytes(version);
            using var stream = OpenShared(path);
            long count = (stream.Length - HeaderBytes) / recordBytes;
            if (count <= 0) continue;
            stream.Seek(HeaderBytes + (count - 1) * recordBytes, SeekOrigin.Begin);
            using var reader = new BinaryReader(stream);
            long time = reader.ReadInt64();
            if (time > best) best = time;
        }
        return best;
    }

    public static void Append(string symbolDirectory, OrderBookSnapshot snapshot, Action<string>? log = null)
    {
        int year = DateTimeOffset.FromUnixTimeSeconds(snapshot.TimeUnix).UtcDateTime.Year;
        var path = YearPath(symbolDirectory, year);
        System.IO.Directory.CreateDirectory(Directory(symbolDirectory));
        if (!File.Exists(path) && File.Exists(path + ".v1"))
            RestoreBackup(path + ".v1", log);
        if (File.Exists(path))
        {
            Repair(path, log);
            if (File.Exists(path) && FileVersion(path) == 1)
                Upgrade(symbolDirectory, path, log);
        }
        bool fresh = !File.Exists(path) || new FileInfo(path).Length < HeaderBytes;
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        using var writer = new BinaryWriter(stream);
        if (fresh)
        {
            stream.SetLength(0);
            writer.Write(Magic);
            writer.Write(Version);
        }
        else
        {
            stream.Seek(0, SeekOrigin.End);
        }
        WriteRecord(writer, snapshot);
    }

    private static void WriteRecord(BinaryWriter writer, OrderBookSnapshot snapshot)
    {
        writer.Write(snapshot.TimeUnix);
        writer.Write(snapshot.ImageTimeUnix);
        writer.Write(snapshot.PricePoints);
        writer.Write(snapshot.MiddlePoints);
        writer.Write(snapshot.StepPoints);
        writer.Write(snapshot.AnchorPoints != 0 ? snapshot.AnchorPoints : snapshot.MiddlePoints);
        writer.Write(snapshot.MvoPoints);
        writer.Write(snapshot.MvpPoints);
        writer.Write(snapshot.SrPoints);
        writer.Write(snapshot.GrPoints);
        writer.Write(snapshot.ObPoints);
        writer.Write(snapshot.OsPoints);
        writer.Write(snapshot.OrdersCount);
        writer.Write(snapshot.PositionsCount);
        writer.Write(snapshot.PendingLeft);
        writer.Write(snapshot.PendingRight);
        writer.Write(snapshot.PositionsLeft);
        writer.Write(snapshot.PositionsRight);
    }

    public static void SaveRaw(string symbolDirectory, long imageTimeUnix, byte[] png)
    {
        var dir = RawDirectory(symbolDirectory);
        System.IO.Directory.CreateDirectory(dir);
        var path = RawPath(symbolDirectory, imageTimeUnix);
        if (File.Exists(path)) return;
        File.WriteAllBytes(path, png);
    }

    public static void UpgradeAll(string symbolDirectory, Action<string>? log)
    {
        var dir = Directory(symbolDirectory);
        if (!System.IO.Directory.Exists(dir)) return;
        foreach (var tmp in System.IO.Directory.GetFiles(dir, "*.obk.tmp"))
            Guarded(tmp, log, () => File.Delete(tmp));
        foreach (var path in System.IO.Directory.GetFiles(dir, "*.obk"))
            Guarded(path, log, () => Repair(path, log));
        foreach (var backup in System.IO.Directory.GetFiles(dir, "*.obk.v1"))
            Guarded(backup, log, () => RestoreBackup(backup, log));
        foreach (var path in System.IO.Directory.GetFiles(dir, "*.obk"))
            Guarded(path, log, () =>
            {
                Repair(path, log);
                if (File.Exists(path) && FileVersion(path) == 1)
                    Upgrade(symbolDirectory, path, log);
            });
    }

    private static void RestoreBackup(string backup, Action<string>? log)
    {
        var main = backup[..^3];
        if (File.Exists(main)) return;
        File.Move(backup, main);
        log?.Invoke($"order book: {Path.GetFileName(main)} was missing, " +
            "restored from its backup");
    }

    private static void Guarded(string path, Action<string>? log, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log?.Invoke($"order book: {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static void Upgrade(string symbolDirectory, string path, Action<string>? log)
    {
        var records = new List<OrderBookSnapshot>();
        ReadFile(path, records);
        int calibrated = 0;
        foreach (var record in records)
        {
            var rawPath = RawPath(symbolDirectory, record.ImageTimeUnix);
            if (!File.Exists(rawPath)) continue;
            try
            {
                var panels = OrderBookDecoder.Decode(
                    File.ReadAllBytes(rawPath), record.MiddlePoints, record.StepPoints);
                if (!panels.Calibrated) continue;
                record.AnchorPoints = panels.AnchorPoints;
                calibrated++;
            }
            catch (Exception)
            {
            }
        }

        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(Version);
            foreach (var record in records)
                WriteRecord(writer, record);
        }

        File.Replace(tmp, path, path + ".v1");
        log?.Invoke($"order book: {Path.GetFileName(path)} upgraded to v{Version}, " +
            $"{records.Count} records, {calibrated} calibrated from raw images");
    }

    public static List<OrderBookSnapshot> ReadAll(string symbolDirectory)
    {
        var result = new List<OrderBookSnapshot>();
        var dir = Directory(symbolDirectory);
        if (!System.IO.Directory.Exists(dir)) return result;
        var files = System.IO.Directory.GetFiles(dir, "*.obk");
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var path in files) ReadFile(path, result);
        return result;
    }

    public static List<OrderBookSnapshot> ReadYear(string symbolDirectory, int year)
    {
        var result = new List<OrderBookSnapshot>();
        ReadFile(YearPath(symbolDirectory, year), result);
        return result;
    }

    private static void ReadFile(string path, List<OrderBookSnapshot> result)
    {
        if (!File.Exists(path)) return;
        using var stream = OpenShared(path);
        using var reader = new BinaryReader(stream);
        var magic = reader.ReadBytes(Magic.Length);
        if (magic.Length < Magic.Length) return;
        for (int i = 0; i < Magic.Length; i++)
            if (magic[i] != Magic[i]) return;
        int version = reader.ReadInt32();
        if (version is not (1 or 2)) return;
        long count = (stream.Length - HeaderBytes) / RecordBytes(version);
        for (long i = 0; i < count; i++)
        {
            long time = reader.ReadInt64();
            long imageTime = reader.ReadInt64();
            int price = reader.ReadInt32();
            int middle = reader.ReadInt32();
            int step = reader.ReadInt32();
            int anchor = version >= 2 ? reader.ReadInt32() : 0;
            result.Add(new OrderBookSnapshot
            {
                TimeUnix = time,
                ImageTimeUnix = imageTime,
                PricePoints = price,
                MiddlePoints = middle,
                StepPoints = step,
                AnchorPoints = anchor != 0 ? anchor : middle,
                MvoPoints = reader.ReadInt32(),
                MvpPoints = reader.ReadInt32(),
                SrPoints = reader.ReadInt32(),
                GrPoints = reader.ReadInt32(),
                ObPoints = reader.ReadInt32(),
                OsPoints = reader.ReadInt32(),
                OrdersCount = reader.ReadInt32(),
                PositionsCount = reader.ReadInt32(),
                PendingLeft = reader.ReadBytes(OrderBookSnapshot.Rows),
                PendingRight = reader.ReadBytes(OrderBookSnapshot.Rows),
                PositionsLeft = reader.ReadBytes(OrderBookSnapshot.Rows),
                PositionsRight = reader.ReadBytes(OrderBookSnapshot.Rows),
            });
        }
    }
}
