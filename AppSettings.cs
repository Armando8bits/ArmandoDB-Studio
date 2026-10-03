using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MySmdb;

public class AppSettings
{
    private static readonly string FilePath = Path.Combine(App.DataFolder, "settings.json");

    public string EditorFont { get; set; } = "Consolas";
    public double EditorFontSize { get; set; } = 13;
    public double GridFontSize { get; set; } = 12;

    /// <summary>Vista dividida: true = izquierda/derecha; false = arriba/abajo.</summary>
    public bool SplitSideBySide { get; set; } = true;

    public bool ShowExplorer { get; set; } = true;

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Diálogo para elegir la fuente del editor y el tamaño de letra de los resultados.</summary>
public class FontSettingsDialog : Window
{
    private static readonly double[] Sizes = { 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32 };

    private readonly ComboBox _font = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly ComboBox _size = new() { Margin = new Thickness(0, 4, 0, 4), IsEditable = true };
    private readonly ComboBox _gridSize = new() { Margin = new Thickness(0, 4, 0, 4), IsEditable = true };
    private readonly TextBlock _preview = new()
    {
        Text = "SELECT id, nombre FROM clientes WHERE activo = 1;",
        Margin = new Thickness(8),
        TextWrapping = TextWrapping.Wrap,
    };

    public FontSettingsDialog()
    {
        Title = "Fuente y tamaño";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var settings = AppSettings.Current;
        _font.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n).ToList();
        _font.SelectedItem = settings.EditorFont;
        _size.ItemsSource = Sizes;
        _size.Text = settings.EditorFontSize.ToString(CultureInfo.CurrentCulture);
        _gridSize.ItemsSource = Sizes;
        _gridSize.Text = settings.GridFontSize.ToString(CultureInfo.CurrentCulture);

        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 5; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddRow(int row, string label, UIElement control)
        {
            var text = new Label { Content = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
        }

        AddRow(0, "Fuente del editor:", _font);
        AddRow(1, "Tamaño del editor:", _size);
        AddRow(2, "Tamaño en la cuadrícula:", _gridSize);

        var previewBox = new Border
        {
            Child = _preview,
            BorderBrush = Brushes.Silver,
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Margin = new Thickness(0, 8, 0, 0),
            MinHeight = 60,
        };
        Grid.SetRow(previewBox, 3);
        Grid.SetColumnSpan(previewBox, 2);
        grid.Children.Add(previewBox);

        var ok = new Button { Content = "Aceptar", Width = 90, Padding = new Thickness(0, 4, 0, 4), IsDefault = true };
        var cancel = new Button { Content = "Cancelar", Width = 90, Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        ok.Click += Ok_Click;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 4);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;

        _font.SelectionChanged += (_, _) => UpdatePreview();
        _size.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdatePreview()));
        _size.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdatePreview);
        UpdatePreview();
    }

    private static bool TryParseSize(string text, out double size) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out size) && size >= 6 && size <= 72;

    private void UpdatePreview()
    {
        if (_font.SelectedItem is string font)
            _preview.FontFamily = new FontFamily(font);
        if (TryParseSize(_size.Text, out double size))
            _preview.FontSize = size;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseSize(_size.Text, out double size) || !TryParseSize(_gridSize.Text, out double gridSize))
        {
            MessageBox.Show(this, "El tamaño debe ser un número entre 6 y 72.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = AppSettings.Current;
        if (_font.SelectedItem is string font) settings.EditorFont = font;
        settings.EditorFontSize = size;
        settings.GridFontSize = gridSize;
        try { settings.Save(); } catch { }
        DialogResult = true;
    }
}
