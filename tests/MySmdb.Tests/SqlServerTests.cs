using System.Text.RegularExpressions;

namespace MySmdb.Tests;

/// <summary>
/// SQL Server local para las pruebas: (localdb)\MSSQLLocalDB, que viene con Visual Studio y SQL Server Express.
/// Si no está instalado, las pruebas que lo necesitan se omiten (no fallan).
/// </summary>
public static class LocalDb
{
    public static readonly ConnectionProfile Profile = new()
    {
        Kind = DbKind.SqlServer, Host = @"(localdb)\MSSQLLocalDB", IntegratedSecurity = true, Alias = "pruebas-localdb",
    };

    private static readonly Lazy<string?> Unavailable = new(() =>
    {
        try
        {
            Db.QueryAsync(Profile, "master", "SELECT 1").GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            return "LocalDB no disponible: " + ex.Message.Split('\n')[0];
        }
    });

    /// <summary>null si se puede usar; si no, el motivo.</summary>
    public static string? WhyUnavailable => Unavailable.Value;

    public static Task<List<string?[]>> Query(string database, string sql) => Db.QueryAsync(Profile, database, sql);

    public static async Task<string> Scalar(string database, string sql) => (await Query(database, sql))[0][0] ?? "";

    /// <summary>Ejecuta un script con lotes GO.</summary>
    public static async Task Run(string database, string script)
    {
        foreach (var batch in SqlSplitter.Split(script, DbKind.SqlServer))
            await Query(database, batch.Text);
    }

    public static async Task<string> CreateDatabase(string prefix)
    {
        string name = $"{prefix}_{Guid.NewGuid():N}"[..40];
        await Query("master", $"CREATE DATABASE [{name}]");
        return name;
    }

    public static async Task DropDatabase(string name)
    {
        try
        {
            await Query("master", $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
        }
        catch
        {
            // Una base temporal que no se pudo borrar no debe hacer fallar las pruebas.
        }
    }
}

/// <summary>Prueba que necesita LocalDB: se omite si no está disponible.</summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public LocalDbFactAttribute()
    {
        if (LocalDb.WhyUnavailable is { } reason) Skip = reason;
    }
}

/// <summary>Una base temporal con tablas, vista, función, procedimiento y trigger, compartida por las pruebas de lectura.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public string Database { get; private set; } = "";

    public const string Setup = @"
CREATE SCHEMA ventas;
GO
CREATE TABLE dbo.cliente (
    id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_cliente PRIMARY KEY,
    nombre NVARCHAR(80) NOT NULL,
    correo VARCHAR(120) NULL CONSTRAINT UQ_cliente_correo UNIQUE,
    saldo DECIMAL(12,2) NOT NULL CONSTRAINT DF_cliente_saldo DEFAULT (0),
    activo BIT NOT NULL CONSTRAINT DF_cliente_activo DEFAULT (1),
    alta DATETIME2(3) NOT NULL CONSTRAINT DF_cliente_alta DEFAULT (SYSDATETIME()),
    CONSTRAINT CK_cliente_saldo CHECK (saldo >= -1000)
);
GO
CREATE TABLE ventas.pedido (
    id INT IDENTITY(100,5) NOT NULL CONSTRAINT PK_pedido PRIMARY KEY,
    cliente_id INT NOT NULL CONSTRAINT FK_pedido_cliente FOREIGN KEY REFERENCES dbo.cliente(id) ON DELETE CASCADE,
    total DECIMAL(10,2) NOT NULL,
    iva AS (total * 0.21) PERSISTED,
    referencia UNIQUEIDENTIFIER NULL,
    adjunto VARBINARY(MAX) NULL,
    fecha DATE NULL,
    [select] NVARCHAR(20) NULL,
    version ROWVERSION
);
GO
CREATE INDEX IX_pedido_cliente ON ventas.pedido (cliente_id, fecha DESC) INCLUDE (total) WHERE total > 0;
GO
CREATE VIEW dbo.v_resumen AS
SELECT c.id, c.nombre, COUNT(p.id) AS pedidos, SUM(p.total) AS total
FROM dbo.cliente c LEFT JOIN ventas.pedido p ON p.cliente_id = c.id
GROUP BY c.id, c.nombre;
GO
CREATE FUNCTION dbo.f_doble (@x INT) RETURNS INT AS
BEGIN
    RETURN @x * 2;
