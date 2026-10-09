using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;

namespace MySmdb;

/// <summary>
/// Historial de consultas ejecutadas (ver QueryHistory): lista con filtro por texto y por conexión, vista previa
/// de la consulta elegida y opción de reabrirla en una pestaña nueva. No modal: se puede dejar abierta.
/// </summary>
public class HistoryWindow : Window
{
    private const string AllConnections = "Todas las conexiones";

    private readonly Action<HistoryEntry> _open;
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Single,
        SelectionUnit = DataGridSelectionUnit.FullRow,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        BorderThickness = new Thickness(0),
        CanUserAddRows = false,
    };
    private readonly TextBox _filter = new() { Width = 240, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Muestra solo las consultas que contienen este texto" };
    private readonly ComboBox _connection = new() { Width = 200, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly TextEditor _preview = new()
    {
        IsReadOnly = true,
        FontFamily = new System.Windows.Media.FontFamily(AppSettings.Current.EditorFont),
        FontSize = AppSettings.Current.EditorFontSize,
        Padding = new Thickness(4, 2, 4, 2),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL"),
    };
    private readonly Button _openButton, _copyButton, _clearButton;
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private List<HistoryEntry> _all = new();
    private bool _loading;

    private HistoryEntry? Selected => _grid.SelectedItem as HistoryEntry;

    /// <param name="open">Abre la consulta de una entrada en una pestaña nueva de la ventana principal.</param>
    public HistoryWindow(Window owner, Action<HistoryEntry> open)
    {
        _open = open;
        Owner = owner;
        Icon = owner.Icon;
        Title = "Historial de consultas";
        Width = 1100;
        Height = 640;
        MinWidth = 1020;   // lo que ocupan el filtro y los cuatro botones de arriba
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        RollUp.Attach(this);   // minimizar la pliega a su barra de título, dentro de la aplicación

        GridStyles.ApplyCompact(_grid);
        _grid.FontSize = AppSettings.Current.GridFontSize;
        void Column(string header, string path, double width, bool fill = false) => _grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path),
            Width = fill ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
        });
        Column("Fecha", nameof(HistoryEntry.WhenText), 135);
        Column("Conexión", nameof(HistoryEntry.Connection), 150);
        Column("Base", nameof(HistoryEntry.Database), 110);
        Column("Segundos", nameof(HistoryEntry.SecondsText), 70);
        Column("Filas", nameof(HistoryEntry.RowsText), 70);
        Column("Resultado", nameof(HistoryEntry.Outcome), 85);
        Column("Consulta", nameof(HistoryEntry.Summary), 0, fill: true);
        _grid.SelectionChanged += (_, _) => ShowSelected();
        _grid.MouseDoubleClick += (_, e) =>
        {
            // Solo sobre una fila: el doble clic en una cabecera ordena, no abre.
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(_grid, source) is DataGridRow) OpenSelected();
        };
        _grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            OpenSelected();
        };

        // Los mismos colores que el editor de consultas, que cambian solos con el tema.
        _preview.SetResourceReference(BackgroundProperty, "Brush.EditorBackground");
        _preview.SetResourceReference(ForegroundProperty, "Brush.EditorForeground");
        _preview.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "Brush.EditorLineNumbers");
        _preview.TextArea.SetResourceReference(TextArea.SelectionBrushProperty, "Brush.EditorSelection");
        _preview.TextArea.SelectionForeground = null;
        _preview.TextArea.SelectionBorder = null;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");

        Button MakeButton(string text, string toolTip, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = toolTip };
            button.Click += (_, _) => action();
            return button;
        }
        _openButton = MakeButton("Abrir en pestaña nueva", "También con doble clic o Intro", OpenSelected);
        _copyButton = MakeButton("Copiar", "Copia el texto de la consulta", () => { if (Selected != null) Clipboard.SetText(Selected.Sql); });
        _clearButton = MakeButton("Borrar historial", "Elimina todas las consultas guardadas (pide confirmación)", ClearAll);
        var refresh = MakeButton("Actualizar", "Vuelve a leer el historial (F5)", Reload);

        var top = new DockPanel { Margin = new Thickness(10, 8, 10, 8), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(new TextBlock { Text = "Filtrar:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        left.Children.Add(_filter);
        left.Children.Add(_connection);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(refresh);
        right.Children.Add(_openButton);
        right.Children.Add(_copyButton);
        right.Children.Add(_clearButton);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(left);
        top.Children.Add(right);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 80 });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 60 });
        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        splitter.SetResourceReference(BackgroundProperty, "Brush.Splitter");
        Grid.SetRow(splitter, 1);
        Grid.SetRow(_preview, 2);
        body.Children.Add(_grid);
        body.Children.Add(splitter);
        body.Children.Add(_preview);

        // El recuento va abajo: arriba, junto a los botones, les quitaba sitio en una ventana estrecha.
        _status.Margin = new Thickness(10, 4, 10, 6);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(body);
        Content = root;

        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFilter(); };
        _filter.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        _connection.SelectionChanged += (_, _) => { if (!_loading) ApplyFilter(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { e.Handled = true; Reload(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        Loaded += (_, _) => { Reload(); _filter.Focus(); };
        Closed += (_, _) => _filterTimer.Stop();
    }

    /// <summary>Vuelve a leer el historial del disco, conservando el filtro.</summary>
    public void Reload()
    {
        _all = QueryHistory.Load();

        _loading = true;
        string? chosen = _connection.SelectedItem as string;
        var names = new List<string> { AllConnections };
        names.AddRange(_all.Select(e => e.Connection).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
        _connection.ItemsSource = names;
        _connection.SelectedItem = chosen != null && names.Contains(chosen) ? chosen : AllConnections;
        _loading = false;

        _clearButton.IsEnabled = _all.Count > 0;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string text = _filter.Text.Trim();
        string? connection = _connection.SelectedItem as string;
        var shown = _all
            .Where(e => connection is null or AllConnections || e.Connection == connection)
            .Where(e => text.Length == 0 || e.Sql.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        _grid.ItemsSource = shown;
        if (shown.Count > 0) _grid.SelectedIndex = 0;
        _status.Text = !AppSettings.Current.HistoryEnabled ? $"{shown.Count:N0} de {_all.Count:N0} · el historial está desactivado (menú Herramientas)"
            : _all.Count == 0 ? "Aún no hay consultas en el historial."
            : $"{shown.Count:N0} de {_all.Count:N0} consultas";
        ShowSelected();
    }

    private void ShowSelected()
    {
        var entry = Selected;
        _preview.Text = entry == null ? "" : (entry.Error != null ? $"-- {entry.Error}\n\n" : "") + entry.Sql;
        _preview.ScrollToHome();
        _openButton.IsEnabled = _copyButton.IsEnabled = entry != null;
    }

    private void OpenSelected()
    {
        if (Selected is { } entry) _open(entry);
    }

    private void ClearAll()
    {
        var answer = MessageBox.Show(this, $"¿Borrar las {_all.Count:N0} consultas del historial? No se puede deshacer.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        QueryHistory.Clear();
        Reload();
    }
}
