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
