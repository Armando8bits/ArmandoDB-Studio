using System.Data.Common;

namespace MySmdb.Tests;

/// <summary>Interpretación de los planes de ejecución de cada motor como árbol de pasos.</summary>
public class PlanParserTests
{
    private static ResultSet Result(string[] columns, params object?[][] rows)
    {
        var result = new ResultSet { Columns = columns };
        result.Rows.AddRange(rows);
        return result;
    }

    // ---------- MySQL: EXPLAIN FORMAT=TREE ----------

    private const string MySqlTree =
        "-> Sort: p.total DESC  (cost=9.50 rows=70)\n" +
        "    -> Nested loop inner join  (cost=9.03 rows=70)\n" +
        "        -> Table scan on c  (cost=1.25 rows=10)\n" +
        "        -> Index lookup on p using fk_cliente (cliente_id=c.id)  (cost=0.75 rows=7)\n";

    [Fact]
    public void MySql_la_sangria_define_el_arbol()
    {
        var root = PlanParser.FromMySqlTree(MySqlTree)!;

        Assert.Equal("Sort", root.Operation);
        Assert.Equal("p.total DESC", root.Detail);
        var join = Assert.Single(root.Children);
        Assert.Equal("Nested loop inner join", join.Operation);
        Assert.Equal(new[] { "Table scan", "Index lookup" }, join.Children.Select(c => c.Operation).ToArray());
        Assert.Equal("on p using fk_cliente (cliente_id=c.id)", join.Children[1].Detail);
    }

    [Fact]
    public void MySql_costo_y_filas_estimadas()
    {
        var root = PlanParser.FromMySqlTree(MySqlTree)!;
        Assert.Equal(9.50, root.Cost);
        Assert.Equal(70, root.Rows);
        Assert.Equal(1.25, root.Children[0].Children[0].Cost);
    }

    [Fact]
    public void MySql_cada_paso_se_clasifica_para_colorearlo()
    {
        var root = PlanParser.FromMySqlTree(MySqlTree)!;
        Assert.Equal(PlanNodeKind.SortOrTemp, root.Kind);
        Assert.Equal(PlanNodeKind.Join, root.Children[0].Kind);
        Assert.Equal(PlanNodeKind.FullScan, root.Children[0].Children[0].Kind);   // lo que más interesa ver
        Assert.Equal(PlanNodeKind.Index, root.Children[0].Children[1].Kind);
    }

    [Fact]
    public void MySql_con_EXPLAIN_ANALYZE_trae_los_datos_reales()
    {
        var root = PlanParser.FromMySqlTree("-> Table scan on t  (cost=1.25 rows=10) (actual time=0.031..0.045 rows=12 loops=1)\n")!;
        Assert.Equal("real: 12 filas, 0.045 ms", root.Actual);
        Assert.Equal("Table scan", root.Operation);
        Assert.Equal(10, root.Rows);
    }

    [Fact]
    public void MySql_varias_raices_cuelgan_de_un_nodo_comun_y_sin_pasos_no_hay_arbol()
    {
        var root = PlanParser.FromMySqlTree("-> Table scan on a  (cost=1 rows=1)\n-> Table scan on b  (cost=1 rows=1)\n")!;
        Assert.Equal("Consulta", root.Operation);
        Assert.Equal(2, root.Children.Count);

        Assert.Null(PlanParser.FromMySqlTree("esto no es un plan"));
    }

    // ---------- MySQL / MariaDB: EXPLAIN clásico ----------

    [Fact]
    public void Tabla_clasica_una_cadena_de_tablas_en_el_orden_en_que_se_combinan()
    {
        var result = Result(new[] { "id", "select_type", "table", "type", "key", "rows", "Extra" },
            new object?[] { 1, "SIMPLE", "c", "ALL", null, 10, "Using where" },
            new object?[] { 1, "SIMPLE", "p", "ref", "fk_cliente", 7, null });

        var root = PlanParser.FromClassicTable(result)!;

        Assert.Equal("SIMPLE c", root.Operation);
        Assert.Equal(PlanNodeKind.FullScan, root.Kind);   // type = ALL
        Assert.Equal(10, root.Rows);
        var next = Assert.Single(root.Children);
        Assert.Equal(PlanNodeKind.Index, next.Kind);
        Assert.Contains("índice: fk_cliente", next.Detail);
    }

    [Fact]
    public void Un_resultado_que_no_es_un_EXPLAIN_no_da_arbol()
    {
        Assert.Null(PlanParser.FromClassicTable(Result(new[] { "a", "b" }, new object?[] { 1, 2 })));
    }

