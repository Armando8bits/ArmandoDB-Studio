using System.Data.Common;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MySmdb;

/// <summary>Contenido de un archivo a importar: nombres de columna y filas (texto, o números/fechas en Excel).</summary>
public sealed class ImportSource
{
    public required string[] Headers { get; init; }
    public required List<object?[]> Rows { get; init; }
    public string Description { get; init; } = "";
}

public sealed record ImportResult(long Inserted, string? Error, long ErrorRow);

/// <summary>Lee CSV/TXT y Excel (.xlsx), e inserta las filas en una tabla dentro de una transacción.</summary>
public static class DataImporter
{
    public const string AutoDelimiter = "";

    // ---------- CSV ----------

    /// <param name="delimiter">Separador, o <see cref="AutoDelimiter"/> para detectarlo en la primera línea.</param>
    public static ImportSource ReadCsv(string path, string delimiter, bool firstRowHeaders)
    {
        string text = DecodeText(File.ReadAllBytes(path), out string encodingName);
        if (delimiter == AutoDelimiter) delimiter = DetectDelimiter(text);
        var rows = ParseCsv(text, delimiter[0]);
        // Una última línea en blanco no es una fila.
        while (rows.Count > 0 && rows[^1].All(v => string.IsNullOrEmpty(v as string))) rows.RemoveAt(rows.Count - 1);
        // Tampoco las líneas en blanco intermedias: se importarían como filas vacías.
        rows.RemoveAll(r => r.Length == 1 && string.IsNullOrEmpty(r[0] as string));
        string name = delimiter switch { "," => "coma", ";" => "punto y coma", "\t" => "tabulador", "|" => "barra vertical", _ => delimiter };
        return Build(rows, firstRowHeaders, $"separador: {name} · codificación: {encodingName}");
    }

