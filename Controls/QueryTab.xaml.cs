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

/// <summary>
/// Un script abierto: su texto, su archivo y si tiene cambios. Lo comparten todas las pestañas que son vistas
/// del mismo script ("Duplicar vista"): lo que se escribe o se guarda en una vale para todas.
/// </summary>
public sealed class ScriptFile
{
    public ICSharpCode.AvalonEdit.Document.TextDocument Document { get; } = new();
    public required string DefaultTitle { get; init; }
    public string? FilePath { get; set; }
    public bool IsDirty { get; private set; }
    /// <summary>Codificación con la que se leyó el archivo: se guarda con la misma, para no cambiársela a quien lo comparte.</summary>
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);
    /// <summary>Pestañas abiertas sobre este script.</summary>
    public List<QueryTab> Views { get; } = new();

    /// <summary>Cambió el archivo, el estado de "con cambios" o el número de vistas.</summary>
    public event Action? Changed;

    public void SetDirty(bool dirty)
    {
        IsDirty = dirty;
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();
}

public partial class QueryTab : UserControl
{
    /// <summary>Tope de filas por conjunto de resultados, para no agotar la memoria. (Las pruebas lo bajan.)</summary>
    public static int MaxRows { get; set; } = 500_000;

    /// <summary>Alto máximo de cada cuadrícula cuando hay varios resultados apilados.</summary>
    private const double MaxStackedGridHeight = 260;
    /// <summary>A partir de estas filas, guardar a archivo muestra una barra de progreso.</summary>
    private const int ExportProgressThreshold = 5000;

    private readonly int _viewNumber;
    private readonly List<DataGrid> _grids = new();
    private DbConnection? _conn;
    private CancellationTokenSource? _cts;

    public ConnectionProfile Profile { get; private set; }
    public string? CurrentDatabase { get; private set; }
    /// <summary>El script que muestra; compartido con las demás vistas del mismo script.</summary>
    public ScriptFile Script { get; }
    public string? FilePath => Script.FilePath;
    public bool IsDirty => Script.IsDirty;
    public bool IsRunning { get; private set; }
    public string StatusText { get; private set; } = "Listo";
    public string RowsText { get; private set; } = "";
    /// <summary>Filas obtenidas en la última ejecución (para copiarlas desde la barra de estado); null si no hay.</summary>
    public int? RowCount { get; private set; }
    public string TimeText { get; private set; } = "";

    public string Title => FilePath != null ? Path.GetFileName(FilePath) : Script.DefaultTitle;
    public bool HasText => !string.IsNullOrWhiteSpace(Editor.Text);
    /// <summary>Es la única pestaña abierta sobre su script: cerrarla es cerrar el script.</summary>
    public bool IsLastView => Script.Views.Count <= 1;
    /// <summary>":2", ":3"... para distinguir las vistas de un mismo script; vacío si solo hay una.</summary>
    public string ViewSuffix => Script.Views.Count > 1 ? $":{_viewNumber}" : "";

    /// <summary>Cambió algo que la ventana principal muestra (título, estado, base de datos).</summary>
    public event Action<QueryTab>? StateChanged;

    /// <summary>
    /// El resaltado base es el de T-SQL, compartido por todas las pestañas; aquí se le añaden los identificadores
    /// entre acentos graves. Los comentarios con '#' son solo de MySQL y van por pestaña (ver ApplyDialect):
    /// en SQL Server y Sybase '#' es el prefijo de las tablas temporales.
    /// </summary>
    static QueryTab()
    {
        var definition = HighlightingManager.Instance.GetDefinition("TSQL");
        if (definition == null) return;

        // Sin color propio: evita que una palabra clave dentro de `nombre` se resalte.
        definition.MainRuleSet.Spans.Add(new HighlightingSpan
        {
            StartExpression = new Regex("`"),
            EndExpression = new Regex("`"),
            RuleSet = new HighlightingRuleSet(),
        });
    }

    private readonly HashCommentColorizer _hashComments = new();