    // ---------- SQLite: EXPLAIN QUERY PLAN ----------

    [Fact]
    public void Sqlite_enlaza_los_pasos_por_id_y_padre()
    {
        var result = Result(new[] { "id", "parent", "notused", "detail" },
            new object?[] { 3L, 0L, 0L, "SCAN c" },
            new object?[] { 5L, 0L, 0L, "SEARCH p USING INDEX ix_pedido_cliente (cliente_id=?)" },
            new object?[] { 9L, 0L, 0L, "USE TEMP B-TREE FOR ORDER BY" });

        var root = PlanParser.FromSqlite(result)!;

        Assert.Equal("Consulta", root.Operation);
        Assert.Equal(new[] { "SCAN", "SEARCH", "USE TEMP B-TREE" }, root.Children.Select(c => c.Operation).ToArray());
        Assert.Equal(new[] { PlanNodeKind.FullScan, PlanNodeKind.Index, PlanNodeKind.SortOrTemp }, root.Children.Select(c => c.Kind).ToArray());
        Assert.Equal("p USING INDEX ix_pedido_cliente (cliente_id=?)", root.Children[1].Detail);
    }

    [Fact]
    public async Task Sqlite_plan_de_una_consulta_real()
    {
        using var db = new SqliteDb();
        await db.RunAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, n TEXT); CREATE INDEX ix_n ON t (n);");

        async Task<PlanNode> Plan(string sql)
        {
            await using var conn = db.Profile.CreateConnection("main");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            await using var reader = await cmd.ExecuteReaderAsync();
            var result = new ResultSet { Columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray() };
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                reader.GetValues(row!);
                result.Rows.Add(row);
            }
            return PlanParser.FromSqlite(result)!;
        }

        Assert.Equal(PlanNodeKind.Index, (await Plan("SELECT * FROM t WHERE n = 'a'")).Kind);
        Assert.Equal(PlanNodeKind.Index, (await Plan("SELECT * FROM t WHERE id = 1")).Kind);
    }

    // ---------- SQL Server: SET SHOWPLAN_ALL ----------

    private static readonly string[] ShowplanColumns = { "StmtText", "StmtId", "NodeId", "Parent", "PhysicalOp", "LogicalOp", "Argument", "EstimateRows", "TotalSubtreeCost", "Type" };

    [Fact]
    public void SqlServer_la_sentencia_es_la_raiz_y_los_operadores_cuelgan_por_NodeId()
    {
        var result = Result(ShowplanColumns,
            new object?[] { "SELECT c.nombre FROM cliente c JOIN pedido p ON ...", 1, 1, 0, null, null, null, 3.0, 0.0105, "SELECT" },
            new object?[] { "  |--Nested Loops(Inner Join)", 1, 2, 1, "Nested Loops", "Inner Join", "OUTER REFERENCES:([p].[cliente_id])", 3.0, 0.0105, "PLAN_ROW" },
            new object?[] { "       |--Clustered Index Scan(...)", 1, 3, 2, "Clustered Index Scan", "Clustered Index Scan", "OBJECT:([pedido])", 3.0, 0.0033, "PLAN_ROW" },
            new object?[] { "       |--Clustered Index Seek(...)", 1, 4, 2, "Clustered Index Seek", "Clustered Index Seek", "OBJECT:([cliente])", 1.0, 0.0065, "PLAN_ROW" });

        var root = PlanParser.FromSqlServer(result)!;

        Assert.Equal("SELECT", root.Operation);
        Assert.Equal(0.0105, root.Cost);
        var join = Assert.Single(root.Children);
        Assert.Equal("Nested Loops", join.Operation);
        Assert.Equal(PlanNodeKind.Join, join.Kind);
        Assert.StartsWith("Inner Join", join.Detail);
        Assert.Equal(new[] { PlanNodeKind.FullScan, PlanNodeKind.Index }, join.Children.Select(c => c.Kind).ToArray());
        Assert.Equal(1.0, join.Children[1].Rows);
    }

    [Fact]
    public void SqlServer_las_sentencias_sin_operadores_no_salen_y_varias_con_plan_se_agrupan()
    {
        var result = Result(ShowplanColumns,
            new object?[] { "DECLARE @n int", 1, 1, 0, null, null, null, null, null, "DECLARE" },
            new object?[] { "SELECT * FROM a", 2, 2, 0, null, null, null, 1.0, 0.003, "SELECT" },
            new object?[] { "  |--Table Scan(a)", 2, 3, 2, "Table Scan", "Table Scan", "OBJECT:([a])", 1.0, 0.003, "PLAN_ROW" },
            new object?[] { "SELECT * FROM b", 3, 4, 0, null, null, null, 1.0, 0.003, "SELECT" },
            new object?[] { "  |--Table Scan(b)", 3, 5, 4, "Table Scan", "Table Scan", "OBJECT:([b])", 1.0, 0.003, "PLAN_ROW" });

        var root = PlanParser.FromSqlServer(result)!;

        Assert.Equal("Consulta", root.Operation);
        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Children, statement => Assert.Equal(PlanNodeKind.FullScan, Assert.Single(statement.Children).Kind));
    }
}

