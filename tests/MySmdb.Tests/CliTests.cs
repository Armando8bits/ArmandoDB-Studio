using System.Text.Json;

namespace MySmdb.Tests;

/// <summary>Modo de línea de comandos (armandodb.exe), llamando a lo mismo que llama el ejecutable.</summary>
[Collection("Ejecución de consultas")]   // usa el historial y las conexiones guardadas de la carpeta de pruebas
public class CliTests : IAsyncLifetime
{
    private readonly SqliteDb _db = new();
    private readonly string _name = "cli-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _production = "cli-prod-" + Guid.NewGuid().ToString("N")[..8];
    private readonly TempFolder _folder = new();
    private readonly string _previousHistory = QueryHistory.FilePath;

    public async Task InitializeAsync()
    {
        await _db.RunAsync("CREATE TABLE cliente (id INTEGER PRIMARY KEY, nombre TEXT, saldo REAL, foto BLOB);" +
                           "INSERT INTO cliente VALUES (1, 'Ana Ñandú', 10.5, x'00FF'), (2, 'O''Brien, \"Bob\"', NULL, NULL), (3, 'Línea\npartida', -3, NULL);");
        // Dos conexiones guardadas que apuntan a la misma base: una normal y otra marcada como producción.
        ProfileStore.Save(new ConnectionProfile { Kind = DbKind.Sqlite, FilePath = _db.Profile.FilePath, Database = "main", Alias = _name }, rememberPassword: false);
        ProfileStore.Save(new ConnectionProfile { Kind = DbKind.Sqlite, FilePath = _db.Profile.FilePath, Database = "main", Alias = _production, IsProduction = true }, rememberPassword: false);
        QueryHistory.FilePath = _folder.File("historial.jsonl");
        QueryHistory.Reset();
    }

    public Task DisposeAsync()
    {
        ProfileStore.Delete(_name);
        ProfileStore.Delete(_production);
        QueryHistory.FilePath = _previousHistory;
        QueryHistory.Reset();
        _folder.Dispose();
        _db.Dispose();
        return Task.CompletedTask;
    }

    private sealed record Run(int Exit, string Output, string Error);

    private static async Task<Run> Cli(string? stdin, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exit = await CliRunner.RunAsync(args, output, error, new StringReader(stdin ?? ""));
        return new Run(exit, output.ToString(), error.ToString());
    }

    private Task<Run> Query(string sql, params string[] more) =>
        Cli(null, new[] { "consulta", "--conexion", _name, "--sql", sql }.Concat(more).ToArray());

    [Fact]
    public async Task Sin_argumentos_o_con_ayuda_explica_el_uso()
    {
        var help = await Cli(null);
        Assert.Equal(CliRunner.Ok, help.Exit);
        Assert.Contains("armandodb consulta --conexion", help.Output);
        Assert.Equal(help.Output, (await Cli(null, "ayuda")).Output);
        Assert.Equal(help.Output, (await Cli(null, "--help")).Output);
    }

    [Theory]
    [InlineData("borrar")]
    [InlineData("consulta", "--sql", "SELECT 1")]                                   // falta la conexión
    [InlineData("consulta", "--conexion", "x", "--inventada", "1")]
    [InlineData("consulta", "--conexion", "x", "--sql")]                            // opción sin valor
    [InlineData("consulta", "--conexion", "x", "--sql", "SELECT 1", "--formato", "xml")]
    [InlineData("consulta", "--conexion", "x", "--sql", "SELECT 1", "--max-filas", "muchas")]
    [InlineData("consulta", "--conexion", "x", "--sql", "SELECT 1", "--archivo", "a.sql")]
    public async Task Un_uso_incorrecto_lo_dice_por_la_salida_de_error(params string[] args)
    {
        var run = await Cli(null, args);
        Assert.Equal(CliRunner.UsageError, run.Exit);
        Assert.Equal("", run.Output);
        Assert.StartsWith("Error:", run.Error);
    }

