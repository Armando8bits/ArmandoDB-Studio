using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace MySmdb;

/// <summary>
/// Importar datos a una tabla desde CSV/TXT o Excel: se elige el archivo, se empareja cada columna de la tabla
/// con una del archivo (por nombre, automáticamente) y se ve una muestra antes de insertar. Todo o nada.
/// </summary>
public class ImportDialog : Window
{
    private const int PreviewRows = 50;
    private const string Skip = "(no importar)";

    private readonly ConnectionProfile _profile;
    private readonly string _database, _table;
    private readonly IReadOnlyList<ColumnInfo> _columns;
    private readonly TextBox _file = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _headers = new() { Content = "La primera fila tiene los nombres de columna", IsChecked = true, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _delimiter = new() { Width = 130, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _emptyAsNull = new() { Content = "Celdas vacías como NULL", IsChecked = true, Margin = new Thickness(18, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _deleteFirst = new() { Content = "Vaciar la tabla antes de importar", VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _mapping = new();
    private readonly DataGrid _preview = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, BorderThickness = new Thickness(0), CanUserSortColumns = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _progress = new() { Height = 6, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Hidden };
    private readonly Button _import = new() { Content = "Importar", MinWidth = 100, Padding = new Thickness(10, 4, 10, 4), FontWeight = FontWeights.SemiBold, IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Cerrar", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
    private readonly List<ComboBox> _choices = new();
    private ImportSource? _source;
    private CancellationTokenSource? _running;

    /// <summary>Filas insertadas, si se llegó a importar.</summary>
    public long Imported { get; private set; }

    public ImportDialog(Window owner, ConnectionProfile profile, string database, string table, IReadOnlyList<ColumnInfo> columns)
    {
        (_profile, _database, _table, _columns) = (profile, database, table, columns);
        Owner = owner;
        Title = $"Importar datos a {table} — {(profile.Kind == DbKind.Sqlite ? profile.Name : $"{database} ({profile.Name})")}";
        Width = 900;
        Height = 700;
        MinWidth = 640;
        MinHeight = 480;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var browse = new Button { Content = "Examinar...", Margin = new Thickness(6, 4, 0, 4), Padding = new Thickness(10, 2, 10, 2) };
        browse.Click += (_, _) => Browse();
        var fileRow = new DockPanel();
        var fileLabel = new TextBlock { Text = "Archivo:", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(fileLabel, Dock.Left);
        DockPanel.SetDock(browse, Dock.Right);
        fileRow.Children.Add(fileLabel);
        fileRow.Children.Add(browse);
        fileRow.Children.Add(_file);
        _file.IsReadOnly = true;

        foreach (var (text, value) in new[] { ("Automático", DataImporter.AutoDelimiter), ("Coma  ,", ","), ("Punto y coma  ;", ";"), ("Tabulador", "\t"), ("Barra  |", "|") })
            _delimiter.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        _delimiter.SelectedIndex = 0;
        var options = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        options.Children.Add(_headers);
        options.Children.Add(new TextBlock { Text = "Separador:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        options.Children.Add(_delimiter);
        options.Children.Add(_emptyAsNull);
        options.Children.Add(_deleteFirst);
        _headers.Click += (_, _) => LoadFile();
        _delimiter.SelectionChanged += (_, _) => LoadFile();

        var top = new StackPanel();
        top.Children.Add(fileRow);
        top.Children.Add(options);

        _import.Click += async (_, _) => await ImportAsync();
        _cancel.Click += (_, _) =>
        {
            if (_running != null) _running.Cancel();
            else Close();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_import);
        buttons.Children.Add(_cancel);
        var bottomRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        bottomRow.Children.Add(buttons);
        bottomRow.Children.Add(_status);
        var bottom = new StackPanel();
        bottom.Children.Add(bottomRow);
        bottom.Children.Add(_progress);

        GridStyles.ApplyCompact(_preview);
        _preview.FontSize = AppSettings.Current.GridFontSize;

        var middle = new Grid();
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        middle.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 100 });
        var mappingBox = Section("Columnas de la tabla  ←  columnas del archivo", new ScrollViewer { Content = _mapping, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var previewBox = Section($"Muestra del archivo (primeras {PreviewRows} filas)", _preview);
        var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent };
        Grid.SetRow(splitter, 1);
        Grid.SetRow(previewBox, 2);
        middle.Children.Add(mappingBox);
        middle.Children.Add(splitter);
        middle.Children.Add(previewBox);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(middle);
        Content = root;

        SetStatus("Elige un archivo CSV, TXT o Excel (.xlsx).", null);
        Closing += (_, e) =>
        {
            // Mientras importa, cerrar la ventana equivale a cancelar (y esperar a que se deshaga).
            if (_running == null) return;
            _running.Cancel();
            e.Cancel = true;
        };
        Loaded += (_, _) => Browse();
    }

    private static FrameworkElement Section(string title, UIElement content)
    {
        var border = new Border { Child = content, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");
        var panel = new DockPanel();
        var label = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(label, Dock.Top);
        panel.Children.Add(label);
        panel.Children.Add(border);
        return panel;
    }

    /// <param name="ok">true: éxito; false: error; null: informativo.</param>
    private void SetStatus(string text, bool? ok)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, ok switch { true => "Brush.OkText", false => "Brush.ErrorText", _ => "Brush.SecondaryText" });
    }

    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Elegir el archivo a importar",
            Filter = "Datos (*.csv;*.txt;*.tsv;*.xlsx)|*.csv;*.txt;*.tsv;*.xlsx|CSV y texto (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|Excel (*.xlsx)|*.xlsx|Todos los archivos (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        _file.Text = dialog.FileName;
        LoadFile();
    }

    /// <summary>Lee el archivo con las opciones actuales y reconstruye el emparejamiento y la muestra.</summary>
    private void LoadFile()
    {
        string path = _file.Text;
        if (path.Length == 0) return;
        bool excel = Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
        _delimiter.IsEnabled = !excel;
        try
        {
            _source = excel
                ? DataImporter.ReadXlsx(path, _headers.IsChecked == true)
                : DataImporter.ReadCsv(path, (string)((ComboBoxItem)_delimiter.SelectedItem).Tag, _headers.IsChecked == true);
        }
        catch (Exception ex)
        {
            _source = null;
            _mapping.Children.Clear();
            _preview.ItemsSource = null;
            _import.IsEnabled = false;
            SetStatus("No se pudo leer el archivo: " + ex.Message, false);
            return;
        }

        BuildMapping(_source);
        _preview.Columns.Clear();
        for (int i = 0; i < _source.Headers.Length; i++)
            _preview.Columns.Add(new DataGridTextColumn
            {
                Header = _source.Headers[i].Replace("_", "__"),   // en los encabezados "_" marca una tecla de acceso
                Binding = new Binding($"[{i}]") { Mode = BindingMode.OneTime, Converter = CellConverter.Instance, FallbackValue = "" },
            });
        _preview.ItemsSource = _source.Rows.Take(PreviewRows).ToList();

        _import.IsEnabled = _source.Rows.Count > 0;
        SetStatus($"{_source.Rows.Count:N0} filas y {_source.Headers.Length} columnas en el archivo ({_source.Description}).", null);
    }

    /// <summary>Una fila por columna de la tabla, con un desplegable para elegir de qué columna del archivo sale.</summary>
    private void BuildMapping(ImportSource source)
    {
        // Sin mayúsculas, acentos, espacios ni signos: "País", "pais" y "PAIS" son la misma columna; "E-mail" y "email", también.
        static string Normalize(string name) => new string(name.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => char.IsLetterOrDigit(c) && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant();

        _mapping.Children.Clear();
        _choices.Clear();
        bool named = _headers.IsChecked == true;
        for (int c = 0; c < _columns.Count; c++)
        {
            var column = _columns[c];
            var choice = new ComboBox { Width = 300, Margin = new Thickness(0, 2, 0, 2) };
            choice.Items.Add(Skip);
            foreach (string header in source.Headers) choice.Items.Add(header);

            // Por nombre si el archivo trae encabezados; si no, por posición. Las autonuméricas, por defecto, no se importan.
            int match = named ? Array.FindIndex(source.Headers, h => Normalize(h) == Normalize(column.Name))
                : c < source.Headers.Length && !column.AutoIncrement ? c : -1;
            choice.SelectedIndex = match + 1;
            _choices.Add(choice);

            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            label.Inlines.Add(new System.Windows.Documents.Run(column.Name) { FontWeight = FontWeights.SemiBold });
            label.Inlines.Add(new System.Windows.Documents.Run(
                $"   {column.Type}{(column.PrimaryKey ? ", PK" : "")}{(column.AutoIncrement ? ", autonumérica" : "")}{(column.Nullable ? "" : ", obligatoria")}"));
            var row = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(choice, Dock.Right);
            row.Children.Add(choice);
            row.Children.Add(label);
            _mapping.Children.Add(row);
        }
    }

    private async Task ImportAsync()
    {
        if (_source == null) return;
        var mapping = _columns.Select((column, i) => (column.Name, SourceIndex: _choices[i].SelectedIndex - 1)).Where(m => m.SourceIndex >= 0).ToList();
        if (mapping.Count == 0)
        {
            SetStatus("Empareja al menos una columna de la tabla con una del archivo.", false);
            return;
        }

        bool deleteFirst = _deleteFirst.IsChecked == true;
        string where = _profile.IsProduction ? $"en PRODUCCIÓN ({_profile.Name})" : $"en {_profile.Name}";
        if (deleteFirst || _profile.IsProduction && AppSettings.Current.ConfirmProductionWrites)
        {
            string action = deleteFirst
                ? $"Se BORRARÁN todas las filas de {_table} y después se insertarán {_source.Rows.Count:N0}"
                : $"Se insertarán {_source.Rows.Count:N0} filas en {_table}";
            if (MessageBox.Show(this, $"{action} {where}.\n\n¿Continuar?", "Confirmar importación",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }

        _running = new CancellationTokenSource();
        var token = _running.Token;
        _import.IsEnabled = false;
        _cancel.Content = "Cancelar";
        _progress.Visibility = Visibility.Visible;
        var progress = new Progress<BackupProgress>(p =>
        {
            _progress.Value = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
            SetStatus($"Importando... {p.Done:N0} de {p.Total:N0} filas", null);
        });

        try
        {
            var source = _source;
            bool emptyAsNull = _emptyAsNull.IsChecked == true;
            var result = await Task.Run(() => DataImporter.ImportAsync(_profile, _database, _table, mapping, source.Rows, emptyAsNull, deleteFirst, progress, token));
            if (result.Error != null)
            {
                int offset = _headers.IsChecked == true ? 1 : 0;
                SetStatus($"No se importó nada. La fila {result.ErrorRow:N0} de datos (línea {result.ErrorRow + offset:N0} del archivo) no se pudo insertar: {result.Error}", false);
            }
            else
            {
                Imported = result.Inserted;
                SetStatus($"✔ {result.Inserted:N0} filas importadas en {_table}.", true);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Importación cancelada: no se insertó ninguna fila.", null);
        }
        catch (Exception ex)
        {
            SetStatus("No se importó nada: " + ex.Message, false);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            _progress.Visibility = Visibility.Hidden;
            _cancel.Content = "Cerrar";
            _import.IsEnabled = true;
        }
    }
}
