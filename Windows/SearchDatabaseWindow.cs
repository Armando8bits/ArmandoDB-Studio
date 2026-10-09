using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace MySmdb;

/// <summary>
/// Buscar en la base de datos (ver SchemaSearch): un texto en nombres de objetos, columnas y código. Al abrir un
/// resultado se muestra el script del objeto en una pestaña nueva. No modal: se puede dejar abierta.
/// </summary>
public class SearchDatabaseWindow : Window
{
    private readonly ConnectionProfile _profile;
    private readonly string _database;
    private readonly Func<SearchHit, string, Task> _open;
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
    private readonly TextBox _text = new() { Width = 280, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _names = Check("Nombres", "Tablas, vistas, procedimientos, funciones y triggers");
    private readonly CheckBox _columns = Check("Columnas", "Nombres de columna de tablas y vistas");
    private readonly CheckBox _code = Check("Código", "Texto de vistas, procedimientos, funciones y triggers");
    private readonly Button _searchButton = new() { Content = "Buscar", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(12, 0, 0, 0), IsDefault = true };
    private readonly Button _openButton = new() { Content = "Abrir el script", Padding = new Thickness(10, 3, 10, 3), ToolTip = "También con doble clic" };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, Visibility = Visibility.Hidden };
    private string _searched = "";
    private bool _searching;

    private static CheckBox Check(string text, string toolTip) =>
        new() { Content = text, IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), ToolTip = toolTip };

    private SearchHit? Selected => _grid.SelectedItem as SearchHit;

    /// <param name="open">Abre el script del objeto en la ventana principal; recibe también el texto buscado, para señalarlo.</param>
    public SearchDatabaseWindow(Window owner, ConnectionProfile profile, string database, Func<SearchHit, string, Task> open)
    {
        _profile = profile;
        _database = database;
        _open = open;
        Owner = owner;
        Icon = owner.Icon;
        Title = profile.Kind == DbKind.Sqlite ? $"Buscar en {profile.Name}" : $"Buscar en {database} ({profile.Name})";
        Width = 980;
        Height = 560;
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
        Column("Tipo", nameof(SearchHit.KindText), 105);
        Column("Objeto", nameof(SearchHit.Name), 260);
        Column("Coincide en", nameof(SearchHit.Where), 90);
        Column("Detalle", nameof(SearchHit.Detail), 0, fill: true);
        _grid.SelectionChanged += (_, _) => _openButton.IsEnabled = Selected != null;
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
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
        _status.Text = $"Escribe al menos {SchemaSearch.MinLength} caracteres y pulsa Intro.";
        _openButton.IsEnabled = false;
        _openButton.Click += (_, _) => OpenSelected();
        _searchButton.Click += (_, _) => SearchAsync().Watch("Buscar en la base de datos");

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 6) };
        top.Children.Add(new TextBlock { Text = "Buscar:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(_text);
        top.Children.Add(_names);
        top.Children.Add(_columns);
        top.Children.Add(_code);
        top.Children.Add(_searchButton);

        var bottom = new DockPanel { Margin = new Thickness(10, 6, 10, 8) };
        DockPanel.SetDock(_openButton, Dock.Right);
        bottom.Children.Add(_openButton);
        bottom.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_progress, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_progress);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        Loaded += (_, _) => _text.Focus();
    }

    /// <summary>Lanza la búsqueda con lo que hay escrito. (Pública para poder probar la ventana.)</summary>
    public async Task SearchAsync(string? text = null)
    {
        if (text != null) _text.Text = text;
        string wanted = _text.Text.Trim();
        if (_searching) return;
        if (wanted.Length < SchemaSearch.MinLength)
        {
            _status.Text = $"Escribe al menos {SchemaSearch.MinLength} caracteres.";
            return;
        }
        if (_names.IsChecked != true && _columns.IsChecked != true && _code.IsChecked != true)
        {
            _status.Text = "Marca al menos una casilla: Nombres, Columnas o Código.";
            return;
        }

        _searching = true;
        _searchButton.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _status.Text = "Buscando...";
        try
        {
            var result = await SchemaSearch.SearchAsync(_profile, _database, wanted,
                _names.IsChecked == true, _columns.IsChecked == true, _code.IsChecked == true);
            _searched = wanted;
            _grid.ItemsSource = result.Hits;
            if (result.Hits.Count > 0) _grid.SelectedIndex = 0;
            _status.Text = result.Hits.Count == 0 ? $"No se encontró \"{wanted}\"."
                : result.Truncated ? $"Se muestran las primeras {result.Hits.Count:N0} coincidencias; hay más. Afina el texto."
                : $"{result.Hits.Count:N0} coincidencias de \"{wanted}\".";
        }
        catch (Exception ex)
        {
            _grid.ItemsSource = null;
            _status.Text = "Error: " + ex.Message.ReplaceLineEndings(" ");
        }
        finally
        {
            _searching = false;
            _searchButton.IsEnabled = true;
            _progress.Visibility = Visibility.Hidden;
        }
    }

    public int ResultCount => _grid.Items.Count;

    private void OpenSelected()
    {
        if (Selected is { } hit) _open(hit, _searched).Watch("Abrir el script del objeto");
    }
}
