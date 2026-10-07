using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;
using System.Data.Common;

namespace MySmdb;

/// <summary>Un valor de la barra de estado (Suma, Promedio...): cómo se muestra y qué se copia al pulsarlo.</summary>
public record SelectionStat(string Label, string Display, string CopyText);

public class ResultSet
{
    public required string[] Columns { get; init; }
    public List<object?[]> Rows { get; } = new();
    public bool Truncated { get; set; }
    /// <summary>Tabla de la que vienen todas las columnas, si es una sola (destino propuesto al exportar como INSERT).</summary>
    public string? SourceTable { get; set; }
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

    public ConnectionProfile Profile { get; private set; }
    public string? CurrentDatabase { get; private set; }
    public string? FilePath { get; private set; }
    public bool IsDirty { get; private set; }
    public bool IsRunning { get; private set; }
    public string StatusText { get; private set; } = "Listo";
    public string RowsText { get; private set; } = "";
    /// <summary>Filas obtenidas en la última ejecución (para copiarlas desde la barra de estado); null si no hay.</summary>
    public int? RowCount { get; private set; }
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
        PlanView.TextArea.TextView.LineTransformers.Add(new ErrorLineColorizer());
        foreach (var editor in new[] { Editor, Messages, PlanView })
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
        _filterTimer.Tick += (_, _) => ApplyResultFilter();
        PlanLegend.Content = PlanDiagram.Legend();
        PlanAsDiagram.IsChecked = AppSettings.Current.PlanAsDiagram;
        PlanAsText.IsChecked = !AppSettings.Current.PlanAsDiagram;
        PlanViewMode_Changed(this, new RoutedEventArgs());

