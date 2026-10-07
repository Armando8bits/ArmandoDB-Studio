using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace MySmdb.Tests;

/// <summary>Valores escritos como literales SQL: lo que usan la copia de seguridad y "Guardar resultados como .sql".</summary>
public class SqlLiteralTests
{
    [Theory]
    [InlineData(DbKind.MySql)]
    [InlineData(DbKind.Sqlite)]
    [InlineData(DbKind.SqlServer)]
    [InlineData(DbKind.Sybase)]
    public void Nulos_numeros_y_booleanos_son_iguales_en_todos_los_motores(DbKind kind)
    {
        Assert.Equal("NULL", ResultExporter.SqlLiteral(null, kind));
        Assert.Equal("42", ResultExporter.SqlLiteral(42, kind));
        Assert.Equal("-7", ResultExporter.SqlLiteral(-7L, kind));
        Assert.Equal("10.50", ResultExporter.SqlLiteral(10.50m, kind));
        Assert.Equal("1.5", ResultExporter.SqlLiteral(1.5, kind));
        Assert.Equal("1", ResultExporter.SqlLiteral(true, kind));
        Assert.Equal("0", ResultExporter.SqlLiteral(false, kind));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Un_numero_no_finito_va_como_NULL(double value)
    {
        Assert.Equal("NULL", ResultExporter.SqlLiteral(value, DbKind.MySql));
    }

    [Fact]
    public void Los_decimales_no_dependen_del_idioma_del_equipo()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");   // coma decimal
            Assert.Equal("1234.5", ResultExporter.SqlLiteral(1234.5m, DbKind.MySql));
            Assert.Equal("0.25", ResultExporter.SqlLiteral(0.25, DbKind.SqlServer));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(DbKind.MySql, "'O''Brien'")]
    [InlineData(DbKind.Sqlite, "'O''Brien'")]
    [InlineData(DbKind.Sybase, "'O''Brien'")]
    [InlineData(DbKind.SqlServer, "N'O''Brien'")]
    public void El_apostrofo_se_duplica_y_SqlServer_usa_cadenas_Unicode(DbKind kind, string expected)
    {
        Assert.Equal(expected, ResultExporter.SqlLiteral("O'Brien", kind));
    }

    [Theory]
    [InlineData(DbKind.MySql, @"'C:\\datos'")]     // en MySQL la barra invertida es un escape
    [InlineData(DbKind.Sqlite, @"'C:\datos'")]
    [InlineData(DbKind.SqlServer, @"N'C:\datos'")]
    [InlineData(DbKind.Sybase, @"'C:\datos'")]
    public void La_barra_invertida_solo_se_escapa_en_MySql(DbKind kind, string expected)
    {
        Assert.Equal(expected, ResultExporter.SqlLiteral(@"C:\datos", kind));
    }

    [Theory]
    [InlineData(DbKind.MySql, "X'01FF'")]
    [InlineData(DbKind.Sqlite, "X'01FF'")]
    [InlineData(DbKind.SqlServer, "0x01FF")]
    [InlineData(DbKind.Sybase, "0x01FF")]
    public void Binarios_segun_el_motor(DbKind kind, string expected)
    {
        Assert.Equal(expected, ResultExporter.SqlLiteral(new byte[] { 1, 255 }, kind));
    }

    [Fact]
    public void Fechas_segun_el_motor_y_con_la_precision_que_tengan()
    {
        var seconds = new DateTime(2026, 1, 2, 3, 4, 5);
        Assert.Equal("'2026-01-02 03:04:05'", ResultExporter.SqlLiteral(seconds, DbKind.MySql));
        Assert.Equal("'2026-01-02 03:04:05'", ResultExporter.SqlLiteral(seconds, DbKind.Sybase));
        // SQL Server: formato ISO con "T", que no depende del idioma de la sesión.
        Assert.Equal("'2026-01-02T03:04:05'", ResultExporter.SqlLiteral(seconds, DbKind.SqlServer));

        var millis = seconds.AddMilliseconds(678);
        Assert.Equal("'2026-01-02 03:04:05.678000'", ResultExporter.SqlLiteral(millis, DbKind.MySql));
        Assert.Equal("'2026-01-02 03:04:05.678'", ResultExporter.SqlLiteral(millis, DbKind.Sybase));
        Assert.Equal("'2026-01-02T03:04:05.678'", ResultExporter.SqlLiteral(millis, DbKind.SqlServer));
    }

