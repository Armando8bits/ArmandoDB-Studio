using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using System.Data.Common;

namespace MySmdb;

public class ResultSet
{
    public required string[] Columns { get; init; }
    public List<object?[]> Rows { get; } = new();
    public bool Truncated { get; set; }
}

public partial class QueryTab : UserControl
{
    /// <summary>Tope de filas por conjunto de resultados, para no agotar la memoria.</summary>
    private const int MaxRows = 500_000;

    /// <summary>Alto máximo de cada cuadrícula cuando hay varios resultados apilados.</summary>
    private const double MaxStackedGridHeight = 260;
    /// <summary>A partir de estas filas, guardar a archivo muestra una barra de progreso.</summary>
    private const int ExportProgressThreshold = 5000;

    private readonly string _defaultTitle;
    private readonly List<DataGrid> _grids = new();
    private DbConnection? _conn;
    private CancellationTokenSource? _cts;

    public ConnectionProfile Profile { get; }
    public string? CurrentDatabase { get; private set; }
    public string? FilePath { get; private set; }
    public bool IsDirty { get; private set; }
    public bool IsRunning { get; private set; }
    public string StatusText { get; private set; } = "Listo";
    public string RowsText { get; private set; } = "";
    public string TimeText { get; private set; } = "";

    public string Title => FilePath != null ? Path.GetFileName(FilePath) : _defaultTitle;
    public bool HasText => !string.IsNullOrWhiteSpace(Editor.Text);

    /// <summary>Cambió algo que la ventana principal muestra (título, estado, base de datos).</summary>
    public event Action<QueryTab>? StateChanged;

    /// <summary>
    /// El resaltado base es el de T-SQL; aquí se le añade lo propio de MySQL:
    /// comentarios con '#' e identificadores entre acentos graves.
    /// </summary>
    static QueryTab()
    {
        var definition = HighlightingManager.Instance.GetDefinition("TSQL");
        if (definition == null) return;

        var comment = definition.GetNamedColor("Comment")
            ?? new HighlightingColor { Foreground = new SimpleHighlightingBrush(Colors.Green) };
        var spans = definition.MainRuleSet.Spans;
        spans.Add(new HighlightingSpan
        {
            StartExpression = new Regex("#"),
            EndExpression = new Regex("$"),
            SpanColor = comment,
            SpanColorIncludesStart = true,
            SpanColorIncludesEnd = true,
            RuleSet = new HighlightingRuleSet(),
        });
        // Sin color propio: evita que un '#' o una palabra clave dentro de `nombre` se resalten.
        spans.Add(new HighlightingSpan
        {
            StartExpression = new Regex("`"),
            EndExpression = new Regex("`"),
            RuleSet = new HighlightingRuleSet(),
        });
    }

