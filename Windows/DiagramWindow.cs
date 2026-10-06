using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using MsaglPoint = Microsoft.Msagl.Core.Geometry.Point;
using Path = System.Windows.Shapes.Path;

namespace MySmdb;

/// <summary>
/// Diagrama entidad-relación de una base (solo lectura): cada tabla es una caja con sus columnas y cada clave
/// foránea, una flecha hacia la tabla referenciada. Las cajas se arrastran, hay zoom (Ctrl+rueda) y
/// distribución automática; las posiciones se recuerdan por conexión y base.
/// </summary>
public class DiagramWindow : Window
{
    private const double HeaderHeight = 26, RowHeight = 19, Gap = 24;

    private sealed class TableBox
    {
        public required string Name { get; init; }
        public required List<ColumnInfo> Columns { get; init; }
        public Border Visual = null!;
        /// <summary>Lista de columnas: se desplaza cuando la caja es más baja que su contenido.</summary>
        public ScrollViewer Rows = null!;
        public double X, Y, Width, Height;
        /// <summary>Tamaño que ocupa con todas sus filas a la vista.</summary>
        public double NaturalWidth, NaturalHeight;
        /// <summary>El usuario cambió el tamaño de la caja: se guarda junto con la posición.</summary>
        public bool CustomSize;
        /// <summary>Columna → distancia desde el borde superior hasta el centro de su fila (solo las visibles).</summary>
        public Dictionary<string, double> RowCenter = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Altura a la que sale la flecha de una columna; si la fila está desplazada fuera de la caja, su borde.</summary>
        public double AnchorY(string? column)
        {
            if (column == null || !RowCenter.TryGetValue(column, out double offset)) return Y + HeaderHeight / 2;
            return Y + Math.Clamp(offset - Rows.VerticalOffset, HeaderHeight, Math.Max(HeaderHeight, Height - 3));
        }
    }

    private sealed class Relation
    {
        public required ForeignKey Key { get; init; }
        public required TableBox Child { get; init; }
        public required TableBox Parent { get; init; }
        public required Path Line { get; init; }
        public required Path Arrow { get; init; }
    }

