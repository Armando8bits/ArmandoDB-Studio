namespace MySmdb.Tests;

/// <summary>Explorador de objetos sobre una base SQLite real (un archivo temporal).</summary>
public class SqliteCatalogTests : IDisposable
{
    private readonly SqliteDb _db = new();

    private const string Schema = @"
        CREATE TABLE cliente (
            id INTEGER PRIMARY KEY,
            nombre TEXT NOT NULL,
            correo TEXT UNIQUE,
            saldo REAL DEFAULT 0
        );
        CREATE TABLE pedido (
            id INTEGER PRIMARY KEY,
            cliente_id INTEGER NOT NULL REFERENCES cliente(id),
            total NUMERIC,
            nota TEXT
        );
        CREATE TABLE linea (
            pedido_id INTEGER NOT NULL,
            numero INTEGER NOT NULL,
            producto TEXT,
            PRIMARY KEY (pedido_id, numero),
            FOREIGN KEY (pedido_id) REFERENCES pedido(id)
        );
        CREATE INDEX ix_pedido_cliente ON pedido (cliente_id, total);
        CREATE VIEW v_resumen AS SELECT c.nombre, COUNT(p.id) AS pedidos FROM cliente c LEFT JOIN pedido p ON p.cliente_id = c.id GROUP BY c.id;
        CREATE TRIGGER tr_pedido AFTER INSERT ON pedido BEGIN
            UPDATE cliente SET saldo = saldo + NEW.total WHERE id = NEW.cliente_id;
        END;";

    public SqliteCatalogTests() => _db.RunAsync(Schema).GetAwaiter().GetResult();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task La_unica_base_se_llama_main()
    {
        Assert.Equal(new[] { "main" }, await Db.ListDatabasesAsync(_db.Profile));
    }

    [Fact]
    public async Task Tablas_y_vistas()
    {
        var objects = await Db.ListTablesAsync(_db.Profile, "main");
        Assert.Equal(new[] { "cliente", "linea", "pedido" }, objects.Where(o => !o.IsView).Select(o => o.Name).ToArray());
        Assert.Equal(new[] { "v_resumen" }, objects.Where(o => o.IsView).Select(o => o.Name).ToArray());
    }

    [Fact]
    public async Task Columnas_con_tipo_clave_y_nulabilidad()
    {
        var columns = await Db.GetColumnsAsync(_db.Profile, "main", "cliente");

        Assert.Equal(new[] { "id", "nombre", "correo", "saldo" }, columns.Select(c => c.Name).ToArray());
        // La clave INTEGER única es el rowid: autonumérica y nunca nula.
        Assert.Equal(new ColumnInfo("id", "INTEGER", PrimaryKey: true, Nullable: false, AutoIncrement: true), columns[0]);
        Assert.Equal(new ColumnInfo("nombre", "TEXT", false, Nullable: false, false), columns[1]);
        Assert.Equal(new ColumnInfo("correo", "TEXT", false, Nullable: true, false), columns[2]);
    }

    [Fact]
    public async Task Una_clave_primaria_compuesta_no_es_autonumerica()
    {
        var columns = await Db.GetColumnsAsync(_db.Profile, "main", "linea");
        Assert.Equal(new[] { "pedido_id", "numero" }, columns.Where(c => c.PrimaryKey).Select(c => c.Name).ToArray());
        Assert.DoesNotContain(columns, c => c.AutoIncrement);
    }

    [Fact]
    public async Task Texto_de_una_columna_para_el_arbol()
    {
        var columns = await Db.GetColumnsAsync(_db.Profile, "main", "cliente");
        Assert.Equal("id (INTEGER, PK, not null)", columns[0].ToString());
        Assert.Equal("correo (TEXT, null)", columns[2].ToString());
    }

