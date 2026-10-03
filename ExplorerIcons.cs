using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MySmdb;

public enum ExplorerIcon { Server, Database, Table, View, Column, KeyColumn }

/// <summary>Iconos vectoriales (16x16) del explorador de objetos, dibujados en código.</summary>
public static class ExplorerIcons
{
    private static readonly Brush Gold = Frozen(0xF2, 0xC4, 0x4D);
    private static readonly Brush GoldLight = Frozen(0xFB, 0xE3, 0x9A);
    private static readonly Brush GoldDark = Frozen(0x9A, 0x74, 0x12);
    private static readonly Brush Blue = Frozen(0x3B, 0x6F, 0xB5);
    private static readonly Brush BlueLight = Frozen(0xDD, 0xE8, 0xF7);
    private static readonly Brush Purple = Frozen(0x7A, 0x55, 0xA8);
    private static readonly Brush PurpleLight = Frozen(0xEA, 0xE2, 0xF5);
    private static readonly Brush Steel = Frozen(0x8C, 0x9B, 0xB2);
    private static readonly Brush SteelDark = Frozen(0x4A, 0x58, 0x70);
    private static readonly Brush Green = Frozen(0x3F, 0xC0, 0x5A);
    private static readonly Brush Gray = Frozen(0x7A, 0x7A, 0x7A);
    private static readonly Brush GrayLight = Frozen(0xD9, 0xD9, 0xD9);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Encabezado de un nodo del árbol: icono + texto.</summary>
    public static StackPanel Header(ExplorerIcon icon, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Create(icon));
        panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    public static FrameworkElement Create(ExplorerIcon icon)
    {
        var canvas = new Canvas { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };

        void Add(string data, Brush? fill, Brush? stroke, double thickness = 1) => canvas.Children.Add(new Path
        {
            Data = Geometry.Parse(data),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

        switch (icon)
        {
            case ExplorerIcon.Server:
                // Torre con dos ranuras y un piloto verde.
                Add("M3.5,1.5 H12.5 V14.5 H3.5 Z", Steel, SteelDark);
                Add("M5.5,4.5 H10.5 M5.5,7 H10.5", null, Brushes.White);
                Add("M9.5,11.5 A1,1 0 1 0 11.5,11.5 A1,1 0 1 0 9.5,11.5 Z", Green, null);
                break;

            case ExplorerIcon.Database:
                // Cilindro clásico: cuerpo, tapa y las dos franjas de los discos.
                Add("M2,4 V12 C2,13.7 4.7,15 8,15 C11.3,15 14,13.7 14,12 V4 Z", Gold, GoldDark);
                Add("M2,4 C2,2.3 4.7,1 8,1 C11.3,1 14,2.3 14,4 C14,5.7 11.3,7 8,7 C4.7,7 2,5.7 2,4 Z", GoldLight, GoldDark);
                Add("M2,8 C2,9.7 4.7,11 8,11 C11.3,11 14,9.7 14,8", null, GoldDark);
                break;

            case ExplorerIcon.Table:
                AddGrid(Add, Blue, BlueLight);
                break;

            case ExplorerIcon.View:
                // Cuadrícula como la tabla, en otro color y con un ojo: es una consulta, no datos propios.
                AddGrid(Add, Purple, PurpleLight);
                Add("M6.5,11.5 C8,9 13,9 14.5,11.5 C13,14 8,14 6.5,11.5 Z", Brushes.White, SteelDark);
                Add("M9.4,11.5 A1.1,1.1 0 1 0 11.6,11.5 A1.1,1.1 0 1 0 9.4,11.5 Z", SteelDark, null);
                break;

            case ExplorerIcon.Column:
                // Una sola columna con su celda de encabezado.
                Add("M5.5,2.5 H10.5 V13.5 H5.5 Z", Brushes.White, Gray);
                Add("M5.5,2.5 H10.5 V5.5 H5.5 Z", GrayLight, Gray);
                Add("M5.5,9.5 H10.5", null, Gray);
                break;

            case ExplorerIcon.KeyColumn:
                // Llave: clave primaria.
                Add("M2.5,5.5 A3,3 0 1 0 8.5,5.5 A3,3 0 1 0 2.5,5.5 Z", null, GoldDark, 1.8);
                Add("M7.7,7.7 L13.5,13.5 M11,11 L12.8,9.2 M13.2,13.2 L14.6,11.8", null, GoldDark, 1.8);
                break;
        }
        return canvas;
    }

    private static void AddGrid(Action<string, Brush?, Brush?, double> add, Brush main, Brush light)
    {
        add("M1.5,2.5 H14.5 V13.5 H1.5 Z", Brushes.White, main, 1);
        add("M1.5,2.5 H14.5 V5.5 H1.5 Z", main, main, 1);
        add("M2,9.5 H14 M6,6 V13 M10.5,6 V13", null, main, 1);
        add("M2.5,6.5 H5.5 V9 H2.5 Z M2.5,10 H5.5 V13 H2.5 Z", light, null, 1);
    }
}
