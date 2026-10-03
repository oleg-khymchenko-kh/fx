using System.Windows;

namespace Fx.WeekPuzzle;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return app.Run(new MainWindow(PuzzleOptions.Parse(args)));
    }
}