    [Fact]
    public async Task Indices_propios_y_los_de_las_restricciones()
    {
        var pedido = await Db.ListIndexesAsync(_db.Profile, "main", "pedido");
        var index = Assert.Single(pedido);
        Assert.Equal(new IndexInfo("ix_pedido_cliente", Unique: false, Primary: false, "cliente_id, total"), index);

        var cliente = await Db.ListIndexesAsync(_db.Profile, "main", "cliente");
        Assert.Contains(cliente, i => i.Unique && i.Columns == "correo");
    }

    [Fact]
    public async Task Triggers_con_su_tabla_y_sin_rutinas()
    {
        Assert.Equal(new[] { ("tr_pedido", "pedido") }, await Db.ListTriggersAsync(_db.Profile, "main"));
        Assert.Empty(await Db.ListRoutinesAsync(_db.Profile, "main"));
    }

    [Fact]
    public async Task Todas_las_tablas_con_sus_columnas_para_el_diagrama()
    {
        var tables = await Db.GetAllTablesAsync(_db.Profile, "main");
        Assert.Equal(new[] { "cliente", "linea", "pedido" }, tables.Select(t => t.Table).ToArray());
        Assert.Equal(new[] { "id", "cliente_id", "total", "nota" }, tables.Single(t => t.Table == "pedido").Columns.Select(c => c.Name).ToArray());
        Assert.True(tables.Single(t => t.Table == "cliente").Columns[0].PrimaryKey);
    }

    [Fact]
    public async Task Claves_foraneas_para_el_diagrama()
    {
        var keys = await Db.ListForeignKeysAsync(_db.Profile, "main");
        Assert.Contains(new ForeignKey("pedido", "cliente_id", "cliente", "id"), keys);
        Assert.Contains(new ForeignKey("linea", "pedido_id", "pedido", "id"), keys);
        Assert.Equal(2, keys.Count);
    }

    [Theory]
    [InlineData(SchemaObject.Table, "cliente", null, "CREATE TABLE cliente")]
    [InlineData(SchemaObject.View, "v_resumen", null, "CREATE VIEW v_resumen")]
    [InlineData(SchemaObject.Trigger, "tr_pedido", "pedido", "CREATE TRIGGER tr_pedido")]
    [InlineData(SchemaObject.Index, "ix_pedido_cliente", "pedido", "CREATE INDEX ix_pedido_cliente")]
    public async Task Script_CREATE_de_cada_tipo_de_objeto(SchemaObject kind, string name, string? table, string start)
    {
        string script = await Db.GetCreateScriptAsync(_db.Profile, "main", kind, name, table);
        Assert.StartsWith(start, script);
        Assert.EndsWith(";", script);
    }

    [Fact]
    public async Task El_script_generado_recrea_el_objeto()
    {
        string script = await Db.GetCreateScriptAsync(_db.Profile, "main", SchemaObject.Table, "cliente");

        using var copy = new SqliteDb("copia.db");
        await copy.RunAsync(script);
        Assert.Equal(await Db.GetColumnsAsync(_db.Profile, "main", "cliente"), await Db.GetColumnsAsync(copy.Profile, "main", "cliente"));
    }

