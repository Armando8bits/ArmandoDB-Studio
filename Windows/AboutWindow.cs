using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MySmdb;

/// <summary>Ayuda → Acerca de: nombre, versión, autor, repositorio y librerías usadas.</summary>
public class AboutWindow : Window
{
    public AboutWindow(Window owner)
    {
        Owner = owner;
        Title = "Acerca de " + App.Name;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var version = Assembly.GetExecutingAssembly().GetName().Version;

        // El .ico trae varios tamaños; por defecto WPF toma el primero (16 px) y lo estira. Se elige el mayor (256 px).
        var frames = BitmapDecoder.Create(new Uri("pack://application:,,,/ArmandoDBStudio;component/app.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
        var icon = new Image
        {
            Source = frames.OrderByDescending(frame => frame.PixelWidth).First(),
            Width = 72,
            Height = 72,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = App.Name, FontSize = 20, FontWeight = FontWeights.SemiBold });
        text.Children.Add(Secondary($"Versión {version?.ToString(3)}"));
        text.Children.Add(new TextBlock
        {
            Text = "Cliente ligero para MySQL y SQLite, con atajos y una forma de trabajo familiares para quien viene de SQL Server Management Studio.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 10),
        });
        text.Children.Add(new TextBlock { Text = "Autor: " + App.Author });

        var repository = new TextBlock { Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        repository.Inlines.Add("Repositorio: ");
        if (App.RepositoryUrl.Length > 0)
        {
            var link = new Hyperlink(new Run(App.RepositoryUrl));
            link.Click += (_, _) => OpenInBrowser(App.RepositoryUrl);
            repository.Inlines.Add(link);
        }
        else
        {
            repository.Inlines.Add("aún no publicado");
        }
        text.Children.Add(repository);
        text.Children.Add(new TextBlock { Text = "Licencia: MIT", Margin = new Thickness(0, 2, 0, 0) });
        text.Children.Add(Secondary("Usa AvalonEdit, MySqlConnector, Microsoft.Data.Sqlite y SSH.NET.", top: 10));
        text.Children.Add(Secondary(
            "Proyecto independiente: no está afiliado a Microsoft ni a Oracle, ni cuenta con su respaldo. " +
            "SQL Server Management Studio es una marca de Microsoft Corporation y MySQL, de Oracle Corporation.", top: 8));

        var body = new DockPanel { Margin = new Thickness(18, 18, 18, 8) };
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        body.Children.Add(text);

        var close = new Button
        {
            Content = "Cerrar",
            MinWidth = 90,
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(18, 4, 18, 16),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,
            IsCancel = true,
        };

        var root = new StackPanel();
        root.Children.Add(body);
        root.Children.Add(close);
        Content = root;
    }

    private static TextBlock Secondary(string text, double top = 0)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
        return block;
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