END
GO
CREATE PROCEDURE ventas.p_pedidos @cliente INT AS
BEGIN
    SET NOCOUNT ON;
    SELECT id, total FROM ventas.pedido WHERE cliente_id = @cliente;
END
GO
CREATE TRIGGER dbo.trg_cliente ON dbo.cliente AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
END
GO
INSERT INTO dbo.cliente (nombre, correo, saldo) VALUES (N'Ana Ñandú 日本', 'ana@x.com', 10.5), (N'O''Brien', NULL, -3), (N'Línea
partida', 'c@x.com', 0);
INSERT INTO ventas.pedido (cliente_id, total, referencia, adjunto, fecha, [select]) VALUES
    (1, 100, NEWID(), 0x00FF10, '2026-01-15', N'a'), (1, 50.25, NULL, NULL, NULL, NULL), (2, 7, NEWID(), 0x, '2025-12-31', N'ñ');
GO
";

    public async Task InitializeAsync()
    {
        if (LocalDb.WhyUnavailable != null) return;
        Database = await LocalDb.CreateDatabase("armandodb_pruebas");
        await LocalDb.Run(Database, Setup);
    }

    public async Task DisposeAsync()
    {
        if (Database.Length > 0) await LocalDb.DropDatabase(Database);
    }
}

/// <summary>Explorador, scripts y copia de seguridad contra un SQL Server real.</summary>
public class SqlServerTests : IClassFixture<SqlServerFixture>
{
    private readonly string _db;
    private static ConnectionProfile Profile => LocalDb.Profile;

    public SqlServerTests(SqlServerFixture fixture) => _db = fixture.Database;

    [LocalDbFact]
    public async Task Las_bases_del_usuario_van_antes_que_las_del_sistema()
    {
        var databases = await Db.ListDatabasesAsync(Profile);
        Assert.Contains(_db, databases);
        Assert.True(databases.IndexOf(_db) < databases.IndexOf("master"));
        Assert.Equal(new[] { "master", "model", "msdb", "tempdb" }, databases.TakeLast(4).ToArray());
    }

    [LocalDbFact]
    public async Task Tablas_y_vistas_con_su_esquema()
    {
        var objects = await Db.ListTablesAsync(Profile, _db);
        Assert.Equal(new[] { "dbo.cliente", "ventas.pedido" }, objects.Where(o => !o.IsView).Select(o => o.Name).ToArray());
        Assert.Equal(new[] { "dbo.v_resumen" }, objects.Where(o => o.IsView).Select(o => o.Name).ToArray());
    }

    [LocalDbFact]
    public async Task Columnas_con_tipos_completos_clave_identidad_y_nulabilidad()
    {
        var columns = await Db.GetColumnsAsync(Profile, _db, "ventas.pedido");

        Assert.Equal(new[] { "id", "cliente_id", "total", "iva", "referencia", "adjunto", "fecha", "select", "version" }, columns.Select(c => c.Name).ToArray());
        Assert.Equal(new ColumnInfo("id", "int", PrimaryKey: true, Nullable: false, AutoIncrement: true), columns[0]);
        Assert.Equal("decimal(10,2)", columns[2].Type);
        Assert.Equal("varbinary(max)", columns[5].Type);
        Assert.Equal("nvarchar(20)", columns[7].Type);   // longitud en caracteres, no en bytes
        Assert.True(columns[4].Nullable);
        Assert.False(columns[1].PrimaryKey);
    }