    /// <summary>Ajusta el resaltado al motor de la conexión: los comentarios con '#' solo en MySQL.</summary>
    private void ApplyDialect()
    {
        var transformers = Editor.TextArea.TextView.LineTransformers;
        bool wanted = Profile.Kind == DbKind.MySql, present = transformers.Contains(_hashComments);
        if (wanted && !present) transformers.Add(_hashComments);
        else if (!wanted && present) transformers.Remove(_hashComments);
        Editor.TextArea.TextView.Redraw();
    }

    public QueryTab(ConnectionProfile profile, string? database, string defaultTitle)
        : this(profile, database, new ScriptFile { DefaultTitle = defaultTitle })
    {
    }

    /// <summary>Pestaña sobre un script que puede estar ya abierto en otra: comparten texto, archivo y deshacer.</summary>
    public QueryTab(ConnectionProfile profile, string? database, ScriptFile script)
    {
        InitializeComponent();
        Profile = profile;
        CurrentDatabase = database;
        Script = script;
        Editor.Document = script.Document;
        _viewNumber = script.Views.Count == 0 ? 1 : script.Views.Max(v => v._viewNumber) + 1;
        script.Views.Add(this);
        script.Changed += Script_Changed;

        UpdateResultsPaneButtons();
        // Al pasar a otra pestaña del panel (a mano o porque llegó un resultado), un panel minimizado se despliega.
        // La comprobación descarta los cambios de selección de las cuadrículas de dentro, que también llegan aquí.
        ResultTabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, ResultTabs)) RevealResults(); };

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
        ApplyDialect();
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
            // Con las sugerencias automáticas desactivadas no aparece nada al escribir, tampoco tras un punto
            // (Ctrl+Espacio sigue funcionando).
            if (!AppSettings.Current.AutoCompleteEnabled) return;
            if (e.Text == ".")
                ShowCompletion(forced: true);
            else if (_completion == null && e.Text.Length == 1
                     && (char.IsLetter(e.Text[0]) || e.Text[0] == '_'))
                ShowCompletion(forced: false);
        };
        // Al soltar un nombre arrastrado desde el explorador, el editor ya lo insertó donde estaba el ratón, pero lo
        // deja seleccionado: se quita la selección (lo siguiente que se escriba no debe sustituirlo), el cursor
        // queda detrás del nombre y el teclado pasa al editor. Se registra para ejecutarse después del editor.
        Editor.TextArea.AddHandler(DragDrop.DropEvent, new DragEventHandler((_, e) =>
        {
            if (!e.Data.GetDataPresent(ExplorerDrag.Format)) return;
            int end = Editor.SelectionStart + Editor.SelectionLength;
            Editor.SelectionLength = 0;
            Editor.CaretOffset = Math.Min(end, Editor.Document.TextLength);
            Window.GetWindow(this)?.Activate();
            Editor.Focus();
        }), handledEventsToo: true);
        Editor.TextChanged += (_, _) =>
        {
            // Con varias vistas el aviso llega a todas; la primera marca el script y las demás ya lo ven marcado.
            if (!IsDirty) Script.SetDirty(true);
        };
        // Para que los botones Deshacer/Rehacer se activen y desactiven según haya algo que deshacer o rehacer.
        Script.Document.UndoStack.PropertyChanged += UndoStack_PropertyChanged;
    }

    private void Script_Changed() => StateChanged?.Invoke(this);

    private void UndoStack_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "CanUndo" or "CanRedo") StateChanged?.Invoke(this);
    }

    /// <summary>
    /// Deja a la vista la primera aparición del texto, sin seleccionarla: con el texto seleccionado, lo siguiente
    /// que se escribiera o pegara lo sustituiría. Devuelve la línea, o 0 si no está.
    /// </summary>
    public int ShowFirst(string text)
    {
        int index = text.Length == 0 ? -1 : Editor.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return 0;
        int line = Editor.Document.GetLineByOffset(index).LineNumber;
        // El editor puede no haberse medido aún (pestaña recién creada): se desplaza cuando ya tiene tamaño.
        Dispatcher.BeginInvoke(() => Editor.ScrollTo(line, 1), System.Windows.Threading.DispatcherPriority.Loaded);
        return line;
    }

    /// <summary>
    /// Pone el cursor al final, en una línea nueva y sin nada seleccionado: tras generar un script, lo que se
    /// escriba o se pegue a continuación va después, no en medio.
    /// </summary>
    public void MoveCaretToEnd()
    {
        Editor.SelectionLength = 0;
        Editor.CaretOffset = Editor.Document.TextLength;
    }

    /// <summary>Coloca el cursor y el desplazamiento donde los tiene otra vista del mismo script.</summary>
    public void ShowSamePlaceAs(QueryTab other)
    {
        Editor.CaretOffset = Math.Min(other.Editor.CaretOffset, Editor.Document.TextLength);
        // El editor aún no se ha medido: se desplaza cuando ya tiene tamaño.
        Dispatcher.BeginInvoke(() => Editor.ScrollToVerticalOffset(other.Editor.VerticalOffset), System.Windows.Threading.DispatcherPriority.Loaded);
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
        if (cursor < 0) cursor = text.Length;   // un fragmento sin marca deja el cursor al final
        else text = text.Remove(cursor, 1);

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
        Script.SetDirty(false);   // avisa a todas las vistas del script, esta incluida
    }

    public void LoadFile(string path)
    {
        Editor.Text = TextFiles.Read(path, out var encoding);
        Script.Encoding = encoding;
        Editor.Document.UndoStack.ClearAll();
        Script.FilePath = path;
        Script.SetDirty(false);
    }

    public void SaveFile(string path)
    {
        // Si el texto ya no cabe en la codificación original (se añadió, p. ej., un emoji a un archivo ANSI), UTF-8.
        if (!TextFiles.CanEncode(Editor.Text, Script.Encoding)) Script.Encoding = new UTF8Encoding(false);
        File.WriteAllText(path, Editor.Text, Script.Encoding);
        Script.FilePath = path;
        Script.SetDirty(false);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        // El controlador de SQLite ejecuta de forma síncrona y no atiende la señal de cancelación: hay que
        // interrumpir la sentencia en curso directamente en el motor.
        try
        {
            if (IsRunning && _conn is Microsoft.Data.Sqlite.SqliteConnection { State: System.Data.ConnectionState.Open, Handle: { } handle })
                SQLitePCL.raw.sqlite3_interrupt(handle);
        }
        catch
        {
            // Si no se puede interrumpir, la consulta termina por su cuenta, como antes.
        }
    }

    public void Close()
    {
        // Deja de ser una vista de su script; las que queden actualizan su título (":2" deja de hacer falta).
        if (Script.Views.Remove(this))
        {
            Script.Changed -= Script_Changed;
            Script.Document.UndoStack.PropertyChanged -= UndoStack_PropertyChanged;
            Script.NotifyChanged();
        }
        Cancel();
        var conn = _conn;
        _conn = null;
        if (conn != null)
            _ = Task.Run(async () => { try { await conn.DisposeAsync(); } catch { } });
    }

    /// <summary>
    /// Suelta la conexión abierta: la próxima ejecución abre una nueva con los datos actuales del perfil.
    /// Para cuando la conexión se editó (otro servidor, puerto o usuario) con la pestaña ya abierta.
    /// </summary>
    public void ResetConnection()
    {
        if (IsRunning) return;
        var conn = _conn;
        _conn = null;
        if (conn != null)
            _ = Task.Run(async () => { try { await conn.DisposeAsync(); } catch { } });
        ApplyDialect();   // el perfil puede haber cambiado de motor
        StateChanged?.Invoke(this);
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
        ApplyDialect();   // la otra conexión puede ser de otro motor
        StatusText = profile.IsOffline ? "Conexión cerrada. El script sigue abierto; al ejecutar se pedirá la conexión." : $"Pestaña cambiada a {profile.Name}.";
        StateChanged?.Invoke(this);
    }

    /// <summary>La pestaña se abrió sin conexión: se edita y se guarda, pero para ejecutar hay que conectarla.</summary>
    public bool IsOffline => Profile.IsOffline;

    /// <summary>Deja constancia de que no se hizo lo pedido porque la pestaña sigue sin conexión.</summary>
    public void ReportNotConnected(string action)
    {
        Messages.Text = $"Error: no se pudo {action} porque esta pestaña no tiene conexión.\n"
            + "Conéctate a un servidor (Archivo > Conectar) o elige una base de datos en la lista de la barra de herramientas.";
        ResultTabs.SelectedIndex = MessagesTab;
        RevealResults();
        StatusText = "Sin conexión.";
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

    // ---------- Panel de resultados: normal, minimizado o maximizado ----------

    public enum ResultsPaneState { Normal, Minimized, Maximized }

    private GridLength _editorHeight = new(1, GridUnitType.Star), _resultsHeight = new(1, GridUnitType.Star);

    public ResultsPaneState ResultsPane { get; private set; }

    /// <summary>
    /// Minimizado: del panel solo queda su fila de pestañas y el editor ocupa el resto (para ver una consulta larga).
    /// Maximizado: el panel ocupa todo y el editor se oculta (para recorrer datos, mensajes o el plan).
    /// Al volver a Normal se recupera el reparto que había.
    /// </summary>
    public void SetResultsPane(ResultsPaneState state)
    {
        if (state == ResultsPane) return;
        // El reparto elegido con el separador solo existe en Normal: se guarda al salir de ahí.
        if (ResultsPane == ResultsPaneState.Normal)
            (_editorHeight, _resultsHeight) = (EditorRow.Height, ResultsRow.Height);
        ResultsPane = state;

        bool normal = state == ResultsPaneState.Normal;
        Editor.Visibility = state == ResultsPaneState.Maximized ? Visibility.Collapsed : Visibility.Visible;
        ResultsSplitter.Visibility = normal ? Visibility.Visible : Visibility.Collapsed;
        EditorRow.MinHeight = state == ResultsPaneState.Maximized ? 0 : 60;
        ResultsRow.MinHeight = normal ? 60 : 0;
        switch (state)
        {
            case ResultsPaneState.Minimized:
                // Alto de la fila de pestañas: el contenido queda fuera de la vista.
                double strip = ResultTabs.Items.OfType<TabItem>().Select(t => t.ActualHeight).DefaultIfEmpty(0).Max();
                EditorRow.Height = new GridLength(1, GridUnitType.Star);
                ResultsRow.Height = new GridLength(strip > 0 ? strip + 1 : 33);
                break;
            case ResultsPaneState.Maximized:
                EditorRow.Height = new GridLength(0);
                ResultsRow.Height = new GridLength(1, GridUnitType.Star);
                break;
            default:
                (EditorRow.Height, ResultsRow.Height) = (_editorHeight, _resultsHeight);
                break;
        }
        UpdateResultsPaneButtons();
        if (state != ResultsPaneState.Maximized) FocusEditor();
    }

    /// <summary>Minimiza el panel; si ya lo estaba, lo devuelve a su tamaño.</summary>
    public void ToggleMinimizeResults() =>
        SetResultsPane(ResultsPane == ResultsPaneState.Minimized ? ResultsPaneState.Normal : ResultsPaneState.Minimized);

    /// <summary>Maximiza el panel; si ya lo estaba, lo devuelve a su tamaño.</summary>
    public void ToggleMaximizeResults() =>
        SetResultsPane(ResultsPane == ResultsPaneState.Maximized ? ResultsPaneState.Normal : ResultsPaneState.Maximized);

    private void MinimizeResults_Click(object sender, RoutedEventArgs e) => ToggleMinimizeResults();
    private void MaximizeResults_Click(object sender, RoutedEventArgs e) => ToggleMaximizeResults();

    /// <summary>Cada botón muestra lo que hará: minimizar/maximizar, o restaurar si ese es el estado actual.</summary>
    private void UpdateResultsPaneButtons()
    {
        bool minimized = ResultsPane == ResultsPaneState.Minimized, maximized = ResultsPane == ResultsPaneState.Maximized;
        MinimizeResultsButton.Content = ExplorerIcons.Create(minimized ? ExplorerIcon.PaneRestore : ExplorerIcon.PaneMinimize);
        MinimizeResultsButton.ToolTip = minimized ? "Restaurar el panel de resultados (Ctrl+R)" : "Minimizar el panel de resultados: deja todo el espacio a la consulta (Ctrl+R)";
        MaximizeResultsButton.Content = ExplorerIcons.Create(maximized ? ExplorerIcon.PaneRestore : ExplorerIcon.PaneMaximize);
        MaximizeResultsButton.ToolTip = maximized ? "Restaurar el panel de resultados (Ctrl+Mayús+R)" : "Maximizar el panel de resultados: oculta la consulta (Ctrl+Mayús+R)";
    }

    /// <summary>Hay algo nuevo que ver (resultados, mensajes, plan): un panel minimizado vuelve a su tamaño.</summary>
    private void RevealResults()
    {
        if (ResultsPane == ResultsPaneState.Minimized) SetResultsPane(ResultsPaneState.Normal);
    }

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
        if (IsRunning) return;
        if (IsOffline) { ReportNotConnected("ejecutar la consulta"); return; }
        if (ScriptToRun(Messages, MessagesTab) is not { } script) return;
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
        bool failed = false, cancelled = false, opened = false, transportFailed = false;
        string? database = CurrentDatabase;
        // Para el historial: lo que se ejecuta (la selección o todo), dónde y el primer error.
        string executed = Editor.SelectionLength > 0 ? Editor.SelectedText : Editor.Text;
        string? startDatabase = database, firstError = null;
        var watch = Stopwatch.StartNew();

        try
        {
            await Task.Run(async () =>
            {
                if (await EnsureOpenAsync(token)) log.AppendLine(ReconnectedNotice).AppendLine();
                opened = true;
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
                            firstError = $"Error {Db.ErrorCode(ex)}, línea {ErrorLine(ex, statement.Line + lineOffset)}: {ex.Message.ReplaceLineEndings(" ")}";
                            log.AppendLine(firstError);
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
            // No es un error de la consulta: falló abrir la conexión o se cortó a mitad.
            firstError = "Error: " + ex.Message.ReplaceLineEndings(" ");
            log.AppendLine(firstError);
            failed = transportFailed = true;
        }

        watch.Stop();
        _cts.Dispose();
        _cts = null;
        _lastUsed = Environment.TickCount64;
        // Si el fallo dejó la conexión inservible (se cortó a mitad), se suelta: la próxima ejecución abre otra,
        // sin tener que desconectar y volver a conectar a mano.
        if (failed && !cancelled && (transportFailed || _conn?.State != System.Data.ConnectionState.Open))
        {
            DropConnection();
            if (opened) log.AppendLine("La conexión se perdió. Vuelve a ejecutar: se abrirá de nuevo automáticamente.");
        }

        QueryHistory.Add(new HistoryEntry(DateTime.Now, Profile.Name, startDatabase, executed.Trim(), watch.Elapsed.TotalSeconds,
            cancelled ? null : results.Sum(r => r.Rows.Count),
            cancelled ? HistoryEntry.Cancelled : failed ? HistoryEntry.Failed : HistoryEntry.Ok, firstError));

        if (cancelled) log.AppendLine("La consulta fue cancelada por el usuario.");
        log.AppendLine();
        log.AppendLine($"Hora de finalización: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        try
        {
            ShowResults(results);
            Messages.Text = log.ToString();
            ResultTabs.SelectedIndex = results.Count > 0 && !failed ? GridTab : MessagesTab;
            RevealResults();
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

    /// <summary>Oculta la pestaña del plan y vuelve a los resultados.</summary>
    private void ClosePlan_Click(object sender, RoutedEventArgs e)
    {
        if (ResultTabs.SelectedIndex == PlanTab) ResultTabs.SelectedIndex = GridTab;
        PlanTabItem.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Muestra el plan de ejecución estimado de la selección o de todo el contenido, sin ejecutar las consultas.
    /// MySQL: EXPLAIN FORMAT=TREE (o el EXPLAIN clásico en MariaDB y MySQL anteriores a 8.0.16).
    /// SQLite: EXPLAIN QUERY PLAN, dibujado como árbol.
    /// </summary>
    public async Task ExplainAsync()
    {
        if (IsRunning) return;
        if (IsOffline) { ReportNotConnected("obtener el plan de ejecución"); return; }
        // La pestaña del plan solo existe a la vista desde que se pide el primero.
        PlanTabItem.Visibility = Visibility.Visible;
        if (ScriptToRun(PlanView, PlanTab) is not { } script) return;
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
        RevealResults();
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
                if (await EnsureOpenAsync(token)) output.AppendLine("-- " + ReconnectedNotice).AppendLine();
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
            DropConnection();   // falló abrirla o se cortó: la próxima vez se abre otra
        }

        watch.Stop();
        _cts.Dispose();
        _cts = null;
        _lastUsed = Environment.TickCount64;
        if (_conn != null && _conn.State != System.Data.ConnectionState.Open) DropConnection();
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
    private static readonly Regex ExecStatement = new(@"^\s*(EXEC|EXECUTE)\b", RegexOptions.IgnoreCase);

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

        // Un lote que llama a procedimientos no se envía: con NOEXEC el servidor no muestra su plan, y si esa
        // opción no llegara a aplicarse, pedir el plan acabaría ejecutándolos.
        if (SqlSplitter.SplitTSqlForReview(new SqlStatement(batch, 1)).Any(s => ExecStatement.IsMatch(s.Text)))
            return ("(sin plan: el lote ejecuta procedimientos almacenados; su plan no se puede obtener sin ejecutarlos)", null);

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

    /// <summary>Tras este tiempo sin usarse, antes de ejecutar se comprueba que la conexión sigue viva. (Las pruebas lo bajan.)</summary>
    public static int IdleCheckSeconds { get; set; } = 30;
    /// <summary>Lo que se espera la respuesta a esa comprobación; pasado este tiempo, la conexión se da por caída.</summary>
    private const int PingTimeoutSeconds = 5;
    /// <summary>Texto que queda en Mensajes cuando hubo que reabrir la conexión.</summary>
    private const string ReconnectedNotice =
        "Aviso: la conexión se había cerrado (por inactividad o por un corte de red) y se volvió a abrir sola. " +
        "Se perdió lo propio de la sesión anterior: variables, tablas temporales y transacciones sin confirmar.";

    /// <summary>Momento del último uso de la conexión (Environment.TickCount64).</summary>
    private long _lastUsed;

    /// <summary>
    /// Deja la conexión abierta y utilizable. Una conexión que lleva un rato sin usarse puede haber muerto sin que
    /// el controlador lo sepa (el servidor la cerró por inactividad, se cayó el túnel SSH, se cortó la red): antes
    /// de ejecutar se comprueba y, si no responde, se abre otra, sin que haya que desconectar y volver a conectar.
    /// Devuelve true si hubo que reabrir una conexión que ya existía.
    /// </summary>
    private async Task<bool> EnsureOpenAsync(CancellationToken token)
    {
        bool reopened = _conn != null;
        if (_conn?.State == System.Data.ConnectionState.Open)
        {
            bool recent = Environment.TickCount64 - _lastUsed < IdleCheckSeconds * 1000;
            if (recent || await IsAliveAsync(_conn))
            {
                _lastUsed = Environment.TickCount64;
                return false;
            }
        }

        DropConnection();
        try
        {
            await OpenNewAsync(token);
        }
        catch (Exception) when (reopened && Profile.UseSsh && !token.IsCancellationRequested)
        {
            // El túnel SSH puede haber quedado a medio caer: se descarta y se prueba una vez con uno nuevo.
            // Solo si antes había conexión (las credenciales ya funcionaron): un primer intento fallido no se
            // repite, para no doblar los intentos con una contraseña incorrecta.
            DropConnection();
            SshTunnels.Close(Profile);
            await OpenNewAsync(token);
        }
        _lastUsed = Environment.TickCount64;
        return reopened;
    }

    private async Task OpenNewAsync(CancellationToken token)
    {
        _conn = Profile.CreateConnection(CurrentDatabase);
        if (_conn is Microsoft.Data.SqlClient.SqlConnection sqlServer)
            sqlServer.InfoMessage += (_, e) => _serverMessages?.AppendLine(e.Message);
        if (_conn is AdoNetCore.AseClient.AseConnection sybase)
            sybase.InfoMessage += (_, e) => _serverMessages?.AppendLine((e.Message ?? "").TrimEnd('\r', '\n'));
        await Db.OpenAsync(_conn, token);
    }

    /// <summary>Suelta la conexión sin esperar a que se cierre: si está caída, cerrarla puede tardar.</summary>
    private void DropConnection()
    {
        var conn = _conn;
        _conn = null;
        if (conn != null)
            _ = Task.Run(async () => { try { await conn.DisposeAsync(); } catch { } });
    }

    /// <summary>¿Responde la conexión? Una consulta mínima con un límite de tiempo corto.</summary>
    private async Task<bool> IsAliveAsync(DbConnection conn)
    {
        if (Profile.Kind == DbKind.Sqlite) return true;   // un archivo local no se cae por inactividad
        var ping = Task.Run(async () =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = PingTimeoutSeconds;
            await cmd.ExecuteScalarAsync();
        });
        if (await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(PingTimeoutSeconds))) != ping)
        {
            _ = ping.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);   // terminará (o fallará) al soltar la conexión
            return false;
        }
        try
        {
            await ping;
            return true;
        }
        catch
        {
            return false;
        }
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
        int maxRows = MaxRows;
        var outcome = await StatementRunner.RunAsync(_conn!, sql, maxRows, results, token, result =>
        {
            // Los datos solo se ven en la cuadrícula; aquí queda el registro de lo ocurrido.
            log.AppendLine($"{result.Rows.Count} filas en el conjunto ({Seconds(watch)})");
            if (result.Truncated)
                log.AppendLine($"Aviso: el resultado se truncó a {maxRows} filas. El resto no se leyó, ni los resultados siguientes de esta sentencia.");
            log.AppendLine();
        });

        if (outcome.Truncated) return;
        if (outcome.RecordsAffected >= 0)
            log.AppendLine($"Consulta OK, {outcome.RecordsAffected} filas afectadas ({Seconds(watch)})").AppendLine();
        else if (!outcome.AnyResultSet)
            log.AppendLine($"Consulta OK ({Seconds(watch)})").AppendLine();
    }

    private static string Seconds(Stopwatch watch) =>
        watch.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s";

    private static object? ReadValue(DbDataReader reader, int index) => StatementRunner.ReadValue(reader, index);

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
        _columnAnchor.Clear();   // si no, las cuadrículas anteriores (y sus filas) seguirían en memoria
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
        // Copiar: la cuadrícula de WPF termina cada fila con un salto de línea, también la última, así que al pegar
        // una sola celda aparecía un salto de más. Se recogen las filas tal como las copia y se deja en el
        // portapapeles el mismo texto sin ese salto final (celdas separadas por tabuladores, filas por saltos).
        var copiedRows = new List<string>();
        grid.CopyingRowClipboardContent += (_, e) =>
            copiedRows.Add(string.Join("\t", e.ClipboardRowContent.Select(cell => cell.Content?.ToString() ?? "")));
        grid.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler((_, e) =>
        {
            if (e.Command == ApplicationCommands.Copy) copiedRows.Clear();
        }), handledEventsToo: true);
        grid.AddHandler(CommandManager.ExecutedEvent, new ExecutedRoutedEventHandler((_, e) =>
        {
            if (e.Command != ApplicationCommands.Copy || copiedRows.Count == 0) return;
            string text = string.Join("\r\n", copiedRows);
            copiedRows.Clear();
            CopyText(text);
        }), handledEventsToo: true);
        // Mayús + rueda: desplazamiento horizontal, como en el navegador o en Excel.
        grid.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Shift || FindChild<ScrollViewer>(grid) is not { } inner) return;
            e.Handled = true;
            inner.ScrollToHorizontalOffset(inner.HorizontalOffset - e.Delta);
        };

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

        // El nombre del campo, para pegarlo en la consulta sin tener que escribirlo.
        Add("Copiar el nombre de la columna", column => CopyText(column.Header as string ?? ""));
        Add("Copiar los nombres de todas las columnas", _ =>
            CopyText(string.Join(", ", grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => c.Header as string ?? ""))));
        menu.Items.Add(new Separator());
        Add("Ordenar ascendente", column => SortBy(grid, column, ListSortDirection.Ascending));
        Add("Ordenar descendente", column => SortBy(grid, column, ListSortDirection.Descending));
        Add("Quitar el orden", column => SortBy(grid, column, null));
        menu.Items.Add(new Separator());
        Add("Seleccionar la columna", column => SelectColumnRange(grid, column.DisplayIndex, 1, add: false));
        MenuIcons.Apply(menu);
        return menu;
    }

    private void CopyText(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true);
        }
        catch (Exception ex)
        {
            // El portapapeles puede estar ocupado por otra aplicación.
            Errors.Show(Window.GetWindow(this), "No se pudo copiar al portapapeles", ex);
        }
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
            try { File.Delete(path); } catch { }   // un archivo a medias parecería un resultado completo
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