    [Fact]
    public async Task Un_error_de_SQL_trae_su_codigo_y_cuenta_como_error_de_base_de_datos()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => _db.QueryAsync("SELECT * FROM no_existe"));
        Assert.True(Db.IsDatabaseError(error));
        Assert.Equal(1, Db.ErrorCode(error));   // SQLITE_ERROR
        Assert.Contains("no_existe", error.Message);
    }

    [Fact]
    public async Task Los_valores_de_las_consultas_internas_no_dependen_del_idioma_del_equipo()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");   // coma decimal
            Assert.Equal("17.5", await _db.ScalarAsync("SELECT 17.5"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Los_parametros_evitan_problemas_con_las_comillas()
    {
        await _db.RunAsync("CREATE TABLE \"mi 'tabla'\" (a INTEGER);");
        Assert.Single(await Db.GetColumnsAsync(_db.Profile, "main", "mi 'tabla'"));
    }
}

/// <summary>Copia de seguridad a .sql y restauración, de extremo a extremo con SQLite.</summary>
public class BackupRestoreTests : IDisposable
{
    private readonly SqliteDb _db = new();

    private const string Setup = @"
        CREATE TABLE cliente (id INTEGER PRIMARY KEY, nombre TEXT NOT NULL, saldo REAL, foto BLOB, alta TEXT);
        CREATE TABLE pedido (id INTEGER PRIMARY KEY, cliente_id INTEGER REFERENCES cliente(id), total NUMERIC);
        CREATE INDEX ix_pedido_cliente ON pedido (cliente_id);
        CREATE VIEW v_total AS SELECT cliente_id, SUM(total) AS total FROM pedido GROUP BY cliente_id;
        CREATE TRIGGER tr_borrar AFTER DELETE ON cliente BEGIN
            DELETE FROM pedido WHERE cliente_id = OLD.id;
        END;
        INSERT INTO cliente (nombre, saldo, foto, alta) VALUES
            ('Ana Ñandú 日本', 10.5, X'00FF10', '2026-01-02 03:04:05'),
            ('O''Brien; con punto y coma', -3, NULL, NULL),
            ('Línea
partida', 0, X'', '2025-12-31');
        INSERT INTO pedido (cliente_id, total) VALUES (1, 100), (1, 50.25), (2, 7);";

    private static readonly BackupOptions Everything = new(new[] { "cliente", "pedido" }, Structure: true, Data: true, DropFirst: true, OtherObjects: true);

    public BackupRestoreTests() => _db.RunAsync(Setup).GetAwaiter().GetResult();

    public void Dispose() => _db.Dispose();

    private async Task<string> Backup(BackupOptions options, string name = "copia.sql")
    {
        string path = _db.File(name);
        await DatabaseBackup.ExportAsync(_db.Profile, "main", options, path, NoProgress.Backup, CancellationToken.None);
        return path;
    }

    private static async Task<RestoreSummary> Restore(SqliteDb target, string path) =>
        await DatabaseBackup.RestoreAsync(target.Profile, "main", DatabaseBackup.ReadScript(path, DbKind.Sqlite), NoProgress.Backup, CancellationToken.None);

    /// <summary>Todo el contenido de la base como texto, para comparar original y copia.</summary>
    private static async Task<string> Dump(SqliteDb db)
    {
        var lines = new List<string>();
        foreach (string table in new[] { "cliente", "pedido" })
            foreach (var row in await db.QueryAsync($"SELECT * FROM {table} ORDER BY id"))
                lines.Add(table + ": " + string.Join(" | ", row.Select(v => v ?? "NULL")));
        lines.Add("vista: " + string.Join(", ", (await db.QueryAsync("SELECT cliente_id, total FROM v_total ORDER BY 1")).Select(r => $"{r[0]}={r[1]}")));
        lines.Add("foto: " + await db.ScalarAsync("SELECT group_concat(COALESCE(hex(foto), 'NULL')) FROM cliente"));
        return string.Join("\n", lines);
    }

    [Fact]
    public async Task El_resumen_cuenta_tablas_filas_y_demas_objetos()
    {
        string path = _db.File("resumen.sql");
        var summary = await DatabaseBackup.ExportAsync(_db.Profile, "main", Everything, path, NoProgress.Backup, CancellationToken.None);
        Assert.Equal(new BackupSummary(Tables: 2, Rows: 6, OtherObjects: 2), summary);   // vista + trigger
    }

    [Fact]
    public async Task Restaurar_en_una_base_vacia_deja_una_copia_identica()
    {
        string path = await Backup(Everything);
        using var copy = new SqliteDb("destino.db");

        var result = await Restore(copy, path);

        Assert.Null(result.Error);
        Assert.Equal(result.Total, result.Executed);
        Assert.Equal(await Dump(_db), await Dump(copy));
        // También la estructura: índices y triggers.
        Assert.Equal(await Db.ListIndexesAsync(_db.Profile, "main", "pedido"), await Db.ListIndexesAsync(copy.Profile, "main", "pedido"));
        Assert.Equal(await Db.ListTriggersAsync(_db.Profile, "main"), await Db.ListTriggersAsync(copy.Profile, "main"));
    }

    [Fact]
    public async Task Restaurar_encima_devuelve_la_base_a_como_estaba_en_la_copia()
    {
        string path = await Backup(Everything);
        string before = await Dump(_db);

        await _db.RunAsync("DELETE FROM pedido; UPDATE cliente SET nombre = 'cambiado'; INSERT INTO cliente (nombre) VALUES ('nuevo');");
        Assert.NotEqual(before, await Dump(_db));

        Assert.Null((await Restore(_db, path)).Error);
        Assert.Equal(before, await Dump(_db));
    }

    [Fact]
    public async Task Una_copia_solo_de_estructura_no_lleva_datos()
    {
        string path = await Backup(Everything with { Data = false });
        Assert.DoesNotContain("INSERT INTO", File.ReadAllText(path));

        using var copy = new SqliteDb("solo_estructura.db");
        Assert.Null((await Restore(copy, path)).Error);
        Assert.Equal("0", await copy.ScalarAsync("SELECT COUNT(*) FROM cliente"));
        Assert.Equal(5, (await Db.GetColumnsAsync(copy.Profile, "main", "cliente")).Count);
    }

    [Fact]
    public async Task Una_copia_solo_de_datos_no_recrea_las_tablas()
    {
        string text = File.ReadAllText(await Backup(Everything with { Structure = false, OtherObjects = false }));
        Assert.DoesNotContain("CREATE TABLE", text);
        Assert.DoesNotContain("DROP TABLE", text);
        Assert.Contains("INSERT INTO `cliente`", text);
    }

    [Fact]
    public async Task Solo_se_copian_las_tablas_elegidas()
    {
        string text = File.ReadAllText(await Backup(Everything with { Tables = new[] { "pedido" }, OtherObjects = false }));
        Assert.Contains("CREATE TABLE pedido", text);
        Assert.DoesNotContain("CREATE TABLE cliente", text);
    }

    [Fact]
    public async Task El_archivo_es_un_script_normal_que_se_puede_leer_como_sentencias()
    {
        string path = await Backup(Everything);
        var statements = DatabaseBackup.ReadScript(path, DbKind.Sqlite);

        Assert.Contains(statements, s => s.Text.StartsWith("DROP TABLE IF EXISTS `cliente`"));
        Assert.Contains(statements, s => s.Text.StartsWith("CREATE TRIGGER tr_borrar") && s.Text.EndsWith("END"));
        // El punto y coma dentro de un dato no parte la sentencia.
        Assert.Contains(statements, s => s.Text.Contains("'O''Brien; con punto y coma'"));
    }

    [Fact]
    public async Task Si_el_script_falla_se_indica_la_linea_y_no_queda_nada_a_medias()
    {
        string path = _db.File("roto.sql");
        File.WriteAllText(path, "BEGIN TRANSACTION;\nCREATE TABLE nueva (a INTEGER);\nINSERT INTO nueva VALUES (1);\nINSERT INTO no_existe VALUES (1);\nCOMMIT;");

        var result = await Restore(_db, path);

        Assert.Equal(3, result.Executed);
        Assert.Equal(5, result.Total);
        Assert.Equal(4, result.ErrorLine);
        Assert.Contains("no_existe", result.Error);
        // La transacción abierta se deshace al cerrar la conexión: la tabla "nueva" no llegó a existir.
        Assert.DoesNotContain(await Db.ListTablesAsync(_db.Profile, "main"), t => t.Name == "nueva");
    }

    [Fact]
    public async Task Se_puede_cancelar()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseBackup.ExportAsync(_db.Profile, "main", Everything, _db.File("cancelada.sql"), NoProgress.Backup, cancellation.Token));
    }
}