    [LocalDbFact]
    public async Task Procedimientos_funciones_y_triggers()
    {
        var routines = await Db.ListRoutinesAsync(Profile, _db);
        Assert.Contains(("dbo.f_doble", true), routines);
        Assert.Contains(("ventas.p_pedidos", false), routines);
        Assert.Equal(2, routines.Count);

        Assert.Equal(new[] { ("dbo.trg_cliente", "dbo.cliente") }, await Db.ListTriggersAsync(Profile, _db));
    }

    [LocalDbFact]
    public async Task Indices_con_la_clave_primaria_primero()
    {
        var indexes = await Db.ListIndexesAsync(Profile, _db, "ventas.pedido");
        Assert.Equal(new IndexInfo("PK_pedido", Unique: true, Primary: true, "id"), indexes[0]);
        Assert.Equal(new IndexInfo("IX_pedido_cliente", Unique: false, Primary: false, "cliente_id, fecha"), indexes[1]);
    }

    [LocalDbFact]
    public async Task Tablas_y_claves_foraneas_para_el_diagrama()
    {
        var tables = await Db.GetAllTablesAsync(Profile, _db);
        Assert.Equal(new[] { "dbo.cliente", "ventas.pedido" }, tables.Select(t => t.Table).ToArray());
        Assert.Equal(9, tables[1].Columns.Count);

        Assert.Equal(new[] { new ForeignKey("ventas.pedido", "cliente_id", "dbo.cliente", "id") }, await Db.ListForeignKeysAsync(Profile, _db));
    }