    [Fact]
    public void SqlServer_guid_y_hora()
    {
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("'11111111-2222-3333-4444-555555555555'", ResultExporter.SqlLiteral(guid, DbKind.SqlServer));
        Assert.Equal("'01:02:03.0000000'", ResultExporter.SqlLiteral(new TimeSpan(1, 2, 3), DbKind.SqlServer));
    }
}

/// <summary>Guardar un resultado en archivo: CSV, texto, JSON, Excel y sentencias INSERT.</summary>
public class ResultExporterTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private static readonly string[] Columns = { "id", "nombre", "nota", "fecha", "bin" };

    private static readonly byte[] Binary = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();

    private static List<object?[]> Rows() => new()
    {
        new object?[] { 1, "Ana Ñandú", "con, coma y \"comillas\"", new DateTime(2026, 1, 2, 3, 4, 5), Binary },
        new object?[] { 2, "O'Brien", "dos\nlíneas", null, null },
        new object?[] { 3, null, "", new DateTime(2026, 12, 31), new byte[] { 1, 2 } },
    };

    private string Export(string fileName, DbKind kind = DbKind.MySql, string? sourceTable = "cliente", string[]? columns = null, List<object?[]>? rows = null)
    {
        string path = _folder.File(fileName);
        ResultExporter.Export(path, columns ?? Columns, rows ?? Rows(), sourceTable, kind, NoProgress.Rows, CancellationToken.None);
        return path;
    }

    public void Dispose() => _folder.Dispose();

    // ---------- CSV / TXT ----------

    [Fact]
    public void Csv_con_encabezados_comillas_donde_hacen_falta_y_BOM_para_Excel()
    {
        string path = Export("r.csv");
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());

        string text = File.ReadAllText(path);
        Assert.StartsWith("id,nombre,nota,fecha,bin", text);
        Assert.Contains("1,Ana Ñandú,\"con, coma y \"\"comillas\"\"\",2026-01-02 03:04:05,", text);
        Assert.Contains("2,O'Brien,\"dos\nlíneas\",,", text);   // NULL: campo vacío
    }

    [Fact]
    public void Csv_lo_que_se_exporta_se_puede_volver_a_importar_igual()
    {
        var source = DataImporter.ReadCsv(Export("ida_y_vuelta.csv"), DataImporter.AutoDelimiter, firstRowHeaders: true);

        Assert.Equal(Columns, source.Headers);
        Assert.Equal(3, source.Rows.Count);
        Assert.Equal("con, coma y \"comillas\"", source.Rows[0][2]);
        Assert.Equal("dos\nlíneas", source.Rows[1][2]);
        Assert.Equal("Ana Ñandú", source.Rows[0][1]);
    }

    [Fact]
    public void Txt_separado_por_tabuladores_sin_saltos_dentro_de_los_campos()
    {
        string[] lines = File.ReadAllLines(Export("r.txt"));
        Assert.Equal(4, lines.Length);   // encabezado + 3 filas: el salto de línea del dato no parte la fila
        Assert.Equal("id\tnombre\tnota\tfecha\tbin", lines[0]);
        Assert.StartsWith("2\tO'Brien\tdos líneas\tNULL\tNULL", lines[2]);
    }

    [Theory]
    [InlineData("r.csv")]
    [InlineData("r.txt")]
    public void Los_binarios_van_completos_no_abreviados_como_en_pantalla(string fileName)
    {
        string text = File.ReadAllText(Export(fileName));
        Assert.Contains("0x" + Convert.ToHexString(Binary), text);
        Assert.DoesNotContain("...", text);
    }

    // ---------- JSON ----------

    [Fact]
    public void Json_valido_con_tipos_y_acentos_legibles()
    {
        string path = Export("r.json");
        Assert.Contains("Ana Ñandú", File.ReadAllText(path));   // sin \u00D1

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var items = document.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(1, items[0].GetProperty("id").GetInt32());
        Assert.Equal("2026-01-02T03:04:05", items[0].GetProperty("fecha").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("fecha").ValueKind);
        Assert.Equal("dos\nlíneas", items[1].GetProperty("nota").GetString());
        Assert.Equal("0x0102", items[2].GetProperty("bin").GetString());
    }

    [Fact]
    public void Json_las_columnas_con_el_mismo_nombre_se_numeran()
    {
        string path = Export("repetidas.json", columns: new[] { "id", "id", "ID" }, rows: new() { new object?[] { 1, 2, 3 } });
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var names = document.RootElement[0].EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "id", "id_2", "ID_3" }, names);
    }

    // ---------- Excel ----------

    private static XDocument Sheet(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        return XDocument.Load(stream);
    }

    [Fact]
    public void Excel_es_un_libro_valido_que_la_propia_aplicacion_sabe_leer()
    {
        var source = DataImporter.ReadXlsx(Export("r.xlsx"), firstRowHeaders: true);

        Assert.Equal(Columns, source.Headers);
        Assert.Equal(3, source.Rows.Count);
        Assert.Equal(1L, source.Rows[0][0]);                                  // número como número
        Assert.Equal("Ana Ñandú", source.Rows[0][1]);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), source.Rows[0][3]);   // fecha como fecha
        Assert.Equal("dos\nlíneas", source.Rows[1][2]);
    }

    [Fact]
    public void Excel_los_enteros_demasiado_largos_van_como_texto_para_no_perder_digitos()
    {
        string path = Export("largos.xlsx", columns: new[] { "n" }, rows: new() { new object?[] { 1234567890123456789L }, new object?[] { 12345L } });
        var source = DataImporter.ReadXlsx(path, firstRowHeaders: true);
        Assert.Equal("1234567890123456789", source.Rows[0][0]);
        Assert.Equal(12345L, source.Rows[1][0]);
    }

    [Fact]
    public void Excel_un_caracter_no_valido_no_impide_escribir_el_archivo()
    {
        string bad = "ok\u0001\uFFFFmal" + '\uD800' + "fin";
        string path = Export("raros.xlsx", columns: new[] { "t" }, rows: new() { new object?[] { bad } });
        Assert.Equal("okmalfin", DataImporter.ReadXlsx(path, firstRowHeaders: true).Rows[0][0]);
    }

    [Fact]
    public void Excel_una_fecha_anterior_a_1900_va_como_texto()
    {
        string path = Export("antiguas.xlsx", columns: new[] { "f" }, rows: new() { new object?[] { new DateTime(1850, 5, 6, 7, 8, 9) } });
        Assert.Equal("1850-05-06 07:08:09", DataImporter.ReadXlsx(path, firstRowHeaders: true).Rows[0][0]);
    }

    [Fact]
    public void Excel_las_columnas_pasan_de_la_Z_a_la_AA()
    {
        string[] columns = Enumerable.Range(1, 30).Select(i => "c" + i).ToArray();
        var row = Enumerable.Range(1, 30).Select(i => (object?)i).ToArray();
        string path = Export("anchas.xlsx", columns: columns, rows: new() { row });

        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var references = Sheet(path).Descendants(main + "row").First().Elements(main + "c").Select(c => (string)c.Attribute("r")!).ToList();
        Assert.Equal("Z1", references[25]);
        Assert.Equal("AA1", references[26]);
        Assert.Equal(30L, DataImporter.ReadXlsx(path, true).Rows[0][29]);
    }

    // ---------- INSERT ----------

    [Fact]
    public void Inserts_para_la_tabla_de_origen_con_las_comillas_del_motor()
    {
        string mysql = File.ReadAllText(Export("m.sql", DbKind.MySql));
        Assert.Contains("INSERT INTO `cliente` (`id`, `nombre`, `nota`, `fecha`, `bin`) VALUES", mysql);
        Assert.Contains("(2, 'O''Brien', 'dos\nlíneas', NULL, NULL)", mysql);
        Assert.Contains("X'0102'", mysql);

        string sqlServer = File.ReadAllText(Export("s.sql", DbKind.SqlServer));
        Assert.Contains("INSERT INTO [cliente] ([id], [nombre], [nota], [fecha], [bin]) VALUES", sqlServer);
        Assert.Contains("(2, N'O''Brien', N'dos\nlíneas', NULL, NULL)", sqlServer);
        Assert.Contains("0x0102", sqlServer);
    }

    [Fact]
    public void Inserts_sin_tabla_de_origen_usan_un_nombre_a_reemplazar_y_lo_avisan()
    {
        string sql = File.ReadAllText(Export("sin_tabla.sql", sourceTable: null));
        Assert.Contains("INSERT INTO `tabla_destino`", sql);
        Assert.Contains("cambia `tabla_destino` por la tabla real", sql);
    }

    [Fact]
    public void Inserts_en_bloques_de_500_filas_que_SQLite_ejecuta_tal_cual()
    {
        var rows = Enumerable.Range(1, 1203).Select(i => new object?[] { i, "n" + i }).ToList();
        string path = Export("muchos.sql", DbKind.Sqlite, "t", new[] { "id", "n" }, rows);
        var statements = SqlSplitter.Split(File.ReadAllText(path), DbKind.Sqlite);
        Assert.Equal(3, statements.Count);   // 500 + 500 + 203
    }

    // ---------- General ----------

    [Fact]
    public void Una_extension_desconocida_se_guarda_como_CSV()
    {
        Assert.StartsWith("id,nombre", File.ReadAllText(Export("r.dat")));
    }

    [Fact]
    public void Un_resultado_sin_filas_deja_solo_los_encabezados()
    {
        Assert.Equal("id,nombre,nota,fecha,bin", File.ReadAllText(Export("vacio.csv", rows: new())).Trim());
        Assert.Empty(DataImporter.ReadXlsx(Export("vacio.xlsx", rows: new()), firstRowHeaders: true).Rows);
    }

    [Fact]
    public void Se_puede_cancelar_a_mitad()
    {
        var rows = Enumerable.Range(1, 10_000).Select(i => new object?[] { i }).ToList();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ResultExporter.Export(_folder.File("cancelado.csv"), new[] { "id" }, rows, null, DbKind.MySql, NoProgress.Rows, cancellation.Token));
    }
}

