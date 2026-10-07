using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MySmdb;

/// <summary>
/// La ventana de error de toda la aplicación: qué se estaba haciendo, el mensaje, y debajo el detalle técnico
/// en un cuadro con desplazamiento, con un botón para copiarlo e informar del fallo.
/// </summary>
public class ErrorDialog : Window
{
    private readonly TextBox _details = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        AcceptsReturn = true,
    };
    private readonly TextBlock _repeated = new() { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private readonly Button _copy = new() { Content = "Copiar", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Copia el detalle completo para pegarlo en el informe del fallo" };
    private int _repeatedCount;

    public ErrorDialog(string title, string message, string report)
    {
        Title = $"{App.Name} - {title}";
        Width = 660;
        Height = 440;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var mark = new TextBlock { Text = "⚠", FontSize = 28, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        mark.SetResourceReference(TextBlock.ForegroundProperty, "Brush.ErrorText");

        // El mensaje también puede ser largo (errores de la base con varias líneas): desplazamiento propio.
        var messageText = new TextBox
        {
            Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontSize = 14, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0),
        };
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        var summary = new StackPanel();
        summary.Children.Add(heading);
        summary.Children.Add(messageText);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(mark, Dock.Left);
        top.Children.Add(mark);
        top.Children.Add(summary);

        var hint = new TextBlock
        {
            Text = "La aplicación sigue abierta y tus consultas no se han perdido. Si quieres informar del fallo, pulsa «Copiar» y pega el detalle en el informe " +
                   "(revisa antes que no contenga datos que no quieras compartir).",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), FontSize = 12,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
        _repeated.SetResourceReference(TextBlock.ForegroundProperty, "Brush.ErrorText");

        _details.Text = report;

        _copy.Click += (_, _) => Copy();
        var reportButton = new Button { Content = "Informar del fallo...", Padding = new Thickness(14, 4, 14, 4), ToolTip = "Copia el detalle y abre la página de incidencias del proyecto en el navegador" };
        reportButton.Click += (_, _) =>
        {
            Copy();
            try { Process.Start(new ProcessStartInfo(App.RepositoryUrl + "/issues/new") { UseShellExecute = true }); }
            catch { }
        };
        var close = new Button { Content = "Cerrar", Padding = new Thickness(18, 4, 18, 4), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_copy);
        left.Children.Add(reportButton);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(left);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_repeated, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(hint);
        root.Children.Add(buttons);
        root.Children.Add(_repeated);
        root.Children.Add(_details);
        Content = root;
    }

    /// <summary>Otro error inesperado mientras esta ventana seguía abierta: se añade aquí en lugar de abrir otra.</summary>
    public void AddRepeated(string report)
    {
        _repeatedCount++;
        _repeated.Text = $"Mientras esta ventana estaba abierta se produjeron {_repeatedCount} error(es) más; su detalle se ha añadido abajo y al registro.";
        _repeated.Visibility = Visibility.Visible;
        // Un tope, por si el mismo error se repite sin parar.
        if (_repeatedCount <= 5)
            _details.AppendText(Environment.NewLine + new string('-', 60) + Environment.NewLine + report);
    }

    private void Copy()
    {
        // El portapapeles puede estar ocupado un instante por otra aplicación: se reintenta.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(_details.Text, copy: true);
                _copy.Content = "Copiado ✔";
                return;
            }
            catch
            {
                Thread.Sleep(80);
            }
        }
        _copy.Content = "No se pudo copiar";
        _details.Focus();
        _details.SelectAll();   // al menos queda seleccionado para Ctrl+C
    }
}
