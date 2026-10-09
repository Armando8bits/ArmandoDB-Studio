namespace MySmdb.Tests;

/// <summary>"Buscar en la base de datos" contra SQLite.</summary>
public class SchemaSearchSqliteTests : IAsyncLifetime
{
    private readonly SqliteDb _db = new();

    public async Task InitializeAsync() => await _db.RunAsync(@"
CREATE TABLE cliente (id INTEGER PRIMARY KEY, nombre TEXT NOT NULL, saldo_total REAL);
CREATE TABLE pedido (id INTEGER PRIMARY KEY, cliente_id INTEGER, total REAL, [100%] TEXT);
CREATE TABLE auditoria (id INTEGER PRIMARY KEY AUTOINCREMENT, nota TEXT);
CREATE VIEW v_resumen AS
SELECT c.id, c.nombre,
       SUM(p.total) AS importe_facturado
FROM cliente c LEFT JOIN pedido p ON p.cliente_id = c.id GROUP BY c.id;
CREATE TRIGGER trg_pedido AFTER INSERT ON pedido BEGIN
    INSERT INTO auditoria (nota) VALUES ('pedido nuevo');
END;");

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private async Task<List<SearchHit>> Search(string text, bool names = true, bool columns = true, bool code = true) =>
        (await SchemaSearch.SearchAsync(_db.Profile, "main", text, names, columns, code)).Hits;

    [Fact]
    public async Task Por_nombre_encuentra_tablas_vistas_y_triggers()
    {
        var hits = await Search("pedido", columns: false, code: false);
        Assert.Equal(new[]
        {
            new SearchHit(SchemaObject.Table, "pedido", null, SearchHit.InName, ""),
            new SearchHit(SchemaObject.Trigger, "trg_pedido", "pedido", SearchHit.InName, "en pedido"),
        }, hits);
        Assert.Equal(SchemaObject.View, Assert.Single(await Search("RESUMEN", columns: false, code: false)).Kind);   // sin distinguir mayúsculas
    }

    [Fact]
    public async Task Por_columna_dice_la_tabla_y_el_tipo()
    {
        var hits = await Search("saldo", names: false, code: false);
        Assert.Equal(new SearchHit(SchemaObject.Table, "cliente", null, SearchHit.InColumn, "saldo_total  REAL"), Assert.Single(hits));

        // Las columnas de las vistas también cuentan.
        Assert.Contains(await Search("importe", names: false, code: false), h => h.Kind == SchemaObject.View && h.Name == "v_resumen");
    }

    [Fact]
    public async Task Por_codigo_da_la_linea_donde_aparece()
    {
        var view = Assert.Single(await Search("importe_facturado", names: false, columns: false));
        Assert.Equal((SchemaObject.View, "v_resumen", SearchHit.InCode), (view.Kind, view.Name, view.Where));
        Assert.Equal("línea 3: SUM(p.total) AS importe_facturado", view.Detail);

        var trigger = Assert.Single(await Search("pedido nuevo", names: false, columns: false));
        Assert.Equal((SchemaObject.Trigger, "trg_pedido", "pedido"), (trigger.Kind, trigger.Name, trigger.Table));
        Assert.StartsWith("línea 2:", trigger.Detail);
    }

    [Fact]
    public async Task Los_comodines_de_LIKE_se_buscan_como_texto()
    {
        Assert.Equal("100%  TEXT", Assert.Single(await Search("0%", names: false, code: false)).Detail);
        // '_' no vale por "cualquier carácter": "v_r" está, "vxr" no; y "c_i" no encuentra "cliente".
        Assert.Single(await Search("v_r", columns: false, code: false));
        Assert.Empty(await Search("c_i", columns: false, code: false));
        Assert.Empty(await Search("%%"));
        Assert.Empty(await Search("!!"));
    }

    [Fact]
    public async Task Un_objeto_puede_coincidir_por_nombre_y_por_codigo()
    {
        var hits = await Search("resumen");
        Assert.Contains(hits, h => h.Where == SearchHit.InName && h.Name == "v_resumen");
        Assert.Contains(hits, h => h.Where == SearchHit.InCode && h.Name == "v_resumen");
        // Los nombres van antes que las columnas, y estas antes que el código.
        Assert.Equal(hits.OrderBy(h => h.Where == SearchHit.InName ? 0 : h.Where == SearchHit.InColumn ? 1 : 2).ToList(), hits);
    }

    [Fact]
    public async Task Sin_coincidencias_o_con_texto_demasiado_corto_no_devuelve_nada()
    {
        Assert.Empty(await Search("no_existe_en_ninguna_parte"));
        Assert.Empty(await Search("c"));
        Assert.Empty(await Search("   "));
        Assert.Empty(await Search("sqlite_sequence"));   // las tablas internas no son del usuario
    }

    [Fact]
    public async Task Con_demasiadas_coincidencias_se_corta_y_se_avisa()
    {
        await _db.RunAsync(string.Join("\n", Enumerable.Range(1, SchemaSearch.MaxResults + 20).Select(i => $"CREATE TABLE masiva_{i} (id INTEGER);")));
        var result = await SchemaSearch.SearchAsync(_db.Profile, "main", "masiva", columns: false, code: false);
        Assert.True(result.Truncated);
        Assert.Equal(SchemaSearch.MaxResults, result.Hits.Count);

        Assert.False((await SchemaSearch.SearchAsync(_db.Profile, "main", "cliente")).Truncated);
    }

    [Fact]
    public async Task El_resultado_sirve_para_pedir_el_script_del_objeto()
    {
        foreach (var hit in await Search("pedido"))
            Assert.NotEmpty(await Db.GetCreateScriptAsync(_db.Profile, "main", hit.Kind, hit.Name, hit.Table));
    }
}

/// <summary>La ventana de búsqueda y el salto al texto encontrado.</summary>
public class SchemaSearchWindowTests
{
    [Fact]
    public void La_ventana_busca_y_muestra_los_resultados() => Ui.Run(async () =>
    {
        using var db = new SqliteDb();
        await db.RunAsync("CREATE TABLE cliente (id INTEGER PRIMARY KEY, nombre TEXT); CREATE VIEW v_cliente AS SELECT nombre FROM cliente;");
        var owner = Ui.HiddenOwner();
        try
        {
            var window = new SearchDatabaseWindow(owner, db.Profile, "main", (_, _) => Task.CompletedTask) { ShowActivated = false };
            window.Show();
            await Ui.Settle();

            await window.SearchAsync("cliente");
            Assert.Equal(3, window.ResultCount);   // la tabla y la vista por nombre, y la vista por su código

            await window.SearchAsync("no_esta");
            Assert.Equal(0, window.ResultCount);
            await window.SearchAsync("x");          // demasiado corto: no busca ni falla
            window.Close();
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public void Al_abrir_un_resultado_se_localiza_el_texto_sin_seleccionarlo() => Ui.Run(() =>
    {
        var tab = new QueryTab(ConnectionProfile.Offline, null, "Consulta1.sql");
        tab.SetText("CREATE VIEW v AS\nSELECT Nombre\nFROM cliente;\n");

        Assert.Equal(2, tab.ShowFirst("nombre"));
        Assert.Equal(0, tab.ShowFirst("no está"));
        Assert.Equal(0, tab.ShowFirst(""));
        // Nada seleccionado: lo que se escriba después no sustituye texto del script.
        Assert.Equal("", tab.SqlEditor.SelectedText);

        tab.MoveCaretToEnd();
        Assert.Equal(tab.SqlEditor.Text.Length, tab.SqlEditor.CaretOffset);
        Assert.Equal(4, tab.SqlEditor.TextArea.Caret.Line);   // en la línea siguiente a la última del script
        tab.Close();
    });

    [Fact]
    public void Lo_que_se_escribe_tras_generar_un_script_se_puede_deshacer() => Ui.Run(() =>
    {
        var tab = new QueryTab(ConnectionProfile.Offline, null, "Consulta1.sql");
        tab.SetText("CREATE TABLE t (id INT);\n");
        tab.MoveCaretToEnd();
        Assert.False(tab.SqlEditor.CanUndo);   // el script generado es el punto de partida: no hay nada que deshacer

        tab.SqlEditor.TextArea.PerformTextInput("SELECT 1;");
        Assert.Equal("CREATE TABLE t (id INT);\nSELECT 1;", tab.SqlEditor.Text);
        Assert.True(tab.SqlEditor.CanUndo);
        tab.SqlEditor.Undo();
        Assert.Equal("CREATE TABLE t (id INT);\n", tab.SqlEditor.Text);
        tab.Close();
    });
}

/// <summary>"Buscar en la base de datos" contra SQL Server (LocalDB).</summary>
public class SchemaSearchSqlServerTests : IClassFixture<SqlServerFixture>
{
    private readonly string _db;

    public SchemaSearchSqlServerTests(SqlServerFixture fixture) => _db = fixture.Database;

    private async Task<List<SearchHit>> Search(string text, bool names = true, bool columns = true, bool code = true) =>
        (await SchemaSearch.SearchAsync(LocalDb.Profile, _db, text, names, columns, code)).Hits;

    [LocalDbFact]
    public async Task Por_nombre_encuentra_cada_tipo_de_objeto_con_su_esquema()
    {
        Assert.Equal(new SearchHit(SchemaObject.Table, "ventas.pedido", null, SearchHit.InName, ""),
            Assert.Single(await Search("pedido", columns: false, code: false), h => h.Kind == SchemaObject.Table));
        Assert.Equal(new SearchHit(SchemaObject.Procedure, "ventas.p_pedidos", null, SearchHit.InName, ""),
            Assert.Single(await Search("p_pedidos", columns: false, code: false)));
        Assert.Equal(new SearchHit(SchemaObject.Function, "dbo.f_doble", null, SearchHit.InName, ""),
            Assert.Single(await Search("F_DOBLE", columns: false, code: false)));
        Assert.Equal(new SearchHit(SchemaObject.View, "dbo.v_resumen", null, SearchHit.InName, ""),
            Assert.Single(await Search("v_resumen", columns: false, code: false)));
        Assert.Equal(new SearchHit(SchemaObject.Trigger, "dbo.trg_cliente", "dbo.cliente", SearchHit.InName, "en dbo.cliente"),
            Assert.Single(await Search("trg_cliente", columns: false, code: false)));
    }

    [LocalDbFact]
    public async Task Por_columna_dice_la_tabla_y_el_tipo()
    {
        var hits = await Search("cliente_id", names: false, code: false);
        Assert.Equal(new SearchHit(SchemaObject.Table, "ventas.pedido", null, SearchHit.InColumn, "cliente_id  int"), Assert.Single(hits));
    }

    [LocalDbFact]
    public async Task Por_codigo_busca_en_vistas_rutinas_y_triggers()
    {
        var procedure = Assert.Single(await Search("WHERE cliente_id = @cliente", names: false, columns: false));
        Assert.Equal((SchemaObject.Procedure, "ventas.p_pedidos"), (procedure.Kind, procedure.Name));
        Assert.Contains("SELECT id, total FROM ventas.pedido", procedure.Detail);

        Assert.Equal("dbo.f_doble", Assert.Single(await Search("@x * 2", names: false, columns: false)).Name);
        Assert.Contains(await Search("LEFT JOIN ventas.pedido", names: false, columns: false), h => h.Kind == SchemaObject.View);
        var trigger = Assert.Single(await Search("AFTER UPDATE", names: false, columns: false));
        Assert.Equal((SchemaObject.Trigger, "dbo.trg_cliente", "dbo.cliente"), (trigger.Kind, trigger.Name, trigger.Table));
    }

    [LocalDbFact]
    public async Task Los_comodines_de_LIKE_se_buscan_como_texto()
    {
        Assert.Empty(await Search("c_i", columns: false, code: false));     // '_' no es "cualquier carácter"
        Assert.Empty(await Search("[a-z]", columns: false, code: false));   // ni los corchetes un conjunto
        Assert.Empty(await Search("%%"));
    }

    [LocalDbFact]
    public async Task El_resultado_sirve_para_pedir_el_script_del_objeto()
    {
        var hits = await Search("cliente");
        Assert.NotEmpty(hits);
        foreach (var hit in hits)
            Assert.NotEmpty(await Db.GetCreateScriptAsync(LocalDb.Profile, _db, hit.Kind, hit.Name, hit.Table));
    }
}
