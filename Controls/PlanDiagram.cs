using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MySmdb;

/// <summary>
/// Dibuja un plan de ejecución como árbol, al estilo de SSMS: el resultado final a la izquierda y, hacia la
/// derecha, los pasos de los que se alimenta. Cada caja lleva una franja de color según el tipo de paso
/// (los recorridos completos de tabla, en naranja) y las líneas son más gruesas cuantas más filas pasan.
/// </summary>
public static class PlanDiagram
{
    private const double BoxWidth = 250, HorizontalGap = 46, VerticalGap = 14, Padding = 10, EdgeGap = 28;

    private sealed class Placed
    {
        public required PlanNode Node { get; init; }
        public required FrameworkElement Box { get; init; }
        public double X, Y, Height, SubtreeHeight;
        public List<Placed> Children { get; } = new();
    }

    public static string BrushKey(PlanNodeKind kind) => kind switch
    {
        PlanNodeKind.FullScan => "Brush.PlanFullScan",
        PlanNodeKind.Index => "Brush.PlanIndex",
        PlanNodeKind.Join => "Brush.PlanJoin",
        PlanNodeKind.SortOrTemp => "Brush.PlanSort",
        _ => "Brush.PlanOther",
    };

    public static FrameworkElement Build(PlanNode root)
    {
        // A la izquierda: con el ancho fijo, el panel lo centraría bajo un título más ancho que el árbol.
        var canvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Left };
        var placed = Measure(root);
        Place(placed, Padding, Padding);
        double right = 0, bottom = 0;
        Draw(canvas, placed, ref right, ref bottom);
        // Margen amplio a la derecha y abajo: las barras de desplazamiento se dibujan sobre el contenido.
        canvas.Width = right + EdgeGap;
        canvas.Height = bottom + EdgeGap;
        return canvas;
    }

    /// <summary>Leyenda de colores, para la barra de la pestaña del plan.</summary>
    public static FrameworkElement Legend()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void Add(PlanNodeKind kind, string text)
        {
            var mark = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            mark.SetResourceReference(Border.BackgroundProperty, BrushKey(kind));
            var label = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
            panel.Children.Add(mark);
            panel.Children.Add(label);
        }
        Add(PlanNodeKind.FullScan, "recorrido completo de tabla");
        Add(PlanNodeKind.Index, "uso de índice");
        Add(PlanNodeKind.Join, "combinación");
        Add(PlanNodeKind.SortOrTemp, "orden / agrupación / temporal");
        return panel;
    }

    private static Placed Measure(PlanNode node)
    {
        var box = BuildBox(node);
        box.Measure(new Size(BoxWidth, double.PositiveInfinity));
        var placed = new Placed { Node = node, Box = box, Height = Math.Ceiling(box.DesiredSize.Height) };
        foreach (var child in node.Children) placed.Children.Add(Measure(child));
        double childrenHeight = placed.Children.Sum(c => c.SubtreeHeight) + VerticalGap * Math.Max(0, placed.Children.Count - 1);
        placed.SubtreeHeight = Math.Max(placed.Height, childrenHeight);
        return placed;
    }

    /// <summary>Los hijos se apilan a la derecha; el padre queda centrado respecto a ellos.</summary>
    private static void Place(Placed node, double x, double top)
    {
        node.X = x;
        if (node.Children.Count == 0)
        {
            node.Y = top + (node.SubtreeHeight - node.Height) / 2;
            return;
        }

        double childrenHeight = node.Children.Sum(c => c.SubtreeHeight) + VerticalGap * (node.Children.Count - 1);
        double y = top + (node.SubtreeHeight - childrenHeight) / 2;
        foreach (var child in node.Children)
        {
            Place(child, x + BoxWidth + HorizontalGap, y);
            y += child.SubtreeHeight + VerticalGap;
        }
        var (first, last) = (node.Children[0], node.Children[^1]);
        double center = (first.Y + first.Height / 2 + last.Y + last.Height / 2) / 2;
        node.Y = Math.Max(top, center - node.Height / 2);
    }

    private static void Draw(Canvas canvas, Placed node, ref double right, ref double bottom)
    {
        foreach (var child in node.Children)
        {
            // Los datos van del hijo (derecha) al padre (izquierda): la flecha apunta al padre.
            double x1 = child.X, y1 = child.Y + child.Height / 2, x2 = node.X + BoxWidth, y2 = node.Y + node.Height / 2;
            double thickness = 1 + Math.Min(4, Math.Log10((child.Node.Rows ?? 1) + 1));
            var figure = new PathFigure { StartPoint = new Point(x1, y1) };
            double middle = (x1 + x2) / 2;
            figure.Segments.Add(new BezierSegment(new Point(middle, y1), new Point(middle, y2), new Point(x2 + 8, y2), true));
            var line = new Path { Data = new PathGeometry(new[] { figure }), StrokeThickness = thickness };
            line.SetResourceReference(Shape.StrokeProperty, "Brush.SecondaryText");
            canvas.Children.Add(line);

            var head = new PathFigure { StartPoint = new Point(x2, y2), IsClosed = true };
            head.Segments.Add(new LineSegment(new Point(x2 + 9, y2 - 4 - thickness / 2), true));
            head.Segments.Add(new LineSegment(new Point(x2 + 9, y2 + 4 + thickness / 2), true));
            var arrow = new Path { Data = new PathGeometry(new[] { head }) };
            arrow.SetResourceReference(Shape.FillProperty, "Brush.SecondaryText");
            canvas.Children.Add(arrow);

            Draw(canvas, child, ref right, ref bottom);
        }

        Canvas.SetLeft(node.Box, node.X);
        Canvas.SetTop(node.Box, node.Y);
        canvas.Children.Add(node.Box);
        right = Math.Max(right, node.X + BoxWidth);
        bottom = Math.Max(bottom, node.Y + node.Height);
    }

    private static FrameworkElement BuildBox(PlanNode node)
    {
        var text = new StackPanel { Margin = new Thickness(8, 5, 8, 6) };
        text.Children.Add(new TextBlock { Text = node.Operation, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (node.Detail.Length > 0)
            text.Children.Add(Secondary(node.Detail));

        var metrics = new List<string>();
        if (node.Cost is { } cost) metrics.Add("costo " + cost.ToString("#,##0.##", CultureInfo.CurrentCulture));
        if (node.Rows is { } rows) metrics.Add(rows.ToString("#,##0.##", CultureInfo.CurrentCulture) + " filas");
        if (metrics.Count > 0)
            text.Children.Add(new TextBlock { Text = string.Join("  ·  ", metrics), FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
        if (node.Actual != null)
            text.Children.Add(Secondary(node.Actual));

        var stripe = new Border { Width = 5, CornerRadius = new CornerRadius(3, 0, 0, 3) };
        stripe.SetResourceReference(Border.BackgroundProperty, BrushKey(node.Kind));
        var content = new DockPanel();
        DockPanel.SetDock(stripe, Dock.Left);
        content.Children.Add(stripe);
        content.Children.Add(text);

        var box = new Border { Child = content, Width = BoxWidth, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), SnapsToDevicePixels = true };
        box.SetResourceReference(Border.BackgroundProperty, "Brush.GridBackground");
        box.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");
        box.ToolTip = string.Join("\n", new[] { node.Operation, node.Detail, string.Join("  ·  ", metrics), node.Actual ?? "" }.Where(s => s.Length > 0));
        return box;
    }

    private static TextBlock Secondary(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
        return block;
    }
}
