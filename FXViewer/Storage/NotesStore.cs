using System.IO;
using System.Text.Json;

namespace FXViewer.Storage;

public static class NotesStore
{
    private const string FileName = "notes.json";

    private static string FilePath => Path.Combine(AppConfig.Dir, FileName);

    public static List<Note> Load()
    {
        if (!File.Exists(FilePath)) return new List<Note>();
        List<Note>? notes;
        try
        {
            notes = JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(FilePath));
        }
        catch
        {
            try { File.Move(FilePath, FilePath + ".bad", true); } catch { }
            return new List<Note>();
        }
        if (notes == null) return new List<Note>();
        foreach (var note in notes)
        {
            if (note.Id.Length == 0) note.Id = Note.NewId();
            if (note.Name.Trim().Length == 0) note.Name = "Note";
            note.State?.EnsureTiltedGrids();
        }
        return notes;
    }

    public static void Save(IReadOnlyList<Note> notes)
    {
        Directory.CreateDirectory(AppConfig.Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp,
            JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, true);
    }
}
