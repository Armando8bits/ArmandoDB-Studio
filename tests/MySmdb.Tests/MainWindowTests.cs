using System.Collections;
using System.Reflection;

namespace MySmdb.Tests;

/// <summary>
/// La ventana principal sin mostrarla: se construye, se le da una conexión ya "abierta" y se llama a sus
/// operaciones internas. No se muestra porque al cargarse abre el diálogo de conexión.
/// </summary>
internal sealed class MainWindowHarness
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public MainWindow Window { get; } = new();

    public void AddConnection(ConnectionProfile profile, params string[] databases)
    {
        var field = typeof(MainWindow).GetField("_databases", Private)!;
        ((Dictionary<ConnectionProfile, List<string>>)field.GetValue(Window)!)[profile] = databases.ToList();
    }

    public object? Call(string method, params object?[] args) =>
        typeof(MainWindow).GetMethods(Private).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(Window, args);

    /// <summary>Las pestañas abiertas, en orden.</summary>
    public List<QueryTab> Tabs
    {
        get
        {
            var entries = (IEnumerable)typeof(MainWindow).GetField("_tabs", Private)!.GetValue(Window)!;
            return entries.Cast<object>().Select(e => (QueryTab)e.GetType().GetProperty("Tab")!.GetValue(e)!).ToList();
        }
    }
}

/// <summary>Menú de clic derecho del editor.</summary>
public class EditorMenuTests
{
    [Fact]
    public void Cortar_y_copiar_solo_se_ofrecen_con_texto_seleccionado() => Ui.Run(async () =>
    {
        var owner = Ui.HiddenOwner();
        try
        {
            var tab = new QueryTab(ConnectionProfile.Offline, null, "Consulta1.sql");
            owner.Content = tab;
            tab.SetText("SELECT 1;\nSELECT 2;");
            await Ui.Settle();
            var menu = tab.SqlEditor.ContextMenu!;
            var items = menu.Items.OfType<System.Windows.Controls.MenuItem>().ToDictionary(i => (string)i.Header);
            Assert.Equal(new[] { "Cortar", "Copiar", "Pegar", "Seleccionar todo" }, items.Keys.ToArray());

            async Task<(bool Cut, bool Copy, bool SelectAll)> Open()
            {
                menu.PlacementTarget = tab.SqlEditor;
                menu.IsOpen = true;
                await Ui.Settle();
                var state = (items["Cortar"].IsEnabled, items["Copiar"].IsEnabled, items["Seleccionar todo"].IsEnabled);
                menu.IsOpen = false;
                await Ui.Settle();
                return state;
            }

            // Sin selección: ni cortar ni copiar; seleccionar todo, sí.
            Assert.Equal((false, false, true), await Open());

            tab.SqlEditor.Select(0, 6);
            Assert.Equal((true, true, true), await Open());

            tab.SqlEditor.SelectionLength = 0;
            Assert.Equal((false, false, true), await Open());
            tab.Close();
        }
        finally
        {
            owner.Close();
        }
    });
}

public class MainWindowTests
{
    [Fact]
    public void Desconectar_no_cierra_los_scripts_los_deja_sin_conexion() => Ui.Run(async () =>
    {
        using var db = new SqliteDb();
        using var folder = new TempFolder();
        string file = folder.File("guardado.sql");
        File.WriteAllText(file, "SELECT 'de archivo';");
        var main = new MainWindowHarness();
        main.AddConnection(db.Profile, "main");

        var edited = (QueryTab)main.Call("AddTab", db.Profile, "main", null)!;
        edited.SqlEditor.Document.Insert(0, "SELECT 'sin guardar';");
        var fromFile = (QueryTab)main.Call("AddTab", db.Profile, "main", null)!;
        fromFile.LoadFile(file);
        main.Call("AddTab", db.Profile, "main", null);   // una consulta nueva, vacía

        await (Task)main.Call("DisconnectAsync", db.Profile)!;

        // Los dos scripts siguen abiertos, con su texto y su estado, pero ya sin conexión; la vacía se cerró sin preguntar.
        Assert.Equal(new[] { edited, fromFile }, main.Tabs);
        Assert.All(main.Tabs, tab => Assert.True(tab.IsOffline));
        Assert.Equal(("SELECT 'sin guardar';", true), (edited.SqlEditor.Text, edited.IsDirty));
        Assert.Equal(("SELECT 'de archivo';", false, file), (fromFile.SqlEditor.Text, fromFile.IsDirty, fromFile.FilePath));
        Assert.True(edited.SqlEditor.CanUndo);   // ni siquiera se pierde el historial de deshacer

        // Sin conexión no se ejecuta nada hasta volver a conectar.
        await edited.ExecuteAsync();
        Assert.Equal("Sin conexión.", edited.StatusText);
        foreach (var tab in main.Tabs) tab.Close();
    });

    [Fact]
    public void Abrir_dos_resultados_de_la_busqueda_abre_dos_pestanas_independientes() => Ui.Run(async () =>
    {
        using var db = new SqliteDb();
        await db.RunAsync("CREATE TABLE cliente (id INTEGER PRIMARY KEY, nombre TEXT); CREATE TABLE pedido (id INTEGER PRIMARY KEY, cliente_id INTEGER);");
        var main = new MainWindowHarness();
        main.AddConnection(db.Profile, "main");

        var first = new SearchHit(SchemaObject.Table, "cliente", null, SearchHit.InColumn, "nombre  TEXT");
        var second = new SearchHit(SchemaObject.Table, "pedido", null, SearchHit.InColumn, "cliente_id  INTEGER");
        await (Task)main.Call("OpenSearchHitAsync", db.Profile, "main", first, "nombre")!;
        await (Task)main.Call("OpenSearchHitAsync", db.Profile, "main", second, "cliente")!;

        var tabs = main.Tabs;
        Assert.Equal(2, tabs.Count);
        Assert.StartsWith("CREATE TABLE cliente", tabs[0].SqlEditor.Text);
        Assert.StartsWith("CREATE TABLE pedido", tabs[1].SqlEditor.Text);
        // Sin nada seleccionado y con el cursor al final, en una línea nueva: lo siguiente que se escriba va después.
        foreach (var tab in tabs)
        {
            Assert.Equal("", tab.SqlEditor.SelectedText);
            Assert.Equal(tab.SqlEditor.Text.Length, tab.SqlEditor.CaretOffset);
            Assert.EndsWith("\n", tab.SqlEditor.Text);
            Assert.False(tab.IsDirty);
            tab.Close();
        }
    });
}