/// <summary>Perfiles de conexión: nombres, resumen y la conexión que crea cada motor.</summary>
public class ConnectionProfileTests
{
    private static DbConnectionStringBuilder Parse(DbConnection connection) => new() { ConnectionString = connection.ConnectionString };

    [Fact]
    public void Por_defecto_es_MySql_en_localhost()
    {
        var profile = new ConnectionProfile();
        Assert.Equal(DbKind.MySql, profile.Kind);
        Assert.Equal("root@localhost:3306", profile.DefaultName);
        Assert.Equal("MySQL · root@localhost:3306", profile.Summary);
    }

    [Fact]
    public void El_alias_manda_sobre_el_nombre_por_defecto()
    {
        Assert.Equal("Producción", new ConnectionProfile { Alias = "Producción" }.Name);
        Assert.Equal("root@localhost:3306", new ConnectionProfile { Alias = "  " }.Name);
        Assert.Equal("Producción", new ConnectionProfile { Alias = "Producción" }.ToString());
    }

    [Fact]
    public void Sqlite_se_identifica_por_su_archivo()
    {
        var profile = new ConnectionProfile { Kind = DbKind.Sqlite, FilePath = @"C:\datos\tienda.db" };
        Assert.Equal(@"tienda.db (C:\datos)", profile.DefaultName);
        Assert.Equal(@"SQLite · C:\datos\tienda.db", profile.Summary);
    }

    [Fact]
    public void El_tunel_SSH_aparece_en_el_nombre_y_en_el_resumen()
    {
        var profile = new ConnectionProfile { Host = "127.0.0.1", User = "app", UseSsh = true, SshHost = "bastion", SshUser = "admin" };
        Assert.Equal("app@127.0.0.1:3306 vía bastion", profile.DefaultName);
        Assert.EndsWith("· SSH admin@bastion", profile.Summary);
    }

    [Theory]
    [InlineData("10.0.0.5", false)]
    [InlineData(@"SERVIDOR\SQLEXPRESS", true)]
    [InlineData(@"(localdb)\MSSQLLocalDB", true)]
    [InlineData("(LocalDB)", true)]
    public void SqlServer_distingue_las_instancias_con_nombre(string host, bool hasInstance)
    {
        Assert.Equal(hasInstance, new ConnectionProfile { Kind = DbKind.SqlServer, Host = host }.HasInstanceName);
    }

    [Fact]
    public void SqlServer_nombre_segun_la_autenticacion_y_la_instancia()
    {
        Assert.Equal("sa@10.0.0.5:1433", new ConnectionProfile { Kind = DbKind.SqlServer, Host = "10.0.0.5", Port = 1433, User = "sa" }.DefaultName);
        Assert.Equal(@"sa@SRV\SQLEXPRESS", new ConnectionProfile { Kind = DbKind.SqlServer, Host = @"SRV\SQLEXPRESS", Port = 1433, User = "sa" }.DefaultName);
        var windows = new ConnectionProfile { Kind = DbKind.SqlServer, Host = @"(localdb)\MSSQLLocalDB", IntegratedSecurity = true };
        Assert.Equal(@"Windows@(localdb)\MSSQLLocalDB", windows.DefaultName);
        Assert.StartsWith("SQL Server · ", windows.Summary);
    }

    [Fact]
    public void Cada_motor_crea_su_tipo_de_conexion()
    {
        using var mysql = new ConnectionProfile().CreateConnection(null);
        using var sqlite = new ConnectionProfile { Kind = DbKind.Sqlite, FilePath = "x.db" }.CreateConnection(null);
        using var sqlServer = new ConnectionProfile { Kind = DbKind.SqlServer, Host = "x", User = "u" }.CreateConnection(null);
        using var sybase = new ConnectionProfile { Kind = DbKind.Sybase, Host = "x", User = "u" }.CreateConnection(null);

        Assert.Equal("MySqlConnection", mysql.GetType().Name);
        Assert.Equal("SqliteConnection", sqlite.GetType().Name);
        Assert.Equal("SqlConnection", sqlServer.GetType().Name);
        Assert.Equal("AseConnection", sybase.GetType().Name);
    }

