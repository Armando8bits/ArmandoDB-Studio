using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MySmdb;

/// <summary>
/// Barra de estado de una ventana (la principal o una flotante), siempre con el mismo comportamiento:
/// estado de la pestaña a la izquierda; a la derecha, recuento/suma/promedio de la selección, conexión,
/// tiempo y filas. Los valores numéricos se copian al portapapeles con un clic, como en Excel.
/// En conexiones de producción se pinta de rojo.
/// </summary>
public class StatusBarView : Border
{
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _stats = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0) };
    private readonly TextBlock _connection = new() { Margin = new Thickness(12, 0, 12, 0) };
    private readonly TextBlock _time = new() { Margin = new Thickness(12, 0, 12, 0) };
    private readonly TextBlock _rows = new() { Margin = new Thickness(12, 0, 12, 0) };
    private IReadOnlyList<SelectionStat>? _shownStats;
    private QueryTab? _tab;

    public StatusBarView()
    {
        Padding = new Thickness(6, 2, 6, 3);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_stats);
        right.Children.Add(_connection);
        right.Children.Add(_time);
        right.Children.Add(_rows);
        MakeCopyable(_rows, () => _tab?.RowCount is { } rows ? ("Número de filas", rows.ToString()) : null);

        var panel = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        panel.Children.Add(right);
        panel.Children.Add(_status);
        Child = panel;

        _status.Text = "Sin conexión";
        ApplyColors(production: false);
    }

    /// <param name="tab">Pestaña visible de la ventana; null si no hay.</param>
    /// <param name="idleText">Texto cuando no hay pestaña ("Listo", "Sin conexión").</param>
    public void Update(QueryTab? tab, string idleText)
    {
        _tab = tab;
        bool production = tab?.Profile.IsProduction == true;
        _status.Text = tab?.StatusText ?? idleText;
        _connection.Text = tab == null ? ""
            : tab.IsOffline ? "Sin conexión"
            : $"{(production ? "PRODUCCIÓN  ·  " : "")}{tab.Profile.Name}  |  {tab.CurrentDatabase ?? "(sin base)"}";
        _time.Text = tab?.TimeText ?? "";
        _rows.Text = tab?.RowsText ?? "";
        _rows.ToolTip = tab?.RowCount is { } rows ? $"Clic para copiar {rows}" : null;
        ShowSelectionStats(tab?.SelectionStats ?? Array.Empty<SelectionStat>());
        ApplyColors(production);
    }

    private void ApplyColors(bool production)
    {
        SetResourceReference(BackgroundProperty, production ? "Brush.ProdStatusBar" : "Brush.StatusBar");
        SetResourceReference(TextBlock.ForegroundProperty, production ? "Brush.ProdStatusBarText" : "Brush.StatusBarText");
    }

    /// <summary>Cada valor (Recuento, Suma...) es pulsable: copia su número al portapapeles.</summary>
    private void ShowSelectionStats(IReadOnlyList<SelectionStat> stats)
    {
        if (ReferenceEquals(stats, _shownStats)) return;
        _shownStats = stats;
        _stats.Children.Clear();
        foreach (var stat in stats)
        {
            var text = new TextBlock
            {
                Text = $"{stat.Label}: {stat.Display}",
                Margin = new Thickness(6, 0, 6, 0),
                ToolTip = $"Clic para copiar {stat.CopyText}",
            };
            MakeCopyable(text, () => (stat.Label, stat.CopyText));
            _stats.Children.Add(text);
        }
    }

    /// <summary>Texto que, al pulsarlo, copia un valor al portapapeles y lo confirma en la propia barra.</summary>
    private void MakeCopyable(TextBlock text, Func<(string Label, string Value)?> value)
    {
        text.Cursor = Cursors.Hand;
        text.MouseEnter += (_, _) => text.TextDecorations = TextDecorations.Underline;
        text.MouseLeave += (_, _) => text.TextDecorations = null;
        text.MouseLeftButtonUp += (_, _) =>
        {
            if (value() is not var (label, copy)) return;
            try
            {
                Clipboard.SetText(copy);
                _status.Text = $"{label} copiado al portapapeles: {copy}";
            }
            catch (Exception ex)
            {
                // El portapapeles puede estar bloqueado por otra aplicación.
                _status.Text = "No se pudo copiar: " + ex.Message;
            }
        };
    }
}
