using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace MySmdb;

/// <summary>
/// Guarda un resultado en archivo. El formato sale de la extensión: .xlsx, .csv, .txt, .json o .sql (sentencias INSERT).
/// Se ejecuta fuera del hilo de la interfaz e informa el avance cada cierto número de filas.
/// </summary>
public static class ResultExporter
{
    private const int ProgressStep = 2000;
    private const int InsertBatch = 500;

    public static readonly string DialogFilter =
        "Excel (*.xlsx)|*.xlsx|CSV separado por comas (*.csv)|*.csv|Texto separado por tabuladores (*.txt)|*.txt|" +
        "JSON (*.json)|*.json|Sentencias INSERT (*.sql)|*.sql";

    public static void Export(string path, string[] columns, IReadOnlyList<object?[]> rows, string? sourceTable, DbKind kind,
        IProgress<int> progress, CancellationToken token)
    {
        void Step(int i)
        {
            if (i % ProgressStep != 0) return;
            token.ThrowIfCancellationRequested();
            progress.Report(i);
        }

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".xlsx": WriteXlsx(path, columns, rows, Step); break;
            case ".json": WriteJson(path, columns, rows, Step); break;
            case ".sql": WriteInserts(path, columns, rows, sourceTable, kind, Step); break;
            case ".txt": WriteDelimited(path, columns, rows, csv: false, Step); break;
            default: WriteDelimited(path, columns, rows, csv: true, Step); break;
        }
    }

    // ---------- CSV / TXT ----------

    private static void WriteDelimited(string path, string[] columns, IReadOnlyList<object?[]> rows, bool csv, Action<int> step)
    {
        string separator = csv ? "," : "\t";
        string Field(object? value)
        {
            if (csv)
            {
                if (value == null) return "";
                string text = CellText.Format(value);
                return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
            }
            return CellText.Format(value).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        // Con BOM, para que Excel reconozca los acentos.
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(separator, columns.Select(c => Field(c))));
        for (int i = 0; i < rows.Count; i++)
        {
            step(i);
            writer.WriteLine(string.Join(separator, rows[i].Select(Field)));
        }
    }

    // ---------- Excel (.xlsx) ----------

    /// <summary>
    /// Libro de Excel mínimo (Office Open XML) escrito a mano: una hoja "Resultados", encabezado en negrita
    /// y fijado, números como números y fechas como fechas.
    /// </summary>
    private static void WriteXlsx(string path, string[] columns, IReadOnlyList<object?[]> rows, Action<int> step)
    {
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string OfficeRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        void Part(string name, string xml)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + xml);
        }

        Part("[Content_Types].xml",
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>");
        Part("_rels/.rels",
            $"<Relationships xmlns=\"{Rels}\"><Relationship Id=\"rId1\" Type=\"{OfficeRels}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Part("xl/workbook.xml",
            $"<workbook xmlns=\"{Main}\" xmlns:r=\"{OfficeRels}\"><sheets><sheet name=\"Resultados\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels",
            $"<Relationships xmlns=\"{Rels}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{OfficeRels}/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{OfficeRels}/styles\" Target=\"styles.xml\"/></Relationships>");
        // Estilos: 0 normal, 1 negrita (encabezado), 2 fecha y hora.
        Part("xl/styles.xml",
            $"<styleSheet xmlns=\"{Main}\">" +
            "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd hh:mm:ss\"/></numFmts>" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>" +
            "</styleSheet>");

        using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        xml.WriteStartDocument(true);
        xml.WriteStartElement("worksheet", Main);
        xml.WriteStartElement("sheetViews");
        xml.WriteStartElement("sheetView");
        xml.WriteAttributeString("workbookViewId", "0");
        xml.WriteStartElement("pane");   // encabezado fijo al desplazarse
        xml.WriteAttributeString("ySplit", "1");
        xml.WriteAttributeString("topLeftCell", "A2");
        xml.WriteAttributeString("activePane", "bottomLeft");
        xml.WriteAttributeString("state", "frozen");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("sheetData");

        string[] letters = columns.Select((_, i) => ColumnLetters(i)).ToArray();

        void Cell(int column, int row, object? value, bool header = false)
        {
            if (value == null) return;
            xml.WriteStartElement("c");
            xml.WriteAttributeString("r", letters[column] + row.ToString(CultureInfo.InvariantCulture));
            switch (value)
            {
                case bool b:
                    xml.WriteAttributeString("t", "b");
                    xml.WriteElementString("v", b ? "1" : "0");
                    break;
                case DateTime d:
                    xml.WriteAttributeString("s", "2");
                    xml.WriteElementString("v", d.ToOADate().ToString("R", CultureInfo.InvariantCulture));
                    break;
                // Excel solo guarda 15 dígitos de precisión: los enteros más largos van como texto.
                case long or ulong or decimal when Math.Abs(Convert.ToDecimal(value, CultureInfo.InvariantCulture)) >= 1e15m:
                    InlineString(CellText.Format(value));
                    break;
                case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                    xml.WriteElementString("v", Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case float or double when double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture)):
                    xml.WriteElementString("v", Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    if (header) xml.WriteAttributeString("s", "1");
                    InlineString(value as string ?? CellText.Format(value));
                    break;
            }
            xml.WriteEndElement();
        }

        void InlineString(string text)
        {
            xml.WriteAttributeString("t", "inlineStr");
            xml.WriteStartElement("is");
            xml.WriteStartElement("t");
            text = CleanForXml(text);
            if (text.Length > 32767) text = text[..32767];   // límite de una celda de Excel
            if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])))
                xml.WriteAttributeString("xml", "space", null, "preserve");
            xml.WriteString(text);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        xml.WriteStartElement("row");
        for (int c = 0; c < columns.Length; c++) Cell(c, 1, columns[c], header: true);
        xml.WriteEndElement();

        for (int r = 0; r < rows.Count; r++)
        {
            step(r);
            xml.WriteStartElement("row");
            var row = rows[r];
            for (int c = 0; c < columns.Length && c < row.Length; c++) Cell(c, r + 2, row[c]);
            xml.WriteEndElement();
        }

        xml.WriteEndElement();   // sheetData
        xml.WriteEndElement();   // worksheet
    }

    private static string ColumnLetters(int index)
    {
        string letters = "";
        for (index++; index > 0; index = (index - 1) / 26)
            letters = (char)('A' + (index - 1) % 26) + letters;
        return letters;
    }

    /// <summary>XML no admite caracteres de control (salvo tabulador y saltos de línea).</summary>
    private static string CleanForXml(string text) =>
        text.Any(ch => ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r')
            ? new string(text.Where(ch => ch >= 0x20 || ch == '\t' || ch == '\n' || ch == '\r').ToArray())
            : text;

    // ---------- JSON ----------

    /// <summary>Lista de objetos { "columna": valor }. Los nombres repetidos se numeran (id, id_2).</summary>
    private static void WriteJson(string path, string[] columns, IReadOnlyList<object?[]> rows, Action<int> step)
    {
        var names = new string[columns.Length];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Length; i++)
        {
            string name = columns[i];
            for (int n = 2; !used.Add(name); n++) name = $"{columns[i]}_{n}";
            names[i] = name;
        }

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // acentos legibles
        });
        json.WriteStartArray();
        for (int r = 0; r < rows.Count; r++)
        {
            step(r);
            json.WriteStartObject();
            var row = rows[r];
            for (int c = 0; c < names.Length && c < row.Length; c++)
            {
                json.WritePropertyName(names[c]);
                switch (row[c])
                {
                    case null: json.WriteNullValue(); break;
                    case bool b: json.WriteBooleanValue(b); break;
                    case byte or sbyte or short or ushort or int or uint or long:
                        json.WriteNumberValue(Convert.ToInt64(row[c], CultureInfo.InvariantCulture)); break;
                    case ulong u: json.WriteNumberValue(u); break;
                    case decimal m: json.WriteNumberValue(m); break;
                    case float or double when double.IsFinite(Convert.ToDouble(row[c], CultureInfo.InvariantCulture)):
                        json.WriteNumberValue(Convert.ToDouble(row[c], CultureInfo.InvariantCulture)); break;
                    case DateTime d: json.WriteStringValue(d.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)); break;
                    case byte[] bytes: json.WriteStringValue("0x" + Convert.ToHexString(bytes)); break;
                    default: json.WriteStringValue(CellText.Format(row[c])); break;
                }
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    // ---------- INSERT ----------

    /// <summary>
    /// INSERT de varias filas (de 500 en 500) para cargar los datos en otra base. La tabla de destino es la
    /// de origen si la consulta leía de una sola; si no, un nombre a reemplazar.
    /// </summary>
    private static void WriteInserts(string path, string[] columns, IReadOnlyList<object?[]> rows, string? sourceTable, DbKind kind, Action<int> step)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        string table = Db.QuoteId(sourceTable ?? "tabla_destino");
        writer.WriteLine($"-- Exportado con {App.Name} el {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({rows.Count:N0} filas).");
        if (sourceTable == null)
            writer.WriteLine("-- El resultado no venía de una sola tabla: cambia `tabla_destino` por la tabla real.");
        string header = $"INSERT INTO {table} ({string.Join(", ", columns.Select(Db.QuoteId))}) VALUES";

        for (int r = 0; r < rows.Count; r++)
        {
            step(r);
            if (r % InsertBatch == 0)
            {
                writer.WriteLine();
                writer.WriteLine(header);
            }
            bool lastOfBatch = r % InsertBatch == InsertBatch - 1 || r == rows.Count - 1;
            writer.Write("    (");
            writer.Write(string.Join(", ", rows[r].Take(columns.Length).Select(v => SqlLiteral(v, kind))));
            writer.WriteLine(lastOfBatch ? ");" : "),");
        }
    }

    /// <summary>Valor como literal SQL. En MySQL la barra invertida también se escapa dentro de las cadenas.</summary>
    public static string SqlLiteral(object? value, DbKind kind) => value switch
    {
        null => "NULL",
        bool b => b ? "1" : "0",
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        float or double => double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture))
            ? Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)
            : "NULL",
        DateTime d => "'" + d.ToString(d.TimeOfDay.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'",
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        _ => Quote(value as string ?? CellText.Format(value), kind),
    };

    private static string Quote(string text, DbKind kind)
    {
        if (kind == DbKind.MySql) text = text.Replace("\\", "\\\\");
        return "'" + text.Replace("'", "''") + "'";
    }
}
