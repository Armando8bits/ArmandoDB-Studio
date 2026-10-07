using System.Diagnostics;
using System.IO;
using System.Text;

namespace MySmdb;

/// <summary>Una consulta abierta con cambios sin guardar.</summary>
public sealed record UnsavedQuery(string Title, string Connection, string? FilePath, string Text);

/// <summary>
/// Copia de recuperación de las consultas con cambios sin guardar. Se escribe cada pocos segundos mientras se
/// trabaja; al salir con normalidad se borra. Si la aplicación (o el equipo) se cae, la copia queda en disco y
/// al volver a abrirla se avisa de dónde están.
/// </summary>
public static class RecoveryStore
{
    private static readonly string Root = Path.Combine(App.DataFolder, "recuperacion");
    private static readonly string Session = $"{Environment.ProcessId}";
    private static Func<IReadOnlyList<UnsavedQuery>>? _provider;
    private static string _lastWritten = "";

    /// <summary>Carpeta de la copia de esta ejecución.</summary>
    public static string Folder => Path.Combine(Root, Session);

    /// <summary>Carpeta a la que pasan las copias de ejecuciones anteriores que no terminaron bien.</summary>
    public static string RecoveredFolder => Path.Combine(Root, "recuperadas");

    /// <param name="provider">Devuelve las consultas sin guardar. Se llama siempre en el hilo de la interfaz.</param>
    public static void Start(Func<IReadOnlyList<UnsavedQuery>> provider) => _provider = provider;

    /// <summary>Consultas sin guardar ahora mismo, leídas en el hilo de la interfaz desde cualquier hilo.</summary>
    private static IReadOnlyList<UnsavedQuery> Snapshot()
    {
        if (_provider == null) return Array.Empty<UnsavedQuery>();
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) return _provider();
        // Si la interfaz está bloqueada, no se espera para siempre.
        return dispatcher.Invoke(_provider, System.Windows.Threading.DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(3));
    }

    /// <summary>Guarda ya la copia de recuperación. Devuelve cuántas consultas se guardaron.</summary>
    public static int SaveNow()
    {
        var queries = Snapshot();
        Write(queries, force: true);
        return queries.Count;
    }

    /// <summary>Guardado periódico: solo escribe si algo cambió desde la última vez.</summary>
    public static void SaveIfChanged()
    {
        try
        {
            Write(Snapshot(), force: false);
        }
        catch
        {
            // La copia de recuperación nunca debe interrumpir el trabajo.
        }
    }

    private static void Write(IReadOnlyList<UnsavedQuery> queries, bool force)
    {
        string signature = string.Join("\u0001", queries.Select(q => $"{q.Title}\u0002{q.Connection}\u0002{q.FilePath}\u0002{q.Text}"));
        if (!force && signature == _lastWritten) return;

        // Primero se escriben las copias nuevas y solo después se quitan las que sobran: así, si el equipo se
        // apaga a mitad, nunca hay un momento sin copia.
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (queries.Count > 0) Directory.CreateDirectory(Folder);
        for (int i = 0; i < queries.Count; i++)
        {
            var query = queries[i];
            string name = string.Concat(query.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            if (name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length == 0) name = "consulta";
            if (name.Length > 60) name = name[..60];
            // La cabecera dice de dónde salió, para saber con qué conexión se estaba trabajando.
            string header = $"-- Copia de recuperación de {App.Name} ({DateTime.Now:yyyy-MM-dd HH:mm:ss})\n" +
                            $"-- Conexión: {query.Connection}\n" +
                            $"-- Origen: {query.FilePath ?? "consulta sin guardar"}\n\n";
            string file = Path.Combine(Folder, $"{i + 1:00} {name}.sql");
            File.WriteAllText(file, (header + query.Text).ReplaceLineEndings(), new UTF8Encoding(false));
            written.Add(file);
        }
        if (Directory.Exists(Folder))
            foreach (string old in Directory.GetFiles(Folder).Where(f => !written.Contains(f))) File.Delete(old);
        _lastWritten = signature;
    }

    /// <summary>Salida normal: no hay nada que recuperar.</summary>
    public static void Clear()
    {
        try
        {
            _provider = null;
            _lastWritten = "";   // ya no hay nada escrito con lo que comparar
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Al arrancar: copias que dejó una ejecución anterior que no se cerró bien. Se pasan a la carpeta de
    /// recuperadas (para no avisar otra vez) y se devuelve esa carpeta y cuántas consultas hay; null si no hay ninguna.
    /// </summary>
    public static (string Folder, int Count)? CollectFromCrashes()
    {
        try
        {
            if (!Directory.Exists(Root)) return null;
            int count = 0;
            string target = "";
            foreach (string folder in Directory.GetDirectories(Root))
            {
                string name = Path.GetFileName(folder);
                // Otra copia de la aplicación que sigue abierta conserva la suya.
                if (!int.TryParse(name, out int pid) || pid == Environment.ProcessId || IsRunning(pid)) continue;
                var files = Directory.GetFiles(folder, "*.sql");
                if (files.Length > 0)
                {
                    if (target.Length == 0)
                    {
                        target = Path.Combine(RecoveredFolder, DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"));
                        Directory.CreateDirectory(target);
                    }
                    foreach (string file in files)
                    {
                        count++;
                        File.Move(file, Path.Combine(target, $"{count:00} {Path.GetFileName(file)[3..]}"), overwrite: true);
                    }
                }
                Directory.Delete(folder, recursive: true);
            }
            return count > 0 ? (target, count) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
