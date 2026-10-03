using System.IO;
using System.Text.Json;

namespace MySmdb;

public class SessionTab
{
    public string Path { get; set; } = "";
    public bool Pinned { get; set; }
    public string? Database { get; set; }

    /// <summary>Grupo de la vista dividida: 0 el primero, 1 el segundo.</summary>
    public int Group { get; set; }
}

/// <summary>
/// Archivos abiertos de cada sesión. Una sesión es una conexión, identificada por su nombre;
/// al volver a conectarse se reabren los archivos que tenía.
/// </summary>
public static class SessionStore
{
    private static readonly string FilePath = System.IO.Path.Combine(App.DataFolder, "sessions.json");

    private static Dictionary<string, List<SessionTab>> LoadAll()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, List<SessionTab>>>(File.ReadAllText(FilePath))
                    ?? new Dictionary<string, List<SessionTab>>();
        }
        catch
        {
        }
        return new Dictionary<string, List<SessionTab>>();
    }

    public static List<SessionTab> Load(string session) =>
        LoadAll().TryGetValue(session, out var tabs) ? tabs : new List<SessionTab>();

    public static void Save(Dictionary<string, List<SessionTab>> sessions)
    {
        var all = LoadAll();
        foreach (var (name, tabs) in sessions)
            all[name] = tabs;
        WriteAll(all);
    }

    /// <summary>Al renombrar una conexión, su sesión la acompaña.</summary>
    public static void Rename(string oldName, string newName)
    {
        if (oldName == newName) return;
        var all = LoadAll();
        if (!all.Remove(oldName, out var tabs)) return;
        all[newName] = tabs;
        WriteAll(all);
    }

    private static void WriteAll(Dictionary<string, List<SessionTab>> all)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // No poder guardar la sesión no debe interrumpir el trabajo.
        }
    }
}
