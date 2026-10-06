using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MySmdb;

/// <summary>Opciones de una copia de seguridad a .sql: qué tablas, estructura y/o datos, y dónde guardarla.</summary>
public class BackupDialog : Window
{
    private readonly StackPanel _tables = new();
    private readonly CheckBox _structure = Option("Estructura (CREATE TABLE e índices)", true);
    private readonly CheckBox _data = Option("Datos (INSERT)", true);
    private readonly CheckBox _dropFirst = Option("Añadir DROP ... IF EXISTS antes de crear (para restaurar sobre una base existente)", true);
    private readonly CheckBox _others = Option("Vistas, procedimientos, funciones y triggers", true);
    private readonly TextBox _file = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MinHeight = 18 };

    public BackupOptions? Options { get; private set; }
    public string FilePath => _file.Text.Trim().Trim('"');

    public BackupDialog(Window owner, string title, IReadOnlyList<string> tables, string suggestedFile)
    {
        Owner = owner;
        Title = "Copia de seguridad — " + title;
        Width = 560;
        Height = 620;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 440;
        MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _file.Text = suggestedFile;

        foreach (string table in tables)
            _tables.Children.Add(new CheckBox { Content = table, IsChecked = true, Margin = new Thickness(4, 1, 4, 1) });

        var selectButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        selectButtons.Children.Add(new TextBlock { Text = $"Tablas ({tables.Count}):", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        selectButtons.Children.Add(SmallButton("Todas", () => SetAll(true)));
        selectButtons.Children.Add(SmallButton("Ninguna", () => SetAll(false)));

        var fileRow = new DockPanel();
        var browse = new Button { Content = "Examinar...", Margin = new Thickness(6, 4, 0, 4), Padding = new Thickness(10, 2, 10, 2) };
        browse.Click += (_, _) => Browse();
        DockPanel.SetDock(browse, Dock.Right);
        fileRow.Children.Add(browse);
        fileRow.Children.Add(_file);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var export = new Button { Content = "Crear copia", MinWidth = 100, Padding = new Thickness(10, 4, 10, 4), IsDefault = true, FontWeight = FontWeights.SemiBold };
        export.Click += (_, _) => Accept();
        buttons.Children.Add(export);
        buttons.Children.Add(new Button { Content = "Cancelar", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true });

        var bottom = new StackPanel();
        bottom.Children.Add(_structure);
        bottom.Children.Add(_data);
        bottom.Children.Add(_dropFirst);
        bottom.Children.Add(_others);
        bottom.Children.Add(new TextBlock { Text = "Archivo:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        bottom.Children.Add(fileRow);
        bottom.Children.Add(_status);
        bottom.Children.Add(buttons);

        var list = new ScrollViewer { Content = _tables, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 10) };
        var listBorder = new Border { Child = list, BorderThickness = new Thickness(1), Padding = new Thickness(2) };
        listBorder.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(selectButtons, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(selectButtons);
        root.Children.Add(bottom);
        root.Children.Add(listBorder);
        Content = root;
    }

    private static CheckBox Option(string text, bool isChecked) => new() { Content = text, IsChecked = isChecked, Margin = new Thickness(0, 3, 0, 0) };

    private static Button SmallButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(0, 0, 4, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private void SetAll(bool value)
    {
        foreach (var box in _tables.Children.OfType<CheckBox>()) box.IsChecked = value;
    }

    private void Browse()
    {
        var dialog = new SaveFileDialog { Filter = "Script SQL (*.sql)|*.sql", FileName = System.IO.Path.GetFileName(FilePath), DefaultExt = ".sql", AddExtension = true };
        try { dialog.InitialDirectory = System.IO.Path.GetDirectoryName(FilePath); } catch { }
        if (dialog.ShowDialog(this) == true) _file.Text = dialog.FileName;
    }

    private void Accept()
    {
        var tables = _tables.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList();
        bool structure = _structure.IsChecked == true, data = _data.IsChecked == true, others = _others.IsChecked == true;

        string? problem =
            FilePath.Length == 0 ? "Indica el archivo donde guardar la copia."
            : tables.Count == 0 && !others ? "Selecciona al menos una tabla."
            : tables.Count > 0 && !structure && !data ? "Marca Estructura, Datos o ambos."
            : null;
        if (problem != null)
        {
            _status.Text = problem;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.ErrorText");
            return;
        }

        Options = new BackupOptions(tables, structure, data, _dropFirst.IsChecked == true, others);
        DialogResult = true;
    }
}