    private readonly ConnectionProfile _profile;
    private readonly string _database;
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), MinWidth = 44, TextAlignment = TextAlignment.Center };
    private readonly Border _loading = new() { Child = null, Padding = new Thickness(0, 0, 0, 60) };
    private readonly CheckBox _keysOnly = new() { Content = "Solo claves", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

    private List<(string Table, List<ColumnInfo> Columns)> _tables = new();
    private List<ForeignKey> _keys = new();
    private readonly List<TableBox> _boxes = new();
    private readonly List<Relation> _relations = new();
    private TableBox? _selected;
    private double _zoom = 1;
    /// <summary>Tablas que muestran todas sus columnas aunque esté marcado «Solo claves».</summary>
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

    public DiagramWindow(ConnectionProfile profile, string database, ImageSource? icon)
    {
        _profile = profile;
        _database = database;
        Title = $"Diagrama — {(profile.Kind == DbKind.Sqlite ? profile.Name : $"{database} ({profile.Name})")}";
        Icon = icon;
        Width = 1200;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowState = WindowState.Maximized;   // el tamaño de arriba es el que toma al restaurarla

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
        void AddButton(string text, string toolTip, Action action)
        {
            var button = new Button { Content = text, ToolTip = toolTip, Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 0, 4, 0) };
            button.Click += (_, _) => action();
            bar.Children.Add(button);
        }
        AddButton("Reorganizar", "Distribuir las tablas automáticamente (se pierden las posiciones manuales)", () => { AutoLayout(_boxes); AfterMove(save: true); Fit(); });
        AddButton("Ajustar a la ventana", "Ver todo el diagrama", Fit);
        AddButton("−", "Alejar (Ctrl+rueda)", () => SetZoom(_zoom / 1.2));
        bar.Children.Add(_zoomText);
        AddButton("+", "Acercar (Ctrl+rueda)", () => SetZoom(_zoom * 1.2));
        AddButton("100 %", "Tamaño real", () => SetZoom(1));
        AddButton("Exportar PNG...", "Guardar el diagrama como imagen", ExportPng);
        _keysOnly.ToolTip = "Mostrar solo las columnas que son clave primaria o foránea (útil en bases grandes)";
        _keysOnly.Click += (_, _) => { BuildDiagram(); Fit(); };
        bar.Children.Add(_keysOnly);
        bar.Children.Add(_info);
        _info.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");

        _scroll.Content = _canvas;
        _scroll.SetResourceReference(BackgroundProperty, "Brush.EditorBackground");
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        };
        EnablePanning();

        // Aviso de carga, sobre la zona del diagrama: con latencia (túnel, servidor lejano) leer el esquema tarda.
        var loadingText = new TextBlock { Text = "Cargando tablas y relaciones...", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        var loadingPanel = new StackPanel { Width = 320, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        loadingPanel.Children.Add(loadingText);
        loadingPanel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6 });
        _loading.Child = loadingPanel;
        _loading.SetResourceReference(BackgroundProperty, "Brush.EditorBackground");

        var body = new Grid();
        body.Children.Add(_scroll);
        body.Children.Add(_loading);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(body);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _info.Text = "Cargando tablas y relaciones...";
        _loading.Visibility = Visibility.Visible;
        try
        {
            // En segundo plano: abrir la conexión (y el túnel, si lo hay) no debe congelar la ventana ni la barra.
            (_tables, _keys) = await Task.Run(async () =>
                (await Db.GetAllTablesAsync(_profile, _database), await Db.ListForeignKeysAsync(_profile, _database)));
        }
        catch (Exception ex)
        {
            _info.Text = "Error: " + ex.Message;
            _loading.Visibility = Visibility.Collapsed;
            return;
        }
        // Con muchas tablas, de entrada solo las claves: se lee mejor.
        _keysOnly.IsChecked = _tables.Count > 25;
        BuildDiagram();
        _loading.Visibility = Visibility.Collapsed;
        // Tras el diseño: la zona visible ya tiene su tamaño definitivo (ventana maximizada, aviso retirado).
        await Dispatcher.InvokeAsync(Fit, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---------- Construcción ----------

    private void BuildDiagram()
    {
        _canvas.Children.Clear();
        _boxes.Clear();
        _relations.Clear();
        _selected = null;
        bool keysOnly = _keysOnly.IsChecked == true;
        var names = new HashSet<string>(_tables.Select(t => t.Table), StringComparer.OrdinalIgnoreCase);
        var keys = _keys.Where(k => names.Contains(k.Table) && names.Contains(k.RefTable)).ToList();
        var foreignColumns = new HashSet<(string, string)>(keys.Select(k => (k.Table.ToLowerInvariant(), k.Column.ToLowerInvariant())));

        foreach (var (table, columns) in _tables)
        {
            var box = new TableBox { Name = table, Columns = columns };
            BuildBox(box, keysOnly, column => foreignColumns.Contains((table.ToLowerInvariant(), column.ToLowerInvariant())));
            _boxes.Add(box);
        }

        // Posiciones guardadas; las tablas nuevas (o todas, la primera vez) se distribuyen automáticamente.
        var saved = DiagramStore.Load(_profile.Name, _database);
        var unplaced = new List<TableBox>();
        foreach (var box in _boxes)
        {
            if (!saved.TryGetValue(box.Name, out var position) || position.Length < 2)
            {
                unplaced.Add(box);
                continue;
            }
            (box.X, box.Y) = (position[0], position[1]);
            // Tamaño cambiado por el usuario: [x, y, ancho, alto].
            if (position.Length >= 4 && position[2] > 0) Resize(box, position[2], position[3]);
        }
        if (unplaced.Count == _boxes.Count) AutoLayout(_boxes);
        else if (unplaced.Count > 0) PlaceInGrid(unplaced, 0, _boxes.Except(unplaced).Max(b => b.Y + b.Height) + Gap * 2, 1600);

        var byName = _boxes.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var relation = new Relation
            {
                Key = key,
                Child = byName[key.Table],
                Parent = byName[key.RefTable],
                Line = new Path { StrokeThickness = 1.3, IsHitTestVisible = false },
                Arrow = new Path { IsHitTestVisible = false },
            };
            _relations.Add(relation);
            _canvas.Children.Add(relation.Line);
            _canvas.Children.Add(relation.Arrow);
        }
        foreach (var box in _boxes)
        {
            Panel.SetZIndex(box.Visual, 1);
            _canvas.Children.Add(box.Visual);
        }

        _info.Text = $"{_boxes.Count} tablas · {_relations.Count} relaciones" +
                     (_relations.Count == 0 && _boxes.Count > 1 ? " (la base no declara claves foráneas)" : "");
        AfterMove(save: false);
        ApplySelection();
    }

    /// <summary>Límites al redimensionar: filas visibles como mínimo y ancho extra como máximo.</summary>
    private const double MinVisibleRows = 3, MaxExtraWidth = 240;

    /// <summary>
    /// Cambia el tamaño de una caja dentro de sus límites, de modo que el texto siempre se lea entero:
    /// nunca más estrecha que su nombre y sus columnas (ni mucho más ancha), y de alto entre la cabecera con
    /// unas pocas filas (el resto se recorre con la rueda) y el de todas sus filas.
    /// </summary>
    private static void Resize(TableBox box, double width, double height)
    {
        double minHeight = Math.Min(box.NaturalHeight, HeaderHeight + MinVisibleRows * RowHeight + 5);
        box.Width = Math.Clamp(Math.Round(width), box.NaturalWidth, box.NaturalWidth + MaxExtraWidth);
        box.Height = Math.Clamp(Math.Round(height), minHeight, box.NaturalHeight);
        box.CustomSize = true;
        box.Visual.Width = box.Width;
        box.Visual.Height = box.Height;
    }

    /// <summary>Muestra todas las columnas de una tabla (o vuelve a solo sus claves) sin mover el resto del diagrama.</summary>
    private void ToggleExpanded(TableBox box)
    {
        if (!_expanded.Remove(box.Name)) _expanded.Add(box.Name);
        // El alto guardado era el de la otra vista: la caja vuelve a su tamaño automático.
        box.CustomSize = false;
        AfterMove(save: true);   // fija las posiciones actuales antes de reconstruir
        string name = box.Name;
        BuildDiagram();
        _selected = _boxes.FirstOrDefault(b => b.Name == name);
        ApplySelection();
    }

    private void BuildBox(TableBox box, bool keysOnly, Func<string, bool> isForeign)
    {
        bool collapsible = keysOnly;
        keysOnly &= !_expanded.Contains(box.Name);

        // Margen derecho: hueco para la barra de desplazamiento, que se dibuja sobre las filas.
        var rows = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var header = new Border { Height = HeaderHeight, Padding = new Thickness(8, 0, 8, 0), Cursor = Cursors.SizeAll };
        header.SetResourceReference(Border.BackgroundProperty, "Brush.TabStripPinned");
        header.Child = new TextBlock { Text = box.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        header.ToolTip = box.Name + "\nArrastra para mover la tabla; arrastra la esquina inferior derecha para cambiar su tamaño.";

        double y = HeaderHeight;
        int hidden = 0;
        foreach (var column in box.Columns)
        {
            bool foreign = isForeign(column.Name);
            if (keysOnly && !column.PrimaryKey && !foreign) { hidden++; continue; }

            var row = new DockPanel { Height = RowHeight, Margin = new Thickness(6, 0, 8, 0) };
            var mark = new TextBlock
            {
                Text = column.PrimaryKey ? "PK" : foreign ? "FK" : "",
                Width = 20, FontSize = 9, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            };
            mark.SetResourceReference(TextBlock.ForegroundProperty, column.PrimaryKey ? "Brush.TabAccent" : "Brush.SecondaryText");
            var type = new TextBlock { Text = column.Type, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            type.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");
            DockPanel.SetDock(mark, Dock.Left);
            DockPanel.SetDock(type, Dock.Right);
            row.Children.Add(mark);
            row.Children.Add(type);
            row.Children.Add(new TextBlock { Text = column.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            row.ToolTip = $"{column.Name}  {column.Type}";
            rows.Children.Add(row);
            box.RowCenter[column.Name] = y + RowHeight / 2;
            y += RowHeight;
        }
        // Con «Solo claves», cada tabla se puede abrir por separado para ver el resto de sus columnas.
        if (hidden > 0 || (collapsible && !keysOnly))
        {
            var more = new TextBlock
            {
                Text = hidden > 0 ? $"▸ {hidden} columnas más" : "▴ ver solo las claves",
                Height = RowHeight, Padding = new Thickness(26, 2, 8, 0), FontSize = 11, Cursor = Cursors.Hand, Background = Brushes.Transparent,
                ToolTip = hidden > 0 ? "Mostrar todas las columnas de esta tabla" : "Volver a mostrar solo las columnas clave de esta tabla",
            };
            more.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TabAccent");
            more.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;   // que no empiece a arrastrar la caja
                ToggleExpanded(box);
            };
            rows.Children.Add(more);
        }

        // Las filas van en una zona con desplazamiento propio: si la caja se hace más baja, se recorren con la rueda.
        box.Rows = new ScrollViewer
        {
            Content = rows, Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        box.Rows.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange == 0) return;
            foreach (var relation in _relations.Where(r => r.Child == box || r.Parent == box)) DrawRelation(relation);
        };
        box.Rows.PreviewMouseWheel += (_, e) =>
        {
            // Si la caja no tiene nada que desplazar, la rueda sigue moviendo el diagrama.
            if (box.Rows.ScrollableHeight > 0) return;
            e.Handled = true;
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - e.Delta);
        };

        // Esquina inferior derecha: arrastrar cambia el tamaño; doble clic vuelve al automático.
        var gripMark = new Path { Data = Geometry.Parse("M9,1 L9,9 L1,9 Z"), Opacity = 0.7 };
        gripMark.SetResourceReference(Shape.FillProperty, "Brush.SecondaryText");
        var grip = new Border
        {
            Child = gripMark, Width = 12, Height = 12, Background = Brushes.Transparent, Cursor = Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            ToolTip = "Arrastra para cambiar el tamaño de la tabla (dentro de unos límites, para que el texto siempre se lea); doble clic para volver al tamaño automático",
        };

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(box.Rows);
        var content = new Grid();
        content.Children.Add(layout);
        content.Children.Add(grip);

        box.Visual = new Border { Child = content, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(0, 0, 0, 3), SnapsToDevicePixels = true, ClipToBounds = true };
        box.Visual.SetResourceReference(Border.BackgroundProperty, "Brush.GridBackground");
        box.Visual.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");
        box.Visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        box.Width = box.NaturalWidth = Math.Ceiling(box.Visual.DesiredSize.Width);
        box.Height = box.NaturalHeight = Math.Ceiling(box.Visual.DesiredSize.Height);
        box.Visual.Width = box.Width;

        Point resizeStart = default;
        Size startSize = default;
        grip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;   // que no empiece a arrastrar la caja
            _selected = box;
            ApplySelection();
            if (e.ClickCount == 2)
            {
                box.CustomSize = false;
                (box.Width, box.Height) = (box.NaturalWidth, box.NaturalHeight);
                box.Visual.Width = box.Width;
                box.Visual.Height = double.NaN;
                AfterMove(save: true);
                return;
            }
            resizeStart = e.GetPosition(_canvas);
            startSize = new Size(box.Width, box.Height);
            grip.CaptureMouse();
        };
        grip.MouseMove += (_, e) =>
        {
            if (!grip.IsMouseCaptured) return;
            var position = e.GetPosition(_canvas);
            Resize(box, startSize.Width + position.X - resizeStart.X, startSize.Height + position.Y - resizeStart.Y);
            AfterMove(save: false);
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!grip.IsMouseCaptured) return;
            grip.ReleaseMouseCapture();
            e.Handled = true;
            AfterMove(save: true);
        };

        // Arrastrar la caja; un clic la selecciona y resalta sus relaciones.
        Point grab = default;
        box.Visual.MouseLeftButtonDown += (_, e) =>
        {
            _selected = box;
            ApplySelection();
            grab = e.GetPosition(_canvas);
            grab = new Point(grab.X - box.X, grab.Y - box.Y);
            box.Visual.CaptureMouse();
            e.Handled = true;
        };
        box.Visual.MouseMove += (_, e) =>
        {
            if (!box.Visual.IsMouseCaptured) return;
            var position = e.GetPosition(_canvas);
            box.X = Math.Max(0, position.X - grab.X);
            box.Y = Math.Max(0, position.Y - grab.Y);
            AfterMove(save: false);
        };
        box.Visual.MouseLeftButtonUp += (_, _) =>
        {
            if (!box.Visual.IsMouseCaptured) return;
            box.Visual.ReleaseMouseCapture();
            AfterMove(save: true);
        };
    }

    // ---------- Distribución ----------

    /// <summary>
    /// Las tablas relacionadas se ordenan por capas (la referenciada arriba, las que dependen de ella debajo),
    /// minimizando cruces; las tablas sin relaciones van en una rejilla debajo.
    /// </summary>
    private void AutoLayout(List<TableBox> boxes)
    {
        var related = new HashSet<TableBox>();
        var pairs = new HashSet<(TableBox Parent, TableBox Child)>();
        var byName = boxes.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _keys)
        {
            if (!byName.TryGetValue(key.Table, out var child) || !byName.TryGetValue(key.RefTable, out var parent) || child == parent) continue;
            related.Add(child);
            related.Add(parent);
            pairs.Add((parent, child));
        }

        double bottom = 0, right = 1200;
        if (related.Count > 0)
        {
            var graph = new GeometryGraph();
            var nodes = new Dictionary<TableBox, Node>();
            foreach (var box in related)
            {
                var node = new Node(Microsoft.Msagl.Core.Geometry.Curves.CurveFactory.CreateRectangle(box.Width, box.Height, new MsaglPoint(0, 0)), box);
                nodes[box] = node;
                graph.Nodes.Add(node);
            }
            foreach (var (parent, child) in pairs)
                graph.Edges.Add(new Edge(nodes[parent], nodes[child]));

            LayoutHelpers.CalculateLayout(graph, new SugiyamaLayoutSettings { NodeSeparation = 36, LayerSeparation = 70 }, null);

            // MSAGL usa el eje Y hacia arriba y coordenadas de centro.
            double minX = nodes.Values.Min(n => n.Center.X - n.Width / 2), maxY = nodes.Values.Max(n => n.Center.Y + n.Height / 2);
            foreach (var (box, node) in nodes)
            {
                box.X = node.Center.X - node.Width / 2 - minX + Gap;
                box.Y = maxY - (node.Center.Y + node.Height / 2) + Gap;
            }
            bottom = related.Max(b => b.Y + b.Height) + Gap * 2;
            right = Math.Max(right, related.Max(b => b.X + b.Width));
        }

        PlaceInGrid(boxes.Where(b => !related.Contains(b)).ToList(), Gap, bottom + (related.Count > 0 ? 0 : Gap), right);
    }

    /// <summary>Coloca las cajas en filas, de izquierda a derecha, saltando de fila al llegar al ancho indicado.</summary>
    private static void PlaceInGrid(List<TableBox> boxes, double left, double top, double maxRight)
    {
        double x = Math.Max(left, Gap), y = top, rowHeight = 0;
        foreach (var box in boxes)
        {
            if (x > Gap && x + box.Width > maxRight)
            {
                x = Math.Max(left, Gap);
                y += rowHeight + Gap;
                rowHeight = 0;
            }
            (box.X, box.Y) = (x, y);
            x += box.Width + Gap;
            rowHeight = Math.Max(rowHeight, box.Height);
        }
    }

    // ---------- Dibujo ----------

    /// <summary>Recoloca cajas y flechas tras mover algo; ajusta el tamaño del lienzo.</summary>
    private void AfterMove(bool save)
    {
        foreach (var box in _boxes)
        {
            Canvas.SetLeft(box.Visual, box.X);
            Canvas.SetTop(box.Visual, box.Y);
        }
        foreach (var relation in _relations) DrawRelation(relation);
        _canvas.Width = _boxes.Count == 0 ? 0 : _boxes.Max(b => b.X + b.Width) + Gap * 3;
        _canvas.Height = _boxes.Count == 0 ? 0 : _boxes.Max(b => b.Y + b.Height) + Gap * 3;
        // Posición y, si el usuario lo cambió, tamaño: [x, y] o [x, y, ancho, alto].
        if (save) DiagramStore.Save(_profile.Name, _database,
            _boxes.ToDictionary(b => b.Name, b => b.CustomSize ? new[] { b.X, b.Y, b.Width, b.Height } : new[] { b.X, b.Y }));
    }

    /// <summary>Curva desde la columna con la clave foránea hasta la columna referenciada, con punta de flecha en esta.</summary>
    private static void DrawRelation(Relation relation)
    {
        var (child, parent) = (relation.Child, relation.Parent);
        double y1 = child.AnchorY(relation.Key.Column);
        string? targetColumn = relation.Key.RefColumn ?? parent.Columns.FirstOrDefault(c => c.PrimaryKey)?.Name;
        double y2 = parent.AnchorY(targetColumn);

        // Sale por el lado más cercano a la otra tabla; si se solapan en horizontal, ambas por la derecha.
        double x1, x2, d1, d2;
        if (child.X + child.Width + 30 < parent.X) { x1 = child.X + child.Width; d1 = 1; x2 = parent.X; d2 = -1; }
        else if (parent.X + parent.Width + 30 < child.X) { x1 = child.X; d1 = -1; x2 = parent.X + parent.Width; d2 = 1; }
        else { x1 = child.X + child.Width; d1 = 1; x2 = parent.X + parent.Width; d2 = 1; }

        const double head = 9;
        double bend = Math.Max(40, Math.Abs(x2 - x1) / 2);
        var figure = new PathFigure { StartPoint = new Point(x1, y1) };
        figure.Segments.Add(new BezierSegment(new Point(x1 + d1 * bend, y1), new Point(x2 + d2 * bend, y2), new Point(x2 + d2 * head, y2), true));
        relation.Line.Data = new PathGeometry(new[] { figure });

        var arrow = new PathFigure { StartPoint = new Point(x2, y2), IsClosed = true, IsFilled = true };
        arrow.Segments.Add(new LineSegment(new Point(x2 + d2 * head, y2 - 4), true));
        arrow.Segments.Add(new LineSegment(new Point(x2 + d2 * head, y2 + 4), true));
        relation.Arrow.Data = new PathGeometry(new[] { arrow });
    }

    /// <summary>Resalta la tabla seleccionada y sus relaciones; el resto de flechas se atenúa.</summary>
    private void ApplySelection()
    {
        foreach (var box in _boxes)
        {
            bool selected = box == _selected;
            box.Visual.BorderThickness = new Thickness(selected ? 2 : 1);
            box.Visual.SetResourceReference(Border.BorderBrushProperty, selected ? "Brush.TabAccent" : "Brush.PanelBorder");
        }
        foreach (var relation in _relations)
        {
            bool mine = _selected != null && (relation.Child == _selected || relation.Parent == _selected);
            string brush = mine ? "Brush.TabAccent" : "Brush.SecondaryText";
            relation.Line.SetResourceReference(Shape.StrokeProperty, brush);
            relation.Arrow.SetResourceReference(Shape.FillProperty, brush);
            relation.Line.StrokeThickness = mine ? 2 : 1.3;
            relation.Line.Opacity = relation.Arrow.Opacity = _selected == null || mine ? 1 : 0.3;
            Panel.SetZIndex(relation.Line, mine ? 1 : 0);
        }
    }

    // ---------- Zoom y desplazamiento ----------

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.1, 3);
        _canvas.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        _zoomText.Text = $"{_zoom * 100:0} %";
    }

    private void Fit()
    {
        UpdateLayout();
        if (_canvas.Width <= 0 || _scroll.ViewportWidth <= 0) { SetZoom(1); return; }
        SetZoom(Math.Min(1, Math.Min(_scroll.ViewportWidth / _canvas.Width, _scroll.ViewportHeight / _canvas.Height)));
    }

    /// <summary>Arrastrar el fondo desplaza el diagrama; un clic en el fondo quita la selección.</summary>
    private void EnablePanning()
    {
        Point start = default;
        double startX = 0, startY = 0;
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            _selected = null;
            ApplySelection();
            start = e.GetPosition(_scroll);
            (startX, startY) = (_scroll.HorizontalOffset, _scroll.VerticalOffset);
            _canvas.CaptureMouse();
            _canvas.Cursor = Cursors.ScrollAll;
        };
        _canvas.MouseMove += (_, e) =>
        {
            if (!_canvas.IsMouseCaptured) return;
            var now = e.GetPosition(_scroll);
            _scroll.ScrollToHorizontalOffset(startX - (now.X - start.X));
            _scroll.ScrollToVerticalOffset(startY - (now.Y - start.Y));
        };
        _canvas.MouseLeftButtonUp += (_, _) =>
        {
            _canvas.ReleaseMouseCapture();
            _canvas.Cursor = null;
        };
    }

    // ---------- Exportación ----------

    private void ExportPng()
    {
        if (_boxes.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Imagen PNG (*.png)|*.png",
            FileName = $"diagrama_{_database}",
            DefaultExt = ".png",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SavePng(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "No se pudo guardar la imagen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Dibuja el lienzo completo (no solo lo visible) al doble de resolución, con el fondo del tema.</summary>
    public void SavePng(string path)
    {
        // Límite para no pedir un mapa de bits desmesurado en bases enormes.
        double scale = Math.Min(2, 12000 / Math.Max(_canvas.Width, _canvas.Height));
        var bitmap = new RenderTargetBitmap((int)(_canvas.Width * scale), (int)(_canvas.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);

        // El lienzo se dibuja con el zoom de la ventana: se pone al 100 % mientras se copia y luego se restaura.
        double zoom = _zoom;
        try
        {
            SetZoom(1);
            UpdateLayout();
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var area = new Rect(0, 0, _canvas.Width, _canvas.Height);
                context.DrawRectangle((Brush)FindResource("Brush.EditorBackground"), null, area);
                context.DrawRectangle(new VisualBrush(_canvas) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, area);
            }
            bitmap.Render(visual);
        }
        finally
        {
            SetZoom(zoom);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}

/// <summary>Posiciones de las tablas de cada diagrama, por conexión y base.</summary>
public static class DiagramStore
{
    private static readonly string FilePath = System.IO.Path.Combine(App.DataFolder, "diagrams.json");

    private static Dictionary<string, Dictionary<string, double[]>> LoadAll()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double[]>>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
        }
        return new();
    }

    public static Dictionary<string, double[]> Load(string connection, string database) =>
        LoadAll().TryGetValue($"{connection}|{database}", out var positions)
            ? new Dictionary<string, double[]>(positions, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);

    public static void Save(string connection, string database, Dictionary<string, double[]> positions)
    {
        try
        {
            var all = LoadAll();
            all[$"{connection}|{database}"] = positions;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
        }
        catch
        {
            // No poder guardar las posiciones no debe interrumpir el trabajo.
        }
    }
}