    [Fact]
    public void MySql_cadena_de_conexion()
    {
        using var connection = new ConnectionProfile { Host = "db.ejemplo.com", Port = 3307, User = "app", Password = "s3;cr'et\"o" }.CreateConnection("tienda");
        var parsed = Parse(connection);

        Assert.Equal("db.ejemplo.com", parsed["Server"]);
        Assert.Equal("3307", parsed["Port"]);
        Assert.Equal("tienda", parsed["Database"]);
        Assert.Equal("s3;cr'et\"o", parsed["Password"]);          // los caracteres especiales no rompen la cadena
        Assert.Equal("False", parsed["Pooling"].ToString());       // cada pestaña es una sesión propia
        Assert.Equal("True", parsed["Allow User Variables"].ToString());
    }

    [Fact]
    public void SqlServer_cadena_de_conexion_con_puerto_o_con_instancia()
    {
        using var byPort = new ConnectionProfile { Kind = DbKind.SqlServer, Host = "10.0.0.5", Port = 1433, User = "sa", Password = "p" }.CreateConnection("ventas");
        Assert.Equal("10.0.0.5,1433", Parse(byPort)["Data Source"]);
        Assert.Equal("ventas", Parse(byPort)["Initial Catalog"]);
        Assert.Equal("sa", Parse(byPort)["User ID"]);

        using var byInstance = new ConnectionProfile { Kind = DbKind.SqlServer, Host = @"SRV\SQLEXPRESS", Port = 1433, IntegratedSecurity = true }.CreateConnection(null);
        Assert.Equal(@"SRV\SQLEXPRESS", Parse(byInstance)["Data Source"]);   // el puerto no se usa
        Assert.Equal("True", Parse(byInstance)["Integrated Security"].ToString());
        Assert.False(Parse(byInstance).ContainsKey("User ID"));
    }

    [Fact]
    public void Sybase_cadena_de_conexion_y_base_por_defecto()
    {
        using var connection = new ConnectionProfile { Kind = DbKind.Sybase, Host = "10.1.2.3", Port = 2050, User = "u", Password = "a;b='c\"" }.CreateConnection("cob_cuentas");
        var parsed = Parse(connection);
        Assert.Equal("10.1.2.3", parsed["Data Source"]);
        Assert.Equal("2050", parsed["Port"]);
        Assert.Equal("cob_cuentas", parsed["Database"]);
        Assert.Equal("a;b='c\"", parsed["Pwd"]);

        // El controlador exige siempre una base: sin ninguna elegida, master.
        using var noDatabase = new ConnectionProfile { Kind = DbKind.Sybase, Host = "x", User = "u" }.CreateConnection(null);
        Assert.Equal("master", Parse(noDatabase)["Database"]);
    }

    [Theory]
    [InlineData(DbKind.MySql, "mysql> ")]
    [InlineData(DbKind.Sqlite, "sqlite> ")]
    [InlineData(DbKind.SqlServer, "mssql> ")]
    [InlineData(DbKind.Sybase, "sybase> ")]
    public void Indicador_de_cada_motor_en_el_registro_de_mensajes(DbKind kind, string prompt)
    {
        Assert.Equal(prompt, Db.Prompt(kind));
    }
}

/// <summary>Copia de recuperación de las consultas sin guardar.</summary>
[Collection("Copia de recuperación")]   // comparten estado: no en paralelo con otras
public class RecoveryStoreTests : IDisposable
{
    public void Dispose() => RecoveryStore.Clear();

    private static void Provide(params UnsavedQuery[] queries) => RecoveryStore.Start(() => queries);

    private static string[] Files() => Directory.Exists(RecoveryStore.Folder)
        ? Directory.GetFiles(RecoveryStore.Folder).Select(f => Path.GetFileName(f)).OrderBy(n => n).ToArray()
        : Array.Empty<string>();

    [Fact]
    public void La_carpeta_de_datos_de_las_pruebas_no_es_la_del_usuario()
    {
        Assert.Equal(TestSetup.DataFolder, App.DataFolder);
        Assert.StartsWith(TestSetup.DataFolder, RecoveryStore.Folder);
    }

