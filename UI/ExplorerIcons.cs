using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MySmdb;

public enum ExplorerIcon
{
    Server, Database, Table, View, Column, KeyColumn, Folder, Procedure, Function, Trigger, Index, Connect, Disconnect,
    // Acciones de la barra y los menús (los mismos en la ventana principal y en las flotantes).
    Execute, Plan, Cancel, Diagram, SplitSide, SplitStack, Unsplit, Float, DockBack,
    // Resto de opciones de menú.
    NewQuery, Open, Save, SaveAs, SaveAll, History, Close, Exit, Undo, Redo, Find, Replace, Format, Complete, Snippets, Filter, Panel, Pin,
    Next, Previous, MoveGroup, Monitor, Font, Confirm, Theme, Keyboard, Log, About, Refresh, Backup, Restore, Import,
    Copy, CopyHeaders, SelectAll, SortAscending, SortDescending, ClearSort,
    // Panel de resultados: minimizado, maximizado y repartido con el editor.
    PaneMinimize, PaneMaximize, PaneRestore,
}

/// <summary>Iconos vectoriales (16x16) del explorador de objetos y de las acciones, dibujados en código.</summary>
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
    private static readonly Brush PlugGreen = Frozen(0x34, 0xA8, 0x53);
    private static readonly Brush PlugGreenDark = Frozen(0x1F, 0x6E, 0x36);
    private static readonly Brush Red = Frozen(0xD1, 0x34, 0x38);
    private static readonly Brush FolderYellow = Frozen(0xE8, 0xB6, 0x4C);
    private static readonly Brush FolderDark = Frozen(0xA0, 0x7A, 0x1C);
    private static readonly Brush Violet = Frozen(0x6A, 0x5A, 0xCD);
    private static readonly Brush Teal = Frozen(0x1F, 0x9E, 0x8E);
    private static readonly Brush TealDark = Frozen(0x13, 0x6B, 0x60);
    private static readonly Brush Orange = Frozen(0xF2, 0x8C, 0x28);
    private static readonly Brush OrangeDark = Frozen(0xA0, 0x52, 0x0A);
    private static readonly Brush RunGreen = Frozen(0x2E, 0x9E, 0x4F);
    private static readonly Brush StopRed = Frozen(0xB2, 0x22, 0x22);
    private static readonly Brush Accent = Frozen(0x37, 0x94, 0xFF);
    private static readonly Brush AccentLight = Frozen(0xBF, 0xDD, 0xFF);

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

            case ExplorerIcon.Folder:
                Add("M1.5,3.5 H6 L7.5,5 H14.5 V13.5 H1.5 Z", FolderYellow, FolderDark);
                Add("M1.5,6.5 H14.5", null, FolderDark);
                break;

            case ExplorerIcon.Procedure:
                // Hoja con líneas de código.
                Add("M3,1.5 H10.5 L13,4 V14.5 H3 Z", Brushes.White, Violet);
                Add("M5,7 H11 M5,9.5 H11 M5,12 H9", null, Violet);
                break;

            case ExplorerIcon.Function:
                // Recuadro con "fx".
                Add("M1.5,2.5 H14.5 V13.5 H1.5 Z", Teal, TealDark);
                canvas.Children.Add(new TextBlock
                {
                    Text = "fx", FontSize = 9, FontWeight = FontWeights.Bold, FontStyle = FontStyles.Italic,
                    Foreground = Brushes.White, Width = 16, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2.5, 0, 0),
                });
                break;

            case ExplorerIcon.Trigger:
                // Rayo: se dispara ante un evento.
                Add("M9.5,1 L3.5,9 H7.5 L6.5,15 L12.5,6.5 H8.5 Z", Orange, OrangeDark);
                break;

            case ExplorerIcon.Index:
                // Barras ordenadas.
                Add("M2.5,3 H13.5 M2.5,6.5 H11 M2.5,10 H8.5 M2.5,13.5 H6", null, Blue, 1.8);
                break;

            case ExplorerIcon.Connect:
                // Enchufe verde (el mismo del botón Conectar de la pantalla de conexión).
                Add("M5.5,1.5 V5 M10.5,1.5 V5 M8,12 V15", null, PlugGreenDark, 1.8);
                Add("M3.5,5 H12.5 V8 C12.5,10.5 10.5,12 8,12 C5.5,12 3.5,10.5 3.5,8 Z", PlugGreen, PlugGreenDark);
                break;

            case ExplorerIcon.Disconnect:
                // Enchufe gris tachado en rojo.
                Add("M5.5,1.5 V5 M10.5,1.5 V5 M8,12 V15", null, SteelDark, 1.8);
                Add("M3.5,5 H12.5 V8 C12.5,10.5 10.5,12 8,12 C5.5,12 3.5,10.5 3.5,8 Z", Steel, SteelDark);
                Add("M2.5,14 L13.5,2", null, Red, 2);
                break;

            case ExplorerIcon.Execute:
                // Triángulo verde de "reproducir".
                Add("M4,2.5 L13.5,8 L4,13.5 Z", RunGreen, RunGreen);
                break;

            case ExplorerIcon.Cancel:
                // Cuadrado rojo de "detener".
                Add("M3.5,3.5 H12.5 V12.5 H3.5 Z", StopRed, StopRed);
                break;

            case ExplorerIcon.Plan:
                // Árbol de pasos: un nodo y dos de los que se alimenta.
                Add("M4.5,5 V12 H9.5 M4.5,8 H9.5", null, Accent, 1.3);
                Add("M1.5,1.5 H7.5 V5.5 H1.5 Z M9.5,6 H14.5 V10 H9.5 Z M9.5,10.5 H14.5 V14.5 H9.5 Z", Accent, null);
                break;

            case ExplorerIcon.Diagram:
                // Dos tablas relacionadas.
                Add("M7,4.5 H9 V11.5 H10", null, Accent, 1.3);
                Add("M1.5,2 H7 V7.5 H1.5 Z M10,9 H14.5 V14 H10 Z", Accent, null);
                break;

            case ExplorerIcon.SplitSide:
                // Ventana partida en izquierda / derecha, con una mitad resaltada.
                Add("M1.5,2.5 H8 V13.5 H1.5 Z", Accent, null);
                Add("M1.5,2.5 H14.5 V13.5 H1.5 Z M8,2.5 V13.5", null, SteelDark);
                break;

            case ExplorerIcon.SplitStack:
                // Ventana partida en arriba / abajo.
                Add("M1.5,2.5 H14.5 V8 H1.5 Z", Accent, null);
                Add("M1.5,2.5 H14.5 V13.5 H1.5 Z M1.5,8 H14.5", null, SteelDark);
                break;

            case ExplorerIcon.Unsplit:
                // Una sola zona, sin división.
                Add("M1.5,2.5 H14.5 V13.5 H1.5 Z", AccentLight, SteelDark);
                Add("M1.5,2.5 H14.5 V5 H1.5 Z", Accent, SteelDark);
                break;

            case ExplorerIcon.Float:
                // Una ventana que sale de otra.
                Add("M1.5,5.5 H10.5 V14.5 H1.5 Z", Brushes.White, SteelDark);
                Add("M5.5,1.5 H14.5 V10.5 H5.5 Z", AccentLight, SteelDark);
                Add("M5.5,1.5 H14.5 V4 H5.5 Z", Accent, SteelDark);
                break;

            case ExplorerIcon.DockBack:
                // Flecha que vuelve a entrar en la ventana.
                Add("M1.5,2.5 H14.5 V13.5 H1.5 Z", Brushes.White, SteelDark);
                Add("M1.5,2.5 H14.5 V5 H1.5 Z", Accent, SteelDark);
                Add("M12,7 L7,11.5 M7,8 V11.5 H10.5", null, RunGreen, 1.6);
                break;

            case ExplorerIcon.NewQuery:
                // Hoja nueva con un "+".
                Add("M2.5,1.5 H8.5 L11.5,4.5 V14.5 H2.5 Z", Brushes.White, SteelDark);
                Add("M10,11 H15 M12.5,8.5 V13.5", null, RunGreen, 2);
                break;

            case ExplorerIcon.Open:
                // Carpeta abierta.
                Add("M1.5,3.5 H6 L7.5,5 H13 V7 H3.5 L1.5,13.5 Z", GoldLight, FolderDark);
                Add("M1.5,13.5 L3.5,7 H15 L13,13.5 Z", FolderYellow, FolderDark);
                break;

            case ExplorerIcon.Save:
            case ExplorerIcon.SaveAs:
                // Disquete; "guardar como" lleva además un lápiz.
                Add("M2.5,2.5 H11.5 L13.5,4.5 V13.5 H2.5 Z", Blue, SteelDark);
                Add("M5,2.5 H10.5 V6 H5 Z", Brushes.White, null);
                Add("M4.5,9 H11.5 V13.5 H4.5 Z", BlueLight, null);
                if (icon == ExplorerIcon.SaveAs) Add("M9.5,14.5 L15,9", null, Orange, 2.4);
                break;

            case ExplorerIcon.SaveAll:
                // Dos disquetes, uno detrás de otro.
                Add("M5.5,1.5 H12.5 L14.5,3.5 V10.5 H5.5 Z", BlueLight, SteelDark);
                Add("M1.5,5.5 H9 L11,7.5 V14.5 H1.5 Z", Blue, SteelDark);
                Add("M3.5,5.5 H8 V8 H3.5 Z", Brushes.White, null);
                Add("M3.5,10.5 H9 V14.5 H3.5 Z", BlueLight, null);
                break;

            case ExplorerIcon.Close:
                Add("M4,4 L12,12 M12,4 L4,12", null, Red, 2);
                break;

            case ExplorerIcon.Exit:
                // Puerta y flecha de salida.
                Add("M2.5,2 H8.5 V14 H2.5 Z", GrayLight, SteelDark);
                Add("M7,8 H14.5 M12,5.5 L14.5,8 L12,10.5", null, Red, 1.7);
                break;

            case ExplorerIcon.Undo:
                Add("M3,6.5 H10 A3.5,3.5 0 0 1 10,13.5 H6.5", null, Accent, 1.8);
                Add("M5.8,3.5 L2.8,6.5 L5.8,9.5", null, Accent, 1.8);
                break;

            case ExplorerIcon.Redo:
                Add("M13,6.5 H6 A3.5,3.5 0 0 0 6,13.5 H9.5", null, Accent, 1.8);
                Add("M10.2,3.5 L13.2,6.5 L10.2,9.5", null, Accent, 1.8);
                break;

            case ExplorerIcon.Find:
                // Lupa.
                Add("M2.5,6.5 A4,4 0 1 0 10.5,6.5 A4,4 0 1 0 2.5,6.5 Z", BlueLight, SteelDark, 1.5);
                Add("M9.6,9.6 L14,14", null, SteelDark, 2.2);
                break;

            case ExplorerIcon.Replace:
                // Dos flechas que se intercambian.
                Add("M2.5,5 H12.5 M10,2.5 L12.5,5 L10,7.5", null, Accent, 1.7);
                Add("M13.5,11 H3.5 M6,8.5 L3.5,11 L6,13.5", null, Orange, 1.7);
                break;

            case ExplorerIcon.Format:
                // Líneas con sangría.
                Add("M2,3 H14 M5,6.5 H14 M5,10 H11 M2,13.5 H14", null, Accent, 1.6);
                break;

            case ExplorerIcon.Complete:
                // Lista de sugerencias con una resaltada.
                Add("M1.5,2.5 H14.5 V13.5 H1.5 Z", Brushes.White, SteelDark);
                Add("M2.5,6.5 H13.5 V9.5 H2.5 Z", AccentLight, null);
                Add("M3.5,5 H9 M3.5,8 H12 M3.5,11 H8", null, Accent, 1.3);
                break;

            case ExplorerIcon.Snippets:
                // Llaves de código.
                Add("M6,2 C4,2 4.5,4.5 4.5,6 C4.5,7.5 3,8 3,8 C3,8 4.5,8.5 4.5,10 C4.5,11.5 4,14 6,14", null, Violet, 1.6);
                Add("M10,2 C12,2 11.5,4.5 11.5,6 C11.5,7.5 13,8 13,8 C13,8 11.5,8.5 11.5,10 C11.5,11.5 12,14 10,14", null, Violet, 1.6);
                break;

            case ExplorerIcon.Filter:
                // Embudo.
                Add("M2,2.5 H14 L9.5,8 V13.5 L6.5,12 V8 Z", AccentLight, Accent, 1.2);
                break;

            case ExplorerIcon.Panel:
                // Ventana con su panel lateral.
                Add("M1.5,2.5 H6 V13.5 H1.5 Z", Accent, null);
                Add("M1.5,2.5 H14.5 V13.5 H1.5 Z M6,2.5 V13.5", null, SteelDark);
                break;

            case ExplorerIcon.Pin:
                // Chincheta.
                Add("M6,2 H10 L9.5,7 L12,9.5 H4 L6.5,7 Z", Orange, OrangeDark);
                Add("M8,9.5 V14.5", null, OrangeDark, 1.5);
                break;

            case ExplorerIcon.Next:
                Add("M3,8 H12.5 M9,4.5 L12.5,8 L9,11.5", null, Accent, 1.8);
                break;

            case ExplorerIcon.Previous:
                Add("M13,8 H3.5 M7,4.5 L3.5,8 L7,11.5", null, Accent, 1.8);
                break;

            case ExplorerIcon.MoveGroup:
                // De una mitad de la ventana a la otra.
                Add("M1.5,2.5 H14.5 V13.5 H1.5 Z M8,2.5 V13.5", null, SteelDark);
                Add("M4,8 H12 M10,6 L12,8 L10,10", null, RunGreen, 1.7);
                break;

            case ExplorerIcon.Monitor:
                // Pulso de actividad.
                Add("M1.5,8.5 H4.5 L6.5,3 L9.5,13.5 L11.5,8.5 H14.5", null, RunGreen, 1.7);
                break;

            case ExplorerIcon.Font:
                canvas.Children.Add(new TextBlock
                {
                    Text = "Aa", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Accent,
                    Width = 16, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0.5, 0, 0),
                });
                break;

            case ExplorerIcon.Confirm:
                // Escudo con una exclamación.
                Add("M8,1.5 L13.5,3.5 V8 C13.5,11 11,13.5 8,14.5 C5,13.5 2.5,11 2.5,8 V3.5 Z", Orange, OrangeDark);
                Add("M8,5 V9 M8,11.3 V11.6", null, Brushes.White, 1.8);
                break;

            case ExplorerIcon.Theme:
                // Círculo mitad claro, mitad oscuro.
                Add("M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 Z", Brushes.White, SteelDark, 1.3);
                Add("M8,2 A6,6 0 0 0 8,14 Z", SteelDark, null);
                break;

            case ExplorerIcon.Keyboard:
                Add("M1.5,4.5 H14.5 V12 H1.5 Z", GrayLight, SteelDark);
                Add("M3.5,7 H4.5 M6.2,7 H7.2 M8.9,7 H9.9 M11.6,7 H12.6 M4.5,9.8 H11.5", null, SteelDark, 1.3);
                break;

            case ExplorerIcon.History:
                // Reloj.
                Add("M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 Z", Brushes.White, SteelDark, 1.4);
                Add("M8,4.5 V8 L10.5,9.6", null, Accent, 1.7);
                break;

            case ExplorerIcon.Log:
                // Hoja con una exclamación: el registro de errores.
                Add("M3,1.5 H10 L13,4.5 V14.5 H3 Z", Brushes.White, SteelDark);
                Add("M8,6 V10 M8,12.2 V12.5", null, Red, 1.8);
                break;

            case ExplorerIcon.About:
                Add("M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 Z", Accent, null);
                Add("M8,7.2 V11.5 M8,4.6 V4.9", null, Brushes.White, 1.9);
                break;

            case ExplorerIcon.Refresh:
                // Flecha circular.
                Add("M13,8.5 A5,5 0 1 1 10.8,4.2", null, RunGreen, 1.8);
                Add("M11.8,1.2 L11,4.6 L7.8,3.8", null, RunGreen, 1.8);
                break;

            case ExplorerIcon.Backup:
            case ExplorerIcon.Restore:
                // Base de datos con una flecha: hacia abajo (sacar una copia) o hacia arriba (volver a cargarla).
                Add("M1.5,3.5 V10 C1.5,11.4 3.5,12.5 6,12.5 C8.5,12.5 10.5,11.4 10.5,10 V3.5 Z", Gold, GoldDark);
                Add("M1.5,3.5 C1.5,2.1 3.5,1 6,1 C8.5,1 10.5,2.1 10.5,3.5 C10.5,4.9 8.5,6 6,6 C3.5,6 1.5,4.9 1.5,3.5 Z", GoldLight, GoldDark);
                if (icon == ExplorerIcon.Backup) Add("M12.8,7.5 V14 M10.3,11.5 L12.8,14 L15.3,11.5", null, RunGreen, 1.8);
                else Add("M12.8,14.5 V8 M10.3,10.5 L12.8,8 L15.3,10.5", null, Accent, 1.8);
                break;

            case ExplorerIcon.Import:
                // Tabla a la que se añaden filas.
                AddGrid(Add, Blue, BlueLight);
                Add("M9.5,9.5 H15.5 V15.5 H9.5 Z", Brushes.White, null);
                Add("M10,12.5 H15 M12.5,10 V15", null, RunGreen, 2);
                break;

            case ExplorerIcon.Copy:
            case ExplorerIcon.CopyHeaders:
                // Dos hojas; "con encabezados" resalta la primera fila.
                Add("M2.5,1.5 H10.5 V11.5 H2.5 Z", BlueLight, SteelDark);
                Add("M5.5,4.5 H13.5 V14.5 H5.5 Z", Brushes.White, SteelDark);
                if (icon == ExplorerIcon.CopyHeaders) Add("M5.5,4.5 H13.5 V7.5 H5.5 Z", Accent, SteelDark);
                break;

            case ExplorerIcon.SelectAll:
                // Esquinas de una selección alrededor de un bloque.
                Add("M4.5,4.5 H11.5 V11.5 H4.5 Z", AccentLight, null);
                Add("M2,5 V2 H5 M11,2 H14 V5 M14,11 V14 H11 M5,14 H2 V11", null, Accent, 1.6);
                break;

            case ExplorerIcon.SortAscending:
                Add("M4.5,13 V3 M2,5.5 L4.5,3 L7,5.5", null, Accent, 1.7);
                Add("M9,4 H11 M9,8 H13 M9,12 H15", null, SteelDark, 1.7);
                break;

            case ExplorerIcon.SortDescending:
                Add("M4.5,3 V13 M2,10.5 L4.5,13 L7,10.5", null, Accent, 1.7);
                Add("M9,4 H15 M9,8 H13 M9,12 H11", null, SteelDark, 1.7);
                break;

            case ExplorerIcon.ClearSort:
                Add("M2,4 H9 M2,8 H9 M2,12 H9", null, SteelDark, 1.7);
                Add("M11,6 L15,10 M15,6 L11,10", null, Red, 1.7);
                break;

            case ExplorerIcon.PaneMinimize:
                // El panel reducido a una franja abajo.
                Add("M1.5,2.5 H14.5 V13.5 H1.5 Z", Brushes.White, Steel);
                Add("M1.5,11 H14.5 V13.5 H1.5 Z", Accent, Steel);
                break;

            case ExplorerIcon.PaneMaximize:
                // El panel ocupando todo.
                Add("M1.5,2.5 H14.5 V13.5 H1.5 Z", Accent, Steel);
                break;

            case ExplorerIcon.PaneRestore:
                // Editor arriba y panel abajo, a medias.
                Add("M1.5,2.5 H14.5 V13.5 H1.5 Z", Brushes.White, Steel);
                Add("M1.5,8 H14.5 V13.5 H1.5 Z", Accent, Steel);
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