/// <summary>Cómo se muestra un valor en la cuadrícula.</summary>
public class CellTextTests
{
    [Fact]
    public void Nulo_y_binarios()
    {
        Assert.Equal("NULL", CellText.Format(null));
        Assert.Equal("0x01FF", CellText.Format(new byte[] { 1, 255 }));
        // En pantalla los binarios largos se abrevian; al exportar van completos.
        Assert.EndsWith("...", CellText.Format(new byte[100]));
        Assert.Equal(2 + 64 * 2 + 3, CellText.Format(new byte[100]).Length);
    }

    [Fact]
    public void Las_fechas_muestran_la_precision_que_tienen()
    {
        var date = new DateTime(2026, 1, 2, 3, 4, 5);
        Assert.Equal("2026-01-02 03:04:05", CellText.Format(date));
        Assert.Equal("2026-01-02 03:04:05.120", CellText.Format(date.AddMilliseconds(120)));
        Assert.Equal("2026-01-02 03:04:05.1234567", CellText.Format(date.AddTicks(1234567)));
        Assert.Equal("2026-01-02 00:00:00", CellText.Format(new DateTime(2026, 1, 2)));
    }

    [Fact]
    public void Los_numeros_no_dependen_del_idioma_del_equipo()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            Assert.Equal("1234.5", CellText.Format(1234.5m));
            Assert.Equal("0.1", CellText.Format(0.1));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}