/// <summary>
/// Comentarios de MySQL que empiezan por '#': desde ahí hasta el final de la línea, con el color de los comentarios.
/// No cuenta un '#' dentro de una cadena o de un nombre entre comillas.
/// </summary>
public class HashCommentColorizer : ICSharpCode.AvalonEdit.Rendering.DocumentColorizingTransformer
{
    protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
    {
        string text = CurrentContext.Document.GetText(line);
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote != '`') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"' or '`') quote = c;
            // Tras "--" el resto ya es comentario para el resaltado base.
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-') return;
            else if (c == '#')
            {
                var brush = HighlightingManager.Instance.GetDefinition("TSQL")?.GetNamedColor("Comment")?.Foreground?.GetBrush(null) ?? Brushes.Green;
                ChangeLinePart(line.Offset + i, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
                return;
            }
        }
    }
}

/// <summary>Lectura de archivos de texto con la codificación que traigan.</summary>
public static class TextFiles
{
    /// <summary>
    /// Lee el archivo como UTF-8 (con o sin BOM) o UTF-16; si no es UTF-8 válido, como Windows-1252, que es como
    /// guardan los .sql muchas herramientas antiguas. Devuelve la codificación para poder guardarlo igual.
    /// </summary>
    public static string Read(string path, out Encoding encoding)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
        {
            encoding = bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
            return encoding.GetString(bytes, 2, bytes.Length - 2);
        }
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        try
        {
            encoding = new UTF8Encoding(bom);
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding(1252);
            return encoding.GetString(bytes);
        }
    }

    /// <summary>¿Se puede guardar ese texto en esa codificación sin perder caracteres?</summary>
    public static bool CanEncode(string text, Encoding encoding)
    {
        if (encoding is UTF8Encoding or UnicodeEncoding) return true;
        try
        {
            Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
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
        // Con la precisión que tenga el valor: sin fracción, milisegundos, o hasta 7 decimales (datetime2, microsegundos).
        DateTime d => d.ToString(d.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss"
            : d.Ticks % TimeSpan.TicksPerMillisecond == 0 ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
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
