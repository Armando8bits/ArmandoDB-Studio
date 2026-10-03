using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MySmdb;

/// <summary>
/// Aspecto compacto de las cuadrículas (resultados, monitor de procesos). El tema Fluent da filas altas
/// (unos 32 px) y fija su propio fondo, que anula el sombreado alterno; estos estilos heredan de los de
/// Fluent y lo corrigen. La cabecera de fila lleva plantilla propia: la de Fluent recorta los números largos.
/// </summary>
public static class GridStyles
{
    public static void ApplyCompact(DataGrid grid)
    {
        grid.MinRowHeight = 0;
        grid.AlternationCount = 2;
        grid.GridLinesVisibility = DataGridGridLinesVisibility.All;
        grid.SetResourceReference(Control.BackgroundProperty, "Brush.GridBackground");
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, "Brush.GridBackground");
        grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "Brush.GridAlternate");
        grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Brush.GridLines");
        grid.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, "Brush.GridLines");

        var rowStyle = new Style(typeof(DataGridRow), grid.TryFindResource(typeof(DataGridRow)) as Style);
        rowStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Brush.GridBackground")));
        var alternate = new Trigger { Property = ItemsControl.AlternationIndexProperty, Value = 1 };
        alternate.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Brush.GridAlternate")));
        rowStyle.Triggers.Add(alternate);
        grid.RowStyle = rowStyle;

        var cellStyle = new Style(typeof(DataGridCell), grid.TryFindResource(typeof(DataGridCell)) as Style);
        cellStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 1, 4, 1)));
        grid.CellStyle = cellStyle;

        // Número de fila alineado a la derecha, sobre el mismo gris que las franjas de pestañas.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.GridLines");
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 1, 1));
        var number = new FrameworkElementFactory(typeof(ContentPresenter));
        number.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        number.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        number.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 6, 0));
        border.AppendChild(number);

        var rowHeaderStyle = new Style(typeof(DataGridRowHeader));
        rowHeaderStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(DataGridRowHeader)) { VisualTree = border }));
        rowHeaderStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Brush.TabStrip")));
        rowHeaderStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Brush.SecondaryText")));
        grid.RowHeaderStyle = rowHeaderStyle;

        var columnHeaderStyle = new Style(typeof(DataGridColumnHeader), grid.TryFindResource(typeof(DataGridColumnHeader)) as Style);
        columnHeaderStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        columnHeaderStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
        grid.ColumnHeaderStyle = columnHeaderStyle;
    }
}
