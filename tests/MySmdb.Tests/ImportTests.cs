using System.Text;

namespace MySmdb.Tests;

/// <summary>Lectura de archivos CSV y de texto para importar.</summary>
public class CsvReaderTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Write(string content, Encoding? encoding = null, string name = "datos.csv")
    {
        string path = _folder.File(name);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private ImportSource Read(string content, string delimiter = DataImporter.AutoDelimiter, bool headers = true) =>
        DataImporter.ReadCsv(Write(content), delimiter, headers);

    [Theory]
    [InlineData("a,b,c\n1,2,3", "coma")]
    [InlineData("a;b;c\n1;2;3", "punto y coma")]
    [InlineData("a\tb\tc\n1\t2\t3", "tabulador")]
    [InlineData("a|b|c\n1|2|3", "barra vertical")]
    public void Detecta_el_separador_por_la_primera_linea(string content, string name)
    {
        var source = Read(content);
        Assert.Equal(new[] { "a", "b", "c" }, source.Headers);
        Assert.Equal(new object?[] { "1", "2", "3" }, Assert.Single(source.Rows));
        Assert.Contains("separador: " + name, source.Description);
    }

    [Fact]
    public void Un_separador_dentro_de_comillas_no_cuenta_para_detectarlo()
    {
        // La primera línea tiene más comas (entre comillas) que puntos y coma, pero el separador es el punto y coma.
        var source = Read("\"a,b,c,d\";e\n1;2");
        Assert.Equal(new[] { "a,b,c,d", "e" }, source.Headers);
    }

    [Fact]
    public void Campos_entre_comillas_con_separador_salto_de_linea_y_comillas_dobles()
    {
        var source = Read("id,texto\n1,\"con, coma\"\n2,\"dos\nlíneas\"\n3,\"dijo \"\"hola\"\"\"");
        Assert.Equal(new[] { "con, coma", "dos\nlíneas", "dijo \"hola\"" }, source.Rows.Select(r => r[1]).ToArray());
    }

    [Fact]
    public void Admite_finales_de_linea_de_Windows()
    {
        var source = Read("a,b\r\n1,2\r\n3,4\r\n");
        Assert.Equal(2, source.Rows.Count);
        Assert.Equal("4", source.Rows[1][1]);
    }

    [Fact]
    public void Las_lineas_en_blanco_no_son_filas()
    {
        Assert.Equal(2, Read("a,b\n1,2\n\n3,4\n\n\n").Rows.Count);
    }

    [Fact]
    public void Un_campo_vacio_es_texto_vacio_y_las_filas_cortas_se_respetan()
    {
        var source = Read("a,b,c\n1,,3\n4");
        Assert.Equal(new object?[] { "1", "", "3" }, source.Rows[0]);
        Assert.Equal(new object?[] { "4" }, source.Rows[1]);
    }

    [Fact]
    public void Sin_encabezados_las_columnas_se_numeran_y_la_primera_linea_es_un_dato()
    {
        var source = Read("1,2\n3,4", headers: false);
        Assert.Equal(new[] { "Columna 1", "Columna 2" }, source.Headers);
        Assert.Equal(2, source.Rows.Count);
    }

    [Fact]
    public void Un_encabezado_vacio_recibe_un_nombre_y_los_demas_se_recortan()
    {
        Assert.Equal(new[] { "id", "Columna 2", "nombre" }, Read("id,, nombre \n1,2,3").Headers);
    }

    [Fact]
    public void El_separador_se_puede_forzar()
    {
        var source = Read("a;b,c\n1;2,3", delimiter: ",");
        Assert.Equal(new[] { "a;b", "c" }, source.Headers);
    }

    [Fact]
    public void Lee_UTF8_con_BOM_sin_que_el_BOM_ensucie_el_primer_encabezado()
    {
        var source = DataImporter.ReadCsv(Write("país,año\nEspaña,2026", new UTF8Encoding(true)), DataImporter.AutoDelimiter, true);
        Assert.Equal("país", source.Headers[0]);
        Assert.Contains("UTF-8", source.Description);
    }

    [Fact]
    public void Lee_Windows_1252_cuando_no_es_UTF8_valido()
    {
        var source = DataImporter.ReadCsv(Write("país;año\nEspaña;2026", Encoding.GetEncoding(1252)), DataImporter.AutoDelimiter, true);
        Assert.Equal(new[] { "país", "año" }, source.Headers);
        Assert.Equal("España", source.Rows[0][0]);
        Assert.Contains("Windows-1252", source.Description);
    }

    [Fact]
    public void Lee_UTF16()
    {
        var source = DataImporter.ReadCsv(Write("país,año\nEspaña,2026", Encoding.Unicode), DataImporter.AutoDelimiter, true);
        Assert.Equal("España", source.Rows[0][0]);
    }

    [Fact]
    public void Un_archivo_vacio_no_tiene_filas_ni_columnas()
    {
        var source = Read("");
        Assert.Empty(source.Rows);
        Assert.Empty(source.Headers);
    }
}

/// <summary>Inserción de las filas importadas en una tabla (con SQLite): todo o nada.</summary>
public class ImportTests : IDisposable
{
    private readonly SqliteDb _db = new();