/// <summary>Lectura y escritura de archivos .sql con la codificación que traigan.</summary>
public class TextFilesTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private const string Sample = "SELECT 'año, niño, café'";

    public void Dispose() => _folder.Dispose();

    private string Write(string name, byte[] bytes)
    {
        string path = _folder.File(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Utf8_sin_BOM()
    {
        string text = TextFiles.Read(Write("a.sql", new UTF8Encoding(false).GetBytes(Sample)), out var encoding);
        Assert.Equal(Sample, text);
        Assert.Empty(encoding.GetPreamble());
        Assert.Equal(65001, encoding.CodePage);
    }

    [Fact]
    public void Utf8_con_BOM_lo_conserva_al_guardar()
    {
        var withBom = new UTF8Encoding(true);
        string text = TextFiles.Read(Write("b.sql", withBom.GetPreamble().Concat(withBom.GetBytes(Sample)).ToArray()), out var encoding);
        Assert.Equal(Sample, text);
        Assert.Equal(3, encoding.GetPreamble().Length);
    }

    [Fact]
    public void Ansi_Windows_1252_se_lee_con_sus_acentos()
    {
        string text = TextFiles.Read(Write("c.sql", Encoding.GetEncoding(1252).GetBytes(Sample)), out var encoding);
        Assert.Equal(Sample, text);
        Assert.Equal(1252, encoding.CodePage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16_en_ambos_ordenes_de_bytes(bool bigEndian)
    {
        var utf16 = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        string text = TextFiles.Read(Write("d.sql", utf16.GetPreamble().Concat(utf16.GetBytes(Sample)).ToArray()), out _);
        Assert.Equal(Sample, text);
    }

    [Fact]
    public void Un_archivo_vacio_es_texto_vacio()
    {
        Assert.Equal("", TextFiles.Read(Write("vacio.sql", Array.Empty<byte>()), out _));
    }

    [Fact]
    public void Se_sabe_si_un_texto_cabe_en_la_codificacion_original()
    {
        var ansi = Encoding.GetEncoding(1252);
        Assert.True(TextFiles.CanEncode("año, niño, café €", ansi));
        Assert.False(TextFiles.CanEncode("日本", ansi));
        Assert.True(TextFiles.CanEncode("日本", new UTF8Encoding(false)));
    }
}
