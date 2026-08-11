using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FXViewer;

public interface INotesHost
{
    IReadOnlyList<Note> Notes { get; }
    string ActiveNoteId { get; }
    string ActiveTabName { get; }
    string NextNoteName();
    void OpenNote(string id);
    string CreateNote(string name);
    void UpdateNote(string id);
    void RenameNote(string id, string name);
    void DeleteNote(string id);
}

public partial class NotesWindow : Window
{
    public sealed record Row(string Id, string Name, string Start, string End);

    private readonly INotesHost _host;
    private bool _applying;

    public NotesWindow(INotesHost host)
    {
        InitializeComponent();
        _host = host;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Activated += (_, _) => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        if (!IsInitialized) return;
        var rows = _host.Notes.Select(ToRow).ToList();
        _applying = true;
        NotesList.ItemsSource = rows;
        NotesList.SelectedItem = rows.FirstOrDefault(r => r.Id == _host.ActiveNoteId);
        _applying = false;
        HintText.Text = rows.Count == 0
            ? $"No notes yet. \"Create from tab\" saves the screen of tab \"{_host.ActiveTabName}\" as a note."
            : $"Click a note to show it in tab \"{_host.ActiveTabName}\".";
        bool hasSelection = NotesList.SelectedItem is Row;
        UpdateBtn.IsEnabled = hasSelection;
        RenameBtn.IsEnabled = hasSelection;
        DeleteBtn.IsEnabled = hasSelection;
    }

    private static Row ToRow(Note note) =>
        new(note.Id, note.Name, TimeText(note.StartUnix), TimeText(note.EndUnix));

    private static string TimeText(long unix) => unix <= 0
        ? ""
        : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private Row? Selected => NotesList.SelectedItem as Row;

    private void NotesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying) return;
        if (Selected is { } row) _host.OpenNote(row.Id);
        Refresh();
    }

    private void NoteRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: Row row } item && item.IsSelected)
        {
            _host.OpenNote(row.Id);
            Refresh();
        }
    }

    private void CreateBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TabNameWindow(_host.NextNoteName(), "Note name") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _host.CreateNote(dlg.TabName);
        Refresh();
    }

    private void UpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (MessageBox.Show(this, $"Overwrite note {row.Name} with tab {_host.ActiveTabName}?",
                "Update note", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _host.UpdateNote(row.Id);
        Refresh();
    }

    private void RenameBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var dlg = new TabNameWindow(row.Name, "Note name") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _host.RenameNote(row.Id, dlg.TabName);
        Refresh();
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (MessageBox.Show(this, $"Delete note {row.Name}?", "Delete note",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _host.DeleteNote(row.Id);
        Refresh();
    }
}