    [LocalDbFact]
    public async Task Script_de_una_tabla_reconstruido_desde_el_catalogo()
    {
        string script = await Db.GetCreateScriptAsync(Profile, _db, SchemaObject.Table, "ventas.pedido");

        Assert.StartsWith("CREATE TABLE [ventas].[pedido] (", script);
        Assert.Contains("[id] int IDENTITY(100,5) NOT NULL", script);
        Assert.Contains("[iva] AS ([total]*(0.21)) PERSISTED", script);
        Assert.Contains("[select] nvarchar(20) NULL", script);                      // nombre que es palabra reservada
        Assert.Contains("CONSTRAINT [PK_pedido] PRIMARY KEY CLUSTERED ([id])", script);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_pedido_cliente] ON [ventas].[pedido] ([cliente_id], [fecha] DESC) INCLUDE ([total]) WHERE ([total]>(0));", script);
        Assert.Contains("ADD CONSTRAINT [FK_pedido_cliente] FOREIGN KEY ([cliente_id]) REFERENCES [dbo].[cliente] ([id]) ON DELETE CASCADE;", script);
        Assert.EndsWith("GO", script);
    }

    [LocalDbFact]
    public async Task Script_de_una_tabla_con_DEFAULT_UNIQUE_y_CHECK()
    {
        string script = await Db.GetCreateScriptAsync(Profile, _db, SchemaObject.Table, "dbo.cliente");
        Assert.Contains("CONSTRAINT [DF_cliente_saldo] DEFAULT ((0))", script);
        Assert.Contains("CONSTRAINT [UQ_cliente_correo] UNIQUE NONCLUSTERED ([correo])", script);
        Assert.Contains("CONSTRAINT [CK_cliente_saldo] CHECK ([saldo]>=(-1000))", script);
        Assert.Contains("[alta] datetime2(3) NOT NULL", script);
    }

    [LocalDbFact]
    public async Task El_script_de_una_tabla_se_puede_ejecutar_y_da_la_misma_tabla()
    {
        string script = await Db.GetCreateScriptAsync(Profile, _db, SchemaObject.Table, "dbo.cliente");
        string copy = await LocalDb.CreateDatabase("armandodb_script");
        try
        {
            await LocalDb.Run(copy, script);
            Assert.Equal(script, await Db.GetCreateScriptAsync(Profile, copy, SchemaObject.Table, "dbo.cliente"));
        }
        finally
        {
            await LocalDb.DropDatabase(copy);
        }
    }

    [LocalDbFact]
    public async Task Definicion_de_vistas_rutinas_triggers_e_indices()
    {
        Assert.StartsWith("CREATE VIEW dbo.v_resumen", await Db.GetCreateScriptAsync(Profile, _db, SchemaObject.View, "dbo.v_resumen"));
        Assert.Contains("@cliente INT", await Db.GetCreateScriptAsync(Profile, _db, SchemaObject.Procedure, "ventas.p_pedidos"));
        Assert.StartsWith("CREATE FUNCTION dbo.f_doble", await Db.GetCreateScriptAsync(Profile, _db, SchemaObject.Function, "dbo.f_doble"));
        Assert.StartsWith("CREATE TRIGGER dbo.trg_cliente", await Db.GetCreateScriptAsync(Profile, _db, SchemaObject.Trigger, "dbo.trg_cliente", "dbo.cliente"));
        Assert.StartsWith("CREATE INDEX [IX_pedido_cliente] ON [ventas].[pedido] ([cliente_id], [fecha]);",
            await Db.GetCreateScriptAsync(Profile, _db, SchemaObject.Index, "IX_pedido_cliente", "ventas.pedido"));
    }

    [LocalDbFact]
    public async Task Esquema_para_el_autocompletado_por_nombre_de_tabla_sin_esquema()
    {
        SchemaCache.Invalidate(Profile);
        var schema = await SchemaCache.GetAsync(Profile, _db);

        Assert.Equal(DbKind.SqlServer, schema.Kind);
        Assert.Equal(new[] { "cliente", "pedido", "v_resumen" }, schema.Tables.Keys.OrderBy(k => k).ToArray());
        Assert.Equal("ventas", schema.Schemas["pedido"]);       // las que no están en dbo recuerdan su esquema
        Assert.False(schema.Schemas.ContainsKey("cliente"));
        Assert.Equal(9, SqlCompletion.Suggestions("SELECT p. FROM [ventas].[pedido] p", 9, "p", schema).Count);
    }

    [LocalDbFact]
    public async Task Un_error_trae_su_numero_y_cuenta_como_error_de_base_de_datos()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => LocalDb.Query(_db, "SELECT * FROM no_existe"));
        Assert.True(Db.IsDatabaseError(error));
        Assert.Equal(208, Db.ErrorCode(error));   // Invalid object name
    }

    [LocalDbFact]
    public async Task Las_plantillas_generadas_son_SQL_valido_para_el_servidor()
    {
        var columns = await Db.GetColumnsAsync(Profile, _db, "ventas.pedido");
        string select = ScriptTemplates.Select(DbKind.SqlServer, Db.FullName(Profile, _db, "ventas.pedido"), columns);
        Assert.Equal(3, (await LocalDb.Query(_db, select)).Count);
    }

    [LocalDbFact]
    public async Task Copia_y_restauracion_dejan_los_datos_identicos()
    {
        string backup = Path.Combine(Path.GetTempPath(), $"armandodb_{Guid.NewGuid():N}.sql");
        string copy = await LocalDb.CreateDatabase("armandodb_copia");
        try
        {
            var options = new BackupOptions(new[] { "dbo.cliente", "ventas.pedido" }, Structure: true, Data: true, DropFirst: true, OtherObjects: true);
            var summary = await DatabaseBackup.ExportAsync(Profile, _db, options, backup, NoProgress.Backup, CancellationToken.None);
            Assert.Equal(new BackupSummary(Tables: 2, Rows: 6, OtherObjects: 4), summary);

            var batches = DatabaseBackup.ReadScript(backup, DbKind.SqlServer);
            var restore = await DatabaseBackup.RestoreAsync(Profile, copy, batches, NoProgress.Backup, CancellationToken.None);
            Assert.Null(restore.Error);
            // Otra vez encima: prueba el borrado previo, claves foráneas incluidas.
            restore = await DatabaseBackup.RestoreAsync(Profile, copy, batches, NoProgress.Backup, CancellationToken.None);
            Assert.Null(restore.Error);

            const string check =
                "SELECT (SELECT CAST(CHECKSUM_AGG(CHECKSUM(id, nombre, correo, saldo, activo, alta)) AS varchar(20)) FROM dbo.cliente) + '/' + " +
                "(SELECT CAST(CHECKSUM_AGG(CHECKSUM(id, cliente_id, total, iva, CAST(referencia AS varchar(40)), CAST(adjunto AS varbinary(100)), fecha, [select])) AS varchar(20)) FROM ventas.pedido) + '/' + " +
                "(SELECT CAST(COUNT(*) AS varchar(10)) FROM dbo.v_resumen) + '/' + CAST(dbo.f_doble(21) AS varchar(10))";
            string original = await LocalDb.Scalar(_db, check);
            Assert.EndsWith("/3/42", original);
            Assert.Equal(original, await LocalDb.Scalar(copy, check));

            // Unicode, apóstrofo y salto de línea intactos; identidades conservadas.
            string names = await LocalDb.Scalar(copy, "SELECT nombre + '|' FROM dbo.cliente ORDER BY id FOR XML PATH(''), TYPE");
            Assert.Contains("Ana Ñandú 日本|O'Brien|Línea", names);
            Assert.Equal("100,105,110", await LocalDb.Scalar(copy, "SELECT STUFF((SELECT ',' + CAST(id AS varchar(10)) FROM ventas.pedido ORDER BY id FOR XML PATH('')), 1, 1, '')"));

            // La estructura restaurada genera el mismo script.
            Assert.Equal(
                await Db.GetCreateScriptAsync(Profile, _db, SchemaObject.Table, "ventas.pedido"),
                await Db.GetCreateScriptAsync(Profile, copy, SchemaObject.Table, "ventas.pedido"));
        }
        finally
        {
            await LocalDb.DropDatabase(copy);
            try { File.Delete(backup); } catch { }
        }
    }

    [LocalDbFact]
    public async Task Importar_en_una_tabla_con_identidad_y_deshacer_si_una_fila_falla()
    {
        string copy = await LocalDb.CreateDatabase("armandodb_import");
        try
        {
            await LocalDb.Run(copy, "CREATE TABLE dbo.cliente (id INT IDENTITY(1,1) PRIMARY KEY, nombre NVARCHAR(80) NOT NULL, correo VARCHAR(120) NOT NULL UNIQUE, saldo DECIMAL(12,2) NULL);");
            var mapping = new (string, int)[] { ("nombre", 0), ("correo", 1), ("saldo", 2) };

            var ok = await DataImporter.ImportAsync(Profile, copy, "dbo.cliente", mapping,
                new List<object?[]> { new object?[] { "Importado Ñ", "a@x.com", 5.5 }, new object?[] { "Otro", "b@x.com", 7L } },
                emptyAsNull: true, deleteFirst: false, NoProgress.Backup, CancellationToken.None);
            Assert.Equal(new ImportResult(2, null, 0), ok);

            var failed = await DataImporter.ImportAsync(Profile, copy, "dbo.cliente", mapping,
                new List<object?[]> { new object?[] { "Bueno", "c@x.com", 1L }, new object?[] { "Repetido", "a@x.com", 1L } },
                emptyAsNull: true, deleteFirst: false, NoProgress.Backup, CancellationToken.None);
            Assert.Equal(2, failed.ErrorRow);
            Assert.Equal(0, failed.Inserted);
            Assert.Equal("2", await LocalDb.Scalar(copy, "SELECT COUNT(*) FROM dbo.cliente"));
        }
        finally
        {
            await LocalDb.DropDatabase(copy);
        }
    }

    [LocalDbFact]
    public async Task Las_bases_temporales_de_las_pruebas_llevan_un_prefijo_reconocible()
    {
        // Si una ejecución se interrumpe, lo que quede se identifica (y se puede borrar) por su nombre.
        Assert.Matches(new Regex("^armandodb_pruebas_[0-9a-f]+$"), _db);
        Assert.Equal(_db, await LocalDb.Scalar(_db, "SELECT DB_NAME()"));
    }
}
