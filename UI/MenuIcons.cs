using System.Windows.Controls;

namespace MySmdb;

/// <summary>
/// Pone icono a las opciones de menú según su texto: así la misma acción lleva el mismo icono en el menú
/// principal y en los menús de clic derecho, sin repetir la asignación en cada sitio donde se construye uno.
/// </summary>
public static class MenuIcons
{
    // Comienzo del texto de la opción (sin el "_" del atajo) → icono. El primero que coincide gana: los más
    // específicos van antes ("Guardar como" antes que "Guardar").
    private static readonly (string Start, ExplorerIcon Icon)[] Map =
    {
        ("Conectar", ExplorerIcon.Connect), ("Desconectar", ExplorerIcon.Disconnect),
        ("Nueva consulta", ExplorerIcon.NewQuery), ("Abrir", ExplorerIcon.Open),
        ("Guardar resultados", ExplorerIcon.Save), ("Guardar como", ExplorerIcon.SaveAs), ("Guardar", ExplorerIcon.Save),
        ("Cerrar las demás", ExplorerIcon.Close), ("Cerrar", ExplorerIcon.Close), ("Salir", ExplorerIcon.Exit),
        ("Deshacer", ExplorerIcon.Undo), ("Rehacer", ExplorerIcon.Redo),
        ("Buscar", ExplorerIcon.Find), ("Reemplazar", ExplorerIcon.Replace),
        ("Formatear", ExplorerIcon.Format), ("Autocompletar", ExplorerIcon.Complete), ("Fragmentos de código", ExplorerIcon.Snippets),
        ("Ejecutar", ExplorerIcon.Execute), ("Mostrar el plan", ExplorerIcon.Plan), ("Cancelar", ExplorerIcon.Cancel),
        ("Filtrar", ExplorerIcon.Filter),
        ("Mostrar u ocultar el explorador", ExplorerIcon.Panel),
        ("Minimizar", ExplorerIcon.PaneMinimize), ("Maximizar", ExplorerIcon.PaneMaximize),
        ("Anclar", ExplorerIcon.Pin), ("Desanclar", ExplorerIcon.Pin),
        ("Pestaña siguiente", ExplorerIcon.Next), ("Pestaña anterior", ExplorerIcon.Previous),
        ("Dividir: izquierda", ExplorerIcon.SplitSide), ("Dividir: arriba", ExplorerIcon.SplitStack),
        ("Mover pestaña al otro grupo", ExplorerIcon.MoveGroup), ("Mover al otro grupo", ExplorerIcon.MoveGroup),
        ("Quitar la división", ExplorerIcon.Unsplit),
        ("Mover la pestaña a una ventana", ExplorerIcon.Float), ("Mover a una ventana", ExplorerIcon.Float),
        ("Devolver", ExplorerIcon.DockBack),
        ("Diagrama", ExplorerIcon.Diagram), ("Ver diagrama", ExplorerIcon.Diagram),
        ("Monitor de procesos", ExplorerIcon.Monitor),
        ("Fuente", ExplorerIcon.Font), ("Confirmaciones", ExplorerIcon.Confirm), ("Tema", ExplorerIcon.Theme),
        ("Atajos", ExplorerIcon.Keyboard), ("Registro de errores", ExplorerIcon.Log), ("Acerca de", ExplorerIcon.About),
        ("Actualizar", ExplorerIcon.Refresh),
        ("Copia de seguridad", ExplorerIcon.Backup), ("Restaurar", ExplorerIcon.Restore), ("Importar", ExplorerIcon.Import),
        ("Seleccionar las primeras", ExplorerIcon.Table),
        ("Generar llamada", ExplorerIcon.Execute), ("Generar script", ExplorerIcon.Procedure),
        ("CREATE", ExplorerIcon.Procedure), ("SELECT", ExplorerIcon.Table), ("INSERT", ExplorerIcon.Import),
        ("UPDATE", ExplorerIcon.Replace), ("DELETE", ExplorerIcon.Close),
        ("Copiar con encabezados", ExplorerIcon.CopyHeaders), ("Copiar", ExplorerIcon.Copy),
        ("Seleccionar todo", ExplorerIcon.SelectAll), ("Seleccionar la columna", ExplorerIcon.Column),
        ("Ordenar ascendente", ExplorerIcon.SortAscending), ("Ordenar descendente", ExplorerIcon.SortDescending),
        ("Quitar el orden", ExplorerIcon.ClearSort),
    };

    /// <summary>Icono de una opción por su texto, o null si no tiene ninguno asignado.</summary>
    public static ExplorerIcon? Find(string header)
    {
        string text = header.Replace("_", "");
        foreach (var (start, icon) in Map)
            if (text.StartsWith(start, StringComparison.OrdinalIgnoreCase)) return icon;
        return null;
    }

    /// <summary>
    /// El tema atenúa el texto de una opción deshabilitada, pero no su icono de color, y entonces parece activa:
    /// se atenúa también el icono, ahora y cada vez que la opción se habilite o deshabilite.
    /// </summary>
    private static void DimWhenDisabled(MenuItem item)
    {
        if (item.Icon is not System.Windows.UIElement icon) return;
        icon.Opacity = item.IsEnabled ? 1 : 0.35;
        item.IsEnabledChanged += (_, _) => icon.Opacity = item.IsEnabled ? 1 : 0.35;
    }

    /// <summary>
    /// Recorre el menú (y sus submenús) y pone icono a las opciones que aún no tienen. Las de marcar no llevan:
    /// ese hueco es para la marca.
    /// </summary>
    public static void Apply(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Icon == null && !item.IsCheckable && item.Header is string header && Find(header) is { } icon)
                item.Icon = ExplorerIcons.Create(icon);
            DimWhenDisabled(item);
            Apply(item);
        }
    }
}
