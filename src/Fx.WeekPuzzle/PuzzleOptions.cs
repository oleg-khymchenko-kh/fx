using System.Globalization;

namespace Fx.WeekPuzzle;

internal sealed record PuzzleOptions(string DataRoot, string Symbol, DateTime FirstMonday, int Weeks, string Drawing)
{
    public const string DefaultDataRoot = @"C:\Users\Oleg\Documents\Oleg\fx\FXViewer\bin\Debug\net10.0-windows\data";
    public const string DefaultDrawing = "GBP-Drawing-3";
    public const string ZigZagLine = "zigzag";

    public static PuzzleOptions Parse(string[] args) => new(
        args.Length > 0 ? args[0] : DefaultDataRoot,
        args.Length > 1 ? args[1] : "GBPUSD",
        args.Length > 2 ? ParseDate(args[2]) : new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
        args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 0,
        args.Length > 4 ? (args[4].Equals(ZigZagLine, StringComparison.OrdinalIgnoreCase) ? "" : args[4]) : DefaultDrawing);

    public string LineName => Drawing.Length > 0 ? Drawing : "ZigZag";

    public static DateTime ParseDate(string text) =>
        DateTime.SpecifyKind(DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);
}
