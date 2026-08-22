namespace FXViewer;

public sealed class DepthProbe
{
    private sealed class Book
    {
        public readonly Dictionary<ulong, Quote> Quotes = new();
        public int Events;
        public int MaxBids;
        public int MaxAsks;
        public long FirstUnix;
        public long LastReportUnix;
    }

    private readonly record struct Quote(long Price, ulong Size, bool IsBid);

    private const int DetailEvents = 3;
    private const int SummarySeconds = 60;
    private const int ShowLevels = 6;

    private readonly Dictionary<long, Book> _books = new();
    private readonly Func<long, string?> _symbolName;
    private readonly Action<string> _log;

    public DepthProbe(Func<long, string?> symbolName, Action<string> log)
    {
        _symbolName = symbolName;
        _log = log;
    }

    public void Apply(ProtoOADepthEvent e)
    {
        long symbolId = (long)e.SymbolId;
        if (!_books.TryGetValue(symbolId, out var book))
        {
            book = new Book { FirstUnix = Now() };
            _books[symbolId] = book;
        }
        foreach (var id in e.DeletedQuotes) book.Quotes.Remove(id);
        foreach (var q in e.NewQuotes)
            book.Quotes[q.Id] = new Quote(q.HasBid ? (long)q.Bid : (long)q.Ask, q.Size, q.HasBid);
        book.Events++;

        int bids = 0;
        int asks = 0;
        foreach (var q in book.Quotes.Values)
            if (q.IsBid) bids++; else asks++;
        if (bids > book.MaxBids) book.MaxBids = bids;
        if (asks > book.MaxAsks) book.MaxAsks = asks;

        string name = _symbolName(symbolId) ?? symbolId.ToString();
        if (book.Events <= DetailEvents)
        {
            _log($"depth {name} #{book.Events}: +{e.NewQuotes.Count} -{e.DeletedQuotes.Count}, " +
                $"book {bids} bids / {asks} asks");
            Dump(name, book);
            return;
        }
        long now = Now();
        if (book.LastReportUnix == 0) book.LastReportUnix = book.FirstUnix;
        if (now - book.LastReportUnix < SummarySeconds) return;
        book.LastReportUnix = now;
        double perSecond = book.Events / Math.Max(1.0, now - book.FirstUnix);
        _log($"depth {name}: {book.Events} events in {now - book.FirstUnix}s ({perSecond:0.0}/s), " +
            $"now {bids}/{asks}, peak {book.MaxBids}/{book.MaxAsks} levels");
        Dump(name, book);
    }

    private void Dump(string name, Book book)
    {
        var bids = new List<Quote>();
        var asks = new List<Quote>();
        foreach (var q in book.Quotes.Values)
            if (q.IsBid) bids.Add(q); else asks.Add(q);
        bids.Sort((a, b) => b.Price.CompareTo(a.Price));
        asks.Sort((a, b) => a.Price.CompareTo(b.Price));
        for (int i = Math.Min(ShowLevels, asks.Count) - 1; i >= 0; i--)
            _log($"    {name} ask {asks[i].Price / 100000.0:0.00000}  {asks[i].Size / 100.0:N0}");
        for (int i = 0; i < Math.Min(ShowLevels, bids.Count); i++)
            _log($"    {name} bid {bids[i].Price / 100000.0:0.00000}  {bids[i].Size / 100.0:N0}");
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