    [Fact]
    public void Guarda_cada_consulta_con_una_cabecera_que_dice_de_donde_salio()
    {
        Provide(new UnsavedQuery("Consulta1.sql", "tienda", null, "SELECT 'Ñ'"), new UnsavedQuery("informe.sql", "ventas", @"C:\sql\informe.sql", "SELECT 2"));
        Assert.Equal(2, RecoveryStore.SaveNow());

        Assert.Equal(new[] { "01 Consulta1.sql", "02 informe.sql" }, Files());
        string first = File.ReadAllText(Path.Combine(RecoveryStore.Folder, "01 Consulta1.sql"));
        Assert.Contains("-- Conexión: tienda", first);
        Assert.Contains("-- Origen: consulta sin guardar", first);
        Assert.EndsWith("SELECT 'Ñ'", first);
        Assert.Contains(@"-- Origen: C:\sql\informe.sql", File.ReadAllText(Path.Combine(RecoveryStore.Folder, "02 informe.sql")));
    }

    [Fact]
    public void Al_cambiar_las_consultas_se_escribe_lo_nuevo_y_se_retira_lo_que_sobra()
    {
        Provide(new UnsavedQuery("uno.sql", "c", null, "SELECT 1"), new UnsavedQuery("dos.sql", "c", null, "SELECT 2"));
        RecoveryStore.SaveIfChanged();
        Provide(new UnsavedQuery("dos.sql", "c", null, "SELECT 22"));
        RecoveryStore.SaveIfChanged();

        Assert.Equal(new[] { "01 dos.sql" }, Files());
        Assert.Contains("SELECT 22", File.ReadAllText(Path.Combine(RecoveryStore.Folder, "01 dos.sql")));
    }

    [Fact]
    public void Sin_cambios_no_se_reescribe_y_sin_consultas_no_queda_ningun_archivo()
    {
        Provide(new UnsavedQuery("uno.sql", "c", null, "SELECT 1"));
        RecoveryStore.SaveIfChanged();
        string file = Path.Combine(RecoveryStore.Folder, "01 uno.sql");
        var stamp = new DateTime(2020, 1, 1);
        File.SetLastWriteTimeUtc(file, stamp);

        RecoveryStore.SaveIfChanged();
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));

        Provide();
        RecoveryStore.SaveIfChanged();
        Assert.Empty(Files());
    }

    [Fact]
    public void Los_caracteres_que_no_valen_en_un_nombre_de_archivo_se_sustituyen()
    {
        Provide(new UnsavedQuery("a/b:c*?.sql", "c", null, "SELECT 1"));
        RecoveryStore.SaveNow();
        Assert.Equal(new[] { "01 a_b_c__.sql" }, Files());
    }

    [Fact]
    public void Al_salir_con_normalidad_se_borra_la_copia()
    {
        Provide(new UnsavedQuery("uno.sql", "c", null, "SELECT 1"));
        RecoveryStore.SaveNow();
        RecoveryStore.Clear();
        Assert.False(Directory.Exists(RecoveryStore.Folder));
    }

    [Fact]
    public void Al_arrancar_se_recogen_las_copias_de_una_ejecucion_que_no_termino_bien()
    {
        // Carpeta de un proceso que ya no existe (un identificador de proceso imposible).
        string dead = Path.Combine(Path.GetDirectoryName(RecoveryStore.Folder)!, "999999999");
        Directory.CreateDirectory(dead);
        File.WriteAllText(Path.Combine(dead, "01 perdida.sql"), "SELECT 1");
        File.WriteAllText(Path.Combine(dead, "02 otra.sql"), "SELECT 2");

        var found = RecoveryStore.CollectFromCrashes();

        Assert.NotNull(found);
        Assert.Equal(2, found.Value.Count);
        Assert.True(File.Exists(Path.Combine(found.Value.Folder, "01 perdida.sql")));
        Assert.False(Directory.Exists(dead));
        // No se vuelve a avisar de las mismas.
        Assert.Null(RecoveryStore.CollectFromCrashes());
    }

    [Fact]
    public void La_copia_de_esta_ejecucion_no_se_toma_por_una_caida()
    {
        Provide(new UnsavedQuery("mia.sql", "c", null, "SELECT 1"));
        RecoveryStore.SaveNow();
        Assert.Null(RecoveryStore.CollectFromCrashes());
        Assert.Equal(new[] { "01 mia.sql" }, Files());
    }
}
