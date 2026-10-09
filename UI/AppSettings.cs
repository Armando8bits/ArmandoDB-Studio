using System.IO;
using System.Text.Json;

namespace MySmdb;

public class AppSettings
{
    private static readonly string FilePath = Path.Combine(App.DataFolder, "settings.json");

    public string EditorFont { get; set; } = "Consolas";
    public double EditorFontSize { get; set; } = 13;
    public double GridFontSize { get; set; } = 12;

    /// <summary>Vista dividida: true = izquierda/derecha; false = arriba/abajo.</summary>
    public bool SplitSideBySide { get; set; } = true;

    public bool ShowExplorer { get; set; } = true;

    /// <summary>"System" (según Windows), "Light" u "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Abreviatura + Tab expande un fragmento de código.</summary>
    public bool SnippetsEnabled { get; set; } = true;

    /// <summary>Mostrar sugerencias al escribir (Ctrl+Espacio funciona siempre).</summary>
    public bool AutoCompleteEnabled { get; set; } = true;

    /// <summary>Confirmar UPDATE/DELETE sin WHERE, DROP y TRUNCATE en cualquier conexión.</summary>
    public bool ConfirmDangerous { get; set; } = true;

    /// <summary>En conexiones de producción, confirmar cualquier sentencia que modifique datos o estructura.</summary>
    public bool ConfirmProductionWrites { get; set; } = true;

    /// <summary>Plan de ejecución como diagrama (true) o como texto (false).</summary>
    public bool PlanAsDiagram { get; set; } = true;

    /// <summary>Guardar cada consulta ejecutada en el historial (su texto queda en disco, en la carpeta de datos).</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>
    /// Cuánto se conserva el historial: días (30, 90...); 0 = solo durante esta sesión (se borra al cerrar);
    /// -1 = sin límite de tiempo (solo el tope de entradas).
    /// </summary>
    public int HistoryRetentionDays { get; set; } = 90;

    /// <summary>Tope de la lista "Abrir reciente".</summary>
    public const int MaxRecentFiles = 15;

    /// <summary>Últimos archivos abiertos o guardados, el más reciente primero.</summary>
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>Pone el archivo al principio de los recientes (sin repetirlo) y recorta la lista.</summary>
    public void AddRecentFile(string path)
    {
        RemoveRecentFile(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    public void RemoveRecentFile(string path) =>
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