    [Fact]
    public async Task Lista_las_conexiones_guardadas()
    {
        var json = JsonDocument.Parse((await Cli(null, "conexiones", "--formato", "json")).Output).RootElement;
        var mine = json.EnumerateArray().Single(c => c.GetProperty("nombre").GetString() == _name);
        Assert.Equal("SQLite", mine.GetProperty("motor").GetString());
        Assert.False(mine.GetProperty("produccion").GetBoolean());
        Assert.True(json.EnumerateArray().Single(c => c.GetProperty("nombre").GetString() == _production).GetProperty("produccion").GetBoolean());
        // Nada de contraseñas en la lista.
        Assert.DoesNotContain("assword", json.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var table = await Cli(null, "conexiones");
        Assert.Contains(_name, table.Output);
        Assert.StartsWith("Nombre", table.Output);
    }

    [Fact]
    public async Task Una_consulta_devuelve_JSON_con_los_tipos_de_cada_valor()
    {
        var run = await Query("SELECT id, nombre, saldo, foto FROM cliente ORDER BY id");
        Assert.Equal(CliRunner.Ok, run.Exit);
        Assert.Equal("", run.Error);

        var json = JsonDocument.Parse(run.Output).RootElement;
        Assert.Equal(_name, json.GetProperty("conexion").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("filasAfectadas").ValueKind);
        var result = json.GetProperty("resultados")[0];
        Assert.Equal(new[] { "id", "nombre", "saldo", "foto" }, result.GetProperty("columnas").EnumerateArray().Select(c => c.GetString()).ToArray());
        Assert.Equal(3, result.GetProperty("totalFilas").GetInt32());
        Assert.False(result.GetProperty("truncado").GetBoolean());

        var rows = result.GetProperty("filas");
        Assert.Equal(1, rows[0].GetProperty("id").GetInt32());
        Assert.Equal("Ana Ñandú", rows[0].GetProperty("nombre").GetString());
        Assert.Equal(10.5, rows[0].GetProperty("saldo").GetDouble());
        Assert.Equal("0x00FF", rows[0].GetProperty("foto").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("saldo").ValueKind);
        Assert.Equal("Línea\npartida", rows[2].GetProperty("nombre").GetString());
        Assert.Contains("Ñandú", run.Output);   // los acentos van legibles, no como Ñ
    }

    [Fact]
    public async Task Tambien_en_CSV_y_como_tabla()
    {
        var csv = await Query("SELECT id, nombre, saldo FROM cliente ORDER BY id", "--formato", "csv");
        Assert.Equal(new[] { "id,nombre,saldo", "1,Ana Ñandú,10.5", "2,\"O'Brien, \"\"Bob\"\"\",", "3,\"Línea", "partida\",-3" },
            csv.Output.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'));

        var table = await Query("SELECT id, nombre FROM cliente WHERE id <= 2 ORDER BY id", "--formato", "tabla");
        string[] lines = table.Output.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("id  nombre", lines[0]);
        Assert.StartsWith("--  ---", lines[1]);
        Assert.Equal("1   Ana Ñandú", lines[2]);
        Assert.Contains("(2 filas)", table.Output);
    }

    [Fact]
    public async Task Un_script_con_varias_sentencias_da_varios_resultados()
    {
        var json = JsonDocument.Parse((await Query("SELECT 1 AS a; SELECT 'x' AS b, 'y' AS c;")).Output).RootElement;
        Assert.Equal(2, json.GetProperty("resultados").GetArrayLength());
        Assert.Equal("y", json.GetProperty("resultados")[1].GetProperty("filas")[0].GetProperty("c").GetString());
    }

    [Fact]
    public async Task La_consulta_puede_venir_de_un_archivo_o_de_la_entrada_estandar()
    {
        string file = _folder.File("consulta.sql");
        File.WriteAllText(file, "-- nota\nSELECT COUNT(*) AS n FROM cliente;");
        var fromFile = await Cli(null, "consulta", "--conexion", _name, "--archivo", file, "--formato", "csv");
        Assert.Equal("n\n3\n", fromFile.Output.ReplaceLineEndings("\n"));

        var fromInput = await Cli("SELECT COUNT(*) AS n FROM cliente WHERE id > 1", "query", "--connection", _name.ToUpperInvariant(), "--format=csv");
        Assert.Equal("n\n2\n", fromInput.Output.ReplaceLineEndings("\n"));

        // PowerShell antepone una marca invisible (BOM) a lo que pasa por una tubería.
        var withMark = await Cli("﻿SELECT COUNT(*) AS n FROM cliente", "consulta", "--conexion", _name, "--formato", "csv");
        Assert.Equal((CliRunner.Ok, "n\n3\n"), (withMark.Exit, withMark.Output.ReplaceLineEndings("\n")));

        Assert.Equal(CliRunner.UsageError, (await Cli("  -- solo un comentario", "consulta", "--conexion", _name)).Exit);
        Assert.Equal(CliRunner.UsageError, (await Cli(null, "consulta", "--conexion", _name, "--archivo", _folder.File("no-existe.sql"))).Exit);
    }

    [Fact]
    public async Task El_tope_de_filas_corta_el_resultado_y_lo_indica()
    {
        var run = await Query("SELECT id FROM cliente ORDER BY id", "--max-filas", "2");
        var result = JsonDocument.Parse(run.Output).RootElement.GetProperty("resultados")[0];
        Assert.Equal(2, result.GetProperty("filas").GetArrayLength());
        Assert.True(result.GetProperty("truncado").GetBoolean());

        var csv = await Query("SELECT id FROM cliente ORDER BY id", "--max-filas", "2", "--formato", "csv");
        Assert.Equal("id\n1\n2\n", csv.Output.ReplaceLineEndings("\n"));
        Assert.Contains("truncó a 2 filas", csv.Error);
    }

    [Theory]
    [InlineData("DELETE FROM cliente")]
    [InlineData("UPDATE cliente SET saldo = 0 WHERE id = 1")]
    [InlineData("SELECT 1; DROP TABLE cliente;")]
    [InlineData("WITH c AS (SELECT 1) DELETE FROM cliente")]
    [InlineData("INSERT INTO cliente (nombre) VALUES ('x')")]
    public async Task Sin_permiso_explicito_solo_se_aceptan_lecturas(string sql)
    {
        var run = await Query(sql);
        Assert.Equal(CliRunner.Rejected, run.Exit);
        Assert.Equal("", run.Output);
        Assert.Contains("--permitir-escritura", run.Error);
        // Nada se ejecutó, ni siquiera el SELECT que iba delante.
        Assert.Equal("3", await _db.ScalarAsync("SELECT COUNT(*) FROM cliente"));
        Assert.Equal("10.5", await _db.ScalarAsync("SELECT saldo FROM cliente WHERE id = 1"));
        Assert.Empty(QueryHistory.Load());
    }

    [Fact]
    public async Task Con_permiso_escribe_y_dice_cuantas_filas_afecto()
    {
        var run = await Query("UPDATE cliente SET saldo = 0 WHERE id <= 2", "--permitir-escritura");
        Assert.Equal(CliRunner.Ok, run.Exit);
        Assert.Equal(2, JsonDocument.Parse(run.Output).RootElement.GetProperty("filasAfectadas").GetInt32());
        Assert.Equal("0", await _db.ScalarAsync("SELECT saldo FROM cliente WHERE id = 1"));
        Assert.Equal("-3", await _db.ScalarAsync("SELECT saldo FROM cliente WHERE id = 3"));   // la que no entraba en el WHERE
    }

    [Fact]
    public async Task En_una_conexion_de_produccion_no_se_escribe_ni_con_permiso()
    {
        var run = await Cli(null, "consulta", "--conexion", _production, "--sql", "DELETE FROM cliente", "--permitir-escritura");
        Assert.Equal(CliRunner.Rejected, run.Exit);
        Assert.Contains("producción", run.Error);
        Assert.Equal("3", await _db.ScalarAsync("SELECT COUNT(*) FROM cliente"));

        // Leer de producción sí se puede.
        Assert.Equal(CliRunner.Ok, (await Cli(null, "consulta", "--conexion", _production, "--sql", "SELECT 1")).Exit);
    }

    [Fact]
    public async Task Una_conexion_que_no_existe_o_que_no_abre_da_su_propio_codigo()
    {
        var unknown = await Cli(null, "consulta", "--conexion", "no-existe-" + Guid.NewGuid().ToString("N"), "--sql", "SELECT 1");
        Assert.Equal(CliRunner.ConnectionError, unknown.Exit);
        Assert.Contains("no hay ninguna conexión guardada", unknown.Error);
        Assert.Contains(_name, unknown.Error);   // dice cuáles hay
    }

    [Fact]
    public async Task Un_error_de_SQL_sale_por_la_salida_de_error_con_su_linea()
    {
        var run = await Query("SELECT 1;\nSELECT * FROM no_existe;");
        Assert.Equal(CliRunner.QueryError, run.Exit);
        Assert.Equal("", run.Output);
        Assert.StartsWith("Error", run.Error);
        Assert.Contains("línea 2", run.Error);
        Assert.Contains("no_existe", run.Error);
    }

    [Fact]
    public async Task Lo_ejecutado_queda_en_el_historial_marcado_como_CLI()
    {
        await Query("SELECT id FROM cliente");
        await Query("SELECT * FROM no_existe");

        var history = QueryHistory.Load();
        Assert.Equal(2, history.Count);
        Assert.All(history, entry => Assert.Equal("CLI", entry.Source));
        Assert.Equal((HistoryEntry.Ok, 3, _name + " · CLI"), (history[1].Outcome, history[1].Rows, history[1].ConnectionText));
        Assert.Equal(HistoryEntry.Failed, history[0].Outcome);
        Assert.Contains("no_existe", history[0].Error);
    }

    // ---------- Modo sesión ----------

    /// <summary>Abre una sesión, le envía esas líneas por la entrada estándar y devuelve lo que respondió, línea a línea.</summary>
    private async Task<(int Exit, string[] Lines, string Error)> Session(string[] input, params string[] options)
    {
        var run = await Cli(string.Join("\n", input) + "\n", new[] { "sesion", "--conexion", _name }.Concat(options).ToArray());
        return (run.Exit, run.Output.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'), run.Error);
    }

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement;

    [Fact]
    public async Task Una_sesion_responde_una_linea_JSON_por_consulta()
    {
        var (exit, lines, error) = await Session(new[]
        {
            "{\"sql\": \"SELECT COUNT(*) AS n FROM cliente\", \"id\": 1}",
            "",
            "{\"id\": \"segunda\", \"sql\": \"SELECT nombre FROM cliente WHERE id = 1\"}",
        });

        Assert.Equal(CliRunner.Ok, exit);
        Assert.Equal("", error);
        Assert.Equal(4, lines.Length);   // lista, dos respuestas y cerrada

        var ready = Parse(lines[0]);
        Assert.Equal(("lista", _name, true), (ready.GetProperty("sesion").GetString(), ready.GetProperty("conexion").GetString(), ready.GetProperty("soloLectura").GetBoolean()));

        var first = Parse(lines[1]);
        Assert.True(first.GetProperty("ok").GetBoolean());
        Assert.Equal(1, first.GetProperty("id").GetInt32());
        Assert.Equal(3, first.GetProperty("resultados")[0].GetProperty("filas")[0].GetProperty("n").GetInt32());

        var second = Parse(lines[2]);
        Assert.Equal("segunda", second.GetProperty("id").GetString());   // el id vuelve tal cual, sea número o texto
        Assert.Equal("Ana Ñandú", second.GetProperty("resultados")[0].GetProperty("filas")[0].GetProperty("nombre").GetString());

        Assert.Equal("cerrada", Parse(lines[3]).GetProperty("sesion").GetString());
    }

    [Fact]
    public async Task La_sesion_usa_una_sola_conexion_para_todas_las_consultas()
    {
        // Una tabla temporal solo existe en la conexión que la creó: si la segunda consulta la ve, es la misma.
        var (_, lines, _) = await Session(new[]
        {
            "{\"sql\": \"CREATE TEMP TABLE apuntes (n INTEGER); INSERT INTO apuntes VALUES (7), (8);\"}",
            "{\"sql\": \"SELECT SUM(n) AS total FROM apuntes\"}",
        }, "--permitir-escritura");

        Assert.Equal(2, Parse(lines[1]).GetProperty("filasAfectadas").GetInt32());
        Assert.Equal(15, Parse(lines[2]).GetProperty("resultados")[0].GetProperty("filas")[0].GetProperty("total").GetInt32());
        Assert.False(Parse(lines[0]).GetProperty("soloLectura").GetBoolean());
    }

    [Fact]
    public async Task Un_error_o_un_rechazo_no_cierran_la_sesion()
    {
        var (exit, lines, _) = await Session(new[]
        {
            "{\"id\": 1, \"sql\": \"SELECT * FROM no_existe\"}",
            "{\"id\": 2, \"sql\": \"DELETE FROM cliente\"}",
            "esto no es json ni sql",
            "GO",
            "{\"id\": 4, \"sql\": 5}",
            "{no es json",
            "{\"id\": 6, \"sql\": \"  -- nada\"}",
            "{\"id\": 7, \"sql\": \"SELECT COUNT(*) AS n FROM cliente\"}",
        });

        Assert.Equal(CliRunner.Ok, exit);
        var sqlError = Parse(lines[1]);
        Assert.Equal((false, CliRunner.QueryError), (sqlError.GetProperty("ok").GetBoolean(), sqlError.GetProperty("codigo").GetInt32()));
        Assert.Contains("no_existe", sqlError.GetProperty("error").GetString());
        Assert.False(sqlError.TryGetProperty("resultados", out _));

        var rejected = Parse(lines[2]);
        Assert.Equal(CliRunner.Rejected, rejected.GetProperty("codigo").GetInt32());
        Assert.Contains("DELETE FROM cliente", rejected.GetProperty("error").GetString());

        Assert.Equal(CliRunner.Rejected, Parse(lines[3]).GetProperty("codigo").GetInt32());     // texto que no se reconoce: no es una lectura
        Assert.Equal(CliRunner.UsageError, Parse(lines[4]).GetProperty("codigo").GetInt32());   // "sql" no es un texto
        Assert.Equal(CliRunner.UsageError, Parse(lines[5]).GetProperty("codigo").GetInt32());   // JSON mal formado
        Assert.Equal(CliRunner.UsageError, Parse(lines[6]).GetProperty("codigo").GetInt32());   // sin sentencias

        // Después de todo eso, la sesión sigue respondiendo y los datos están intactos.
        Assert.Equal(3, Parse(lines[7]).GetProperty("resultados")[0].GetProperty("filas")[0].GetProperty("n").GetInt32());
        Assert.Equal("3", await _db.ScalarAsync("SELECT COUNT(*) FROM cliente"));
    }

    [Fact]
    public async Task En_texto_cada_consulta_termina_con_GO_y_cada_respuesta_con_FIN()
    {
        var (_, lines, _) = await Session(new[]
        {
            "SELECT id",
            "FROM cliente",
            "WHERE id <= 2 ORDER BY id",
            "go",
            "SELECT * FROM no_existe",
            "GO",
            "salir",
            "SELECT 'no se llega a ejecutar'",
            "GO",
        }, "--formato", "csv");

        Assert.StartsWith("#FIN sesion lista: " + _name, lines[0]);
        Assert.Equal(new[] { "id", "1", "2" }, lines[1..4]);
        Assert.StartsWith("#FIN ok (2 filas, ", lines[4]);
        Assert.StartsWith("Error", lines[5]);
        Assert.Equal("#FIN error 4", lines[6]);
        Assert.StartsWith("#FIN sesion cerrada", lines[7]);
        Assert.Equal(8, lines.Length);
    }

    [Fact]
    public async Task Al_cerrarse_la_entrada_se_ejecuta_lo_que_quedaba_y_termina()
    {
        var (exit, lines, _) = await Session(new[] { "SELECT COUNT(*) AS n FROM cliente" }, "--formato", "csv");
        Assert.Equal(CliRunner.Ok, exit);
        Assert.Equal(new[] { "n", "3" }, lines[1..3]);
        Assert.StartsWith("#FIN ok", lines[3]);
        Assert.StartsWith("#FIN sesion cerrada", lines[4]);
    }

    /// <summary>Entrada por la que nunca llega nada, como un agente que se quedó parado.</summary>
    private sealed class SilentReader : TextReader
    {
        private readonly ManualResetEventSlim _never = new();
        public override string? ReadLine()
        {
            _never.Wait();
            return null;
        }
    }

    [Fact]
    public async Task Una_sesion_sin_actividad_se_cierra_sola()
    {
        var output = new StringWriter();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int exit = await CliRunner.RunAsync(new[] { "sesion", "--conexion", _name, "--inactividad", "1" }, output, new StringWriter(), new SilentReader());

        Assert.Equal(CliRunner.Ok, exit);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
        string[] lines = output.ToString().ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Assert.Equal("lista", Parse(lines[0]).GetProperty("sesion").GetString());
        Assert.Equal(("cerrada", "1 s de inactividad"), (Parse(lines[1]).GetProperty("sesion").GetString(), Parse(lines[1]).GetProperty("motivo").GetString()));
    }

    [Fact]
    public async Task La_sesion_tambien_protege_las_conexiones_de_produccion_y_deja_rastro_en_el_historial()
    {
        var run = await Cli("{\"sql\": \"UPDATE cliente SET saldo = 0\"}\n{\"sql\": \"SELECT 1 AS uno\"}\n",
            "sesion", "--conexion", _production, "--permitir-escritura");
        string[] lines = run.Output.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

        Assert.True(Parse(lines[0]).GetProperty("soloLectura").GetBoolean());   // aunque se pidió permiso de escritura
        Assert.Contains("producción", Parse(lines[1]).GetProperty("error").GetString());
        Assert.True(Parse(lines[2]).GetProperty("ok").GetBoolean());
        Assert.Equal("10.5", await _db.ScalarAsync("SELECT saldo FROM cliente WHERE id = 1"));

        // Solo lo que llegó a ejecutarse queda en el historial.
        var entry = Assert.Single(QueryHistory.Load());
        Assert.Equal(("SELECT 1 AS uno", "CLI"), (entry.Sql, entry.Source));
    }

    [Theory]
    [InlineData("sesion", "--sql", "SELECT 1")]                       // en sesión las consultas van por la entrada
    [InlineData("sesion", "--inactividad", "pronto")]
    [InlineData("consulta", "--sql", "SELECT 1", "--inactividad", "5")]
    public async Task Las_opciones_que_no_corresponden_al_comando_se_rechazan(params string[] args)
    {
        var run = await Cli("", args.Concat(new[] { "--conexion", _name }).ToArray());
        Assert.Equal(CliRunner.UsageError, run.Exit);
    }

    [Fact]
    public async Task Una_sesion_a_una_conexion_que_no_existe_no_llega_a_abrirse()
    {
        var run = await Cli("{\"sql\": \"SELECT 1\"}\n", "sesion", "--conexion", "no-existe-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(CliRunner.ConnectionError, run.Exit);
        Assert.Equal("", run.Output);
    }

    [LocalDbFact]
    public async Task Tambien_contra_SQL_Server_con_lotes_GO()
    {
        string name = "cli-mssql-" + Guid.NewGuid().ToString("N")[..8];
        ProfileStore.Save(new ConnectionProfile { Kind = DbKind.SqlServer, Host = LocalDb.Profile.Host, IntegratedSecurity = true, Database = "master", Alias = name }, rememberPassword: false);
        try
        {
            var run = await Cli(null, "consulta", "--conexion", name, "--sql", "DECLARE @n int = 2\nSELECT @n * 21 AS respuesta\nGO\nSELECT DB_NAME() AS base", "--formato", "csv");
            Assert.Equal(CliRunner.Ok, run.Exit);
            Assert.Equal("respuesta\n42\n\nbase\nmaster\n", run.Output.ReplaceLineEndings("\n"));

            Assert.Equal(CliRunner.Rejected, (await Cli(null, "consulta", "--conexion", name, "--sql", "EXEC sp_who")).Exit);
            Assert.Equal(CliRunner.Rejected, (await Cli(null, "consulta", "--conexion", name, "--sql", "SELECT name INTO #copia FROM sys.objects")).Exit);
        }
        finally
        {
            ProfileStore.Delete(name);
        }
    }
}
