using System.Globalization;
using FXViewer.Chart;
using FXViewer.Game;
using FXViewer.Storage;

namespace FXViewer;

public partial class MainWindow
{
    private CommentStore? _comments;

    private CommentStore Comments => _comments ??= CommentStore.Load();

    private void InitComments()
    {
        Chart.CommentEditRequested += OpenCommentDialog;
        ChartTools.CommentsClick += () => ChartTools.SetCommentsRow(Chart.ToggleComments());
        Chart.SetCommentStyle(_config.CommentSpotStyle());
        RefreshComments();
    }

    private void RefreshComments()
    {
        Chart.SetComments(Comments.Points
            .Select(p => new CommentMark(p.Id, p.Pair, p.MinuteUnix, CommentRawOfPrice(p.Pair, p.Price)))
            .ToList());
        ChartTools.SetCommentsRow(Chart.CommentsVisible);
    }

    private void OpenCommentDialog(CommentEditRequest request)
    {
        var store = Comments;
        var existing = request.Id == null ? null : store.Points.FirstOrDefault(p => p.Id == request.Id);
        if (request.Id != null && existing == null) return;
        var body = existing == null ? null : store.Body(existing.Id);
        var pairs = request.Levels
            .Select(l => new CommentPairLevel(
                l.Symbol, GamePairColor(l.Symbol), CommentLevelText(l.Symbol, l.Price)))
            .ToList();
        string pair = body?.Pair ?? request.Pair;
        var known = pairs.FirstOrDefault(p => SymbolNameEquals(p.Symbol, pair));
        if (known != null) pair = known.Symbol;
        else pairs.Insert(0, new CommentPairLevel(
            pair, GamePairColor(pair), CommentLevelText(pair, request.Value)));
        string level = body != null
            ? CommentPipsText(CommentPipsOfPrice(pair, body.Price))
            : CommentLevelText(pair, request.Value);
        var dialog = new CommentWindow(body == null, pairs, pair,
            body?.MinuteUnix ?? request.MinuteUnix, level,
            body?.Title ?? "", body?.Description ?? "")
        {
            Owner = this,
        };
        Chart.SetCommentFocus(request.Id ?? "");
        bool ok = dialog.ShowDialog() == true;
        Chart.SetCommentFocus(null);
        if (!ok) return;
        if (dialog.Deleted && body != null)
        {
            CommitComments(() => store.Delete(body.Id));
            AppendLog($"Comment deleted: {body.Pair} {CommentTimeText(body.MinuteUnix)}");
            return;
        }
        var comment = body ?? new ChartComment();
        comment.Pair = dialog.Pair;
        comment.MinuteUnix = dialog.MinuteUnix;
        comment.Price = CommentPriceOfPips(dialog.Pair, dialog.LevelPips);
        comment.Title = dialog.TitleText;
        comment.Description = dialog.Description;
        Chart.ShowComments();
        CommitComments(() => store.Save(comment));
        AppendLog($"Comment {(body == null ? "added" : "saved")}: {comment.Pair} " +
            $"{CommentTimeText(comment.MinuteUnix)} at " +
            $"{CommentPipsText(CommentPipsOfPrice(comment.Pair, comment.Price))} pips" +
            (comment.Title.Length > 0 ? " - " + comment.Title : ""));
    }

    private void CommitComments(Action change)
    {
        try
        {
            change();
        }
        catch (Exception ex)
        {
            AppendLog("Comments save failed: " + ex.Message);
        }
        RefreshComments();
        RefreshGameIfPlaying();
    }

    private List<GamePanelComment> DayComments(string day)
    {
        var store = Comments;
        var rows = new List<GamePanelComment>();
        foreach (var point in store.Points)
        {
            if (point.Day() != day) continue;
            rows.Add(new GamePanelComment(
                DateTimeOffset.FromUnixTimeSeconds(point.MinuteUnix).UtcDateTime
                    .ToString("HH:mm", CultureInfo.InvariantCulture),
                point.Pair, GamePairColor(point.Pair), store.Body(point.Id).Title));
        }
        rows.Sort((a, b) => string.CompareOrdinal(a.Time, b.Time));
        return rows;
    }

    private static string CommentLevelText(string symbol, int rawPoints) =>
        CommentPipsText(rawPoints / (double)SourcePipPoints(symbol));

    private static string CommentPipsText(double pips) =>
        pips.ToString("0.###", CultureInfo.InvariantCulture);

    private static double CommentPipsOfPrice(string symbol, double price) =>
        price * 100000.0 / (SymbolPriceDiv(symbol) * (double)SourcePipPoints(symbol));

    private static double CommentPriceOfPips(string symbol, double pips) =>
        Math.Round(pips * SourcePipPoints(symbol) * SymbolPriceDiv(symbol) / 100000.0, 5);

    private static int CommentRawOfPrice(string symbol, double price) =>
        (int)Math.Round(price * 100000.0 / SymbolPriceDiv(symbol));

    private static string CommentTimeText(long unixSeconds) => DateTimeOffset
        .FromUnixTimeSeconds(unixSeconds).UtcDateTime
        .ToString(CommentWindow.MinuteFormat, CultureInfo.InvariantCulture);
}
