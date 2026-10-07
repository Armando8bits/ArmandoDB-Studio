using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;

namespace MySmdb;

/// <summary>
/// Tratamiento único de errores de toda la aplicación, sea cual sea el motor de base de datos o la ventana:
/// ningún error cierra la aplicación sin más. Todos se anotan en errors.log y se muestran en la misma ventana,
/// con su detalle y un botón para copiarlo y poder informar del fallo.
/// </summary>
public static class Errors
{
    private const long MaxLogBytes = 1_000_000;
    private static readonly object LogLock = new();
    private static ErrorDialog? _open;

    public static string LogPath => Path.Combine(App.DataFolder, "errors.log");

    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    /// <summary>Engancha los tres sitios por donde puede escaparse un error que nadie capturó.</summary>
    public static void Install(Application app)
    {
        // Hilo de la interfaz (botones, menús, temporizadores, manejadores async void): se muestra y la aplicación sigue.
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            var error = Unwrap(e.Exception);
            Log("Error inesperado", error);
            // La ventana se abre después, ya fuera del tratamiento de la excepción: abrirla aquí dentro bloquea
            // el aviso de los errores que lleguen mientras tanto.
            app.Dispatcher.BeginInvoke(() =>
            {
                Present(null, "Error inesperado", Message(error), Report("Error inesperado", error), unexpected: true);
                // Si el fallo fue al crear la ventana principal, no queda nada que usar: se sale en vez de dejar un proceso invisible.
                if (app.MainWindow == null && !app.Windows.OfType<Window>().Any(w => w.IsVisible))
                    app.Shutdown(1);
            });
        };
        // Tareas en segundo plano cuyo resultado nadie miró: no cierran la aplicación; quedan en el registro.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Log("Tarea en segundo plano", e.Exception);
        };
        // Otros hilos: .NET cierra el proceso sin remedio. Antes se ponen a salvo las consultas sin guardar.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);
    }

    /// <summary>Muestra un error (y lo anota). Se puede llamar desde cualquier hilo.</summary>
    /// <param name="title">Qué se estaba haciendo: "No se pudo guardar el archivo".</param>
    /// <param name="unexpected">Error que nadie esperaba: si se encadenan varios, no se abre una ventana por cada uno.</param>
    public static void Show(Window? owner, string title, Exception exception, bool unexpected = false)
    {
        var error = Unwrap(exception);
        Log(title, error);
        Present(owner, title, Message(error), Report(title, error), unexpected);
    }

    /// <summary>Muestra un error que no viene de una excepción (por ejemplo, el resultado de una restauración incompleta).</summary>
    public static void Show(Window? owner, string title, string message, string? details = null)
    {
        Log(title, message + (details != null ? Environment.NewLine + details : ""));
        Present(owner, title, message, Report(title, message, details), unexpected: false);
    }

    /// <summary>
    /// Para las tareas que se lanzan sin esperar su resultado (un menú que arranca una operación larga):
    /// si fallan, el error se muestra como cualquier otro en lugar de perderse.
    /// </summary>
    public static void Watch(this Task? task, string title)
    {
        task?.ContinueWith(t => Show(null, title, t.Exception!, unexpected: true),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    // ---------- Presentación ----------

    private static void Present(Window? owner, string title, string message, string report, bool unexpected)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Present(owner, title, message, report, unexpected));
            return;
        }

        try
        {
            // Errores en cadena (uno por cada redibujado, por ejemplo): una sola ventana, que cuenta los demás.
            if (unexpected && _open != null)
            {
                _open.AddRepeated(report);
                return;
            }

            var dialog = new ErrorDialog(title, message, report);
            var parent = owner is { IsLoaded: true, IsVisible: true } ? owner
                : Application.Current!.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible && w is not ErrorDialog)
                  ?? (Application.Current.MainWindow is { IsLoaded: true, IsVisible: true } main ? main : null);
            if (parent != null) dialog.Owner = parent;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            bool outermost = _open == null;
            if (outermost) _open = dialog;
            try
            {
                dialog.ShowDialog();
            }
            finally
            {
                if (outermost) _open = null;
            }
        }
        catch (Exception failure)
        {
            // Si hasta la ventana de error falla, queda el cuadro de mensaje del sistema.
            Log("No se pudo mostrar la ventana de error", failure);
            MessageBox.Show(message + Environment.NewLine + Environment.NewLine + "Detalle en: " + LogPath, $"{App.Name} - {title}",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Error en un hilo que no es el de la interfaz y que nadie capturó: el proceso se va a cerrar.</summary>
    private static void Fatal(Exception? exception)
    {
        string saved = "";
        try
        {
            int count = RecoveryStore.SaveNow();
            if (count > 0) saved = $"\n\nSe guardaron {count} consulta(s) sin guardar en:\n{RecoveryStore.Folder}";
        }
        catch
        {
        }
        try
        {
            Log("Error grave: la aplicación se cierra", exception ?? new Exception("Error desconocido"));
            MessageBox.Show(
                $"Ha ocurrido un error grave y {App.Name} tiene que cerrarse.\n\n{Message(Unwrap(exception ?? new Exception("Error desconocido")))}{saved}" +
                $"\n\nEl detalle, para informar del fallo, quedó en:\n{LogPath}",
                $"{App.Name} - Error grave", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
        }
    }

    // ---------- Texto del informe ----------

    /// <summary>Una tarea fallida envuelve su error en AggregateException: interesa el de dentro.</summary>
    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } or TargetInvocationException { InnerException: not null })
            exception = exception.InnerException!;
        return exception;
    }

    private static string Message(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message.Trim();

    private static string Header(string title) =>
        $"{App.Name} {AppVersion}\n" +
        $"Fecha: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
        $"Sistema: {Environment.OSVersion.VersionString} ({(Environment.Is64BitProcess ? "64" : "32")} bits) · .NET {Environment.Version}\n" +
        $"Dónde: {title}\n";

    private static string Report(string title, Exception exception)
    {
        var report = new StringBuilder(Header(title));
        for (var current = exception; current != null; current = current.InnerException)
            report.Append(current == exception ? "Error: " : "Causado por: ").Append(current.GetType().FullName).Append(": ").AppendLine(current.Message);
        report.AppendLine().AppendLine("Pila de llamadas:").AppendLine(exception.ToString());
        return report.ToString().ReplaceLineEndings();
    }

    private static string Report(string title, string message, string? details) =>
        (Header(title) + "Error: " + message + (details != null ? "\n\n" + details : "") + "\n").ReplaceLineEndings();

    // ---------- Registro ----------

    public static void Log(string title, Exception exception) => Log(title, Report(title, Unwrap(exception)), raw: true);

    private static void Log(string title, string text, bool raw = false)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(App.DataFolder);
                // Si el registro crece demasiado, se guarda el anterior y se empieza uno nuevo.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                    File.Move(LogPath, LogPath + ".anterior", overwrite: true);
                File.AppendAllText(LogPath, (raw ? text : Header(title) + text + Environment.NewLine) + new string('-', 70) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // No poder escribir el registro no debe provocar otro error.
        }
    }
}