    /// <summary>UTF-8 (con o sin BOM) o UTF-16; si no es UTF-8 válido, Windows-1252 (lo habitual en CSV de Excel antiguos).</summary>
    private static string DecodeText(byte[] bytes, out string encodingName)
    {
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
        {
            encodingName = "UTF-16";
            return (bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes, 2, bytes.Length - 2);
        }
        try
        {
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            encodingName = "UTF-8";
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, skip, bytes.Length - skip);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encodingName = "Windows-1252";
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    /// <summary>El separador que más aparece en la primera línea, fuera de comillas.</summary>
    private static string DetectDelimiter(string text)
    {
        int end = text.IndexOf('\n');
        string line = end < 0 ? text : text[..end];
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0, ['|'] = 0 };
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && counts.ContainsKey(c)) counts[c]++;
        }
        var best = counts.OrderByDescending(pair => pair.Value).First();
        return best.Value == 0 ? "," : best.Key.ToString();
    }

    /// <summary>CSV estándar: campos entre comillas dobles pueden contener el separador, saltos de línea y "" como comilla.</summary>
    private static List<object?[]> ParseCsv(string text, char delimiter)
    {
        var rows = new List<object?[]>();
        var row = new List<object?>();
        var field = new StringBuilder();
        bool quoted = false, any = false;

        void EndField() { row.Add(field.ToString()); field.Clear(); }
        void EndRow() { EndField(); rows.Add(row.ToArray()); row.Clear(); any = false; }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0) { quoted = true; any = true; }
            else if (c == delimiter) { EndField(); any = true; }
            else if (c == '\r') { }
            else if (c == '\n') EndRow();
            else { field.Append(c); any = true; }
        }
        if (any || field.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }

    // ---------- Excel ----------

    /// <summary>Primera hoja de un .xlsx. Los números y las fechas llegan como tales; el resto, como texto.</summary>
    public static ImportSource ReadXlsx(string path, bool firstRowHeaders)
    {
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

        using var zip = ZipFile.OpenRead(path);
        XDocument? Load(string name)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) return null;
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }

        // Hoja: la primera del libro, localizada por sus relaciones.
        var workbook = Load("xl/workbook.xml") ?? throw new InvalidDataException("El archivo no es un libro de Excel válido.");
        var firstSheet = workbook.Descendants(main + "sheet").FirstOrDefault() ?? throw new InvalidDataException("El libro no tiene hojas.");
        string sheetName = (string?)firstSheet.Attribute("name") ?? "Hoja1";
        string? relationId = (string?)firstSheet.Attribute(rel + "id");
        string sheetPath = Load("xl/_rels/workbook.xml.rels")?.Descendants(pkg + "Relationship")
            .Where(r => (string?)r.Attribute("Id") == relationId).Select(r => (string?)r.Attribute("Target")).FirstOrDefault() ?? "worksheets/sheet1.xml";
        sheetPath = sheetPath.StartsWith('/') ? sheetPath.TrimStart('/') : "xl/" + sheetPath;

        // Textos compartidos (cada uno puede venir en varios trozos con formato).
        var shared = Load("xl/sharedStrings.xml")?.Descendants(main + "si")
            .Select(si => string.Concat(si.Descendants(main + "t").Select(t => t.Value))).ToList() ?? new List<string>();

        // Estilos: para saber qué celdas numéricas son en realidad fechas.
        var styles = Load("xl/styles.xml");
        var customFormats = styles?.Descendants(main + "numFmt").ToDictionary(f => (int)f.Attribute("numFmtId")!, f => (string?)f.Attribute("formatCode") ?? "")
            ?? new Dictionary<int, string>();
        var dateStyles = styles?.Descendants(main + "cellXfs").Elements(main + "xf")
            .Select(xf => IsDateFormat((int?)xf.Attribute("numFmtId") ?? 0, customFormats)).ToList() ?? new List<bool>();

        var sheet = Load(sheetPath) ?? throw new InvalidDataException("No se encontró la primera hoja del libro.");
        var rows = new List<object?[]>();
        foreach (var rowElement in sheet.Descendants(main + "row"))
        {
            var cells = new List<object?>();
            int next = 0;
            foreach (var cell in rowElement.Elements(main + "c"))
            {
                int column = ColumnIndex((string?)cell.Attribute("r")) ?? next;
                while (cells.Count < column) cells.Add(null);   // celdas vacías intermedias no se guardan en el archivo
                cells.Add(CellValue(cell, main, shared, dateStyles));
                next = column + 1;
            }
            // Las filas vacías intermedias tampoco: se respetan según su número de fila.
            int rowNumber = (int?)rowElement.Attribute("r") ?? rows.Count + 1;
            while (rows.Count < rowNumber - 1) rows.Add(Array.Empty<object?>());
            rows.Add(cells.ToArray());
        }
        while (rows.Count > 0 && rows[^1].All(v => v == null || v as string == "")) rows.RemoveAt(rows.Count - 1);
        return Build(rows, firstRowHeaders, $"hoja: {sheetName}");
    }

    private static object? CellValue(XElement cell, XNamespace main, List<string> shared, List<bool> dateStyles)
    {
        string type = (string?)cell.Attribute("t") ?? "n";
        string? value = (string?)cell.Element(main + "v");
        if (type == "inlineStr") return string.Concat(cell.Descendants(main + "t").Select(t => t.Value));
        if (value == null) return null;

        switch (type)
        {
            case "s": return int.TryParse(value, out int index) && index < shared.Count ? shared[index] : value;
            case "b": return value == "1" ? 1L : 0L;
            case "str" or "e": return value;
        }
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return value;

        int style = (int?)cell.Attribute("s") ?? 0;
        if (style < dateStyles.Count && dateStyles[style])
        {
            try { return DateTime.FromOADate(number); }
            catch (ArgumentException) { return number; }
        }
        // El (object) evita que el entero se convierta de nuevo a decimal al unificar los dos tipos del operador.
        return number == Math.Floor(number) && Math.Abs(number) < 9e15 ? (object)(long)number : number;
    }

    /// <summary>Formatos de fecha de Excel: los predefinidos y los personalizados con día, mes, año u hora.</summary>
    private static bool IsDateFormat(int id, Dictionary<int, string> custom)
    {
        if (id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58) return true;
        if (!custom.TryGetValue(id, out string? code)) return false;
        // Sin textos entre comillas ni colores/condiciones entre corchetes: "dd/mm/yyyy", "h:mm", "mmm-yy"...
        string bare = Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]", "").ToLowerInvariant();
        return bare.IndexOfAny(new[] { 'd', 'y', 'h', 's' }) >= 0 || bare.Contains("mm");
    }

    /// <summary>"C7" → 2.</summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        int index = 0;
        foreach (char c in reference)
        {
            if (!char.IsLetter(c)) break;
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return index == 0 ? null : index - 1;
    }

    private static ImportSource Build(List<object?[]> rows, bool firstRowHeaders, string description)
    {
        int width = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        string[] headers;
        if (firstRowHeaders && rows.Count > 0)
        {
            headers = Enumerable.Range(0, width).Select(i =>
                i < rows[0].Length && !string.IsNullOrWhiteSpace(Convert.ToString(rows[0][i])) ? Convert.ToString(rows[0][i])!.Trim() : $"Columna {i + 1}").ToArray();
            rows = rows.Skip(1).ToList();
        }
        else
        {
            headers = Enumerable.Range(0, width).Select(i => $"Columna {i + 1}").ToArray();
        }
        return new ImportSource { Headers = headers, Rows = rows, Description = description };
    }

    // ---------- Inserción ----------

    /// <summary>
    /// Inserta las filas en la tabla, todo o nada (una transacción). Si una fila falla, se deshace todo
    /// y se indica cuál fue y por qué.
    /// </summary>
    /// <param name="mapping">Columna de la tabla → índice de la columna del archivo.</param>
    public static async Task<ImportResult> ImportAsync(ConnectionProfile profile, string database, string table,
        IReadOnlyList<(string Column, int SourceIndex)> mapping, IReadOnlyList<object?[]> rows, bool emptyAsNull, bool deleteFirst,
        IProgress<BackupProgress> progress, CancellationToken token)
    {
        await using var conn = await Task.Run(() => profile.CreateConnection(database), token);
        await conn.OpenAsync(token);
        await using var transaction = await conn.BeginTransactionAsync(token);

        string target = Db.FullName(profile, database, table);
        string columns = string.Join(", ", mapping.Select(m => Db.QuoteId(profile.Kind, m.Column)));
        // Pocas filas por sentencia si la tabla tiene muchas columnas (límite de parámetros de SQLite).
        int batchSize = Math.Clamp(900 / mapping.Count, 1, 100);

        if (deleteFirst)
        {
            await using var delete = conn.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {target}";
            await delete.ExecuteNonQueryAsync(token);
        }

        async Task InsertAsync(int start, int count)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandTimeout = 0;
            var tuples = new List<string>(count);
            for (int r = 0; r < count; r++)
            {
                var names = new string[mapping.Count];
                for (int c = 0; c < mapping.Count; c++)
                {
                    var row = rows[start + r];
                    int source = mapping[c].SourceIndex;
                    object? value = source < row.Length ? row[source] : null;
                    if (value is string text && text.Length == 0 && emptyAsNull) value = null;

                    var parameter = cmd.CreateParameter();
                    parameter.ParameterName = names[c] = $"@r{r}c{c}";
                    parameter.Value = value ?? DBNull.Value;
                    cmd.Parameters.Add(parameter);
                }
                tuples.Add("(" + string.Join(", ", names) + ")");
            }
            cmd.CommandText = $"INSERT INTO {target} ({columns}) VALUES {string.Join(", ", tuples)}";
            await cmd.ExecuteNonQueryAsync(token);
        }

        for (int start = 0; start < rows.Count; start += batchSize)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(new BackupProgress("Importando filas", start, rows.Count));
            int count = Math.Min(batchSize, rows.Count - start);
            try
            {
                await InsertAsync(start, count);
            }
            catch (DbException) when (!token.IsCancellationRequested)
            {
                // El lote falló (y solo él se deshizo): se repite fila a fila para decir exactamente cuál no entra.
                for (int r = 0; r < count; r++)
                {
                    try { await InsertAsync(start + r, 1); }
                    catch (DbException single)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return new ImportResult(0, single.Message, start + r + 1);
                    }
                }
            }
        }

        await transaction.CommitAsync(token);
        return new ImportResult(rows.Count, null, 0);
    }
}
