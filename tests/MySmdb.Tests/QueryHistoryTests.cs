namespace MySmdb.Tests;

/// <summary>Historial de consultas ejecutadas.</summary>
[Collection("Ejecución de consultas")]   // comparten el archivo del historial y sus ajustes: no en paralelo
public class QueryHistoryTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly string _previousFile = QueryHistory.FilePath;
    private readonly bool _previousEnabled = AppSettings.Current.HistoryEnabled;
    private readonly int _previousDays = AppSettings.Current.HistoryRetentionDays;

    public QueryHistoryTests()
    {
        QueryHistory.FilePath = _folder.File("historial.jsonl");
        QueryHistory.Reset();
        AppSettings.Current.HistoryEnabled = true;
        AppSettings.Current.HistoryRetentionDays = 90;
    }

    public void Dispose()
    {
        QueryHistory.FilePath = _previousFile;
        QueryHistory.Reset();
        AppSettings.Current.HistoryEnabled = _previousEnabled;
        AppSettings.Current.HistoryRetentionDays = _previousDays;
        _folder.Dispose();
    }

    // Fechas relativas a hoy: con una fecha fija, las entradas caducarían al pasar el plazo de conservación.
    private static readonly DateTime Recent = DateTime.Today.AddHours(-3);

    private static HistoryEntry Entry(string sql, string connection = "prueba", string outcome = HistoryEntry.Ok, int daysAgo = 0) =>
        new(Recent.AddDays(-daysAgo), connection, "main", sql, 0.25, 3, outcome, null);

    [Fact]
    public void Por_defecto_se_conserva_90_dias() => Assert.Equal(90, new AppSettings().HistoryRetentionDays);

    [Fact]
    public void Lo_mas_antiguo_que_el_plazo_no_se_muestra_y_se_quita_del_disco()
    {
        AppSettings.Current.HistoryRetentionDays = -1;   // para poder guardarlas todas
        QueryHistory.Add(Entry("SELECT 'vieja';", daysAgo: 120));
        QueryHistory.Add(Entry("SELECT 'media';", daysAgo: 45));
        QueryHistory.Add(Entry("SELECT 'nueva';"));
        Assert.Equal(3, QueryHistory.Load().Count);

        AppSettings.Current.HistoryRetentionDays = 90;
        Assert.Equal(new[] { "SELECT 'nueva';", "SELECT 'media';" }, QueryHistory.Load().Select(e => e.Sql).ToArray());
        Assert.Equal(3, File.ReadLines(QueryHistory.FilePath).Count());   // leer no borra
        QueryHistory.ApplyRetention(sessionBoundary: false);
        Assert.Equal(2, File.ReadLines(QueryHistory.FilePath).Count());

        AppSettings.Current.HistoryRetentionDays = 30;
        QueryHistory.ApplyRetention(sessionBoundary: true);
        Assert.Equal("SELECT 'nueva';", Assert.Single(QueryHistory.Load()).Sql);
        Assert.Single(File.ReadLines(QueryHistory.FilePath));
    }

    [Fact]
    public void Sin_limite_de_tiempo_se_conserva_todo()
    {
        AppSettings.Current.HistoryRetentionDays = -1;
        QueryHistory.Add(Entry("SELECT 'antigua';", daysAgo: 2000));
        QueryHistory.ApplyRetention(sessionBoundary: true);
        Assert.Single(QueryHistory.Load());
    }

    [Fact]
    public void Solo_esta_sesion_guarda_mientras_se_trabaja_y_lo_borra_al_cerrar_y_al_arrancar()
    {
        AppSettings.Current.HistoryRetentionDays = 0;
        QueryHistory.Add(Entry("SELECT 1;"));
        QueryHistory.Add(Entry("SELECT 2;"));
        Assert.Equal(2, QueryHistory.Load().Count);

        // Elegir la opción en el menú no borra lo de la sesión en curso.
        QueryHistory.ApplyRetention(sessionBoundary: false);
        Assert.Equal(2, QueryHistory.Load().Count);

        // Al cerrar (o al arrancar, si la vez anterior no cerró bien) sí.
        QueryHistory.ApplyRetention(sessionBoundary: true);
        Assert.Empty(QueryHistory.Load());
        Assert.False(File.Exists(QueryHistory.FilePath));

        QueryHistory.Add(Entry("SELECT 3;"));
        Assert.Single(QueryHistory.Load());
    }

    [Fact]
    public void Lo_guardado_se_lee_igual_y_lo_mas_reciente_va_primero()
    {
        var first = Entry("SELECT 1;");
        var second = new HistoryEntry(Recent.AddMinutes(1), "Ventas ñ", null, "DELETE FROM t\nWHERE id = 'x''y';", 1.5, null,
            HistoryEntry.Failed, "Error 1146: no existe");
        QueryHistory.Add(first);
        QueryHistory.Add(second);

        Assert.Equal(new[] { second, first }, QueryHistory.Load());
    }

    [Fact]
    public void Una_consulta_vacia_no_se_guarda()
    {
        QueryHistory.Add(Entry("   \n"));
        Assert.Empty(QueryHistory.Load());
    }

    [Fact]
    public void Desactivado_no_guarda_nada()
    {
        AppSettings.Current.HistoryEnabled = false;
        QueryHistory.Add(Entry("SELECT 1;"));
        Assert.Empty(QueryHistory.Load());
        Assert.False(File.Exists(QueryHistory.FilePath));
    }

    [Fact]
    public void Un_script_enorme_se_guarda_recortado()
    {
        QueryHistory.Add(Entry(new string('x', QueryHistory.MaxSqlLength + 500)));

        string saved = Assert.Single(QueryHistory.Load()).Sql;
        Assert.StartsWith(new string('x', QueryHistory.MaxSqlLength), saved);
        Assert.Contains("el resto no se guardó", saved);
        Assert.True(saved.Length < QueryHistory.MaxSqlLength + 100);
    }

    [Fact]
    public void Una_linea_a_medio_escribir_no_estropea_las_demas()
    {
        QueryHistory.Add(Entry("SELECT 1;"));
        File.AppendAllText(QueryHistory.FilePath, "{\"When\":\"2026-10-08T09:3");   // cierre brusco a mitad de línea
        File.AppendAllText(QueryHistory.FilePath, "\nesto no es JSON\n");
        QueryHistory.Add(Entry("SELECT 2;"));

        Assert.Equal(new[] { "SELECT 2;", "SELECT 1;" }, QueryHistory.Load().Select(e => e.Sql).ToArray());
    }

    [Fact]
    public void Solo_se_conservan_las_ultimas_entradas()
    {
        int total = QueryHistory.MaxEntries + QueryHistory.MaxEntries / 10 + 5;
        // Se escribe el archivo de golpe (añadir de una en una miles de veces sería lento) y luego se añade una más.
        File.WriteAllLines(QueryHistory.FilePath, Enumerable.Range(1, total).Select(i => System.Text.Json.JsonSerializer.Serialize(Entry($"SELECT {i};"))));
        QueryHistory.Reset();

        var loaded = QueryHistory.Load();
        Assert.Equal(QueryHistory.MaxEntries, loaded.Count);
        Assert.Equal($"SELECT {total};", loaded[0].Sql);

        // Al añadir con el archivo pasado de tamaño, se recorta en disco.
        QueryHistory.Add(Entry("SELECT 'ultima';"));
        Assert.Equal(QueryHistory.MaxEntries, File.ReadLines(QueryHistory.FilePath).Count());
        Assert.Equal("SELECT 'ultima';", QueryHistory.Load()[0].Sql);
    }

    [Fact]
    public void Borrar_lo_vacia()
    {
        QueryHistory.Add(Entry("SELECT 1;"));
        QueryHistory.Clear();
        Assert.Empty(QueryHistory.Load());
        QueryHistory.Add(Entry("SELECT 2;"));
        Assert.Single(QueryHistory.Load());
    }

    [Fact]
    public void El_resumen_es_el_comienzo_en_una_linea()
    {
        Assert.Equal("SELECT a, b FROM t WHERE x = 1", Entry("SELECT a,\n       b\nFROM t\n\tWHERE x = 1").Summary);
        Assert.EndsWith("...", Entry(string.Join(" ", Enumerable.Repeat("palabralarga", 60))).Summary);
    }

    [Fact]
    public void Ejecutar_una_consulta_la_deja_en_el_historial() => Ui.Run(async () =>
    {
        using var db = new SqliteDb();
        await db.RunAsync("CREATE TABLE t (id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1), (2);");

        var tab = new QueryTab(db.Profile, "main", "Consulta1.sql");
        tab.SetText("SELECT * FROM t;");
        await tab.ExecuteAsync();
        tab.SetText("SELECT * FROM no_existe;");
        await tab.ExecuteAsync();
        tab.Close();

        var history = QueryHistory.Load();
        Assert.Equal(2, history.Count);
        Assert.Equal(("SELECT * FROM t;", HistoryEntry.Ok, 2, "prueba", "main"),
            (history[1].Sql, history[1].Outcome, history[1].Rows, history[1].Connection, history[1].Database));
        Assert.Null(history[1].Error);
        Assert.Equal(("SELECT * FROM no_existe;", HistoryEntry.Failed), (history[0].Sql, history[0].Outcome));
        Assert.Contains("no_existe", history[0].Error);
    });

    [Fact]
    public void La_ventana_del_historial_se_abre_con_datos_y_reabre_una_consulta() => Ui.Run(async () =>
    {
        QueryHistory.Add(Entry("SELECT 1;"));
        QueryHistory.Add(Entry("SELECT 2;", connection: "otra", outcome: HistoryEntry.Failed));
        var owner = Ui.HiddenOwner();
        try
        {
            HistoryEntry? opened = null;
            var window = new HistoryWindow(owner, entry => opened = entry) { ShowActivated = false };
            window.Show();
            await Ui.Settle();

            // La más reciente queda seleccionada; Intro la abre.
            var root = (System.Windows.Controls.Panel)window.Content;
            var grid = (System.Windows.Controls.DataGrid)((System.Windows.Controls.Panel)root.Children[2]).Children[0];

            // La barra de arriba (filtro y botones) cabe entera en el ancho de la ventana.
            var bar = (System.Windows.FrameworkElement)root.Children[0];
            bar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(bar.DesiredSize.Width <= root.ActualWidth, $"La barra pide {bar.DesiredSize.Width} y hay {root.ActualWidth}");
            Assert.True(bar.DesiredSize.Width <= window.MinWidth - 20, $"La barra pide {bar.DesiredSize.Width} y el ancho mínimo es {window.MinWidth}");
            Assert.Equal(2, grid.Items.Count);
            grid.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(grid)!, 0, System.Windows.Input.Key.Enter)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            Assert.Equal("SELECT 2;", opened?.Sql);

            window.Reload();

            // Minimizar la pliega a su barra de título (ancha para leer el título) y otra vez la deja como estaba.
            var before = (window.Left, window.Top, window.Width, window.Height, window.ResizeMode);
            RollUp.Toggle(window);
            await Ui.Settle();
            Assert.True(RollUp.IsRolled(window));
            Assert.Equal(System.Windows.WindowState.Normal, window.WindowState);
            Assert.True(window.ActualWidth >= 300, $"Ancho plegada: {window.ActualWidth}");
            Assert.True(window.ActualHeight < 80, $"Alto plegada: {window.ActualHeight}");

            RollUp.Toggle(window);
            await Ui.Settle();
            Assert.False(RollUp.IsRolled(window));
            Assert.Equal(before, (window.Left, window.Top, window.Width, window.Height, window.ResizeMode));
            Assert.True(window.ActualHeight > 300);
            window.Close();
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public void El_plan_de_ejecucion_no_se_guarda_en_el_historial() => Ui.Run(async () =>
    {
        using var db = new SqliteDb();
        var tab = new QueryTab(db.Profile, "main", "Consulta1.sql");
        tab.SetText("SELECT 1;");
        await tab.ExplainAsync();
        tab.Close();
        Assert.Empty(QueryHistory.Load());
    });
}