    public QueryTab(ConnectionProfile profile, string? database, string defaultTitle)
    {
        InitializeComponent();
        Profile = profile;
        CurrentDatabase = database;
        _defaultTitle = defaultTitle;

        Editor.Options.ConvertTabsToSpaces = true;
        Messages.TextArea.TextView.LineTransformers.Add(new ErrorLineColorizer());
        foreach (var editor in new[] { Editor, Messages })
        {
            // El editor no lo pinta el tema Fluent: sus colores salen de la paleta y cambian solos con el tema.
            editor.SetResourceReference(BackgroundProperty, "Brush.EditorBackground");
            editor.SetResourceReference(ForegroundProperty, "Brush.EditorForeground");
            editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "Brush.EditorLineNumbers");
            editor.TextArea.SetResourceReference(TextArea.SelectionBrushProperty, "Brush.EditorSelection");
            // Al seleccionar se conservan los colores del resaltado, como en Visual Studio.
            editor.TextArea.SelectionForeground = null;
            editor.TextArea.SelectionBorder = null;
        }
        ApplyFont();
        ApplyTheme();
        Editor.TextChanged += (_, _) =>
        {
            if (IsDirty) return;
            IsDirty = true;
            StateChanged?.Invoke(this);
        };
    }

    public void FocusEditor() => Editor.Focus();

    /// <summary>Aplica la fuente configurada al editor, a la vista de texto y a las cuadrículas.</summary>
    public void ApplyFont()
    {
        var settings = AppSettings.Current;
        var family = new FontFamily(settings.EditorFont);
        Editor.FontFamily = family;
        Editor.FontSize = settings.EditorFontSize;
        Messages.FontSize = settings.EditorFontSize;
        foreach (var grid in _grids)
            grid.FontSize = settings.GridFontSize;
    }

    /// <summary>Lo que no se actualiza solo al cambiar de tema: cursor y redibujado del resaltado.</summary>
    public void ApplyTheme()
    {
        var foreground = Theme.Brush("Brush.EditorForeground");
        foreach (var editor in new[] { Editor, Messages })
        {
            editor.TextArea.Caret.CaretBrush = foreground;
            editor.TextArea.TextView.Redraw();
        }
    }

    public void SetText(string text)
    {
        Editor.Text = text;
        IsDirty = false;
        StateChanged?.Invoke(this);
    }

    public void LoadFile(string path)
    {
        Editor.Text = File.ReadAllText(path);
        FilePath = path;
        IsDirty = false;
        StateChanged?.Invoke(this);
    }

    public void SaveFile(string path)
    {
        File.WriteAllText(path, Editor.Text, new UTF8Encoding(false));
        FilePath = path;
        IsDirty = false;
        StateChanged?.Invoke(this);
    }

    public void Cancel() => _cts?.Cancel();

    public void Close()
    {
        Cancel();
        var conn = _conn;
        _conn = null;
        if (conn != null)
            _ = Task.Run(async () => { try { await conn.DisposeAsync(); } catch { } });
    }

    public async Task ChangeDatabaseAsync(string database)
    {
        if (IsRunning || database == CurrentDatabase || Profile.Kind != DbKind.MySql) return;
        try
        {
            if (_conn?.State == System.Data.ConnectionState.Open)
                await _conn.ChangeDatabaseAsync(database);
            CurrentDatabase = database;
        }
        catch (Exception ex)
        {
            Messages.Text = "Error al cambiar de base de datos: " + ex.Message.ReplaceLineEndings(" ");
            ResultTabs.SelectedIndex = 1;
        }
        StateChanged?.Invoke(this);
    }

    /// <summary>Ejecuta la selección si la hay; si no, el contenido completo de la pestaña.</summary>
    public async Task ExecuteAsync()
    {
        if (IsRunning) return;

        bool hasSelection = Editor.SelectionLength > 0;
        string sql = hasSelection ? Editor.SelectedText : Editor.Text;
        int lineOffset = hasSelection ? Editor.Document.GetLineByOffset(Editor.SelectionStart).LineNumber - 1 : 0;

        bool mysql = Profile.Kind == DbKind.MySql;
        var statements = SqlSplitter.Split(sql, mysql);
        if (statements.Count == 0)
        {
            Messages.Text = "No hay ninguna sentencia que ejecutar: el texto está vacío o solo contiene comentarios.\n"
                + (mysql ? "En MySQL, '#' y '-- '" : "En SQLite, '--'")
                + " comentan el resto de la línea, y /* ... */ comenta un bloque.";
            ResultTabs.SelectedIndex = 1;
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;
        StatusText = "Ejecutando consulta...";
        RowsText = "";
        TimeText = "";
        StateChanged?.Invoke(this);

        var results = new List<ResultSet>();
        var log = new StringBuilder();
        bool failed = false, cancelled = false;
        string? database = CurrentDatabase;
        var watch = Stopwatch.StartNew();

        try
        {
            await Task.Run(async () =>
            {
                await EnsureOpenAsync(token);
                try
                {
                    foreach (var statement in statements)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            await RunStatementAsync(statement.Text, results, log, token);
                        }
                        catch (DbException ex) when (!token.IsCancellationRequested)
                        {
                            // En una sola línea: la vista de texto pinta en rojo las líneas que empiezan por "Error".
                            log.AppendLine($"Error {Db.ErrorCode(ex)}, línea {statement.Line + lineOffset}: {ex.Message.ReplaceLineEndings(" ")}");
                            failed = true;
                            break;
                        }
                    }
                }
                finally
                {
                    // Un USE dentro del script cambia la base de datos de la sesión.
                    database = await ReadCurrentDatabaseAsync() ?? database;
                }
            });
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            log.AppendLine("Error: " + ex.Message.ReplaceLineEndings(" "));
            failed = true;
        }

        watch.Stop();
        _cts.Dispose();
        _cts = null;

        if (cancelled) log.AppendLine("La consulta fue cancelada por el usuario.");
        log.AppendLine();
        log.AppendLine($"Hora de finalización: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        ShowResults(results);
        Messages.Text = log.ToString();
        ResultTabs.SelectedIndex = results.Count > 0 && !failed ? 0 : 1;

        CurrentDatabase = database;
        IsRunning = false;
        StatusText = cancelled ? "Consulta cancelada."
            : failed ? "Consulta finalizada con errores."
            : "Consulta ejecutada correctamente.";
        RowsText = $"{results.Sum(r => r.Rows.Count)} filas";
        TimeText = watch.Elapsed.ToString(@"hh\:mm\:ss\.fff");
        StateChanged?.Invoke(this);
    }

    private async Task EnsureOpenAsync(CancellationToken token)
    {
        if (_conn?.State == System.Data.ConnectionState.Open) return;

        if (_conn != null)
            await _conn.DisposeAsync();
        _conn = Profile.CreateConnection(CurrentDatabase);
        await _conn.OpenAsync(token);
    }

    private async Task<string?> ReadCurrentDatabaseAsync()
    {
        try
        {
            // SQLite no cambia de base: siempre es "main".
            if (Profile.Kind != DbKind.MySql || _conn?.State != System.Data.ConnectionState.Open) return null;
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DATABASE()";
            return await cmd.ExecuteScalarAsync() as string;
        }
        catch
        {
            return null;
        }
    }

    private async Task RunStatementAsync(string sql, List<ResultSet> results, StringBuilder log, CancellationToken token)
    {
        string firstLine = sql.Split('\n')[0].TrimEnd();
        log.AppendLine(Db.Prompt(Profile.Kind) + (firstLine.Length < sql.Length ? firstLine + " ..." : firstLine));

        var watch = Stopwatch.StartNew();
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        await using var reader = await cmd.ExecuteReaderAsync(token);

        bool anyResultSet = false;
        do
        {
            if (reader.FieldCount == 0) continue;
            anyResultSet = true;

            var columns = new string[reader.FieldCount];
            for (int i = 0; i < columns.Length; i++)
                columns[i] = reader.GetName(i);

            var result = new ResultSet { Columns = columns };
            while (await reader.ReadAsync(token))
            {
                if (result.Rows.Count >= MaxRows)
                {
                    result.Truncated = true;
                    break;
                }
                var row = new object?[columns.Length];
                for (int i = 0; i < row.Length; i++)
                    row[i] = ReadValue(reader, i);
                result.Rows.Add(row);
            }
            results.Add(result);

            // Los datos solo se ven en la cuadrícula; aquí queda el registro de lo ocurrido.
            log.AppendLine($"{result.Rows.Count} filas en el conjunto ({Seconds(watch)})");
            if (result.Truncated)
                log.AppendLine($"Aviso: el resultado se truncó a {MaxRows} filas.");
            log.AppendLine();
        } while (await reader.NextResultAsync(token));

        if (reader.RecordsAffected >= 0)
            log.AppendLine($"Consulta OK, {reader.RecordsAffected} filas afectadas ({Seconds(watch)})").AppendLine();
        else if (!anyResultSet)
            log.AppendLine($"Consulta OK ({Seconds(watch)})").AppendLine();
    }

    private static string Seconds(Stopwatch watch) =>
        watch.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s";

    private static object? ReadValue(DbDataReader reader, int index)
    {
        try
        {
            return reader.IsDBNull(index) ? null : reader.GetValue(index);
        }
        catch
        {
            // Valores que .NET no puede representar (p. ej. fechas fuera de rango).
            try { return reader.GetString(index); }
            catch { return "<valor ilegible>"; }
        }
    }

    private void ShowResults(List<ResultSet> results)
    {
        _grids.Clear();
        if (results.Count == 0)
        {
            ResultsHost.Content = null;
            return;
        }
        if (results.Count == 1)
        {
            ResultsHost.Content = BuildGrid(results[0]);
            return;
        }

        // Varios resultados: apilados, como en SSMS.
        // Cada cuadrícula ocupa solo lo que necesitan sus filas, hasta un máximo; a partir de ahí tiene su propio scroll.
        var panel = new StackPanel();
        var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var result in results)
        {
            var grid = BuildGrid(result);
            grid.MaxHeight = MaxStackedGridHeight;
            grid.Margin = new Thickness(0, 0, 0, 6);
            grid.PreviewMouseWheel += (_, e) =>
            {
                // Si la cuadrícula ya no puede desplazarse en ese sentido, la rueda mueve la lista de resultados.
                var inner = FindChild<ScrollViewer>(grid);
                bool atLimit = inner == null || (e.Delta > 0 ? inner.VerticalOffset <= 0 : inner.VerticalOffset >= inner.ScrollableHeight);
                if (!atLimit) return;
                e.Handled = true;
                scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
            };
            panel.Children.Add(grid);
        }
        ResultsHost.Content = scroller;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private DataGrid BuildGrid(ResultSet result)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = true,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader,
            EnableRowVirtualization = true,
            EnableColumnVirtualization = true,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            BorderThickness = new Thickness(0),
            RowHeaderWidth = 60,
            MaxColumnWidth = 600,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = AppSettings.Current.GridFontSize,
        };
        _grids.Add(grid);
        grid.SetResourceReference(DataGrid.BackgroundProperty, "Brush.GridBackground");
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, "Brush.GridBackground");
        grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Brush.GridAlternate");
        grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Brush.GridLines");
        grid.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "Brush.GridLines");
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        ApplyCompactGridStyles(grid);

        var headerTemplate = (DataTemplate)FindResource("ColumnHeaderTemplate");
        for (int i = 0; i < result.Columns.Length; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[i],
                HeaderTemplate = headerTemplate,
                Binding = new Binding($"[{i}]") { Mode = BindingMode.OneTime, Converter = CellConverter.Instance },
            });
        }

        grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString();

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Copiar", () => ApplicationCommands.Copy.Execute(null, grid)));
        menu.Items.Add(MenuItem("Copiar con encabezados", () =>
        {
            grid.ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader;
            ApplicationCommands.Copy.Execute(null, grid);
            grid.ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader;
        }));
        menu.Items.Add(MenuItem("Seleccionar todo", grid.SelectAllCells));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Guardar resultados como...", () => _ = ExportAsync(result)));
        grid.ContextMenu = menu;

        grid.ItemsSource = result.Rows;
        return grid;
    }

    /// <summary>
    /// El tema Fluent da filas altas (unos 32 px) y fija su propio fondo, que anula el sombreado alterno.
    /// Estos estilos heredan de los de Fluent: filas compactas y sombreado por índice de alternancia.
    /// La cabecera de fila lleva plantilla propia: la de Fluent recorta los números de más de una cifra.
    /// </summary>
    private void ApplyCompactGridStyles(DataGrid grid)
    {
        grid.MinRowHeight = 0;
        grid.AlternationCount = 2;

        var rowStyle = new Style(typeof(DataGridRow), TryFindResource(typeof(DataGridRow)) as Style);
        rowStyle.Setters.Add(new Setter(MinHeightProperty, 0.0));
        rowStyle.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("Brush.GridBackground")));
        var alternate = new Trigger { Property = ItemsControl.AlternationIndexProperty, Value = 1 };
        alternate.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("Brush.GridAlternate")));
        rowStyle.Triggers.Add(alternate);
        grid.RowStyle = rowStyle;

        var cellStyle = new Style(typeof(DataGridCell), TryFindResource(typeof(DataGridCell)) as Style);
        cellStyle.Setters.Add(new Setter(MinHeightProperty, 0.0));
        cellStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(4, 1, 4, 1)));
        grid.CellStyle = cellStyle;

        // Número de fila alineado a la derecha, sobre el mismo gris que las franjas de pestañas.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.GridLines");
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 1));
        var number = new FrameworkElementFactory(typeof(ContentPresenter));
        number.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
        number.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        number.SetValue(MarginProperty, new Thickness(4, 0, 6, 0));
        border.AppendChild(number);

        var rowHeaderStyle = new Style(typeof(DataGridRowHeader));
        rowHeaderStyle.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(DataGridRowHeader)) { VisualTree = border }));
        rowHeaderStyle.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("Brush.TabStrip")));
        rowHeaderStyle.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("Brush.SecondaryText")));
        grid.RowHeaderStyle = rowHeaderStyle;

        var columnHeaderStyle = new Style(typeof(DataGridColumnHeader), TryFindResource(typeof(DataGridColumnHeader)) as Style);
        columnHeaderStyle.Setters.Add(new Setter(MinHeightProperty, 0.0));
        columnHeaderStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(6, 3, 6, 3)));
        grid.ColumnHeaderStyle = columnHeaderStyle;
    }

    /// <summary>Guarda un resultado como .csv (comas) o .txt (tabuladores), con encabezados.</summary>
    private async Task ExportAsync(ResultSet result)
    {
        var owner = Window.GetWindow(this)!;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV separado por comas (*.csv)|*.csv|Texto separado por tabuladores (*.txt)|*.txt",
            FileName = "resultados",
            DefaultExt = ".csv",
            AddExtension = true,
        };
        if (dialog.ShowDialog(owner) != true) return;

        string path = dialog.FileName;
        bool csv = !Path.GetExtension(dialog.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase);
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

        int total = result.Rows.Count;
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        // Con muchos datos se muestra el avance y se bloquea la ventana mientras se escribe.
        ProgressDialog? progressDialog = null;
        if (total >= ExportProgressThreshold)
        {
            progressDialog = new ProgressDialog(owner, "Guardando resultados", cts);
            progressDialog.Report(0, total);
            owner.IsEnabled = false;
            progressDialog.Show();
        }
        var progress = new Progress<int>(done => progressDialog?.Report(done, total));

        try
        {
            await Task.Run(() =>
            {
                // Con BOM, para que Excel reconozca los acentos.
                using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
                writer.WriteLine(string.Join(separator, result.Columns.Select(c => Field(c))));
                for (int i = 0; i < total; i++)
                {
                    if (i % 2000 == 0)
                    {
                        token.ThrowIfCancellationRequested();
                        ((IProgress<int>)progress).Report(i);
                    }
                    writer.WriteLine(string.Join(separator, result.Rows[i].Select(Field)));
                }
            });
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(path); } catch { }
        }
        catch (Exception ex)
        {
            owner.IsEnabled = true;
            MessageBox.Show(owner, ex.Message, "No se pudo guardar el archivo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            owner.IsEnabled = true;
            progressDialog?.Finish();
        }
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}

/// <summary>Pinta en rojo las líneas de error de la vista de texto.</summary>
public class ErrorLineColorizer : ICSharpCode.AvalonEdit.Rendering.DocumentColorizingTransformer
{
    private const string Prefix = "Error";

    protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
    {
        if (line.Length < Prefix.Length || CurrentContext.Document.GetText(line.Offset, Prefix.Length) != Prefix) return;
        ChangeLinePart(line.Offset, line.EndOffset, element =>
        {
            element.TextRunProperties.SetForegroundBrush(Theme.Brush("Brush.ErrorText"));
            var face = element.TextRunProperties.Typeface;
            element.TextRunProperties.SetTypeface(new Typeface(face.FontFamily, face.Style, FontWeights.SemiBold, face.Stretch));
        });
    }
}

public static class CellText
{
    public static string Format(object? value) => value switch
    {
        null => "NULL",
        DateTime d => d.ToString(d.Millisecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 64)) + (bytes.Length > 64 ? "..." : ""),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

public class CellConverter : IValueConverter
{
    public static readonly CellConverter Instance = new();

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => CellText.Format(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
