using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace MySmdb.Tests;

/// <summary>
/// Hilo de interfaz para las pruebas que crean controles: WPF exige un hilo STA y admite una sola aplicación
/// por proceso, así que todas comparten este.
/// </summary>
internal static class Ui
{
    private static readonly Lazy<Dispatcher> Shared = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                Theme.Apply(ThemeChoice.Light);
                ready.SetResult(Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Interfaz de las pruebas" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });

    /// <summary>Ejecuta la prueba en el hilo de interfaz; un fallo dentro llega a la prueba tal cual.</summary>
    public static void Run(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        Shared.Value.Invoke(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        failure?.Throw();
    }

    /// <summary>
    /// Igual, para pruebas que esperan algo (ejecutar una consulta): cada "await" vuelve al hilo de interfaz,
    /// como en la aplicación, mientras el hilo de la prueba espera fuera.
    /// </summary>
    public static void Run(Func<Task> test) =>
        Shared.Value.InvokeAsync(test).Task.Unwrap().GetAwaiter().GetResult();

    /// <summary>
    /// Ventana ya mostrada, fuera de la pantalla, para hacer de dueña de las ventanas que se prueban
    /// (WPF no admite como dueña una ventana que nunca se mostró). Hay que cerrarla al terminar.
    /// </summary>
    public static System.Windows.Window HiddenOwner()
    {
        var owner = new System.Windows.Window
        {
            Left = -20000, Top = -20000, Width = 400, Height = 300, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, WindowStyle = System.Windows.WindowStyle.None,
        };
        owner.Show();
        return owner;
    }

    /// <summary>Deja que la ventana termine de cargarse y pintar (enlaces de datos, eventos Loaded).</summary>
    public static Task Settle() =>
        Shared.Value.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}

/// <summary>"Duplicar vista": varias pestañas sobre el mismo script.</summary>
public class ScriptViewsTests
{
    private static readonly ConnectionProfile Profile = new() { Kind = DbKind.Sqlite, FilePath = "no-se-abre.db", Database = "main", Alias = "prueba" };

    private static QueryTab NewTab(string title = "Consulta1.sql") => new(Profile, "main", title);
    private static QueryTab ViewOf(QueryTab tab) => new(Profile, "main", tab.Script);

    [Fact]
    public void Lo_que_se_escribe_en_una_vista_aparece_en_la_otra() => Ui.Run(() =>
    {
        var first = NewTab();
        first.SetText("SELECT 1;");
        var second = ViewOf(first);
        Assert.Equal("SELECT 1;", second.SqlEditor.Text);
        Assert.False(second.IsDirty);

        second.SqlEditor.Document.Insert(0, "-- nota\n");

        Assert.Equal("-- nota\nSELECT 1;", first.SqlEditor.Text);
        Assert.True(first.IsDirty);
        Assert.True(second.IsDirty);
    });

    [Fact]
    public void Un_cambio_avisa_a_todas_las_vistas_para_que_actualicen_su_titulo() => Ui.Run(() =>
    {
        var first = NewTab();
        first.SetText("SELECT 1;");
        var second = ViewOf(first);
        int firstNotified = 0, secondNotified = 0;
        first.StateChanged += _ => firstNotified++;
        second.StateChanged += _ => secondNotified++;

        second.SqlEditor.Document.Insert(0, "x");

        Assert.True(firstNotified > 0);
        Assert.True(secondNotified > 0);
    });

    [Fact]
    public void Deshacer_es_comun_a_las_vistas() => Ui.Run(() =>
    {
        var first = NewTab();
        first.SetText("SELECT 1;");
        var second = ViewOf(first);
        second.SqlEditor.Document.Insert(0, "-- nota\n");

        Assert.True(first.SqlEditor.CanUndo);
        first.SqlEditor.Undo();

        Assert.Equal("SELECT 1;", second.SqlEditor.Text);
    });

    [Fact]
    public void Guardar_en_una_vista_guarda_el_script_para_todas() => Ui.Run(() =>
    {
        using var folder = new TempFolder();
        string path = folder.File("consulta.sql");
        var first = NewTab();
        var second = ViewOf(first);
        first.SqlEditor.Document.Insert(0, "SELECT 'ñ';");

        second.SaveFile(path);

        Assert.Equal("SELECT 'ñ';", File.ReadAllText(path));
        Assert.False(first.IsDirty);
        Assert.False(second.IsDirty);
        Assert.Equal(path, first.FilePath);
        Assert.Equal("consulta.sql", first.Title);
        Assert.Equal("consulta.sql", second.Title);
    });

    [Fact]
    public void Las_vistas_de_un_mismo_script_se_numeran_y_la_unica_no() => Ui.Run(() =>
    {
        var first = NewTab();
        Assert.Equal("", first.ViewSuffix);
        Assert.True(first.IsLastView);

        var second = ViewOf(first);
        var third = ViewOf(first);
        Assert.Equal(new[] { ":1", ":2", ":3" }, new[] { first.ViewSuffix, second.ViewSuffix, third.ViewSuffix });
        Assert.False(first.IsLastView);

        // El título (nombre para guardar, copia de recuperación) no lleva el número: ':' no vale en un archivo.
        Assert.Equal("Consulta1.sql", second.Title);
    });

    [Fact]
    public void Al_cerrar_una_vista_el_script_sigue_abierto_en_las_demas() => Ui.Run(() =>
    {
        var first = NewTab();
        var second = ViewOf(first);
        first.SqlEditor.Document.Insert(0, "SELECT 1;");
        int closedNotified = 0, remainingNotified = 0;
        first.StateChanged += _ => closedNotified++;
        second.StateChanged += _ => remainingNotified++;

        first.Close();

        Assert.True(remainingNotified > 0);          // su título pierde el ":2"
        Assert.True(second.IsLastView);              // ahora cerrarla sí debe preguntar por los cambios
        Assert.Equal("", second.ViewSuffix);
        Assert.True(second.IsDirty);
        Assert.Equal("SELECT 1;", second.SqlEditor.Text);

        // La vista cerrada ya no recibe avisos del script.
        closedNotified = 0;
        second.SqlEditor.Document.Insert(0, "x");
        second.SetText("otro");
        Assert.Equal(0, closedNotified);
    });

    [Fact]
    public void Una_vista_nueva_tras_cerrar_otra_no_repite_numero() => Ui.Run(() =>
    {
        var first = NewTab();
        var second = ViewOf(first);
        first.Close();
        var third = ViewOf(second);
        Assert.Equal(new[] { ":2", ":3" }, new[] { second.ViewSuffix, third.ViewSuffix });
    });

    [Fact]
    public void Dos_consultas_distintas_no_comparten_nada() => Ui.Run(() =>
    {
        var first = NewTab("Consulta1.sql");
        var other = NewTab("Consulta2.sql");
        first.SqlEditor.Document.Insert(0, "SELECT 1;");

        Assert.Equal("", other.SqlEditor.Text);
        Assert.False(other.IsDirty);
        Assert.NotSame(first.Script, other.Script);
    });

    [Fact]
    public void Una_pestana_sin_conexion_se_edita_y_se_guarda_pero_no_ejecuta() => Ui.Run(() =>
    {
        using var folder = new TempFolder();
        string path = folder.File("suelto.sql");
        File.WriteAllText(path, "SELECT 1;");
        var tab = new QueryTab(ConnectionProfile.Offline, null, "Consulta1.sql");
        Assert.True(tab.IsOffline);

        tab.LoadFile(path);
        tab.SqlEditor.Document.Insert(0, "-- nota\n");
        tab.SaveFile(path);
        Assert.Equal("-- nota\nSELECT 1;", File.ReadAllText(path));

        // Ejecutar o pedir el plan sin conexión no intenta conectar a nada: lo deja dicho y termina.
        tab.ExecuteAsync().GetAwaiter().GetResult();
        Assert.False(tab.IsRunning);
        Assert.Equal("Sin conexión.", tab.StatusText);
        tab.ExplainAsync().GetAwaiter().GetResult();
        Assert.False(tab.IsRunning);
    });

    [Fact]
    public void Una_pestana_sin_conexion_pasa_a_tenerla_al_conectarla() => Ui.Run(() =>
    {
        var tab = new QueryTab(ConnectionProfile.Offline, null, "Consulta1.sql");
        tab.SetText("SELECT 1;");

        tab.ChangeConnectionAsync(Profile, "main").GetAwaiter().GetResult();

        Assert.False(tab.IsOffline);
        Assert.Same(Profile, tab.Profile);
        Assert.Equal("main", tab.CurrentDatabase);
        Assert.Equal("SELECT 1;", tab.SqlEditor.Text);
    });

    [Fact]
    public void Solo_la_marca_de_sin_conexion_es_sin_conexion()
    {
        Assert.True(ConnectionProfile.Offline.IsOffline);
        Assert.False(new ConnectionProfile { Alias = "Sin conexión" }.IsOffline);
        Assert.Equal("Sin conexión", ConnectionProfile.Offline.Name);
    }

    [Fact]
    public void Abrir_un_archivo_en_una_vista_lo_abre_en_todas() => Ui.Run(() =>
    {
        using var folder = new TempFolder();
        string path = folder.File("datos.sql");
        File.WriteAllText(path, "SELECT 2;");
        var first = NewTab();
        var second = ViewOf(first);

        first.LoadFile(path);

        Assert.Equal("SELECT 2;", second.SqlEditor.Text);
        Assert.Equal(path, second.FilePath);
        Assert.False(second.IsDirty);
        Assert.False(second.SqlEditor.CanUndo);
    });
}