        // Ctrl+F, F3 y Mayús+F3: panel de búsqueda del editor, en español.
        _search = SearchPanel.Install(Editor);
        _search.Localization = new SpanishSearchLocalization();
        _search.Template = (ControlTemplate)FindResource("SearchPanelTemplate");
        SuppressSearchPopup(_search);
        _search.SearchOptionsChanged += (_, _) => UpdateMatchInfo();
        _search.Loaded += (_, _) => Dispatcher.BeginInvoke(UpdateMatchInfo, System.Windows.Threading.DispatcherPriority.Background);
        Editor.TextArea.SelectionChanged += (_, _) => UpdateMatchInfo();
        // Abreviatura + Tab: fragmento de código.
        Editor.TextArea.PreviewKeyDown += (_, e) =>
        {
            // Con la lista de sugerencias abierta, Tab acepta la sugerencia.
            if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && _completion == null
                && AppSettings.Current.SnippetsEnabled && TryExpandSnippet())
                e.Handled = true;
        };
        Editor.TextArea.TextEntering += (_, e) =>
        {
            // Un carácter que no forma parte de un nombre (espacio, coma, paréntesis...) cierra la lista sin insertar.
            if (_completion != null && e.Text.Length > 0 && !IsIdentifierChar(e.Text[0]))
                _completion.Close();
        };
        Editor.TextArea.TextEntered += (_, e) =>
        {
            if (e.Text == ".")
                ShowCompletion(forced: true);
            else if (AppSettings.Current.AutoCompleteEnabled && _completion == null && e.Text.Length == 1
                     && (char.IsLetter(e.Text[0]) || e.Text[0] == '_'))
                ShowCompletion(forced: false);
        };
        Editor.TextChanged += (_, _) =>
        {
            if (IsDirty) return;
            IsDirty = true;
            StateChanged?.Invoke(this);
        };
        // Para que los botones Deshacer/Rehacer se activen y desactiven según haya algo que deshacer o rehacer.
        Editor.Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "CanUndo" or "CanRedo") StateChanged?.Invoke(this);
        };
    }

    public void FocusEditor() => Editor.Focus();

    /// <summary>Editor SQL de la pestaña (para buscar y reemplazar).</summary>
    public TextEditor SqlEditor => Editor;

    /// <summary>Recuento, suma, promedio... de las celdas seleccionadas en la cuadrícula; vacío si no aplica.</summary>
    public IReadOnlyList<SelectionStat> SelectionStats { get; private set; } = Array.Empty<SelectionStat>();

    private readonly SearchPanel _search;

    /// <summary>
    /// AvalonEdit avisa de "sin coincidencias" con un globo emergente que tapa las opciones del panel.
    /// Se anula; en su lugar, el panel muestra el conteo en una línea de texto (UpdateMatchInfo).
    /// </summary>
    private static void SuppressSearchPopup(SearchPanel panel)
    {
        if (typeof(SearchPanel).GetField("messageView", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(panel) is ToolTip popup)
            popup.Opened += (_, _) => popup.IsOpen = false;
    }

    /// <summary>"Mostrando N de X coincidencias", "X coincidencias" o "Se encontraron 0 resultados".</summary>
    private void UpdateMatchInfo()
    {
        if (_search.IsClosed || _search.Template?.FindName("MatchInfo", _search) is not TextBlock info) return;

        string pattern = _search.SearchPattern ?? "";
        if (pattern.Length == 0)
        {
            info.Text = "";
            return;
        }

        Regex regex;
        try
        {
            // Mismas reglas que el panel: texto literal salvo "Regex"; "Palabra" exige límites de palabra.
            string expression = _search.UseRegex ? pattern : Regex.Escape(pattern);
            if (_search.WholeWords) expression = $@"\b{expression}\b";
            regex = new Regex(expression, (_search.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.Multiline);
        }
        catch (ArgumentException)
        {
            SetMatchInfo(info, "Expresión regular no válida", error: true);
            return;
        }

        var matches = regex.Matches(Editor.Text).Where(m => m.Length > 0).ToList();
        if (matches.Count == 0)
        {
            SetMatchInfo(info, "Se encontraron 0 resultados", error: true);
            return;
        }

        int current = matches.FindIndex(m => m.Index == Editor.SelectionStart && m.Length == Editor.SelectionLength) + 1;
        string total = matches.Count == 1 ? "1 coincidencia" : $"{matches.Count:N0} coincidencias";
        SetMatchInfo(info, current > 0 ? $"Mostrando {current:N0} de {total}" : total, error: false);
    }

    private static void SetMatchInfo(TextBlock info, string text, bool error)
    {
        info.Text = text;
        info.SetResourceReference(TextBlock.ForegroundProperty, error ? "Brush.ErrorText" : "Brush.SecondaryText");
    }

    /// <summary>Abre el panel de búsqueda (lo mismo que Ctrl+F dentro del editor).</summary>
    public void OpenSearch()
    {
        Editor.Focus();
        ApplicationCommands.Find.Execute(null, Editor.TextArea);
    }

    // ---------- Autocompletado ----------

    private CompletionWindow? _completion;

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>
    /// Lista de sugerencias para la palabra que se está escribiendo. Tras "tabla." o "alias.", las columnas de esa tabla.
    /// <paramref name="forced"/> (Ctrl+Espacio o "."): se muestra aunque la palabra tenga menos de 2 letras.
    /// </summary>
    public void ShowCompletion(bool forced)
    {
        if (_completion != null) return;
        var document = Editor.Document;
        int caret = Editor.CaretOffset;
        var line = document.GetLineByOffset(caret);
        bool mysql = Profile.Kind == DbKind.MySql;
        if (SqlCompletion.InStringOrComment(document.GetText(line), caret - line.Offset, mysql)) return;

        int start = caret;
        while (start > 0 && IsIdentifierChar(document.GetCharAt(start - 1))) start--;

        // ¿Hay "algo." delante? Entonces se sugieren las columnas de "algo" (tabla o alias).
        string? qualifier = null;
        if (start > 0 && document.GetCharAt(start - 1) == '.')
        {
            int end = start - 1, begin = end;
            if (begin > 0 && document.GetCharAt(begin - 1) == '`')
            {
                int open = document.Text.LastIndexOf('`', Math.Max(0, begin - 2));
                if (open >= 0 && open < begin - 1) qualifier = document.GetText(open + 1, begin - 2 - open);
            }
            else
            {
                while (begin > 0 && IsIdentifierChar(document.GetCharAt(begin - 1))) begin--;
                if (begin < end) qualifier = document.GetText(begin, end - begin);
            }
            if (qualifier == null) return;
        }

        string prefix = document.GetText(start, caret - start);
        if (!forced && prefix.Length < 2) return;

        var schema = SchemaCache.TryGet(Profile, CurrentDatabase);
        if (schema == null && qualifier != null && !string.IsNullOrEmpty(CurrentDatabase))
        {
            // El esquema aún se está cargando: se reintenta al terminar, si el cursor sigue en el mismo sitio.
            SchemaCache.GetAsync(Profile, CurrentDatabase).ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully && Editor.CaretOffset == caret) ShowCompletion(forced);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        var items = SqlCompletion.Suggestions(document.Text, caret, qualifier, schema);
        if (items.Count == 0) return;

        var window = new CompletionWindow(Editor.TextArea) { StartOffset = start, EndOffset = caret, MinWidth = 300, MaxHeight = 320 };
        window.SetResourceReference(BackgroundProperty, "Brush.PanelBackground");
        window.SetResourceReference(BorderBrushProperty, "Brush.PanelBorder");
        window.SetResourceReference(ForegroundProperty, "Brush.EditorForeground");
        foreach (var item in items) window.CompletionList.CompletionData.Add(item);
        window.Closed += (_, _) => _completion = null;
        _completion = window;
        window.Show();

        if (prefix.Length > 0) window.CompletionList.SelectItem(prefix);
        // Al escribir, si nada coincide con lo escrito, no se molesta con una lista vacía.
        if (!forced && window.CompletionList.SelectedItem == null) window.Close();
    }

    // ---------- Formato ----------

    /// <summary>Formatea la selección o, si no hay, todo el script (se deshace con Ctrl+Z). Devuelve un aviso si no se pudo.</summary>
    public string? FormatSql()
    {
        bool selection = Editor.SelectionLength > 0;
        string text = selection ? Editor.SelectedText : Editor.Text;
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!SqlFormatter.CanFormat(text))
            return "No se formatean scripts con DELIMITER o con lotes GO (procedimientos, triggers): selecciona solo la consulta que quieras formatear.";

        string formatted = SqlFormatter.Format(text, Profile.Kind == DbKind.MySql);
        if (selection)
        {
            int start = Editor.SelectionStart;
            Editor.Document.Replace(start, Editor.SelectionLength, formatted);
            Editor.Select(start, formatted.Length);
        }
        else
        {
            Editor.Document.Replace(0, Editor.Document.TextLength, formatted + "\n");
            Editor.CaretOffset = 0;
        }
        return null;
    }

    /// <summary>Si justo antes del cursor hay una abreviatura conocida, la sustituye por su fragmento.</summary>
    private bool TryExpandSnippet()
    {
        if (Editor.SelectionLength > 0) return false;
        var document = Editor.Document;
        int caret = Editor.CaretOffset;
        int start = caret;
        while (start > 0 && char.IsLetter(document.GetCharAt(start - 1))) start--;
        if (start == caret) return false;
        // Debe ser una palabra suelta: "xsel" o "t_sel" no cuentan.
        if (start > 0 && (char.IsLetterOrDigit(document.GetCharAt(start - 1)) || document.GetCharAt(start - 1) == '_')) return false;

        string? template = Snippets.Get(document.GetText(start, caret - start), Profile.Kind);
        if (template == null) return false;

        // Las líneas del fragmento heredan la sangría de la línea actual.
        var line = document.GetLineByOffset(start);
        string lineText = document.GetText(line);
        string indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];
        string text = template.Replace("\n", "\n" + indent);
        int cursor = text.IndexOf('|');
        text = text.Remove(cursor, 1);

        document.Replace(start, caret - start, text);
        Editor.CaretOffset = start + cursor;
        return true;
    }

    /// <summary>Aplica la fuente configurada al editor, a la vista de texto y a las cuadrículas.</summary>
    public void ApplyFont()
    {
        var settings = AppSettings.Current;
        var family = new FontFamily(settings.EditorFont);
        Editor.FontFamily = family;
        Editor.FontSize = settings.EditorFontSize;
        Messages.FontSize = settings.EditorFontSize;
        PlanView.FontSize = settings.EditorFontSize;
        foreach (var grid in _grids)
            grid.FontSize = settings.GridFontSize;
    }

    /// <summary>Lo que no se actualiza solo al cambiar de tema: cursor y redibujado del resaltado.</summary>
    public void ApplyTheme()
    {
        var foreground = Theme.Brush("Brush.EditorForeground");
        foreach (var editor in new[] { Editor, Messages, PlanView })
        {
            editor.TextArea.Caret.CaretBrush = foreground;
            editor.TextArea.TextView.Redraw();
        }
    }

    public void SetText(string text)
    {
        Editor.Text = text;
        Editor.Document.UndoStack.ClearAll();   // el texto inicial no es algo que se pueda "deshacer"
        IsDirty = false;
        StateChanged?.Invoke(this);
    }

    public void LoadFile(string path)
    {
        Editor.Text = File.ReadAllText(path);
        Editor.Document.UndoStack.ClearAll();
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

    /// <summary>
    /// Pasa la pestaña a otra conexión (y base): el texto se conserva y la próxima ejecución va contra ella.
    /// Sirve para cuando se abrió la consulta en la conexión equivocada.
    /// </summary>
    public async Task ChangeConnectionAsync(ConnectionProfile profile, string? database)
    {
        if (IsRunning) return;
        var previous = _conn;
        _conn = null;
        if (previous != null)
        {
            try { await previous.DisposeAsync(); } catch { }
        }
        Profile = profile;
        CurrentDatabase = database;
        StatusText = $"Pestaña cambiada a {profile.Name}.";
        StateChanged?.Invoke(this);
    }

    public async Task ChangeDatabaseAsync(string database)
    {
        if (IsRunning || database == CurrentDatabase || Profile.Kind == DbKind.Sqlite) return;
        try
        {
            if (_conn?.State == System.Data.ConnectionState.Open)
                await _conn.ChangeDatabaseAsync(database);
            CurrentDatabase = database;
        }
        catch (Exception ex)
        {
            Messages.Text = "Error al cambiar de base de datos: " + ex.Message.ReplaceLineEndings(" ");
            ResultTabs.SelectedIndex = MessagesTab;
        }
        StateChanged?.Invoke(this);
    }

    private const int GridTab = 0, MessagesTab = 1, PlanTab = 2;

    /// <summary>Pregunta antes de ejecutar sentencias peligrosas. "No" es la opción por defecto.</summary>
    private bool ConfirmDangerous(List<string> warnings)
    {
        const int MaxListed = 10;
        string list = string.Join("\n", warnings.Take(MaxListed))
            + (warnings.Count > MaxListed ? $"\n... y {warnings.Count - MaxListed} más." : "");
        string where = Profile.IsProduction ? $"en PRODUCCIÓN ({Profile.Name})" : $"en {Profile.Name}";
        string title = Profile.IsProduction ? "Confirmar ejecución en PRODUCCIÓN" : "Confirmar sentencias peligrosas";

        var answer = MessageBox.Show(Window.GetWindow(this),
            $"Vas a ejecutar {where}:\n\n{list}\n\n¿Continuar?",
            title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Sentencias de la selección si la hay; si no, de todo el contenido. Devuelve null (y lo explica en
    /// la pestaña indicada) cuando no hay nada que ejecutar.
    /// </summary>
    private (List<SqlStatement> Statements, int LineOffset)? ScriptToRun(TextEditor emptyMessageView, int emptyMessageTab)
    {
        bool hasSelection = Editor.SelectionLength > 0;
        string sql = hasSelection ? Editor.SelectedText : Editor.Text;
        int lineOffset = hasSelection ? Editor.Document.GetLineByOffset(Editor.SelectionStart).LineNumber - 1 : 0;

        var statements = SqlSplitter.Split(sql, Profile.Kind);
        if (statements.Count > 0) return (statements, lineOffset);

        emptyMessageView.Text = "No hay ninguna sentencia que ejecutar: el texto está vacío o solo contiene comentarios.\n"
            + Profile.Kind switch { DbKind.MySql => "En MySQL, '#' y '-- '", DbKind.SqlServer => "En SQL Server, '--'", DbKind.Sybase => "En Sybase, '--'", _ => "En SQLite, '--'" }
            + " comentan el resto de la línea, y /* ... */ comenta un bloque.";
        ResultTabs.SelectedIndex = emptyMessageTab;
        return null;
    }

    /// <summary>Ejecuta la selección si la hay; si no, el contenido completo de la pestaña.</summary>
    public async Task ExecuteAsync()
    {
        if (IsRunning || ScriptToRun(Messages, MessagesTab) is not { } script) return;
        var (statements, lineOffset) = script;

        var settings = AppSettings.Current;
        // En SQL Server y Sybase lo que se ejecuta son lotes; para revisarlos se miran sus sentencias una a una.
        var toReview = !Db.IsTSql(Profile.Kind) ? statements
            : statements.SelectMany(SqlSplitter.SplitTSqlForReview).ToList();
        var warnings = SqlSafety.Review(toReview, lineOffset, Profile.IsProduction,
            settings.ConfirmDangerous, settings.ConfirmProductionWrites);
        if (warnings.Count > 0 && !ConfirmDangerous(warnings))
        {
            StatusText = "Ejecución cancelada.";
            StateChanged?.Invoke(this);
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;
        StatusText = "Ejecutando consulta...";
        RowsText = "";
        RowCount = null;
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
                _serverMessages = log;   // PRINT y avisos de SQL Server van al registro, en su orden
                try
                {
                    foreach (var statement in statements)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            await RunStatementAsync(statement.Text, results, log, token);
                        }
                        catch (Exception ex) when (Db.IsDatabaseError(ex) && !token.IsCancellationRequested)
                        {
                            // En una sola línea: la vista de texto pinta en rojo las líneas que empiezan por "Error".
                            log.AppendLine($"Error {Db.ErrorCode(ex)}, línea {ErrorLine(ex, statement.Line + lineOffset)}: {ex.Message.ReplaceLineEndings(" ")}");
                            failed = true;
                            break;
                        }
                    }
                }
                finally
                {
                    _serverMessages = null;
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

        try
        {
            ShowResults(results);
            Messages.Text = log.ToString();
            ResultTabs.SelectedIndex = results.Count > 0 && !failed ? GridTab : MessagesTab;
        }
        catch (Exception ex)
        {
            // La consulta ya se ejecutó; lo que falló es pintar su resultado. La pestaña debe quedar utilizable.
            failed = true;
            try { Messages.Text = log + Environment.NewLine + "Error: no se pudo mostrar el resultado: " + ex.Message.ReplaceLineEndings(" "); } catch { }
            Errors.Show(Window.GetWindow(this), "No se pudo mostrar el resultado de la consulta", ex);
        }

        CurrentDatabase = database;
        // Si cambió la estructura, el autocompletado vuelve a leer tablas y columnas.
        if (statements.Any(s => StructureChange.IsMatch(s.Text))) SchemaCache.Invalidate(Profile);
        IsRunning = false;
        StatusText = cancelled ? "Consulta cancelada."
            : failed ? "Consulta finalizada con errores."
            : "Consulta ejecutada correctamente.";
        RowCount = results.Sum(r => r.Rows.Count);
        RowsText = $"{RowCount:N0} filas";
        TimeText = watch.Elapsed.ToString(@"hh\:mm\:ss\.fff");
        StateChanged?.Invoke(this);
    }

    /// <summary>
    /// Muestra el plan de ejecución estimado de la selección o de todo el contenido, sin ejecutar las consultas.
    /// MySQL: EXPLAIN FORMAT=TREE (o el EXPLAIN clásico en MariaDB y MySQL anteriores a 8.0.16).
    /// SQLite: EXPLAIN QUERY PLAN, dibujado como árbol.
    /// </summary>
    public async Task ExplainAsync()
    {
        if (IsRunning || ScriptToRun(PlanView, PlanTab) is not { } script) return;
        var (statements, lineOffset) = script;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;
        StatusText = "Generando el plan de ejecución...";
        RowsText = "";
        RowCount = null;
        TimeText = "";
        // Se pasa ya a la pestaña del plan, con el aviso de carga encima del plan anterior.
        PlanLoading.Visibility = Visibility.Visible;
        ResultTabs.SelectedIndex = PlanTab;
        StateChanged?.Invoke(this);

        var output = new StringBuilder();
        var sections = new List<PlanSection>();
        bool failed = false, cancelled = false;
        string? database = CurrentDatabase;
        var watch = Stopwatch.StartNew();

        try
        {
            await Task.Run(async () =>
            {
                await EnsureOpenAsync(token);
                foreach (var statement in statements)
                {
                    token.ThrowIfCancellationRequested();
                    int line = statement.Line + lineOffset;
                    string header = $"Línea {line}: {FirstLine(statement.Text)}";
                    output.AppendLine("-- " + header);
                    try
                    {
                        var (text, root) = await PlanAsync(statement.Text, token);
                        output.AppendLine(text);
                        sections.Add(new PlanSection(header, root, root == null ? text : null, IsError: false));
                    }
                    catch (Exception ex) when (Db.IsDatabaseError(ex) && !token.IsCancellationRequested)
                    {
                        string error = $"Error {Db.ErrorCode(ex)}, línea {line}: {ex.Message.ReplaceLineEndings(" ")}";
                        output.AppendLine(error);
                        sections.Add(new PlanSection(header, null, error, IsError: true));
                        failed = true;
                    }
                    output.AppendLine();
                }
                database = await ReadCurrentDatabaseAsync() ?? database;
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
            output.AppendLine("Error: " + ex.Message.ReplaceLineEndings(" "));
            failed = true;
        }

        watch.Stop();
        _cts.Dispose();
        _cts = null;
        if (cancelled) output.AppendLine("Cancelado por el usuario.");

        PlanLoading.Visibility = Visibility.Collapsed;
        try
        {
            PlanView.Text = output.ToString();
            PlanView.ScrollToHome();
            ResultTabs.SelectedIndex = PlanTab;
            ShowPlanDiagram(sections);
        }
        catch (Exception ex)
        {
            // El plan ya se obtuvo; lo que falló es dibujarlo. Queda el texto y la pestaña sigue utilizable.
            failed = true;
            Errors.Show(Window.GetWindow(this), "No se pudo dibujar el plan de ejecución", ex);
        }

        CurrentDatabase = database;
        IsRunning = false;
        StatusText = cancelled ? "Plan de ejecución cancelado."
            : failed ? "Plan de ejecución generado con errores."
            : "Plan de ejecución generado.";
        TimeText = watch.Elapsed.ToString(@"hh\:mm\:ss\.fff");
        StateChanged?.Invoke(this);
    }

    private static readonly Regex StructureChange = new(@"^\s*(CREATE|ALTER|DROP|RENAME)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Explainable = new(@"^\s*\(*\s*(SELECT|WITH|INSERT|UPDATE|DELETE|REPLACE|TABLE|VALUES)\b", RegexOptions.IgnoreCase);
    private static readonly Regex AlreadyExplain = new(@"^\s*(EXPLAIN|DESCRIBE|DESC)\b", RegexOptions.IgnoreCase);
    private static readonly Regex UseStatement = new(@"^\s*USE\b", RegexOptions.IgnoreCase);

    /// <summary>Plan de una sentencia para la vista de diagrama: su árbol, o una nota/error si no lo tiene.</summary>
    private sealed record PlanSection(string Header, PlanNode? Root, string? Note, bool IsError);

    /// <summary>
    /// Plan de una sentencia: como texto y, si se pudo interpretar, como árbol para el diagrama.
    /// Nunca ejecuta la consulta (salvo que ya venga con EXPLAIN ANALYZE).
    /// </summary>
    private async Task<(string Text, PlanNode? Root)> PlanAsync(string sql, CancellationToken token)
    {
        if (Profile.Kind == DbKind.SqlServer) return await PlanSqlServerAsync(sql, token);
        if (Profile.Kind == DbKind.Sybase) return await PlanSybaseAsync(sql, token);

        // Un USE previo cambia la base con la que se explican las siguientes sentencias, así que se aplica.
        if (UseStatement.IsMatch(sql))
        {
            await QueryAsync(sql, token);
            return ("(USE aplicado; no tiene plan)", null);
        }
        // Si el usuario ya escribió EXPLAIN, se respeta tal cual.
        if (AlreadyExplain.IsMatch(sql))
            return Interpret(await QueryAsync(sql, token));
        if (!Explainable.IsMatch(sql))
            return ("(sin plan: solo se explican SELECT, WITH, INSERT, UPDATE, DELETE y REPLACE)", null);

        if (Profile.Kind == DbKind.Sqlite)
        {
            var result = await QueryAsync("EXPLAIN QUERY PLAN " + sql, token);
            return (FormatSqliteTree(result), PlanParser.FromSqlite(result));
        }

        try
        {
            return Interpret(await QueryAsync("EXPLAIN FORMAT=TREE " + sql, token));
        }
        catch (DbException) when (!token.IsCancellationRequested)
        {
            // MariaDB y MySQL < 8.0.16 no tienen FORMAT=TREE. Si la sentencia es la que falla, el EXPLAIN clásico dará el error real.
            return Interpret(await QueryAsync("EXPLAIN " + sql, token));
        }
    }

    /// <summary>
    /// SQL Server: con SET SHOWPLAN_ALL ON el servidor no ejecuta el lote; devuelve su plan estimado, una fila por paso.
    /// </summary>
    private async Task<(string Text, PlanNode? Root)> PlanSqlServerAsync(string batch, CancellationToken token)
    {
        async Task SetAsync(string value, CancellationToken cancel)
        {
            await using var set = _conn!.CreateCommand();
            set.CommandText = "SET SHOWPLAN_ALL " + value;   // tiene que ir solo en su lote
            await set.ExecuteNonQueryAsync(cancel);
        }

        var plan = new ResultSet { Columns = Array.Empty<string>() };
        await SetAsync("ON", token);
        try
        {
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 0;
            await using var reader = await cmd.ExecuteReaderAsync(token);
            // Un conjunto de filas por sentencia del lote, todos con las mismas columnas.
            do
            {
                if (reader.FieldCount == 0) continue;
                if (plan.Columns.Length == 0) plan = new ResultSet { Columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray() };
                while (await reader.ReadAsync(token))
                {
                    var row = new object?[reader.FieldCount];
                    for (int i = 0; i < row.Length; i++) row[i] = ReadValue(reader, i);
                    plan.Rows.Add(row);
                }
            } while (await reader.NextResultAsync(token));
        }
        finally
        {
            // Sin esto la sesión de la pestaña seguiría sin ejecutar nada.
            try { await SetAsync("OFF", CancellationToken.None); } catch { }
        }

        int text = Array.FindIndex(plan.Columns, c => c.Equals("StmtText", StringComparison.OrdinalIgnoreCase));
        if (text < 0 || plan.Rows.Count == 0) return ("(sin plan)", null);
        // StmtText ya viene sangrado como árbol ("  |--Clustered Index Scan(...)").
        return (string.Join(Environment.NewLine, plan.Rows.Select(r => CellText.Format(r[text]).ReplaceLineEndings(" "))), PlanParser.FromSqlServer(plan));
    }

    /// <summary>
    /// Sybase ASE: con SET SHOWPLAN ON el servidor describe el plan en mensajes de texto, y con SET NOEXEC ON
    /// compila el lote sin ejecutarlo. No hay una forma tabular, así que solo se muestra como texto.
    /// </summary>
    private async Task<(string Text, PlanNode? Root)> PlanSybaseAsync(string batch, CancellationToken token)
    {
        async Task RunAsync(string sql, CancellationToken cancel)
        {
            await using var cmd = _conn!.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            await using var reader = await cmd.ExecuteReaderAsync(cancel);
            while (await reader.NextResultAsync(cancel)) { }
        }

        var plan = new StringBuilder();
        var previous = _serverMessages;
        // Cada opción va sola en su lote; NOEXEC al final, porque a partir de ahí ya no se ejecuta nada más que SET.
        await RunAsync("set showplan on", token);
        try
        {
            await RunAsync("set noexec on", token);
            _serverMessages = plan;   // el plan llega como mensajes del servidor
            await RunAsync(batch, token);
        }
        finally
        {
            _serverMessages = previous;
            // Sin esto la sesión de la pestaña seguiría sin ejecutar nada.
            try { await RunAsync("set noexec off", CancellationToken.None); } catch { }
            try { await RunAsync("set showplan off", CancellationToken.None); } catch { }
        }

        string text = plan.ToString().TrimEnd();
        return (text.Length > 0 ? text : "(el servidor no devolvió ningún plan)", null);
    }

    /// <summary>Texto y árbol de un resultado de EXPLAIN, sea cual sea su forma.</summary>
    private (string Text, PlanNode? Root) Interpret(ResultSet result)
    {
        string text = FormatPlanResult(result);
        if (Profile.Kind == DbKind.Sqlite && result.Columns.Length == 4)
            return (FormatSqliteTree(result), PlanParser.FromSqlite(result));
        var root = result.Columns.Length == 1 ? PlanParser.FromMySqlTree(text) : PlanParser.FromClassicTable(result);
        return (text, root);
    }

    private double _planZoom = 1;

    /// <summary>Un árbol por sentencia, uno debajo de otro; las sentencias sin plan o con error muestran su texto.</summary>
    private void ShowPlanDiagram(List<PlanSection> sections)
    {
        PlanDiagramPanel.Children.Clear();
        foreach (var section in sections)
        {
            var header = new TextBlock { Text = section.Header, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 10, 10, 2), TextTrimming = TextTrimming.CharacterEllipsis,
                // Acotado: una sentencia larga no debe ensanchar la zona y crear desplazamiento horizontal de más.
                MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = section.Header };
            PlanDiagramPanel.Children.Add(header);
            if (section.Root != null)
            {
                PlanDiagramPanel.Children.Add(PlanDiagram.Build(section.Root));
                continue;
            }
            var note = new TextBlock { Text = section.Note, Margin = new Thickness(10, 2, 10, 6), TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
            note.SetResourceReference(TextBlock.ForegroundProperty, section.IsError ? "Brush.ErrorText" : "Brush.SecondaryText");
            PlanDiagramPanel.Children.Add(note);
        }
        CenterPlanDiagram();
    }

    /// <summary>
    /// Centra la vista en el primer árbol. La raíz queda a media altura de su árbol, así que en un plan grande
    /// la esquina superior izquierda está vacía y parecería que no hay nada.
    /// </summary>
    private void CenterPlanDiagram()
    {
        PlanDiagramScroll.ScrollToHome();
        // Tras el diseño: hasta entonces no se conocen el tamaño del diagrama ni el de la zona visible.
        Dispatcher.BeginInvoke(() =>
        {
            if (PlanDiagramPanel.Children.OfType<Canvas>().FirstOrDefault() is not { } tree || !PlanDiagramScroll.IsVisible) return;
            PlanDiagramScroll.UpdateLayout();
            var bounds = tree.TransformToAncestor(PlanDiagramScroll).TransformBounds(new Rect(tree.RenderSize));
            double x = PlanDiagramScroll.HorizontalOffset + bounds.Left + bounds.Width / 2 - PlanDiagramScroll.ViewportWidth / 2;
            double y = PlanDiagramScroll.VerticalOffset + bounds.Top + bounds.Height / 2 - PlanDiagramScroll.ViewportHeight / 2;
            // Si el árbol cabe a lo ancho o a lo alto, en ese eje se deja al principio (con su título a la vista).
            PlanDiagramScroll.ScrollToHorizontalOffset(bounds.Width > PlanDiagramScroll.ViewportWidth ? x : 0);
            PlanDiagramScroll.ScrollToVerticalOffset(bounds.Height > PlanDiagramScroll.ViewportHeight ? y : 0);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PlanViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (PlanDiagramScroll == null) return;   // durante la carga del XAML
        bool diagram = PlanAsDiagram.IsChecked == true;
        PlanDiagramScroll.Visibility = diagram ? Visibility.Visible : Visibility.Collapsed;
        PlanView.Visibility = diagram ? Visibility.Collapsed : Visibility.Visible;
        PlanLegend.Visibility = diagram ? Visibility.Visible : Visibility.Collapsed;
        if (diagram) CenterPlanDiagram();
        if (AppSettings.Current.PlanAsDiagram == diagram) return;
        AppSettings.Current.PlanAsDiagram = diagram;
        try { AppSettings.Current.Save(); } catch { }
    }

    /// <summary>Ctrl+rueda sobre el diagrama del plan: zoom.</summary>
    private void PlanDiagramScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        _planZoom = Math.Clamp(_planZoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.3, 2.5);
        PlanDiagramPanel.LayoutTransform = new ScaleTransform(_planZoom, _planZoom);
    }

    private async Task<ResultSet> QueryAsync(string sql, CancellationToken token)
    {
        await using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var result = new ResultSet { Columns = columns };
        while (await reader.ReadAsync(token))
        {
            var row = new object?[columns.Length];
            for (int i = 0; i < row.Length; i++)
                row[i] = ReadValue(reader, i);
            result.Rows.Add(row);
        }
        return result;
    }

    /// <summary>Una sola columna (FORMAT=TREE, JSON): su texto tal cual. Varias columnas (EXPLAIN clásico): tabla de texto.</summary>
    private static string FormatPlanResult(ResultSet result)
    {
        if (result.Columns.Length == 1)
            return string.Join(Environment.NewLine, result.Rows.Select(r => CellText.Format(r[0]))).TrimEnd();
        return FormatTable(result);
    }

    /// <summary>EXPLAIN QUERY PLAN de SQLite (id, parent, detail) dibujado como lo hace el cliente sqlite3.</summary>
    private static string FormatSqliteTree(ResultSet result)
    {
        var rows = result.Rows.Select(r => (Id: Convert.ToInt64(r[0]), Parent: Convert.ToInt64(r[1]), Detail: CellText.Format(r[3]))).ToList();
        var sb = new StringBuilder("QUERY PLAN");
        void Render(long parent, string indent)
        {
            var children = rows.Where(r => r.Parent == parent).ToList();
            for (int i = 0; i < children.Count; i++)
            {
                bool last = i == children.Count - 1;
                sb.AppendLine().Append(indent).Append(last ? "`--" : "|--").Append(children[i].Detail);
                Render(children[i].Id, indent + (last ? "   " : "|  "));
            }
        }
        Render(0, "");
        return sb.ToString();
    }

    /// <summary>Tabla de texto con columnas alineadas, para el EXPLAIN clásico.</summary>
    private static string FormatTable(ResultSet result)
    {
        var cells = result.Rows.Select(r => r.Select(v => CellText.Format(v).ReplaceLineEndings(" ")).ToArray()).ToList();
        var widths = result.Columns.Select((c, i) => Math.Max(c.Length, cells.Count == 0 ? 0 : cells.Max(r => r[i].Length))).ToArray();
        string separator = "+" + string.Join("+", widths.Select(w => new string('-', w + 2))) + "+";
        string Row(IReadOnlyList<string> values) => "| " + string.Join(" | ", values.Select((v, i) => v.PadRight(widths[i]))) + " |";

        var lines = new List<string> { separator, Row(result.Columns), separator };
        lines.AddRange(cells.Select(Row));
        lines.Add(separator);
        return string.Join(Environment.NewLine, lines);
    }

    private static string FirstLine(string sql)
    {
        string first = sql.Split('\n')[0].TrimEnd();
        return first.Length < sql.Length ? first + " ..." : first;
    }

    private async Task EnsureOpenAsync(CancellationToken token)
    {
        if (_conn?.State == System.Data.ConnectionState.Open) return;

        if (_conn != null)
            await _conn.DisposeAsync();
        _conn = Profile.CreateConnection(CurrentDatabase);
        if (_conn is Microsoft.Data.SqlClient.SqlConnection sqlServer)
            sqlServer.InfoMessage += (_, e) => _serverMessages?.AppendLine(e.Message);
        if (_conn is AdoNetCore.AseClient.AseConnection sybase)
            sybase.InfoMessage += (_, e) => _serverMessages?.AppendLine((e.Message ?? "").TrimEnd('\r', '\n'));
        await Db.OpenAsync(_conn, token);
    }

    /// <summary>Registro de la ejecución en curso, donde se anotan los mensajes del servidor (PRINT de SQL Server).</summary>
    private StringBuilder? _serverMessages;

    /// <summary>
    /// Línea del editor donde está el error. SQL Server indica la línea dentro del lote; los demás, nada:
    /// se usa la primera línea de la sentencia.
    /// </summary>
    private static int ErrorLine(Exception ex, int statementLine) => ex switch
    {
        Microsoft.Data.SqlClient.SqlException { LineNumber: > 0 } sql => statementLine + sql.LineNumber - 1,
        AdoNetCore.AseClient.AseException { Errors.Count: > 0 } sybase when sybase.Errors[0].LineNum > 0 => statementLine + sybase.Errors[0].LineNum - 1,
        _ => statementLine,
    };

    private async Task<string?> ReadCurrentDatabaseAsync()
    {
        try
        {
            // SQLite no cambia de base: siempre es "main".
            if (Profile.Kind == DbKind.Sqlite || _conn?.State != System.Data.ConnectionState.Open) return null;
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = Db.IsTSql(Profile.Kind) ? "SELECT DB_NAME()" : "SELECT DATABASE()";
            return (await cmd.ExecuteScalarAsync() as string)?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private async Task RunStatementAsync(string sql, List<ResultSet> results, StringBuilder log, CancellationToken token)
    {
        log.AppendLine(Db.Prompt(Profile.Kind) + FirstLine(sql));

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

            var result = new ResultSet { Columns = columns, SourceTable = SingleSourceTable(reader) };
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

    /// <summary>Si todas las columnas del resultado vienen de la misma tabla, su nombre; si no (JOIN, cálculos), null.</summary>
    private static string? SingleSourceTable(DbDataReader reader)
    {
        try
        {
            var tables = reader.GetColumnSchema().Select(c => c.BaseTableName).Distinct().ToList();
            return tables.Count == 1 && !string.IsNullOrEmpty(tables[0]) ? tables[0] : null;
        }
        catch
        {
            return null;
        }
    }

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

    private readonly System.Windows.Threading.DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private void ResultFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        ResultFilterHint.Visibility = ResultFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Se espera a que termine de escribir: filtrar muchas filas en cada tecla sería lento.
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    /// <summary>Muestra u oculta el cuadro de filtro de resultados. Al ocultarlo, se quita el filtro.</summary>
    public void ToggleResultFilter()
    {
        if (ResultFilterBar.Visibility == Visibility.Visible)
        {
            ResultFilterBar.Visibility = Visibility.Collapsed;
            ResultFilter.Text = "";
            ApplyResultFilter();
            return;
        }
        ResultFilterBar.Visibility = Visibility.Visible;
        ResultTabs.SelectedIndex = GridTab;
        Dispatcher.BeginInvoke(() => ResultFilter.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseResultFilter_Click(object sender, RoutedEventArgs e)
    {
        if (ResultFilterBar.Visibility == Visibility.Visible) ToggleResultFilter();
    }

    private void ResultFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        ToggleResultFilter();
    }

    /// <summary>Muestra solo las filas con el texto del filtro en alguna columna (sin distinguir mayúsculas); NULL también cuenta.</summary>
    private void ApplyResultFilter()
    {
        _filterTimer.Stop();
        string filter = ResultFilter.Text.Trim();
        int shown = 0, total = 0;
        foreach (var grid in _grids)
        {
            var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
            view.Filter = filter.Length == 0
                ? null
                : item => item is object?[] row && row.Any(v => CellText.Format(v).Contains(filter, StringComparison.OrdinalIgnoreCase));
            shown += ((CollectionView)view).Count;
            total += ((ICollection<object?[]>)grid.ItemsSource).Count;
        }
        FilterInfo.Text = filter.Length == 0 || _grids.Count == 0 ? "" : $"Mostrando {shown:N0} de {total:N0} filas";
    }

    private void ShowResults(List<ResultSet> results)
    {
        _grids.Clear();
        // Resultados nuevos: el filtro anterior ya no aplica.
        _filterTimer.Stop();
        ResultFilter.Text = "";
        FilterInfo.Text = "";
        ResultFilter.IsEnabled = results.Count > 0;
        SelectionStats = Array.Empty<SelectionStat>();
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
            // Como en SSMS, el clic en el encabezado selecciona la columna; ordenar va en su menú contextual.
            CanUserSortColumns = false,
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
        grid.SelectedCellsChanged += (_, _) => UpdateSelectionStats(grid);

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
        menu.Items.Add(MenuItem("Filtrar resultados (Ctrl+Mayús+L)", ToggleResultFilter));
        menu.Items.Add(MenuItem("Guardar resultados como...", () => ExportAsync(result, grid).Watch("Exportar resultados")));
        MenuIcons.Apply(menu);
        grid.ContextMenu = menu;

        grid.ItemsSource = result.Rows;
        return grid;
    }

    /// <summary>Por encima de estas celdas solo se muestra el recuento, para no congelar la interfaz.</summary>
    private const int MaxStatsCells = 1_000_000;

    /// <summary>Como en Excel: con varias celdas seleccionadas, recuento y, si hay números, suma, promedio, mínimo y máximo.</summary>
    private void UpdateSelectionStats(DataGrid grid)
    {
        var cells = grid.SelectedCells;
        var stats = new List<SelectionStat>();
        if (cells.Count > MaxStatsCells)
        {
            stats.Add(Count(cells.Count));
        }
        else if (cells.Count > 1)
        {
            var columnIndex = grid.Columns.Select((column, index) => (column, index)).ToDictionary(c => c.column, c => c.index);
            int numbers = 0;
            double sum = 0, min = double.MaxValue, max = double.MinValue;
            foreach (var cell in cells)
            {
                if (cell.Item is not object?[] row || !columnIndex.TryGetValue(cell.Column, out int i) || i >= row.Length) continue;
                if (row[i] is not (byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)) continue;
                double value = Convert.ToDouble(row[i], CultureInfo.InvariantCulture);
                numbers++;
                sum += value;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
            stats.Add(Count(cells.Count));
            if (numbers > 0)
            {
                stats.Add(Number("Suma", sum));
                stats.Add(Number("Promedio", sum / numbers));
                stats.Add(Number("Mín", min));
                stats.Add(Number("Máx", max));
            }
        }

        if (stats.SequenceEqual(SelectionStats)) return;
        SelectionStats = stats;
        StateChanged?.Invoke(this);
    }

    private static SelectionStat Count(int count) =>
        new("Recuento", count.ToString("N0", CultureInfo.CurrentCulture), count.ToString(CultureInfo.CurrentCulture));

    /// <summary>
    /// Se muestra redondeado y con separador de miles; se copia con toda su precisión, sin separador de miles,
    /// para que al pegarlo en Excel sea un número. El redondeo a 10 decimales quita el ruido de la suma en coma flotante.
    /// </summary>
    private static SelectionStat Number(string label, double value) =>
        new(label, value.ToString("#,##0.####", CultureInfo.CurrentCulture), Math.Round(value, 10).ToString(CultureInfo.CurrentCulture));

    /// <summary>Estilo compacto común y, propio de los resultados, el clic en el encabezado que selecciona la columna.</summary>
    private void ApplyCompactGridStyles(DataGrid grid)
    {
        GridStyles.ApplyCompact(grid);
        var columnHeaderStyle = new Style(typeof(DataGridColumnHeader), grid.ColumnHeaderStyle);
        columnHeaderStyle.Setters.Add(new EventSetter(ButtonBase.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is DataGridColumnHeader { Column: { } column }) SelectColumns(grid, column);
        })));
        columnHeaderStyle.Setters.Add(new Setter(ContextMenuProperty, BuildColumnHeaderMenu(grid)));
        grid.ColumnHeaderStyle = columnHeaderStyle;
    }

    /// <summary>Columna de referencia para Mayús+clic (rango de columnas), por cuadrícula.</summary>
    private readonly Dictionary<DataGrid, DataGridColumn> _columnAnchor = new();

    /// <summary>Clic: solo esa columna. Ctrl+clic: la añade. Mayús+clic: desde la última columna elegida hasta esta.</summary>
    private void SelectColumns(DataGrid grid, DataGridColumn column)
    {
        var modifiers = Keyboard.Modifiers;
        int first = column.DisplayIndex, last = column.DisplayIndex;
        if (modifiers.HasFlag(ModifierKeys.Shift) && _columnAnchor.TryGetValue(grid, out var anchor) && grid.Columns.Contains(anchor))
        {
            first = Math.Min(anchor.DisplayIndex, column.DisplayIndex);
            last = Math.Max(anchor.DisplayIndex, column.DisplayIndex);
        }
        else
        {
            _columnAnchor[grid] = column;
        }

        bool add = modifiers.HasFlag(ModifierKeys.Control);
        SelectColumnRange(grid, first, last - first + 1, add);
        grid.Focus();
    }

    /// <summary>
    /// Selecciona columnas completas. DataGrid no tiene API pública para hacerlo de una vez y añadir celda a celda
    /// es muy lento con muchas filas, así que se usa la misma vía interna que SelectAllCells (con respaldo si cambia).
    /// </summary>
    private static void SelectColumnRange(DataGrid grid, int firstDisplayIndex, int count, bool add)
    {
        if (!add) grid.UnselectAllCells();
        int rows = grid.Items.Count;
        if (rows == 0) return;

        const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic;
        var update = typeof(DataGrid).GetMethod("UpdateSelectedCells", Internal, Type.EmptyTypes);
        var selected = typeof(DataGrid).GetField("_selectedCells", Internal)?.GetValue(grid);
        var addRegion = selected?.GetType().GetMethod("AddRegion", Internal, new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
        if (update != null && addRegion != null)
        {
            using ((IDisposable)update.Invoke(grid, null)!)
                addRegion.Invoke(selected, new object[] { 0, firstDisplayIndex, rows, count });
            return;
        }

        var columns = grid.Columns.Where(c => c.DisplayIndex >= firstDisplayIndex && c.DisplayIndex < firstDisplayIndex + count).ToList();
        foreach (var item in grid.Items)
            foreach (var column in columns)
                grid.SelectedCells.Add(new DataGridCellInfo(item, column));
    }

    /// <summary>Clic derecho en un encabezado: ordenar por esa columna o seleccionarla.</summary>
    private ContextMenu BuildColumnHeaderMenu(DataGrid grid)
    {
        var menu = new ContextMenu();
        DataGridColumn? Target() => (menu.PlacementTarget as DataGridColumnHeader)?.Column;

        void Add(string text, Action<DataGridColumn> action)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => { if (Target() is { } column) action(column); };
            menu.Items.Add(item);
        }

        Add("Ordenar ascendente", column => SortBy(grid, column, ListSortDirection.Ascending));
        Add("Ordenar descendente", column => SortBy(grid, column, ListSortDirection.Descending));
        Add("Quitar el orden", column => SortBy(grid, column, null));
        menu.Items.Add(new Separator());
        Add("Seleccionar la columna", column => SelectColumnRange(grid, column.DisplayIndex, 1, add: false));
        MenuIcons.Apply(menu);
        return menu;
    }

    private static void SortBy(DataGrid grid, DataGridColumn column, ListSortDirection? direction)
    {
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        view.SortDescriptions.Clear();
        foreach (var other in grid.Columns) other.SortDirection = null;
        if (direction is { } dir)
        {
            view.SortDescriptions.Add(new SortDescription(column.SortMemberPath, dir));
            column.SortDirection = dir;
        }
    }

    /// <summary>Guarda un resultado como .csv (comas) o .txt (tabuladores), con encabezados.</summary>
    /// <summary>Guarda las filas visibles de la cuadrícula (con su filtro y orden) en el formato elegido.</summary>
    private async Task ExportAsync(ResultSet result, DataGrid grid)
    {
        var owner = Window.GetWindow(this)!;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = ResultExporter.DialogFilter,
            FileName = result.SourceTable ?? "resultados",
            DefaultExt = ".xlsx",
            AddExtension = true,
        };
        if (dialog.ShowDialog(owner) != true) return;

        string path = dialog.FileName;
        var rows = CollectionViewSource.GetDefaultView(grid.ItemsSource).Cast<object?[]>().ToList();
        int total = rows.Count;
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
            await Task.Run(() => ResultExporter.Export(path, result.Columns, rows, result.SourceTable, Profile.Kind, progress, token));
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(path); } catch { }
        }
        catch (Exception ex)
        {
            owner.IsEnabled = true;
            Errors.Show(owner, "No se pudo guardar el archivo", ex);
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

/// <summary>Textos del panel de búsqueda del editor (Ctrl+F) en español.</summary>
public class SpanishSearchLocalization : ICSharpCode.AvalonEdit.Search.Localization
{
    public override string MatchCaseText => "Coincidir mayúsculas y minúsculas";
    public override string MatchWholeWordsText => "Palabra completa";
    public override string UseRegexText => "Expresión regular";
    public override string FindNextText => "Buscar siguiente (F3)";
    public override string FindPreviousText => "Buscar anterior (Mayús+F3)";
    public override string ErrorText => "Error: ";
    public override string NoMatchesFoundText => "No se encontraron coincidencias";
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
