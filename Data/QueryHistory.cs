using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MySmdb;

/// <summary>Una ejecución guardada en el historial.</summary>
public sealed record HistoryEntry(DateTime When, string Connection, string? Database, string Sql, double Seconds, int? Rows, string Outcome, string? Error)
{
    public const string Ok = "Correcta", Failed = "Con errores", Cancelled = "Cancelada";

    // Lo que muestra la ventana del historial.
    [JsonIgnore] public string WhenText => When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    [JsonIgnore] public string SecondsText => Seconds.ToString("0.000", CultureInfo.InvariantCulture);
    [JsonIgnore] public string RowsText => Rows?.ToString("N0") ?? "";
    /// <summary>Comienzo de la consulta en una sola línea.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            string line = string.Join(" ", Sql.Split((char[]?)null, 40, StringSplitOptions.RemoveEmptyEntries).Take(39));
            return line.Length > 200 ? line[..200] + "..." : line;
        }
    }
}

/// <summary>
/// Historial de consultas ejecutadas: un archivo de texto en la carpeta de datos, con una línea JSON por
/// ejecución (añadir es barato y un cierre brusco no estropea lo anterior). Guarda el texto de las consultas
/// tal cual, así que se puede desactivar (AppSettings.HistoryEnabled) y borrar.
/// </summary>
public static class QueryHistory
{
    /// <summary>Entradas que se conservan; las más antiguas se descartan.</summary>
    public const int MaxEntries = 5000;
    /// <summary>Tope de texto por entrada: un script enorme no debe inflar el historial.</summary>
    public const int MaxSqlLength = 20_000;
    private const string Cut = "\n-- (...) el resto no se guardó en el historial";

    private static readonly object Lock = new();
    private static int? _count;

    /// <summary>Archivo del historial. (Las pruebas lo cambian.)</summary>
    public static string FilePath { get; set; } = Path.Combine(App.DataFolder, "historial.jsonl");

    /// <summary>Añade una ejecución. Nunca interrumpe el trabajo: si no se puede guardar, no se guarda.</summary>
    public static void Add(HistoryEntry entry)
    {
        if (!AppSettings.Current.HistoryEnabled || string.IsNullOrWhiteSpace(entry.Sql)) return;
        try
        {
            if (entry.Sql.Length > MaxSqlLength) entry = entry with { Sql = entry.Sql[..MaxSqlLength] + Cut };
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(entry) + "\n", new UTF8Encoding(false));
                _count = (_count ?? CountLines()) + 1;
                // Se recorta de tarde en tarde, no en cada consulta.
                if (_count > MaxEntries + MaxEntries / 10) Write(Read());
            }
        }
        catch
        {
        }
    }

    /// <summary>Todo el historial, lo más reciente primero.</summary>
    public static List<HistoryEntry> Load()
    {
        try
        {
            lock (Lock)
            {
                var entries = Read();
                entries.Reverse();
                return entries;
            }
        }
        catch
        {
            return new List<HistoryEntry>();
        }
    }

    public static void Clear()
    {
        try
        {
            lock (Lock)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                _count = 0;
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Aplica el tiempo de conservación elegido (AppSettings.HistoryRetentionDays) a lo que hay en disco: quita
    /// lo caducado; y con "solo esta sesión" lo borra todo. Se llama al arrancar, al salir y al cambiar la opción.
    /// </summary>
    /// <param name="sessionBoundary">true al arrancar y al salir, que es cuando "solo esta sesión" vacía el historial.</param>
    public static void ApplyRetention(bool sessionBoundary)
    {
        try
        {
            lock (Lock)
            {
                if (AppSettings.Current.HistoryRetentionDays == 0)
                {
                    if (sessionBoundary && File.Exists(FilePath)) File.Delete(FilePath);
                    if (sessionBoundary) _count = 0;
                    return;
                }
                if (!File.Exists(FilePath)) return;
                var kept = Read();
                if (kept.Count != CountLines()) Write(kept);
            }
        }
        catch
        {
        }
    }

    /// <summary>Lo anterior a este momento ya no se conserva; null si no hay límite de tiempo.</summary>
    private static DateTime? Cutoff =>
        AppSettings.Current.HistoryRetentionDays is > 0 and var days ? DateTime.Now.AddDays(-days) : null;

    /// <summary>Al cambiar de archivo (pruebas) hay que volver a contar.</summary>
    public static void Reset()
    {
        lock (Lock) _count = null;
    }

    private static int CountLines() => File.Exists(FilePath) ? File.ReadLines(FilePath).Count() : 0;

    /// <summary>Las últimas entradas válidas y no caducadas, de la más antigua a la más reciente.</summary>
    private static List<HistoryEntry> Read()
    {
        var entries = new List<HistoryEntry>();
        if (!File.Exists(FilePath)) return entries;
        foreach (string line in File.ReadLines(FilePath))
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize<HistoryEntry>(line) is { Sql: not null } entry) entries.Add(entry);
            }
            catch (JsonException)
            {
                // Una línea a medio escribir (cierre brusco) no invalida las demás.
            }
        }
        if (Cutoff is { } cutoff) entries.RemoveAll(e => e.When < cutoff);
        if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
        return entries;
    }

    private static void Write(List<HistoryEntry> entries)
    {
        var text = new StringBuilder();
        foreach (var entry in entries) text.Append(JsonSerializer.Serialize(entry)).Append('\n');
        File.WriteAllText(FilePath, text.ToString(), new UTF8Encoding(false));
        _count = entries.Count;
    }
}
