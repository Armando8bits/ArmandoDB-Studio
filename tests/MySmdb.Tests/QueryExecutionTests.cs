using System.Diagnostics;

namespace MySmdb.Tests;

/// <summary>Ejecución de consultas desde una pestaña, contra bases reales (SQLite y, si está, LocalDB).</summary>
[Collection("Ejecución de consultas")]   // cambian ajustes comunes (tope de filas, historial): no en paralelo
public class QueryExecutionTests
{
    /// <summary>Ejecuta el texto en una pestaña nueva y la devuelve ya terminada.</summary>
    private static async Task<QueryTab> ExecuteAsync(ConnectionProfile profile, string? database, string sql)
    {
        var tab = new QueryTab(profile, database, "Consulta1.sql");
        tab.SetText(sql);
        await tab.ExecuteAsync();
        tab.Close();
        return tab;
    }

    [Fact]
    public void Una_consulta_correcta_deja_sus_filas_y_su_estado() => Ui.Run(async () =>
    {
        using var db = new SqliteDb();
        await db.RunAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, n TEXT); INSERT INTO t (n) VALUES ('a'), ('b'), ('c');");

        var tab = await ExecuteAsync(db.Profile, "main", "SELECT * FROM t;");

        Assert.Equal(3, tab.RowCount);
        Assert.Equal("Consulta ejecutada correctamente.", tab.StatusText);
        Assert.False(tab.IsRunning);
    });

    [Fact]
    public void Una_consulta_con_error_termina_y_lo_indica() => Ui.Run(async () =>
    {
        using var db = new SqliteDb();
        var tab = await ExecuteAsync(db.Profile, "main", "SELECT * FROM no_existe;");

        Assert.Equal("Consulta finalizada con errores.", tab.StatusText);
        Assert.False(tab.IsRunning);
    });

    [Fact]
    public void Al_llegar_al_tope_de_filas_se_deja_de_leer() => Ui.Run(async () =>
    {
        int previous = QueryTab.MaxRows;
        QueryTab.MaxRows = 1000;
        try
        {
            using var db = new SqliteDb();
            var watch = Stopwatch.StartNew();
            var tab = await ExecuteAsync(db.Profile, "main",
                "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 200000000) SELECT x FROM c;");

            Assert.Equal(1000, tab.RowCount);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Tardó {watch.Elapsed}: siguió leyendo tras el tope.");
        }
        finally
        {
            QueryTab.MaxRows = previous;
        }
    });

    /// <summary>El valor de la primera celda del primer resultado de la pestaña.</summary>
    private static object? FirstCell(QueryTab tab)
    {
        var grids = (List<System.Windows.Controls.DataGrid>)typeof(QueryTab)
            .GetField("_grids", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tab)!;
        return ((List<object?[]>)grids[0].ItemsSource)[0][0];
    }

    [LocalDbFact]
    public void Si_la_conexion_se_cayo_mientras_no_se_usaba_se_reabre_sola_al_ejecutar() => Ui.Run(async () =>
    {
        int previous = QueryTab.IdleCheckSeconds;
        QueryTab.IdleCheckSeconds = 0;   // comprobar siempre, sin esperar al tiempo de inactividad
        try
        {
            var tab = new QueryTab(LocalDb.Profile, "master", "Consulta1.sql");
            // Una tabla temporal marca la sesión: solo existe en la conexión que la creó. (Además impide que el
            // controlador de SQL Server recupere la sesión por su cuenta, que es lo que hace si no hay estado.)
            tab.SetText(MarkSession);
            await tab.ExecuteAsync();
            Assert.Equal("Consulta ejecutada correctamente.", tab.StatusText);
            int session = Convert.ToInt32(FirstCell(tab));

            // El servidor cierra esa sesión por su cuenta, como haría por inactividad o al caerse el túnel.
            await LocalDb.Query("master", $"KILL {session}");

            // Ejecutar otra vez funciona a la primera, sin desconectar ni volver a conectar a mano: es otra sesión.
            tab.SetText(ReadMark);
            await tab.ExecuteAsync();
            Assert.Equal("Consulta ejecutada correctamente.", tab.StatusText);
            Assert.Null(FirstCell(tab));   // la marca no existe: es una conexión nueva

            // Con la conexión sana no se reabre nada: lo que se crea en una ejecución sigue ahí en la siguiente.
            tab.SetText(MarkSession);
            await tab.ExecuteAsync();
            tab.SetText(ReadMark);
            await tab.ExecuteAsync();
            Assert.NotNull(FirstCell(tab));
            tab.Close();
        }
        finally
        {
            QueryTab.IdleCheckSeconds = previous;
        }
    });

    [LocalDbFact]
    public void Si_la_conexion_se_corta_al_ejecutar_la_siguiente_ejecucion_ya_funciona() => Ui.Run(async () =>
    {
        // Con la comprobación previa desactivada (uso reciente), el corte se descubre al ejecutar: esa ejecución
        // falla, pero la conexión rota se suelta y la siguiente abre otra.
        var tab = new QueryTab(LocalDb.Profile, "master", "Consulta1.sql");
        tab.SetText(MarkSession);
        await tab.ExecuteAsync();
        await LocalDb.Query("master", $"KILL {Convert.ToInt32(FirstCell(tab))}");

        tab.SetText(ReadMark);
        await tab.ExecuteAsync();
        Assert.Equal("Consulta finalizada con errores.", tab.StatusText);

        await tab.ExecuteAsync();
        Assert.Equal("Consulta ejecutada correctamente.", tab.StatusText);
        Assert.Null(FirstCell(tab));
        tab.Close();
    });

    private const string MarkSession = "CREATE TABLE #marca (n int);\nSELECT @@SPID AS sesion;";
    private const string ReadMark = "SELECT OBJECT_ID('tempdb..#marca') AS marca;";

    [LocalDbFact]
    public void En_SQL_Server_al_llegar_al_tope_se_cancela_el_resto_y_la_conexion_sigue_sirviendo() => Ui.Run(async () =>
    {
        int previous = QueryTab.MaxRows;
        QueryTab.MaxRows = 1000;
        try
        {
            // Cientos de millones de filas: leerlas todas llevaría minutos.
            const string huge = "SELECT 1 AS n FROM sys.all_columns a CROSS JOIN sys.all_columns b CROSS JOIN sys.all_columns c;";
            var tab = new QueryTab(LocalDb.Profile, "master", "Consulta1.sql");
            tab.SetText(huge);
            var watch = Stopwatch.StartNew();
            await tab.ExecuteAsync();

            Assert.Equal(1000, tab.RowCount);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Tardó {watch.Elapsed}: siguió leyendo tras el tope.");

            // La misma pestaña (y su conexión) ejecuta otra consulta con normalidad.
            tab.SetText("SELECT 1 AS a UNION ALL SELECT 2;");
            await tab.ExecuteAsync();
            Assert.Equal(2, tab.RowCount);
            Assert.Equal("Consulta ejecutada correctamente.", tab.StatusText);
            tab.Close();
        }
        finally
        {
            QueryTab.MaxRows = previous;
        }
    });
}