    public ImportTests()
    {
        _db.RunAsync("CREATE TABLE cliente (id INTEGER PRIMARY KEY, nombre TEXT NOT NULL, correo TEXT UNIQUE, saldo REAL);").GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    private static readonly (string Column, int SourceIndex)[] Mapping = { ("nombre", 0), ("correo", 1), ("saldo", 2) };

    private Task<ImportResult> Import(List<object?[]> rows, bool emptyAsNull = true, bool deleteFirst = false,
        IReadOnlyList<(string Column, int SourceIndex)>? mapping = null, CancellationToken token = default) =>
        DataImporter.ImportAsync(_db.Profile, "main", "cliente", mapping ?? Mapping, rows, emptyAsNull, deleteFirst, NoProgress.Backup, token);

    private async Task<int> Count() => int.Parse((await _db.ScalarAsync("SELECT COUNT(*) FROM cliente"))!);

    [Fact]
    public async Task Inserta_todas_las_filas()
    {
        var result = await Import(new() { new object?[] { "Ana Ñandú", "ana@x.com", 10.5 }, new object?[] { "O'Brien", "ob@x.com", 7L } });

        Assert.Equal(2, result.Inserted);
        Assert.Null(result.Error);
        Assert.Equal("Ana Ñandú|O'Brien", await _db.ScalarAsync("SELECT group_concat(nombre, '|') FROM (SELECT nombre FROM cliente ORDER BY id)"));
        Assert.Equal("17.5", await _db.ScalarAsync("SELECT SUM(saldo) FROM cliente"));
    }

    [Fact]
    public async Task Si_una_fila_falla_no_se_inserta_ninguna_y_se_dice_cual()
    {
        var rows = new List<object?[]>
        {
            new object?[] { "Uno", "a@x.com", 1L },
            new object?[] { "Dos", "b@x.com", 2L },
            new object?[] { "Repetido", "a@x.com", 3L },   // correo duplicado
            new object?[] { "Cuatro", "d@x.com", 4L },
        };
        var result = await Import(rows);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(3, result.ErrorRow);
        Assert.Contains("UNIQUE", result.Error);
        Assert.Equal(0, await Count());
    }

    [Fact]
    public async Task Un_fallo_no_deshace_lo_que_ya_habia_en_la_tabla()
    {
        await _db.RunAsync("INSERT INTO cliente (nombre, correo) VALUES ('Previo', 'previo@x.com');");
        var result = await Import(new() { new object?[] { "Nuevo", "n@x.com", 1L }, new object?[] { null, "sin@nombre.com", 1L } });   // nombre obligatorio

        Assert.Equal(2, result.ErrorRow);
        Assert.Equal("Previo", await _db.ScalarAsync("SELECT group_concat(nombre) FROM cliente"));
    }

    [Fact]
    public async Task Borrar_antes_vacia_la_tabla_y_si_falla_se_recupera_lo_borrado()
    {
        await _db.RunAsync("INSERT INTO cliente (nombre, correo) VALUES ('Viejo', 'v@x.com');");

        var failed = await Import(new() { new object?[] { null, "x@x.com", 1L } }, deleteFirst: true);
        Assert.NotNull(failed.Error);
        Assert.Equal("Viejo", await _db.ScalarAsync("SELECT nombre FROM cliente"));

        var ok = await Import(new() { new object?[] { "Nuevo", "n@x.com", 1L } }, deleteFirst: true);
        Assert.Equal(1, ok.Inserted);
        Assert.Equal("Nuevo", await _db.ScalarAsync("SELECT group_concat(nombre) FROM cliente"));
    }

    [Theory]
    [InlineData(true, "1")]    // el texto vacío se guarda como NULL
    [InlineData(false, "0")]   // o como cadena vacía
    public async Task El_texto_vacio_se_importa_como_NULL_si_asi_se_pide(bool emptyAsNull, string nulls)
    {
        await Import(new() { new object?[] { "Ana", "", 1L } }, emptyAsNull);
        Assert.Equal(nulls, await _db.ScalarAsync("SELECT COUNT(*) FROM cliente WHERE correo IS NULL"));
    }

    [Fact]
    public async Task Solo_se_insertan_las_columnas_emparejadas_y_en_cualquier_orden()
    {
        // El archivo trae (saldo, nombre); el correo no se importa.
        var mapping = new (string, int)[] { ("nombre", 1), ("saldo", 0) };
        await Import(new() { new object?[] { 99L, "Ana" } }, mapping: mapping);

        var row = (await _db.QueryAsync("SELECT nombre, correo, saldo FROM cliente"))[0];
        Assert.Equal(new[] { "Ana", null, "99" }, row);
    }

    [Fact]
    public async Task Una_fila_mas_corta_que_el_emparejamiento_deja_NULL_en_lo_que_falta()
    {
        await Import(new() { new object?[] { "Corta" } });
        var row = (await _db.QueryAsync("SELECT nombre, correo, saldo FROM cliente"))[0];
        Assert.Equal(new[] { "Corta", null, null }, row);
    }

    [Fact]
    public async Task Muchas_filas_van_en_varios_lotes()
    {
        var rows = Enumerable.Range(1, 2500).Select(i => new object?[] { "n" + i, $"c{i}@x.com", (long)i }).ToList();
        var result = await Import(rows);

        Assert.Equal(2500, result.Inserted);
        Assert.Equal(2500, await Count());
        Assert.Equal("3126250", await _db.ScalarAsync("SELECT CAST(SUM(saldo) AS INTEGER) FROM cliente"));
    }

    [Fact]
    public async Task El_error_de_una_fila_lejana_se_localiza_con_exactitud()
    {
        var rows = Enumerable.Range(1, 1000).Select(i => new object?[] { "n" + i, $"c{i}@x.com", (long)i }).ToList();
        rows[776] = new object?[] { "duplicado", "c5@x.com", 0L };
        var result = await Import(rows);

        Assert.Equal(777, result.ErrorRow);
        Assert.Equal(0, await Count());
    }

    [Fact]
    public async Task Cancelar_no_deja_filas_a_medias()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Import(new() { new object?[] { "Ana", "a@x.com", 1L } }, token: cancellation.Token));
        Assert.Equal(0, await Count());
    }
}
