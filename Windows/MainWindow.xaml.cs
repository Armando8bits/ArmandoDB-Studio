using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MySmdb;

public partial class MainWindow : Window
{
    private enum NodeKind { Server, Database, Folder, Table, View, Procedure, Function, Trigger, Index }

    /// <summary>Nodo del explorador. <paramref name="Table"/>: tabla a la que pertenece un trigger o un índice.</summary>
    private record Node(NodeKind Kind, ConnectionProfile Profile, string? Database = null, string? Name = null, string? Table = null);

    private class TabEntry
    {
        public required QueryTab Tab { get; init; }
        public required Border Header { get; init; }
        public required TextBlock Title { get; init; }
        public required Button PinButton { get; init; }
        public bool Pinned { get; set; }
        public required TabGroup Group { get; set; }
    }

    /// <summary>Un panel de la vista dividida, con sus propias franjas de pestañas (ancladas y normales).</summary>
    private class TabGroup
    {
        public required DockPanel Root { get; init; }
        public required Border PinnedRow { get; init; }
        public required WrapPanel PinnedStrip { get; init; }
        public required WrapPanel NormalStrip { get; init; }
        public required Grid Host { get; init; }
        public required GroupArea Area { get; init; }
        public TabEntry? Selected { get; set; }
    }

    /// <summary>
    /// Zona de pestañas de una ventana: la de la ventana principal o la de una ventana flotante
    /// (para llevar pestañas a otro monitor). Cada zona tiene uno o dos grupos (vista dividida).
    /// </summary>
    private class GroupArea
    {
        public required Grid Grid { get; init; }
        public List<TabGroup> Groups { get; } = new();
        /// <summary>Vista dividida: true = izquierda/derecha; false = arriba/abajo.</summary>
        public bool SideBySide { get; set; } = true;
        /// <summary>Último grupo usado de la zona: el que se reactiva al volver a su ventana.</summary>
        public TabGroup? ActiveGroup { get; set; }
        /// <summary>Ventana flotante de la zona; null en la zona de la ventana principal.</summary>
        public Window? Window { get; init; }
        public StatusBarView? Status { get; init; }
        /// <summary>Botón Cancelar de la ventana flotante: solo activo mientras su pestaña ejecuta algo.</summary>
        public Button? CancelButton { get; set; }
        /// <summary>Ejecutar y Plan de la ventana flotante: se desactivan mientras su pestaña ejecuta algo.</summary>
        public Button? ExecuteButton { get; set; }
        public Button? PlanButton { get; set; }
        /// <summary>"Quitar división" de la ventana flotante: solo activo con la vista dividida.</summary>
        public Button? UnsplitButton { get; set; }
        /// <summary>Botones de dividir de la ventana flotante: se desactiva el de la orientación ya aplicada.</summary>
        public Button? SplitSideButton { get; set; }
        public Button? SplitStackButton { get; set; }
    }

    private const string FileFilter = "Archivos SQL (*.sql)|*.sql|Todos los archivos (*.*)|*.*";
    private static readonly object Placeholder = new();

    private readonly Dictionary<ConnectionProfile, List<string>> _databases = new();
    private readonly List<TabEntry> _tabs = new();
    private readonly GroupArea _main;
    private readonly List<GroupArea> _areas = new();
    private TabGroup _activeGroup;

    private IEnumerable<TabGroup> AllGroups => _areas.SelectMany(a => a.Groups);
    private TabEntry? _dragCandidate;
    private Point _dragStart;
    private int _queryCounter;
    private bool _syncingCombo;
    private bool _suspendSessionSave;
    private GridLength _explorerWidth = new(270);

    public MainWindow()
    {
        InitializeComponent();
        _main = new GroupArea { Grid = GroupsGrid, SideBySide = AppSettings.Current.SplitSideBySide };
        _areas.Add(_main);
        _activeGroup = CreateGroup(_main);
        LayoutGroups(_main);
        // Al volver a esta ventana desde una flotante, sus menús y botones actúan sobre sus propias pestañas.
        Activated += (_, _) => ActivateArea(_main);
        SetExplorerVisible(AppSettings.Current.ShowExplorer);

        UpdateThemeMenu();
        // Los mismos iconos que en la pantalla de conexión.
        ConnectButton.Content = ExplorerIcons.Header(ExplorerIcon.Connect, "Conectar");
        DisconnectButton.Content = ExplorerIcons.Header(ExplorerIcon.Disconnect, "Desconectar");
        ConnectMenuItem.Icon = ExplorerIcons.Create(ExplorerIcon.Connect);
        DisconnectMenuItem.Icon = ExplorerIcons.Create(ExplorerIcon.Disconnect);

        // Las acciones llevan el mismo icono en la barra, en los menús y en las ventanas flotantes.
        ExecuteButton.Content = ExplorerIcons.Header(ExplorerIcon.Execute, "Ejecutar");
        ExplainButton.Content = ExplorerIcons.Header(ExplorerIcon.Plan, "Plan");
        CancelButton.Content = ExplorerIcons.Header(ExplorerIcon.Cancel, "Cancelar");
        NewQueryButton.Content = ExplorerIcons.Header(ExplorerIcon.NewQuery, "Nueva consulta");
        UpdateRecentMenu();
        SaveButton.Content = ExplorerIcons.Create(ExplorerIcon.Save);
        SaveAllButton.Content = ExplorerIcons.Create(ExplorerIcon.SaveAll);
        UndoButton.Content = ExplorerIcons.Create(ExplorerIcon.Undo);
        RedoButton.Content = ExplorerIcons.Create(ExplorerIcon.Redo);
        ExecuteMenuItem.Icon = ExplorerIcons.Create(ExplorerIcon.Execute);
        ExplainMenuItem.Icon = ExplorerIcons.Create(ExplorerIcon.Plan);
        CancelMenuItem.Icon = ExplorerIcons.Create(ExplorerIcon.Cancel);
        DiagramMenuItem.Icon = ExplorerIcons.Create(ExplorerIcon.Diagram);
        SplitSideMenuItem.Icon = ExplorerIcons.Create(ExplorerIcon.SplitSide);
        SplitStackMenuItem.Icon = ExplorerIcons.Create(ExplorerIcon.SplitStack);
        UnsplitMenuItem.Icon = ExplorerIcons.Create(ExplorerIcon.Unsplit);
        FloatMenuItem.Icon = ExplorerIcons.Create(ExplorerIcon.Float);
        DockMenuItem.Icon = ExplorerIcons.Create(ExplorerIcon.DockBack);
        // El resto de opciones del menú principal toman su icono por el texto (ver MenuIcons).
        MenuIcons.Apply(MainMenu);
        SnippetsEnabledItem.IsChecked = AppSettings.Current.SnippetsEnabled;
        AutoCompleteItem.IsChecked = AppSettings.Current.AutoCompleteEnabled;
        ConfirmDangerousItem.IsChecked = AppSettings.Current.ConfirmDangerous;
        ConfirmProductionItem.IsChecked = AppSettings.Current.ConfirmProductionWrites;
        Theme.Changed += Theme_Changed;
        Closed += (_, _) => Theme.Changed -= Theme_Changed;
    }

    // ---------- Tema ----------

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var choice = Theme.Parse((string)((MenuItem)sender).Tag);
        AppSettings.Current.Theme = choice.ToString();
        try { AppSettings.Current.Save(); } catch { }
        Theme.Apply(choice);
    }

    private void Theme_Changed()
    {
        UpdateThemeMenu();
        foreach (var entry in _tabs)
        {
            entry.Tab.ApplyTheme();
            StyleHeader(entry);
        }
    }

    private void UpdateThemeMenu()
    {
        ThemeSystemItem.IsChecked = Theme.Choice == ThemeChoice.System;
        ThemeLightItem.IsChecked = Theme.Choice == ThemeChoice.Light;
        ThemeDarkItem.IsChecked = Theme.Choice == ThemeChoice.Dark;
    }

    private void ToggleExplorer_Click(object sender, RoutedEventArgs e)
    {
        bool show = ExplorerPanel.Visibility != Visibility.Visible;
        SetExplorerVisible(show);
        AppSettings.Current.ShowExplorer = show;
        try { AppSettings.Current.Save(); } catch { }
    }

    private void SetExplorerVisible(bool show)
    {
        // Al ocultarlo se recuerda el ancho que el usuario le había dado.
        if (!show && ExplorerPanel.Visibility == Visibility.Visible)
            _explorerWidth = ExplorerColumn.Width;

        ExplorerPanel.Visibility = ExplorerSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ExplorerColumn.MinWidth = show ? 80 : 0;
        ExplorerColumn.Width = show ? _explorerWidth : new GridLength(0);
        ExplorerToggle.IsChecked = show;
    }

    /// <summary>Pestaña seleccionada del grupo activo: sobre ella actúan los menús y atajos.</summary>
    private TabEntry? ActiveEntry => _activeGroup.Selected;

    private QueryTab? Current => ActiveEntry?.Tab;

    /// <summary>Pestañas de un grupo en el orden en que se ven: primero las ancladas.</summary>
    private List<TabEntry> VisualOrder(TabGroup group) =>
        _tabs.Where(t => t.Group == group && t.Pinned).Concat(_tabs.Where(t => t.Group == group && !t.Pinned)).ToList();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartRecovery();
        await ConnectAsync();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(App.DataFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Errors.Show(this, "No se pudo abrir la carpeta de datos", ex);
        }
    }

    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Copia de recuperación de las consultas sin guardar (ver RecoveryStore): avisa si la ejecución anterior
    /// dejó alguna y empieza a guardar las de esta cada pocos segundos.
    /// </summary>
    private void StartRecovery()
    {
        var recovered = RecoveryStore.CollectFromCrashes();
        RecoveryStore.Start(() => _tabs
            .Where(t => t.Tab.IsDirty && t.Tab.SqlEditor.Text.Trim().Length > 0)
            .DistinctBy(t => t.Tab.Script)   // un script con varias vistas se guarda una vez
            .Select(t => new UnsavedQuery(t.Tab.Title, t.Tab.Profile.Name, t.Tab.FilePath, t.Tab.SqlEditor.Text)).ToList());
        _recoveryTimer.Tick += (_, _) => RecoveryStore.SaveIfChanged();
        _recoveryTimer.Start();

        if (recovered is not { } found) return;
        var answer = MessageBox.Show(this,
            $"La última vez {App.Name} no se cerró correctamente.\n\nSe recuperaron {found.Count} consulta(s) con cambios sin guardar en:\n{found.Folder}\n\n" +
            "Puedes abrirlas con Archivo → Abrir. ¿Abrir ahora esa carpeta?",
            "Consultas recuperadas", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(found.Folder) { UseShellExecute = true }); }
        catch (Exception ex) { Errors.Show(this, "No se pudo abrir la carpeta", ex); }
    }

    // ---------- Atajos de teclado ----------

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if ((modifiers == ModifierKeys.Control && e.Key == Key.E) || (modifiers == ModifierKeys.None && e.Key == Key.F5))
        {
            e.Handled = true;
            await ExecuteCurrentAsync();
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            e.Handled = true;
            NewQueryFromCurrent();
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            e.Handled = true;
            await ExplainCurrentAsync();
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.B)
        {
            e.Handled = true;
            ToggleExplorer_Click(sender, e);
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            // Aunque el foco esté en la cuadrícula o en Mensajes, se busca en el editor SQL.
            e.Handled = true;
            Current?.OpenSearch();
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.H)
        {
            e.Handled = true;
            OpenReplace();
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.L)
        {
            e.Handled = true;
            Current?.ToggleResultFilter();
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.F)
        {
            e.Handled = true;
            FormatCurrent();
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.Space)
        {
            e.Handled = true;
            Current?.ShowCompletion(forced: true);
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            e.Handled = true;
            OpenFile();
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            e.Handled = true;
            if (Current != null) Save(Current, saveAs: false);
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        {
            e.Handled = true;
            SaveAll();
        }
        else if (modifiers == ModifierKeys.Alt && (e.Key == Key.System ? e.SystemKey : e.Key) is Key.Left or Key.Right or Key.Up or Key.Down
                 && Keyboard.FocusedElement is not (ComboBox or ComboBoxItem))   // en un desplegable, Alt+↓ lo abre
        {
            // Alt + flecha: la pestaña actual se va a ese lado y las demás al contrario.
            // (Ctrl + flechas no vale: en el editor es saltar por palabras y desplazar el texto.)
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            SplitToward(sideBySide: key is Key.Left or Key.Right, activeSecond: key is Key.Right or Key.Down);
        }
        else if (modifiers == ModifierKeys.Alt && (e.Key == Key.System ? e.SystemKey : e.Key) is Key.Enter or Key.Return)
        {
            // Alt+Intro: quitar la división, sin tener que recordar hacia dónde se dividió.
            // (Alt+Espacio no vale: es el menú de sistema de la ventana en Windows.)
            e.Handled = true;
            Unsplit();
        }
        else if (e.Key == Key.R && modifiers == ModifierKeys.Control)
        {
            // Como en SSMS: Ctrl+R oculta o muestra el panel de resultados.
            e.Handled = true;
            Current?.ToggleMinimizeResults();
        }
        else if (e.Key == Key.R && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            Current?.ToggleMaximizeResults();
        }
        else if (modifiers == ModifierKeys.Control && e.Key == Key.W)
        {
            e.Handled = true;
            if (ActiveEntry != null) CloseTab(ActiveEntry);
        }
        else if (e.Key == Key.Tab && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SelectRelative(1);
        }
        else if (e.Key == Key.Tab && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            SelectRelative(-1);
        }
        else if (modifiers == ModifierKeys.Alt && (e.SystemKey == Key.Pause || e.SystemKey == Key.Cancel))
        {
            e.Handled = true;
            Current?.Cancel();
        }
    }

    // ---------- Menú y barra de herramientas ----------

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();
    private void NewQuery_Click(object sender, RoutedEventArgs e) => NewQueryFromCurrent();
    private void Open_Click(object sender, RoutedEventArgs e) => OpenFile();
    private void Save_Click(object sender, RoutedEventArgs e) { if (Current != null) Save(Current, saveAs: false); }
    private void SaveAs_Click(object sender, RoutedEventArgs e) { if (Current != null) Save(Current, saveAs: true); }
    private void SaveAll_Click(object sender, RoutedEventArgs e) => SaveAll();

    /// <summary>
    /// Cierra todas las pestañas de todas las ventanas, menos las ancladas. Se detiene si en alguna se cancela
    /// el "¿guardar los cambios?".
    /// </summary>
    private void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _tabs.Where(t => !t.Pinned).ToList())
            if (!CloseTab(entry)) break;
    }
    private void CloseTab_Click(object sender, RoutedEventArgs e) { if (ActiveEntry != null) CloseTab(ActiveEntry); }
    private void MinimizeResults_Click(object sender, RoutedEventArgs e) => Current?.ToggleMinimizeResults();
    private void MaximizeResults_Click(object sender, RoutedEventArgs e) => Current?.ToggleMaximizeResults();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private async void Execute_Click(object sender, RoutedEventArgs e) => await ExecuteCurrentAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Current?.Cancel();

    private async void Explain_Click(object sender, RoutedEventArgs e) => await ExplainCurrentAsync();
    private void Find_Click(object sender, RoutedEventArgs e) => Current?.OpenSearch();
    private void Replace_Click(object sender, RoutedEventArgs e) => OpenReplace();

    private ReplaceDialog? _replaceDialog;

    /// <summary>Ventana de reemplazar, única y no modal; siempre actúa sobre la pestaña activa.</summary>
    private void OpenReplace()
    {
        if (_replaceDialog == null)
        {
            _replaceDialog = new ReplaceDialog(this, () => Current?.SqlEditor);
            _replaceDialog.Closed += (_, _) => _replaceDialog = null;
            _replaceDialog.Show();
        }
        _replaceDialog.Prefill(Current?.SqlEditor.SelectedText);
        _replaceDialog.Activate();
    }

    private void Format_Click(object sender, RoutedEventArgs e) => FormatCurrent();
    private void Complete_Click(object sender, RoutedEventArgs e) => Current?.ShowCompletion(forced: true);

    private void FormatCurrent()
    {
        if (Current?.FormatSql() is { } warning)
            MessageBox.Show(this, warning, "Formatear SQL", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AutoComplete_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.AutoCompleteEnabled = AutoCompleteItem.IsChecked;
        try { AppSettings.Current.Save(); } catch { }
    }

    private void SnippetsEnabled_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.SnippetsEnabled = SnippetsEnabledItem.IsChecked;
        try { AppSettings.Current.Save(); } catch { }
    }

    private void ConfirmSettings_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.ConfirmDangerous = ConfirmDangerousItem.IsChecked;
        AppSettings.Current.ConfirmProductionWrites = ConfirmProductionItem.IsChecked;
        try { AppSettings.Current.Save(); } catch { }
    }

    private void Snippets_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "Escribe la abreviatura en el editor y pulsa Tab:\n\n" + Snippets.Describe()
                + (AppSettings.Current.SnippetsEnabled ? "" : "\n\nAhora están desactivados: actívalos en el menú Editar."),
            "Fragmentos de código", MessageBoxButton.OK, MessageBoxImage.Information);

    private void TogglePin_Click(object sender, RoutedEventArgs e) { if (ActiveEntry != null) TogglePin(ActiveEntry); }
    private void NextTab_Click(object sender, RoutedEventArgs e) => SelectRelative(1);
    private void PreviousTab_Click(object sender, RoutedEventArgs e) => SelectRelative(-1);

    private void Font_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FontSettingsDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        foreach (var entry in _tabs)
            entry.Tab.ApplyFont();
    }

    private async Task ExecuteCurrentAsync()
    {
        if (Current is { } tab && await EnsureTabConnectedAsync(tab, "ejecutar la consulta"))
            await tab.ExecuteAsync();
    }

    private async Task ExplainCurrentAsync()
    {
        if (Current is { } tab && await EnsureTabConnectedAsync(tab, "obtener el plan de ejecución"))
            await tab.ExplainAsync();
    }

    /// <summary>
    /// Una pestaña abierta sin conexión la pide en el momento de necesitarla (ejecutar, plan). Si se cancela,
    /// la pestaña lo deja escrito como error y devuelve false.
    /// </summary>
    private async Task<bool> EnsureTabConnectedAsync(QueryTab tab, string action)
    {
        if (!tab.IsOffline) return true;
        if (tab.IsRunning) return false;

        var target = await ConnectCoreAsync(emptyTab: false);
        if (target == null)
        {
            tab.ReportNotConnected(action);
            return false;
        }
        // Al conectar se restaura la sesión de esa conexión: si este archivo estaba en ella, ya quedó conectado.
        if (tab.IsOffline) await tab.ChangeConnectionAsync(target.Value.Profile, target.Value.Database);
        SaveSessions();
        // La sesión restaurada pudo dejar a la vista otra pestaña: se vuelve a la que pidió la conexión.
        if (_tabs.FirstOrDefault(t => t.Tab == tab) is { } entry) Select(entry);
        return !tab.IsOffline;
    }

    /// <summary>Una base de datos de una conexión abierta, como se muestra en el desplegable: "base (conexión)".</summary>
    private sealed record DbTarget(ConnectionProfile Profile, string Database)
    {
        public override string ToString() => $"{Database}  ({Profile.Name})";
    }

    private async void DatabaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCombo || Current is not { } tab || DatabaseCombo.SelectedItem is not DbTarget target) return;
        if (target.Profile == tab.Profile)
            await tab.ChangeDatabaseAsync(target.Database);
        else
            // Otra conexión: la pestaña (con su texto) pasa a ejecutarse contra ella.
            await tab.ChangeConnectionAsync(target.Profile, target.Database);
        SaveSessions();
        tab.FocusEditor();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Current?.SqlEditor.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Current?.SqlEditor.Redo();
    private void ToggleResultFilter_Click(object sender, RoutedEventArgs e) => Current?.ToggleResultFilter();
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow(this).ShowDialog();

    private void Shortcuts_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "Ctrl+E / F5\t\tEjecutar la selección o todo\n" +
            "Ctrl+L\t\t\tPlan de ejecución\n" +
            "Alt+Pausa\t\tCancelar la ejecución\n\n" +
            "Ctrl+N / Ctrl+O / Ctrl+S\tNueva consulta / Abrir / Guardar\n" +
            "Ctrl+Mayús+S\t\tGuardar todas las consultas con cambios\n" +
            "Ctrl+W\t\t\tCerrar la pestaña\n" +
            "Ctrl+Tab\t\tPestaña siguiente (con Mayús, anterior)\n\n" +
            "Ctrl+Z / Ctrl+Y\t\tDeshacer / Rehacer\n" +
            "Ctrl+F / Ctrl+H\t\tBuscar / Reemplazar\n" +
            "Ctrl+Mayús+F\t\tFormatear SQL\n" +
            "Ctrl+Espacio\t\tAutocompletar\n" +
            "Tab\t\t\tExpandir un fragmento (sel, upd, ij...)\n\n" +
            "Ctrl+Mayús+L\t\tFiltrar los resultados\n" +
            "Ctrl+B\t\t\tMostrar u ocultar el explorador\n" +
            "Alt+← / →\t\tDividir la vista: la pestaña actual a la izquierda o a la derecha, el resto al otro lado\n" +
            "Alt+↑ / ↓\t\tDividir la vista: la pestaña actual arriba o abajo, el resto al otro lado\n" +
            "Alt+Intro\t\tQuitar la división\n" +
            "Ctrl+R\t\t\tMinimizar o restaurar el panel de resultados\n" +
            "Ctrl+Mayús+R\t\tMaximizar o restaurar el panel de resultados",
            "Atajos de teclado", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------- Conexión ----------

    /// <summary>
    /// Conectar desde el menú o la barra. Si la pestaña actual está sin conexión, conectar es conectarla a ella
    /// (no se abre además una consulta vacía).
    /// </summary>
    /// <param name="emptyTab">Abrir una consulta vacía al conectar; false si a continuación se va a abrir un archivo.</param>
    private async Task ConnectAsync(bool emptyTab = true)
    {
        var offline = Current is { IsOffline: true, IsRunning: false } tab ? tab : null;
        var target = await ConnectCoreAsync(emptyTab && offline == null);
        if (target == null || offline == null) return;
        if (offline.IsOffline) await offline.ChangeConnectionAsync(target.Value.Profile, target.Value.Database);
        SaveSessions();
        if (_tabs.FirstOrDefault(t => t.Tab == offline) is { } entry) Select(entry);
    }

    /// <summary>Muestra el diálogo de conexión y abre la elegida. Devuelve null si se canceló.</summary>
    private async Task<(ConnectionProfile Profile, string? Database)?> ConnectCoreAsync(bool emptyTab)
    {
        var dialog = new ConnectionDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile == null) return null;

        // Si ya hay una conexión con el mismo nombre, se reutiliza su nodo.
        var profile = _databases.Keys.FirstOrDefault(p => p.Name == dialog.Profile.Name);
        if (profile == null)
        {
            profile = dialog.Profile;
            _databases[profile] = new();
            var root = MakeNode(profile.Name, new Node(NodeKind.Server, profile), expandable: true);
            root.ToolTip = profile.DefaultName;
            Explorer.Items.Add(root);
            await LoadChildrenAsync(root, (Node)root.Tag);
            root.IsExpanded = true;

            if (RestoreSession(profile, dialog.Profile.Database)) return (profile, dialog.Profile.Database);
        }
        else
        {
            profile.Password = dialog.Profile.Password;
            // Si se marcó o desmarcó como producción, se refleja en lo ya abierto.
            if (profile.IsProduction != dialog.Profile.IsProduction)
            {
                profile.IsProduction = dialog.Profile.IsProduction;
                var root = Explorer.Items.Cast<TreeViewItem>().FirstOrDefault(i => (i.Tag as Node)?.Profile == profile);
                if (root != null) root.Header = ServerHeader(profile);
                foreach (var entry in _tabs) StyleHeader(entry);
            }
        }

        if (emptyTab) AddTab(profile, dialog.Profile.Database);
        return (profile, dialog.Profile.Database);
    }

    /// <summary>Encabezado del nodo de una conexión; las de producción, en rojo y rotuladas.</summary>
    private static StackPanel ServerHeader(ConnectionProfile profile)
    {
        if (!profile.IsProduction)
            return ExplorerIcons.Header(ExplorerIcon.Server, profile.Name);

        var header = ExplorerIcons.Header(ExplorerIcon.Server, $"{profile.Name}  ·  PRODUCCIÓN");
        var text = (TextBlock)header.Children[1];
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.ProdText");
        text.FontWeight = FontWeights.SemiBold;
        return header;
    }

    /// <summary>La conexión del nodo seleccionado en el explorador; si no hay, la de la pestaña actual.</summary>
    private ConnectionProfile? SelectedProfile()
    {
        for (var item = Explorer.SelectedItem as TreeViewItem; item != null; item = item.Parent as TreeViewItem)
            if (item.Tag is Node node) return node.Profile;
        return Current?.Profile;
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile() is { } profile)
            Disconnect(profile);
    }

    /// <summary>Base seleccionada en el explorador (ella o cualquier objeto suyo) o, si no hay, la de la pestaña actual.</summary>
    private (ConnectionProfile Profile, string Database)? SelectedDatabase()
    {
        for (var item = Explorer.SelectedItem as TreeViewItem; item != null; item = item.Parent as TreeViewItem)
            if (item.Tag is Node { Database: { } database } node) return (node.Profile, database);
        return Current is { CurrentDatabase: { Length: > 0 } current } tab ? (tab.Profile, current) : null;
    }

    private void Diagram_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDatabase() is not { } target)
        {
            MessageBox.Show(this, "Selecciona una base de datos en el explorador (o abre una pestaña conectada a ella).",
                "Diagrama de la base de datos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new DiagramWindow(target.Profile, target.Database, Icon).Show();
    }

    private void ProcessMonitor_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedProfile();
        if (profile is not { Kind: DbKind.MySql })
        {
            MessageBox.Show(this, "Selecciona una conexión MySQL en el explorador (o abre una pestaña de ella).",
                "Monitor de procesos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new ProcessMonitorWindow(this, profile).Show();
    }

    /// <summary>Cierra las pestañas de la conexión y la quita del explorador. Su sesión se conserva.</summary>
    private void Disconnect(ConnectionProfile profile)
    {
        if (!_databases.ContainsKey(profile)) return;

        // Como al salir: la sesión se guarda con lo que hay abierto, y cerrar las pestañas no la vacía.
        SaveSessions();
        _suspendSessionSave = true;
        try
        {
            foreach (var entry in _tabs.Where(t => t.Tab.Profile == profile).ToList())
                if (!CloseTab(entry)) return;
        }
        finally
        {
            _suspendSessionSave = false;
        }

        var root = Explorer.Items.Cast<TreeViewItem>().FirstOrDefault(i => (i.Tag as Node)?.Profile == profile);
        if (root != null) Explorer.Items.Remove(root);
        _databases.Remove(profile);
        SshTunnels.Close(profile);
        UpdateChrome();
    }

    // ---------- Sesiones ----------

    /// <summary>Reabre los archivos que esta conexión tenía abiertos la última vez.</summary>
    private bool RestoreSession(ConnectionProfile profile, string? defaultDatabase)
    {
        bool restored = false;
        _suspendSessionSave = true;
        try
        {
            foreach (var saved in SessionStore.Load(profile.Name))
            {
                if (!File.Exists(saved.Path)) continue;
                // Ya está abierto (p. ej. se abrió sin conexión y ahora se conecta): no se abre otra vez.
                // Las pestañas que lo tenían sin conexión pasan a esta.
                var open = _tabs.Where(t => string.Equals(t.Tab.FilePath, saved.Path, StringComparison.OrdinalIgnoreCase)).ToList();
                if (open.Count > 0)
                {
                    foreach (var entry in open.Where(t => t.Tab.IsOffline))
                        entry.Tab.ChangeConnectionAsync(profile, saved.Database ?? defaultDatabase).Watch("Conectar la pestaña");
                    restored = true;
                    continue;
                }
                try
                {
                    // Cada archivo vuelve al grupo de la vista dividida en el que estaba.
                    if (saved.Group == 1 && _main.Groups.Count == 1)
                    {
                        CreateGroup(_main);
                        LayoutGroups(_main);
                    }
                    _activeGroup = _main.Groups[Math.Clamp(saved.Group, 0, _main.Groups.Count - 1)];
                    var tab = AddTab(profile, saved.Database ?? defaultDatabase);
                    try
                    {
                        tab.LoadFile(saved.Path);
                    }
                    catch
                    {
                        // Un archivo ilegible no impide restaurar el resto, y no deja una pestaña vacía de recuerdo.
                        CloseTab(_tabs[^1]);
                        continue;
                    }
                    _tabs[^1].Pinned = saved.Pinned;
                    restored = true;
                }
                catch
                {
                    // Cualquier otro fallo con este archivo: se sigue con los demás.
                }
            }
        }
        finally
        {
            _suspendSessionSave = false;
        }

        if (restored)
        {
            _activeGroup = _main.Groups[0];
            RemoveEmptyGroups();
            RefreshTabs();
            ActiveEntry?.Tab.FocusEditor();
        }
        return restored;
    }

    /// <summary>Guarda, por cada conexión abierta, los archivos que tiene en pestañas.</summary>
    private void SaveSessions()
    {
        if (_suspendSessionSave) return;
        var sessions = _databases.Keys.ToDictionary(
            profile => profile.Name,
            profile => AllGroups.SelectMany(VisualOrder)
                .Where(t => t.Tab.Profile == profile && t.Tab.FilePath != null)
                .DistinctBy(t => t.Tab.Script)   // las vistas duplicadas no se restauran: una pestaña por archivo
                .Select(t => new SessionTab
                {
                    Path = t.Tab.FilePath!,
                    Pinned = t.Pinned,
                    Database = t.Tab.CurrentDatabase,
                    // Las pestañas de ventanas flotantes vuelven al primer grupo de la principal al restaurar.
                    Group = Math.Max(0, _main.Groups.IndexOf(t.Group)),
                })
                .ToList());
        SessionStore.Save(sessions);
    }

    // ---------- Pestañas ----------

    private void NewQueryFromCurrent()
    {
        // Una pestaña sin conexión no sirve de referencia para la nueva.
        var current = Current is { IsOffline: false } connected ? connected : null;
        if (current != null)
            AddTab(current.Profile, current.CurrentDatabase);
        else if (_databases.Count > 0)
            AddTab(_databases.Keys.First(), null);
        else if (Current != null)
            ConnectCoreAsync(emptyTab: true).Watch("Conectar");   // consulta nueva, sin tocar la pestaña sin conexión
        else
            ConnectAsync().Watch("Conectar");
    }

    private static Button HeaderButton(string content, string toolTip) => new()
    {
        Content = content,
        FontSize = 9,
        Padding = new Thickness(3, 0, 3, 1),
        Margin = new Thickness(6, 0, 0, 0),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Focusable = false,
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = toolTip,
    };

    /// <param name="script">Script ya abierto del que la pestaña será otra vista; null para una consulta nueva.</param>
    private QueryTab AddTab(ConnectionProfile profile, string? database, ScriptFile? script = null)
    {
        var tab = script != null ? new QueryTab(profile, database, script)
            : new QueryTab(profile, database, $"Consulta{++_queryCounter}.sql");
        tab.StateChanged += Tab_StateChanged;

        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var pin = HeaderButton("📌", "Anclar pestaña");
        // La chincheta en color (la misma del menú): viva en las pestañas ancladas, atenuada en las demás.
        pin.Content = new Viewbox { Width = 13, Height = 13, Child = ExplorerIcons.Create(ExplorerIcon.Pin) };
        var close = HeaderButton("✕", "Cerrar (Ctrl+W)");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(title);
        panel.Children.Add(pin);
        panel.Children.Add(close);

        var header = new Border
        {
            Child = panel,
            Padding = new Thickness(9, 3, 4, 4),
            Margin = new Thickness(0, 0, 1, 0),
            BorderThickness = new Thickness(0, 2, 0, 0),
            AllowDrop = true,
        };
        var entry = new TabEntry { Tab = tab, Header = header, Title = title, PinButton = pin, Group = _activeGroup };

        pin.Click += (_, _) => TogglePin(entry);
        close.Click += (_, _) => CloseTab(entry);

        header.MouseLeftButtonDown += (_, e) =>
        {
            Select(entry);
            _dragCandidate = entry;
            _dragStart = e.GetPosition(this);
        };
        header.MouseLeftButtonUp += (_, _) => _dragCandidate = null;
        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            CloseTab(entry);
        };
        header.MouseMove += (_, e) =>
        {
            if (_dragCandidate != entry || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(this) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragCandidate = null;
            header.Opacity = 0.5;
            try
            {
                DragDrop.DoDragDrop(header, new DataObject(typeof(TabEntry), entry), DragDropEffects.Move);
            }
            finally
            {
                header.Opacity = 1;
            }
        };
        // La pestaña arrastrada se recoloca en vivo: al pasar de la mitad de otra, intercambian sitio.
        header.DragOver += (_, e) =>
        {
            e.Handled = true;
            // Solo dentro de su misma franja (ancladas o normales).
            if (e.Data.GetData(typeof(TabEntry)) is not TabEntry source || source.Pinned != entry.Pinned)
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            e.Effects = DragDropEffects.Move;
            if (source == entry) return;

            bool pastMiddle = e.GetPosition(header).X > header.ActualWidth / 2;
            if (source.Group != entry.Group)
            {
                // Llega desde el otro grupo de la vista dividida: entra antes o después de esta pestaña.
                MoveToGroup(source, entry.Group);
                _tabs.Remove(source);
                _tabs.Insert(_tabs.IndexOf(entry) + (pastMiddle ? 1 : 0), source);
                RemoveEmptyGroups();
                RefreshTabs(animate: true);
                SaveSessions();
                return;
            }

            int from = _tabs.IndexOf(source), to = _tabs.IndexOf(entry);
            if (from < to ? !pastMiddle : pastMiddle) return;

            _tabs.Remove(source);
            _tabs.Insert(to, source);
            RebuildStrips(animate: true);
        };
        header.Drop += (_, e) => e.Handled = true;

        var menu = new ContextMenu();
        void AddMenu(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        // Cierra varias pestañas de este grupo; se detiene si en alguna se cancela el "¿guardar los cambios?".
        // Las ancladas nunca se cierran en bloque: para eso se anclan.
        void CloseMany(Func<TabEntry, bool> which)
        {
            foreach (var other in VisualOrder(entry.Group).Where(t => !t.Pinned && which(t)).ToList())
                if (!CloseTab(other)) break;
        }
        // Pestañas a la derecha de esta, en el orden en que se ven.
        List<TabEntry> ToTheRight() => VisualOrder(entry.Group).SkipWhile(t => t != entry).Skip(1).ToList();
        // Sin cambios pendientes: cerrarla no pierde nada.
        static bool Unmodified(TabEntry t) => !(t.Tab.IsDirty && t.Tab.HasText);
        // Su archivo ya no existe en disco (se borró o se movió desde fuera).
        static bool FileDeleted(TabEntry t) => t.Tab.FilePath is { Length: > 0 } path && !File.Exists(path);

        AddMenu("Anclar o desanclar", () => TogglePin(entry));
        AddMenu("Cerrar", () => CloseTab(entry));
        ((MenuItem)menu.Items[^1]).InputGestureText = "Ctrl+W";
        AddMenu("Cerrar las demás (excepto ancladas)", () => CloseMany(t => t != entry));
        var closeOthers = (MenuItem)menu.Items[^1];
        AddMenu("Cerrar todas (excepto ancladas)", () => CloseMany(_ => true));
        var closeAll = (MenuItem)menu.Items[^1];
        AddMenu("Cerrar las de la derecha", () => { var right = ToTheRight(); CloseMany(right.Contains); });
        var closeRight = (MenuItem)menu.Items[^1];
        AddMenu("Cerrar las que no tienen cambios", () => CloseMany(Unmodified));
        var closeUnmodified = (MenuItem)menu.Items[^1];
        AddMenu("Cerrar las de la derecha que no tienen cambios", () => { var right = ToTheRight(); CloseMany(t => right.Contains(t) && Unmodified(t)); });
        var closeUnmodifiedRight = (MenuItem)menu.Items[^1];
        AddMenu("Cerrar las de archivos borrados", () => CloseMany(FileDeleted));
        var closeDeleted = (MenuItem)menu.Items[^1];
        closeDeleted.ToolTip = "Pestañas cuyo archivo ya no existe en disco";
        menu.Items.Add(new Separator());
        AddMenu("Dividir: izquierda / derecha", () => { Select(entry); Split(sideBySide: true); });
        var splitSideItem = (MenuItem)menu.Items[^1];
        splitSideItem.InputGestureText = "Alt+← / Alt+→";
        AddMenu("Dividir: arriba / abajo", () => { Select(entry); Split(sideBySide: false); });
        var splitStackItem = (MenuItem)menu.Items[^1];
        splitStackItem.InputGestureText = "Alt+↑ / Alt+↓";
        AddMenu("Duplicar vista a la derecha", () => DuplicateView(entry, sideBySide: true));
        ((MenuItem)menu.Items[^1]).ToolTip = "Abre el mismo script en otra pestaña al lado: lo que se escribe en una aparece en la otra";
        AddMenu("Duplicar vista abajo", () => DuplicateView(entry, sideBySide: false));
        ((MenuItem)menu.Items[^1]).ToolTip = "Abre el mismo script en otra pestaña debajo: lo que se escribe en una aparece en la otra";
        AddMenu("Duplicar vista en una ventana nueva", () => DuplicateViewToWindow(entry));
        ((MenuItem)menu.Items[^1]).ToolTip = "Abre el mismo script en otra ventana (para otro monitor): lo que se escribe en una aparece en la otra";
        AddMenu("Mover al otro grupo", () => { Select(entry); MoveActiveToOtherGroup(); });
        AddMenu("Quitar la división", () => { Select(entry); Unsplit(); });
        menu.Items.Add(new Separator());
        AddMenu("Mover a una ventana nueva", () => FloatTab(entry));
        AddMenu("Devolver a la ventana principal", () => DockToMain(entry));
        var dockItem = (MenuItem)menu.Items[^1];
        // "Devolver..." solo tiene sentido en una ventana flotante; las opciones de grupo, con la vista dividida.
        var otherGroupItem = menu.Items.OfType<MenuItem>().First(i => (string)i.Header == "Mover al otro grupo");
        var unsplitItem = menu.Items.OfType<MenuItem>().First(i => (string)i.Header == "Quitar la división");
        menu.Opened += (_, _) =>
        {
            dockItem.Visibility = entry.Group.Area == _main ? Visibility.Collapsed : Visibility.Visible;
            bool split = entry.Group.Area.Groups.Count > 1;
            otherGroupItem.Visibility = unsplitItem.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            splitSideItem.IsEnabled = CanSplit(entry.Group.Area, sideBySide: true);
            splitStackItem.IsEnabled = CanSplit(entry.Group.Area, sideBySide: false);

            // Cada "cerrar varias" solo está disponible si hay alguna pestaña a la que afecte.
            var closable = VisualOrder(entry.Group).Where(t => !t.Pinned).ToList();
            var right = ToTheRight();
            closeOthers.IsEnabled = closable.Any(t => t != entry);
            closeAll.IsEnabled = closable.Count > 0;
            closeRight.IsEnabled = closable.Any(right.Contains);
            closeUnmodified.IsEnabled = closable.Any(Unmodified);
            closeUnmodifiedRight.IsEnabled = closable.Any(t => right.Contains(t) && Unmodified(t));
            closeDeleted.IsEnabled = closable.Any(FileDeleted);
        };
        MenuIcons.Apply(menu);
        header.ContextMenu = menu;

        tab.Visibility = Visibility.Collapsed;
        entry.Group.Host.Children.Add(tab);
        _tabs.Add(entry);
        UpdateHeader(entry);
        RebuildStrips();
        Select(entry);
        return tab;
    }

    private void RebuildStrips(bool animate = false)
    {
        // Posición de cada encabezado (dentro de su ventana) antes del cambio, para deslizarlo hasta la nueva.
        var before = new Dictionary<TabEntry, (Window Window, Point Position)>();
        if (animate)
        {
            foreach (var entry in _tabs.Where(t => t.Header.IsVisible))
                if (GetWindow(entry.Header) is { } window)
                    before[entry] = (window, entry.Header.TranslatePoint(new Point(), window));
        }

        foreach (var group in AllGroups)
        {
            group.PinnedStrip.Children.Clear();
            group.NormalStrip.Children.Clear();
        }
        foreach (var entry in _tabs)
        {
            entry.Header.RenderTransform = null;
            // Si venía de un grupo ya eliminado, sigue colgando de su franja antigua.
            (entry.Header.Parent as Panel)?.Children.Remove(entry.Header);
            (entry.Pinned ? entry.Group.PinnedStrip : entry.Group.NormalStrip).Children.Add(entry.Header);
        }
        foreach (var group in AllGroups)
            group.PinnedRow.Visibility = group.PinnedStrip.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (before.Count == 0) return;
        UpdateLayout();
        foreach (var area in _areas) area.Window?.UpdateLayout();
        var duration = TimeSpan.FromMilliseconds(180);
        foreach (var (entry, (oldWindow, oldPosition)) in before)
        {
            // Si cambió de ventana no hay trayecto que animar.
            if (GetWindow(entry.Header) != oldWindow) continue;
            var delta = oldPosition - entry.Header.TranslatePoint(new Point(), oldWindow);
            if (Math.Abs(delta.X) < 0.5 && Math.Abs(delta.Y) < 0.5) continue;

            var transform = new TranslateTransform(delta.X, delta.Y);
            entry.Header.RenderTransform = transform;
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
        }
    }

    private void TogglePin(TabEntry entry)
    {
        entry.Pinned = !entry.Pinned;
        // Al cambiar de grupo pasa al final de su nueva franja.
        _tabs.Remove(entry);
        _tabs.Add(entry);
        StyleHeader(entry);
        RebuildStrips(animate: true);
        CloseAllMenuItem.IsEnabled = _tabs.Any(t => !t.Pinned);
        SaveSessions();
    }

    private void Select(TabEntry entry)
    {
        entry.Group.Selected = entry;
        entry.Group.Area.ActiveGroup = entry.Group;
        _activeGroup = entry.Group;
        ApplySelection();
        Dispatcher.BeginInvoke(entry.Tab.FocusEditor, DispatcherPriority.Input);
    }

    /// <summary>Muestra la pestaña seleccionada de cada grupo y actualiza estilos, barra de estado y título.</summary>
    private void ApplySelection()
    {
        foreach (var entry in _tabs)
        {
            entry.Tab.Visibility = entry.Group.Selected == entry ? Visibility.Visible : Visibility.Collapsed;
            StyleHeader(entry);
        }
        UpdateChrome();
    }

    private void RefreshTabs(bool animate = false)
    {
        RebuildStrips(animate);
        ApplySelection();
    }

    private void SelectRelative(int step)
    {
        var order = VisualOrder(_activeGroup);
        if (order.Count < 2 || ActiveEntry == null) return;
        int index = (order.IndexOf(ActiveEntry) + step + order.Count) % order.Count;
        Select(order[index]);
    }

    // ---------- Vista dividida ----------

    /// <summary>Crea un grupo de pestañas y lo añade a la zona.</summary>
    private TabGroup CreateGroup(GroupArea area)
    {
        var pinnedStrip = new WrapPanel();
        var normalStrip = new WrapPanel { MinHeight = 27 };
        var pinnedRow = new Border
        {
            Child = pinnedStrip,
            Visibility = Visibility.Collapsed,
            AllowDrop = true,
        };
        pinnedRow.SetResourceReference(Border.BackgroundProperty, "Brush.TabStripPinned");
        var normalRow = new Border
        {
            Child = normalStrip,
            BorderThickness = new Thickness(0, 0, 0, 1),
            AllowDrop = true,
        };
        normalRow.SetResourceReference(Border.BackgroundProperty, "Brush.TabStrip");
        normalRow.SetResourceReference(Border.BorderBrushProperty, "Brush.TabStripBorder");
        var host = new Grid();

        var root = new DockPanel();
        DockPanel.SetDock(pinnedRow, Dock.Top);
        DockPanel.SetDock(normalRow, Dock.Top);
        root.Children.Add(pinnedRow);
        root.Children.Add(normalRow);
        root.Children.Add(host);

        var group = new TabGroup { Root = root, PinnedRow = pinnedRow, PinnedStrip = pinnedStrip, NormalStrip = normalStrip, Host = host, Area = area };
        area.Groups.Add(group);

        // El grupo en el que se trabaja es el activo: recibe las pestañas nuevas y los atajos.
        root.PreviewMouseDown += (_, _) => Activate(group);
        root.GotKeyboardFocus += (_, _) => Activate(group);

        // Soltar una pestaña del otro grupo sobre el hueco libre de las franjas la trae al final de este.
        DragEventHandler dragOver = (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(typeof(TabEntry)) is not TabEntry source)
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            e.Effects = DragDropEffects.Move;
            if (source.Group == group) return;

            MoveToGroup(source, group);
            _tabs.Remove(source);
            _tabs.Add(source);
            RemoveEmptyGroups();
            RefreshTabs(animate: true);
            SaveSessions();
        };
        pinnedRow.DragOver += dragOver;
        normalRow.DragOver += dragOver;
        return group;
    }

    private void Activate(TabGroup group)
    {
        if (!AllGroups.Contains(group)) return;
        group.Area.ActiveGroup = group;
        if (_activeGroup == group) return;
        _activeGroup = group;
        ApplySelection();
    }

    /// <summary>Al pasar a una ventana, se activa el último grupo usado en ella.</summary>
    private void ActivateArea(GroupArea area)
    {
        if (_activeGroup.Area == area || area.Groups.Count == 0) return;
        Activate(area.ActiveGroup is { } last && area.Groups.Contains(last) ? last : area.Groups[0]);
    }

    /// <summary>Coloca los grupos de una zona: uno solo, o dos separados por un divisor arrastrable.</summary>
    private void LayoutGroups(GroupArea area)
    {
        var grid = area.Grid;
        var groups = area.Groups;
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        foreach (var group in groups)
        {
            Grid.SetRow(group.Root, 0);
            Grid.SetColumn(group.Root, 0);
            grid.Children.Add(group.Root);
        }
        if (groups.Count < 2) return;

        var splitter = new GridSplitter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        };
        splitter.SetResourceReference(BackgroundProperty, "Brush.Splitter");
        if (area.SideBySide)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 120 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 120 });
            splitter.Width = 5;
            splitter.ResizeDirection = GridResizeDirection.Columns;
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(groups[1].Root, 2);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { MinHeight = 100 });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { MinHeight = 100 });
            splitter.Height = 5;
            splitter.ResizeDirection = GridResizeDirection.Rows;
            Grid.SetRow(splitter, 1);
            Grid.SetRow(groups[1].Root, 2);
        }
        grid.Children.Add(splitter);
    }

    /// <summary>Cambia la pestaña de grupo (sin tocar su posición en la lista ni refrescar la pantalla).</summary>
    private void MoveToGroup(TabEntry entry, TabGroup target)
    {
        var source = entry.Group;
        if (source == target) return;

        source.Host.Children.Remove(entry.Tab);
        target.Host.Children.Add(entry.Tab);
        entry.Group = target;
        if (source.Selected == entry)
            source.Selected = VisualOrder(source).FirstOrDefault();
        target.Selected = entry;
        target.Area.ActiveGroup = target;
        _activeGroup = target;
    }

    /// <summary>
    /// Quita los grupos que se quedaron sin pestañas: deshace la división de su zona y cierra las ventanas
    /// flotantes vacías. La ventana principal siempre conserva un grupo, aunque esté vacío.
    /// </summary>
    private void RemoveEmptyGroups()
    {
        foreach (var area in _areas.ToList())
        {
            var empty = area.Groups.Where(g => !_tabs.Any(t => t.Group == g)).ToList();
            if (area == _main && empty.Count == area.Groups.Count && empty.Count > 0)
                empty.RemoveAt(0);
            if (empty.Count == 0) continue;

            foreach (var group in empty) area.Groups.Remove(group);
            if (area.Groups.Count == 0)
            {
                _areas.Remove(area);
                area.Window?.Close();
            }
            else
            {
                LayoutGroups(area);
            }
        }

        if (!AllGroups.Contains(_activeGroup))
            _activeGroup = _activeGroup.Area.Groups.FirstOrDefault() ?? _main.Groups[0];
    }

    /// <summary>
    /// ¿Tiene efecto dividir la zona en esa orientación? No, si ya está dividida así; sí, si no está dividida
    /// o lo está en la otra (entonces cambia la orientación).
    /// </summary>
    private static bool CanSplit(GroupArea area, bool sideBySide) => !(area.Groups.Count > 1 && area.SideBySide == sideBySide);

    /// <summary>
    /// Divide en dos grupos la zona de la pestaña actual (botones y menús), o cambia la orientación si ya está dividida.
    /// </summary>
    private void Split(bool sideBySide)
    {
        SplitCore(sideBySide);
        // Botones y menús de dividir dependen de cómo quedó la zona.
        UpdateFloatingWindows();
        UpdateGroupMenu();
    }

    /// <summary>
    /// Divide la zona llevando la pestaña actual al lado indicado y todas las demás al contrario (Alt + flecha).
    /// Si ya estaba dividida, se reordena igual: la actual sola a ese lado. Con una única pestaña, al otro lado
    /// se abre una consulta nueva para que no quede vacío.
    /// </summary>
    /// <param name="activeSecond">true: derecha o abajo; false: izquierda o arriba.</param>
    private void SplitToward(bool sideBySide, bool activeSecond)
    {
        var active = ActiveEntry;
        if (active == null) return;

        var area = active.Group.Area;
        area.SideBySide = sideBySide;
        if (area == _main)
        {
            AppSettings.Current.SplitSideBySide = sideBySide;
            try { AppSettings.Current.Save(); } catch { }
        }
        if (area.Groups.Count < 2) CreateGroup(area);

        // El primer grupo es el de la izquierda (o arriba); el segundo, el de la derecha (o abajo).
        var target = area.Groups[activeSecond ? 1 : 0];
        var other = area.Groups[activeSecond ? 0 : 1];
        foreach (var entry in _tabs.Where(t => t.Group.Area == area && t != active && t.Group != other).ToList())
            MoveToGroup(entry, other);
        MoveToGroup(active, target);
        if (!_tabs.Any(t => t.Group == other))
        {
            _activeGroup = other;
            AddTab(active.Tab.Profile, active.Tab.CurrentDatabase);
        }

        LayoutGroups(area);
        RefreshTabs();
        SaveSessions();
        Select(active);   // el foco sigue en la pestaña que se movió
        UpdateFloatingWindows();
        UpdateGroupMenu();
    }

    /// <summary>
    /// Abre otra vista del mismo script en el otro grupo de su zona (dividiéndola si hace falta), para ver dos
    /// partes del script a la vez. Comparten texto, archivo y deshacer; cada una tiene su cursor, sus resultados
    /// y su conexión. La vista nueva es una pestaña más: se puede mover de grupo o a otra ventana.
    /// </summary>
    private void DuplicateView(TabEntry source, bool sideBySide)
    {
        Select(source);
        var area = source.Group.Area;
        area.SideBySide = sideBySide;
        if (area == _main)
        {
            AppSettings.Current.SplitSideBySide = sideBySide;
            try { AppSettings.Current.Save(); } catch { }
        }
        if (area.Groups.Count < 2) CreateGroup(area);

        // La pestaña nueva nace en el grupo activo: el otro de la zona.
        _activeGroup = area.Groups.First(g => g != source.Group);
        var tab = AddTab(source.Tab.Profile, source.Tab.CurrentDatabase, source.Tab.Script);
        var copy = _tabs.First(t => t.Tab == tab);
        tab.ShowSamePlaceAs(source.Tab);

        LayoutGroups(area);
        RefreshTabs();
        UpdateHeader(source);   // ahora lleva ":1" para distinguirse de la vista nueva
        SaveSessions();
        Select(copy);
        UpdateFloatingWindows();
        UpdateGroupMenu();
    }

    /// <summary>Abre otra vista del mismo script directamente en una ventana propia, sin dividir la zona actual.</summary>
    private void DuplicateViewToWindow(TabEntry source)
    {
        Select(source);
        _activeGroup = source.Group;
        var tab = AddTab(source.Tab.Profile, source.Tab.CurrentDatabase, source.Tab.Script);
        var copy = _tabs.First(t => t.Tab == tab);
        tab.ShowSamePlaceAs(source.Tab);
        FloatTab(copy);
        // Al irse la copia, el grupo de origen vuelve a mostrar la pestaña original.
        source.Group.Selected = source;
        RefreshTabs();
        UpdateHeader(source);   // ahora lleva ":1" para distinguirse de la vista nueva
    }

    private void SplitCore(bool sideBySide)
    {
        var area = _activeGroup.Area;
        area.SideBySide = sideBySide;
        if (area == _main)
        {
            AppSettings.Current.SplitSideBySide = sideBySide;
            try { AppSettings.Current.Save(); } catch { }
        }

        if (area.Groups.Count == 2)
        {
            LayoutGroups(area);
            return;
        }

        // Sin flechas no hay lado elegido: por defecto, la pestaña actual va a la derecha (en columnas)
        // o arriba (en filas), y las demás al lado contrario.
        SplitToward(sideBySide, activeSecond: sideBySide);
    }

    private void MoveActiveToOtherGroup()
    {
        var entry = ActiveEntry;
        if (entry == null || entry.Group.Area.Groups.Count < 2) return;
        MoveEntry(entry, entry.Group.Area.Groups.First(g => g != entry.Group));
    }

    /// <summary>Pasa la pestaña al final de otro grupo (de su ventana o de otra) y refresca todo.</summary>
    private void MoveEntry(TabEntry entry, TabGroup target)
    {
        MoveToGroup(entry, target);
        _tabs.Remove(entry);
        _tabs.Add(entry);
        RemoveEmptyGroups();
        RefreshTabs();
        SaveSessions();
        entry.Tab.FocusEditor();
    }

    /// <summary>Quita la división de la zona de la pestaña actual: sus pestañas pasan al primer grupo.</summary>
    private void Unsplit()
    {
        var area = _activeGroup.Area;
        if (area.Groups.Count < 2) return;

        var active = ActiveEntry;
        var first = area.Groups[0];
        foreach (var entry in _tabs.Where(t => t.Group.Area == area && t.Group != first).ToList())
            MoveToGroup(entry, first);
        if (active != null) first.Selected = active;
        RemoveEmptyGroups();
        RefreshTabs();
        SaveSessions();
        active?.Tab.FocusEditor();
    }

    // ---------- Ventanas flotantes ----------

    /// <summary>
    /// Saca la pestaña a una ventana propia (para otro monitor). La ventana tiene sus botones de minimizar,
    /// maximizar y cerrar, admite vista dividida y los mismos atajos; al cerrarla, sus pestañas vuelven a la principal.
    /// </summary>
    private void FloatTab(TabEntry entry)
    {
        var area = CreateFloatingArea();
        _areas.Add(area);
        var group = CreateGroup(area);
        LayoutGroups(area);
        area.Window!.Show();
        MoveEntry(entry, group);
    }

    /// <summary>Devuelve la pestaña a la ventana principal.</summary>
    private void DockToMain(TabEntry entry)
    {
        if (entry.Group.Area == _main) return;
        MoveEntry(entry, _main.ActiveGroup is { } last && _main.Groups.Contains(last) ? last : _main.Groups[0]);
        Activate();
    }

    private GroupArea CreateFloatingArea()
    {
        var grid = new Grid();
        var statusBar = new StatusBarView();   // la misma barra de estado que la ventana principal

        var window = new Window
        {
            Title = App.Name,
            Icon = Icon,
            Width = 1100,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var area = new GroupArea { Grid = grid, Window = window, Status = statusBar };

        // Barra propia con lo esencial: los botones actúan sobre la pestaña de esta ventana.
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3, 4, 3) };
        void AddButton(ExplorerIcon icon, string text, string toolTip, Action action)
        {
            // Mismo icono y texto que la acción equivalente de la ventana principal.
            var button = new Button { Content = ExplorerIcons.Header(icon, text), ToolTip = toolTip, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
            // El tema no atenúa los iconos de color de un botón deshabilitado.
            button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : 0.4;
            button.Click += (_, _) =>
            {
                ActivateArea(area);
                action();
                UpdateFloatingWindows();   // lo que hizo el botón puede cambiar qué botones tienen sentido
            };
            bar.Children.Add(button);
        }
        // Orden: ejecutar, cancelar y plan (el mismo que en la ventana principal).
        AddButton(ExplorerIcon.Execute, "Ejecutar", "Ejecutar (Ctrl+E o F5)", () => ExecuteCurrentAsync().Watch("Ejecutar la consulta"));
        area.ExecuteButton = (Button)bar.Children[^1];
        AddButton(ExplorerIcon.Cancel, "Cancelar", "Cancelar la ejecución (Alt+Pausa)", () => Current?.Cancel());
        area.CancelButton = (Button)bar.Children[^1];
        area.CancelButton.IsEnabled = false;
        AddButton(ExplorerIcon.Plan, "Plan", "Plan de ejecución (Ctrl+L)", () => ExplainCurrentAsync().Watch("Plan de ejecución"));
        area.PlanButton = (Button)bar.Children[^1];
        AddButton(ExplorerIcon.SplitSide, "Izquierda / derecha", "Dividir esta ventana en columnas: la pestaña actual va a la derecha (con Alt+← o Alt+→ eliges el lado)", () => Split(sideBySide: true));
        area.SplitSideButton = (Button)bar.Children[^1];
        AddButton(ExplorerIcon.SplitStack, "Arriba / abajo", "Dividir esta ventana en filas: la pestaña actual va arriba (con Alt+↑ o Alt+↓ eliges el lado)", () => Split(sideBySide: false));
        area.SplitStackButton = (Button)bar.Children[^1];
        AddButton(ExplorerIcon.Unsplit, "Quitar división", "Volver a un solo grupo de pestañas en esta ventana (Alt+Intro)", Unsplit);
        area.UnsplitButton = (Button)bar.Children[^1];
        area.UnsplitButton.IsEnabled = false;   // una ventana nueva empieza sin dividir
        AddButton(ExplorerIcon.DockBack, "Devolver a la principal", "Devuelve la pestaña actual a la ventana principal",
            () => { if (ActiveEntry is { } entry) DockToMain(entry); });

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(statusBar);
        root.Children.Add(grid);
        window.Content = root;

        window.PreviewKeyDown += Window_PreviewKeyDown;   // los mismos atajos que en la principal
        window.Activated += (_, _) => ActivateArea(area);
        window.Closing += (_, _) =>
        {
            // Cerrar la ventana (X) no cierra las pestañas: vuelven a la principal.
            if (!_areas.Remove(area)) return;   // ya se había vaciado y retirado
            var target = _main.Groups[0];
            foreach (var entry in _tabs.Where(t => t.Group.Area == area).ToList())
            {
                MoveToGroup(entry, target);
                _tabs.Remove(entry);
                _tabs.Add(entry);
            }
            area.Groups.Clear();
            if (_activeGroup.Area == area) _activeGroup = target;
            RefreshTabs();
            SaveSessions();
        };
        return area;
    }

    private void Float_Click(object sender, RoutedEventArgs e) { if (ActiveEntry is { } entry) FloatTab(entry); }
    private void Dock_Click(object sender, RoutedEventArgs e) { if (ActiveEntry is { } entry) DockToMain(entry); }

    private void SplitSideBySide_Click(object sender, RoutedEventArgs e) => Split(sideBySide: true);
    private void SplitStacked_Click(object sender, RoutedEventArgs e) => Split(sideBySide: false);
    /// <summary>"Mover al otro grupo" y "Quitar la división" solo valen con la vista dividida.</summary>
    private void UpdateGroupMenu()
    {
        var area = ActiveEntry?.Group.Area ?? _main;
        bool split = area.Groups.Count > 1;
        MoveGroupMenuItem.IsEnabled = split && ActiveEntry != null;
        UnsplitMenuItem.IsEnabled = split;
        // La orientación que ya está puesta no se puede volver a elegir; la otra sirve para cambiarla.
        SplitSideMenuItem.IsEnabled = CanSplit(area, sideBySide: true);
        SplitStackMenuItem.IsEnabled = CanSplit(area, sideBySide: false);
    }

    // Al abrir el menú, por si la división cambió sin pasar por una actualización general.
    private void WindowMenu_SubmenuOpened(object sender, RoutedEventArgs e) => UpdateGroupMenu();

    private void MoveToOtherGroup_Click(object sender, RoutedEventArgs e) => MoveActiveToOtherGroup();
    private void Unsplit_Click(object sender, RoutedEventArgs e) => Unsplit();

    private void StyleHeader(TabEntry entry)
    {
        bool selected = entry == entry.Group.Selected;
        if (selected)
            entry.Header.SetResourceReference(Border.BackgroundProperty, "Brush.TabSelected");
        else
            entry.Header.Background = Brushes.Transparent;

        // Línea superior: roja siempre en producción; si no, azul en el grupo activo y gris en el otro grupo.
        if (entry.Tab.Profile.IsProduction)
            entry.Header.SetResourceReference(Border.BorderBrushProperty, "Brush.ProdAccent");
        else if (selected)
            entry.Header.SetResourceReference(Border.BorderBrushProperty,
                entry.Group == _activeGroup ? "Brush.TabAccent" : "Brush.TabInactiveAccent");
        else
            entry.Header.BorderBrush = Brushes.Transparent;
        entry.Title.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        entry.PinButton.Opacity = entry.Pinned ? 1 : 0.35;
        entry.PinButton.ToolTip = entry.Pinned ? "Desanclar pestaña" : "Anclar pestaña";
    }

    private bool CloseTab(TabEntry entry)
    {
        var tab = entry.Tab;
        // Si quedan otras vistas del mismo script, cerrar esta no pierde nada: no se pregunta.
        if (tab.IsDirty && tab.HasText && tab.IsLastView)
        {
            Select(entry);
            var answer = MessageBox.Show(this, $"¿Guardar los cambios de {tab.Title}?", App.Name,
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes && !Save(tab, saveAs: false)) return false;
        }

        // Al cerrar la pestaña visible de su grupo se pasa a la vecina.
        var group = entry.Group;
        if (group.Selected == entry)
        {
            var order = VisualOrder(group);
            int index = order.IndexOf(entry);
            order.RemoveAt(index);
            group.Selected = order.Count > 0 ? order[Math.Min(index, order.Count - 1)] : null;
        }

        tab.StateChanged -= Tab_StateChanged;
        tab.Close();
        _tabs.Remove(entry);
        group.Host.Children.Remove(tab);
        RemoveEmptyGroups();
        RefreshTabs();
        if (ActiveEntry != null)
            Dispatcher.BeginInvoke(ActiveEntry.Tab.FocusEditor, DispatcherPriority.Input);
        SaveSessions();
        return true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // La sesión se guarda con lo que hay abierto ahora; cerrar las pestañas al salir no la vacía.
        SaveSessions();
        _suspendSessionSave = true;
        foreach (var entry in _tabs.ToList())
        {
            if (CloseTab(entry)) continue;
            e.Cancel = true;
            _suspendSessionSave = false;
            return;
        }
    }

    private void Tab_StateChanged(QueryTab tab)
    {
        var entry = _tabs.FirstOrDefault(t => t.Tab == tab);
        if (entry != null)
        {
            UpdateHeader(entry);
            StyleHeader(entry);   // la pestaña puede haber cambiado a (o desde) una conexión de producción
        }
        // Siempre todo: la pestaña puede no ser la actual y aun así estar a la vista (en la ventana principal
        // mientras el foco está en una flotante, o al revés), y su barra de estado debe reflejar el cambio.
        UpdateChrome();
    }

    /// <summary>Título y línea de estado de cada ventana flotante, según su pestaña visible.</summary>
    private void UpdateFloatingWindows()
    {
        foreach (var area in _areas)
        {
            if (area.Window == null || area.Status == null) continue;
            var tab = VisibleTab(area);
            area.Window.Title = tab != null ? $"{tab.Title} - {TargetLabel(tab)} - {App.Name}" : App.Name;
            area.Status.Update(tab, "Listo");
            if (area.CancelButton != null) area.CancelButton.IsEnabled = tab is { IsRunning: true };
            if (area.ExecuteButton != null) area.ExecuteButton.IsEnabled = tab is { IsRunning: false };
            if (area.PlanButton != null) area.PlanButton.IsEnabled = tab is { IsRunning: false };
            if (area.UnsplitButton != null) area.UnsplitButton.IsEnabled = area.Groups.Count > 1;
            if (area.SplitSideButton != null) area.SplitSideButton.IsEnabled = CanSplit(area, sideBySide: true);
            if (area.SplitStackButton != null) area.SplitStackButton.IsEnabled = CanSplit(area, sideBySide: false);
        }
    }

    /// <summary>
    /// A qué apunta la pestaña, para su título. En SQLite la base siempre se llama "main", así que se usa
    /// el nombre de la conexión; en MySQL, la base, y la conexión si hay más de una abierta.
    /// </summary>
    private string TargetLabel(QueryTab tab) =>
        tab.IsOffline ? "sin conexión"
        : tab.Profile.Kind == DbKind.Sqlite ? tab.Profile.Name
        : (tab.CurrentDatabase ?? "(sin base)") + (_databases.Count > 1 ? $" ({tab.Profile.Name})" : "");

    private void UpdateHeader(TabEntry entry)
    {
        var tab = entry.Tab;
        entry.Title.Text = $"{tab.Title}{tab.ViewSuffix}{(tab.IsDirty ? "*" : "")} - {TargetLabel(tab)}"
            + (tab.IsRunning ? " (ejecutando...)" : "");
        entry.Header.ToolTip = $"{tab.FilePath ?? tab.Title}\n{tab.Profile.Name}";
    }

    /// <summary>Sincroniza barra de estado, título y combo de bases de datos con la pestaña actual.</summary>
    private void UpdateChrome()
    {
        var tab = Current;

        // Bases de todas las conexiones abiertas, cada una con el nombre de su conexión para distinguirlas
        // (en SQLite todas se llaman "main"). Solo se reasigna la lista si cambió.
        _syncingCombo = true;
        var targets = _databases.SelectMany(pair => pair.Value.Select(database => new DbTarget(pair.Key, database))).ToList();
        if (DatabaseCombo.ItemsSource is not List<DbTarget> shown || !shown.SequenceEqual(targets))
        {
            DatabaseCombo.ItemsSource = targets;
            foreach (var entry in _tabs) UpdateHeader(entry);   // el título depende de cuántas conexiones hay
        }
        DatabaseCombo.SelectedItem = tab == null ? null
            : targets.FirstOrDefault(t => t.Profile == tab.Profile && t.Database == tab.CurrentDatabase);
        DatabaseCombo.IsEnabled = tab is { IsRunning: false };
        _syncingCombo = false;

        // Mientras una consulta está en curso solo se puede cancelar; ejecutar y plan vuelven al terminar.
        ExecuteButton.IsEnabled = ExplainButton.IsEnabled = ExecuteMenuItem.IsEnabled = ExplainMenuItem.IsEnabled = tab is { IsRunning: false };
        CancelButton.IsEnabled = CancelMenuItem.IsEnabled = tab is { IsRunning: true };
        DisconnectButton.IsEnabled = DiagramMenuItem.IsEnabled = _databases.Count > 0;
        UpdateGroupMenu();
        // El tema atenúa el texto de un botón deshabilitado, pero no sus iconos de color: se atenúan aquí.
        foreach (var button in new[] { ExecuteButton, ExplainButton, CancelButton })
            ((UIElement)button.Content).Opacity = button.IsEnabled ? 1 : 0.4;

        // La barra de la principal muestra la pestaña visible de esta ventana (no la de una flotante).
        StatusView.Update(VisibleTab(_main), _databases.Count > 0 ? "Listo" : "Sin conexión");
        var mainTab = VisibleTab(_main);
        Title = mainTab != null ? $"{mainTab.Title} - {mainTab.Profile.Name} - {App.Name}" : App.Name;

        // Deshacer y Rehacer solo están disponibles si hay algo que deshacer o rehacer.
        UndoButton.IsEnabled = UndoMenuItem.IsEnabled = tab?.SqlEditor.CanUndo == true;
        RedoButton.IsEnabled = RedoMenuItem.IsEnabled = tab?.SqlEditor.CanRedo == true;
        // En la barra, el tema apenas distingue un botón deshabilitado: se atenúa a propósito.
        UndoButton.Opacity = UndoButton.IsEnabled ? 1 : 0.35;
        RedoButton.Opacity = RedoButton.IsEnabled ? 1 : 0.35;
        // Guardar: la pestaña actual, si tiene cambios; Guardar todo: si alguna los tiene.
        SaveButton.IsEnabled = tab is { IsDirty: true };
        SaveAllButton.IsEnabled = SaveAllMenuItem.IsEnabled = _tabs.Any(NeedsSaving);
        CloseAllMenuItem.IsEnabled = _tabs.Any(t => !t.Pinned);
        SaveButton.Opacity = SaveButton.IsEnabled ? 1 : 0.35;
        SaveAllButton.Opacity = SaveAllButton.IsEnabled ? 1 : 0.35;
        DockMenuItem.IsEnabled = ActiveEntry is { } active && active.Group.Area != _main;
        UpdateFloatingWindows();
    }

    /// <summary>La pestaña que se ve en una zona: la seleccionada de su último grupo usado.</summary>
    private static QueryTab? VisibleTab(GroupArea area)
    {
        var group = area.ActiveGroup is { } last && area.Groups.Contains(last) ? last : area.Groups.FirstOrDefault();
        return group?.Selected?.Tab;
    }

    // ---------- Archivos ----------

    private void OpenFile() => OpenFileAsync().Watch("Abrir archivo");

    private async Task OpenFileAsync()
    {
        var dialog = new OpenFileDialog { Filter = FileFilter, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        await OfferConnectionAsync();
        OpenFiles(dialog.FileNames);
    }

    /// <summary>
    /// Al abrir un archivo sin ninguna conexión se ofrece conectar, como en SSMS. Si se cancela, el archivo se
    /// abre igualmente, sin conexión: se pedirá otra vez al ejecutar.
    /// </summary>
    private async Task OfferConnectionAsync()
    {
        if (_databases.Count == 0) await ConnectCoreAsync(emptyTab: false);
    }

    /// <summary>
    /// Abre cada archivo en una pestaña de la conexión actual (o sin conexión, si no hay ninguna) y lo anota
    /// en "Abrir reciente".
    /// </summary>
    private void OpenFiles(IEnumerable<string> paths)
    {
        // Una pestaña sin conexión no sirve de referencia: se usa la primera conexión abierta, si la hay.
        var current = Current is { IsOffline: false } connected ? connected : null;
        var profile = current?.Profile ?? _databases.Keys.FirstOrDefault() ?? ConnectionProfile.Offline;
        foreach (string path in paths)
        {
            // Si ya está abierto, se va a su pestaña: dos pestañas del mismo archivo se pisarían al guardar.
            if (_tabs.FirstOrDefault(t => string.Equals(t.Tab.FilePath, path, StringComparison.OrdinalIgnoreCase)) is { } open)
            {
                Select(open);
                continue;
            }
            try
            {
                AddTab(profile, current?.CurrentDatabase).LoadFile(path);
                RememberRecent(path);
            }
            catch (Exception ex)
            {
                // La pestaña que se creó para el archivo no debe quedar vacía de recuerdo.
                if (_tabs.Count > 0 && _tabs[^1].Tab is { FilePath: null, HasText: false }) CloseTab(_tabs[^1]);
                Errors.Show(this, "No se pudo abrir el archivo", ex);
            }
        }
        SaveSessions();
    }

    // ---------- Abrir reciente ----------

    private void RememberRecent(string path)
    {
        AppSettings.Current.AddRecentFile(path);
        try { AppSettings.Current.Save(); } catch { }
        UpdateRecentMenu();
    }

    /// <summary>Rellena el submenú "Abrir reciente" con la lista guardada.</summary>
    private void UpdateRecentMenu()
    {
        var recent = AppSettings.Current.RecentFiles;
        RecentMenuItem.Items.Clear();
        RecentMenuItem.IsEnabled = recent.Count > 0;
        for (int i = 0; i < recent.Count; i++)
        {
            string path = recent[i];
            // "__": en un menú, un solo "_" marcaría la letra siguiente como tecla de acceso.
            string label = $"{Path.GetFileName(path)}   ({Path.GetDirectoryName(path)})".Replace("_", "__");
            var item = new MenuItem { Header = i < 9 ? $"_{i + 1}  {label}" : $"{i + 1}  {label}", ToolTip = path };
            item.Click += (_, _) => OpenRecentAsync(path).Watch("Abrir archivo reciente");
            RecentMenuItem.Items.Add(item);
        }
        if (recent.Count == 0) return;

        RecentMenuItem.Items.Add(new Separator());
        var clear = new MenuItem { Header = "_Borrar la lista" };
        clear.Click += (_, _) =>
        {
            AppSettings.Current.RecentFiles.Clear();
            try { AppSettings.Current.Save(); } catch { }
            UpdateRecentMenu();
        };
        RecentMenuItem.Items.Add(clear);
    }

    private async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"El archivo ya no existe y se quita de la lista:\n{path}", App.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            AppSettings.Current.RemoveRecentFile(path);
            try { AppSettings.Current.Save(); } catch { }
            UpdateRecentMenu();
            return;
        }
        await OfferConnectionAsync();
        OpenFiles(new[] { path });
        // Si ya estaba abierto solo se fue a su pestaña: igualmente pasa a ser el más reciente.
        RememberRecent(path);
    }

    private bool Save(QueryTab tab, bool saveAs)
    {
        string? path = tab.FilePath;
        if (saveAs || path == null)
        {
            var dialog = new SaveFileDialog { Filter = FileFilter, FileName = tab.Title, DefaultExt = ".sql" };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }

        try
        {
            tab.SaveFile(path);
            SaveSessions();
            RememberRecent(path);
            return true;
        }
        catch (Exception ex)
        {
            Errors.Show(this, "No se pudo guardar el archivo", ex);
            return false;
        }
    }

    /// <summary>Con cambios sin guardar y algo escrito (una consulta nueva vacía no cuenta).</summary>
    private static bool NeedsSaving(TabEntry entry) => entry.Tab.IsDirty && entry.Tab.HasText;

    /// <summary>
    /// Guarda todas las consultas con cambios, también las de las ventanas flotantes. Las que aún no tienen
    /// archivo preguntan dónde guardarse; cancelar esa pregunta detiene el resto.
    /// </summary>
    private void SaveAll()
    {
        var active = ActiveEntry;
        foreach (var entry in _tabs.Where(NeedsSaving).ToList())
        {
            if (!NeedsSaving(entry)) continue;   // otra vista del mismo script ya lo guardó
            // Se muestra la pestaña antes de preguntar dónde guardarla, para saber cuál es.
            if (entry.Tab.FilePath == null) Select(entry);
            if (!Save(entry.Tab, saveAs: false)) break;
        }
        if (active != null && _tabs.Contains(active) && ActiveEntry != active) Select(active);
    }

    // ---------- Explorador de objetos ----------

    private TreeViewItem MakeNode(string header, Node node, bool expandable)
    {
        var icon = node.Kind switch
        {
            NodeKind.Server => ExplorerIcon.Server,
            NodeKind.Database => ExplorerIcon.Database,
            NodeKind.Folder => ExplorerIcon.Folder,
            NodeKind.View => ExplorerIcon.View,
            NodeKind.Procedure => ExplorerIcon.Procedure,
            NodeKind.Function => ExplorerIcon.Function,
            NodeKind.Trigger => ExplorerIcon.Trigger,
            NodeKind.Index => ExplorerIcon.Index,
            _ => ExplorerIcon.Table,
        };
        var item = new TreeViewItem
        {
            Header = node.Kind == NodeKind.Server ? ServerHeader(node.Profile) : ExplorerIcons.Header(icon, header),
            Tag = node,
        };
        if (expandable)
            item.Items.Add(new TreeViewItem { Header = "Cargando...", Tag = Placeholder });
        item.ContextMenu = BuildMenu(item, node);
        return item;
    }

    private ContextMenu? BuildMenu(TreeViewItem item, Node node)
    {
        var menu = new ContextMenu();

        void Add(string header, Action action)
        {
            var menuItem = new MenuItem { Header = header };
            menuItem.Click += (_, _) => action();
            menu.Items.Add(menuItem);
        }

        switch (node.Kind)
        {
            case NodeKind.Server:
            case NodeKind.Database:
                Add("Nueva consulta", () => AddTab(node.Profile, node.Database));
                if (node.Kind == NodeKind.Database)
                {
                    Add("Ver diagrama", () => new DiagramWindow(node.Profile, node.Database!, Icon).Show());
                    // Sybase: todavía no (falta probar la generación del script contra un servidor real).
                    if (node.Profile.Kind != DbKind.Sybase)
                    {
                        Add("Copia de seguridad (.sql)...", () => BackupAsync(node).Watch("Copia de seguridad"));
                        Add("Restaurar desde .sql...", () => RestoreAsync(item, node).Watch("Restaurar"));
                    }
                }
                Add("Actualizar", () => RefreshAsync(item, node).Watch("Actualizar el explorador"));
                menu.Items.Add(new Separator());
                Add("Desconectar", () => Disconnect(node.Profile));
                ((MenuItem)menu.Items[^1]).Icon = ExplorerIcons.Create(ExplorerIcon.Disconnect);
                break;

            case NodeKind.Folder:
                return null;

            case NodeKind.Table:
            case NodeKind.View:
                var dialect = node.Profile.Kind;
                string fullName = Db.FullName(node.Profile, node.Database, node.Name!);
                Add("Seleccionar las primeras 1000 filas", () =>
                {
                    var tab = AddTab(node.Profile, node.Database);
                    tab.SetText(ScriptTemplates.SelectTop(dialect, fullName));
                    tab.ExecuteAsync().Watch("Ejecutar la consulta");
                });

                // "Generar script como", como en SSMS. Se abre en una pestaña nueva, sin ejecutar.
                var scriptAs = new MenuItem { Header = "Generar script como" };
                void AddScript(string header, Func<IReadOnlyList<ColumnInfo>, string>? template)
                {
                    var scriptItem = new MenuItem { Header = header };
                    scriptItem.Click += (_, _) => _ = template == null
                        ? ScriptCreateAsync(node)
                        : ScriptFromColumnsAsync(node, columns => template(columns));
                    scriptAs.Items.Add(scriptItem);
                }
                AddScript("CREATE", null);
                AddScript("SELECT", columns => ScriptTemplates.Select(dialect, fullName, columns));
                if (node.Kind == NodeKind.Table)
                {
                    AddScript("INSERT", columns => ScriptTemplates.Insert(dialect, fullName, columns));
                    AddScript("UPDATE", columns => ScriptTemplates.Update(dialect, fullName, columns));
                    AddScript("DELETE", columns => ScriptTemplates.Delete(dialect, fullName, columns));
                }
                menu.Items.Add(scriptAs);
                if (node.Kind == NodeKind.Table && node.Profile.Kind != DbKind.Sybase)
                    Add("Importar datos (CSV, Excel)...", () => ImportAsync(node).Watch("Importar datos"));
                Add("Actualizar", () => RefreshAsync(item, node).Watch("Actualizar el explorador"));
                break;

            case NodeKind.Procedure:
                Add("Generar script CREATE", () => ScriptCreateAsync(node).Watch("Generar script"));
                // SQL Server ejecuta los procedimientos con EXEC; MySQL, con CALL.
                bool exec = Db.IsTSql(node.Profile.Kind);
                Add(exec ? "Generar llamada (EXEC)" : "Generar llamada (CALL)", () =>
                    AddTab(node.Profile, node.Database).SetText(exec
                        ? $"EXEC {Db.FullName(node.Profile, node.Database, node.Name!)};\n"
                        : $"CALL {Db.FullName(node.Profile, node.Database, node.Name!)}();\n"));
                break;

            case NodeKind.Function:
                Add("Generar script CREATE", () => ScriptCreateAsync(node).Watch("Generar script"));
                Add("Generar llamada (SELECT)", () =>
                    AddTab(node.Profile, node.Database).SetText($"SELECT {Db.FullName(node.Profile, node.Database, node.Name!)}();\n"));
                break;

            default:
                Add("Generar script CREATE", () => ScriptCreateAsync(node).Watch("Generar script"));
                break;
        }
        MenuIcons.Apply(menu);
        return menu;
    }

    // ---------- Copia de seguridad y restauración ----------

    /// <summary>Ejecuta un trabajo largo mostrando la ventana de progreso (con Cancelar) y bloqueando la principal.</summary>
    private async Task<T> RunWithProgressAsync<T>(string title, Func<IProgress<BackupProgress>, CancellationToken, Task<T>> work)
    {
        using var cancellation = new CancellationTokenSource();
        var dialog = new ProgressDialog(this, title, cancellation);
        var progress = new Progress<BackupProgress>(p => dialog.Report(p.Step, p.Done, p.Total));
        IsEnabled = false;
        dialog.Show();
        try
        {
            return await Task.Run(() => work(progress, cancellation.Token));
        }
        finally
        {
            IsEnabled = true;
            dialog.Finish();
        }
    }

    private static string DatabaseLabel(Node node) =>
        node.Profile.Kind == DbKind.Sqlite ? node.Profile.Name : $"{node.Database} ({node.Profile.Name})";

    private async Task BackupAsync(Node node)
    {
        string database = node.Database!;
        try
        {
            var tables = (await Db.ListTablesAsync(node.Profile, database)).Where(t => !t.IsView).Select(t => t.Name).ToList();
            string baseName = node.Profile.Kind == DbKind.Sqlite ? Path.GetFileNameWithoutExtension(node.Profile.FilePath) ?? "base" : database;
            string suggested = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"{baseName}_{DateTime.Now:yyyyMMdd_HHmm}.sql");

            var dialog = new BackupDialog(this, DatabaseLabel(node), tables, suggested);
            if (dialog.ShowDialog() != true || dialog.Options == null) return;
            string path = dialog.FilePath;
            if (File.Exists(path) && MessageBox.Show(this, $"El archivo ya existe:\n{path}\n\n¿Reemplazarlo?", "Copia de seguridad",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

            try
            {
                var summary = await RunWithProgressAsync("Creando la copia de seguridad",
                    (progress, token) => DatabaseBackup.ExportAsync(node.Profile, database, dialog.Options, path, progress, token));
                MessageBox.Show(this,
                    $"Copia creada.\n\nTablas: {summary.Tables:N0}\nFilas: {summary.Rows:N0}\nVistas, rutinas y triggers: {summary.OtherObjects:N0}\n\n" +
                    $"{path}\n({new FileInfo(path).Length / 1024.0:N0} KB)",
                    "Copia de seguridad", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                try { File.Delete(path); } catch { }   // un archivo a medias no sirve como copia
            }
            catch
            {
                // Tampoco si se interrumpió por un error: que nadie lo tome por una copia completa.
                try { File.Delete(path); } catch { }
                throw;
            }
        }
        catch (Exception ex)
        {
            Errors.Show(this, "No se pudo crear la copia", ex);
        }
    }

    private async Task RestoreAsync(TreeViewItem item, Node node)
    {
        var open = new OpenFileDialog { Filter = FileFilter, Title = "Elegir el archivo .sql a restaurar" };
        if (open.ShowDialog(this) != true) return;
        string path = open.FileName;

        try
        {
            var statements = await Task.Run(() => DatabaseBackup.ReadScript(path, node.Profile.Kind));
            if (statements.Count == 0)
            {
                MessageBox.Show(this, "El archivo no contiene ninguna sentencia.", "Restaurar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string target = DatabaseLabel(node) + (node.Profile.IsProduction ? "  —  PRODUCCIÓN" : "");
            var answer = MessageBox.Show(this,
                $"Vas a ejecutar {statements.Count:N0} sentencias de\n{path}\n\nsobre: {target}\n\n" +
                "Si es una copia de seguridad, las tablas que contenga se borrarán y se volverán a crear con los datos del archivo. " +
                "No se puede deshacer.\n\n¿Continuar?",
                node.Profile.IsProduction ? "Restaurar en PRODUCCIÓN" : "Restaurar", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            var result = await RunWithProgressAsync("Restaurando",
                (progress, token) => DatabaseBackup.RestoreAsync(node.Profile, node.Database!, statements, progress, token));
            if (result.Error != null)
                Errors.Show(this, "Restauración incompleta",
                    $"Se detuvo en la sentencia de la línea {result.ErrorLine} (se ejecutaron {result.Executed:N0} de {result.Total:N0}).", result.Error);
            else
                MessageBox.Show(this, $"Restauración completada: {result.Executed:N0} sentencias ejecutadas.", "Restaurar", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(this, "Restauración cancelada. La base puede haber quedado a medias.", "Restaurar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Errors.Show(this, "No se pudo restaurar", ex);
        }

        // La estructura pudo cambiar: se refrescan el árbol y el autocompletado.
        SchemaCache.Invalidate(node.Profile);
        await RefreshAsync(item, node);
    }

    private async Task ImportAsync(Node node)
    {
        try
        {
            var columns = await Db.GetColumnsAsync(node.Profile, node.Database!, node.Name!);
            new ImportDialog(this, node.Profile, node.Database!, node.Name!, columns).ShowDialog();
        }
        catch (Exception ex)
        {
            Errors.Show(this, "No se pudo preparar la importación", ex);
        }
    }

    private static SchemaObject ObjectKind(NodeKind kind) => kind switch
    {
        NodeKind.View => SchemaObject.View,
        NodeKind.Procedure => SchemaObject.Procedure,
        NodeKind.Function => SchemaObject.Function,
        NodeKind.Trigger => SchemaObject.Trigger,
        NodeKind.Index => SchemaObject.Index,
        _ => SchemaObject.Table,
    };

    private async Task ScriptFromColumnsAsync(Node node, Func<IReadOnlyList<ColumnInfo>, string> build)
    {
        try
        {
            var columns = await Db.GetColumnsAsync(node.Profile, node.Database!, node.Name!);
            AddTab(node.Profile, node.Database).SetText(build(columns));
        }
        catch (Exception ex)
        {
            Errors.Show(this, "No se pudo generar el script", ex);
        }
    }

    /// <summary>Carpeta del árbol ("Tablas (12)") con sus elementos ya cargados.</summary>
    private TreeViewItem MakeFolder(string title, ConnectionProfile profile, IReadOnlyCollection<TreeViewItem> children)
    {
        var folder = MakeNode($"{title} ({children.Count})", new Node(NodeKind.Folder, profile, Name: title), expandable: false);
        foreach (var child in children) folder.Items.Add(child);
        return folder;
    }

    private async void Explorer_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item || item.Tag is not Node node) return;
        if (item.Items.Count == 1 && item.Items[0] is TreeViewItem { Tag: var tag } && tag == Placeholder)
            await LoadChildrenAsync(item, node);
    }

    private async Task RefreshAsync(TreeViewItem item, Node node)
    {
        item.Items.Clear();
        item.Items.Add(new TreeViewItem { Header = "Cargando...", Tag = Placeholder });
        await LoadChildrenAsync(item, node);
        item.IsExpanded = true;
    }

    private async Task LoadChildrenAsync(TreeViewItem item, Node node)
    {
        var placeholder = (TreeViewItem)item.Items[0];
        placeholder.Header = "Cargando...";
        placeholder.Tag = null;   // evita cargas simultáneas del mismo nodo

        try
        {
            var children = new List<TreeViewItem>();
            switch (node.Kind)
            {
                case NodeKind.Server:
                    var names = await Db.ListDatabasesAsync(node.Profile);
                    // Si se desconectó mientras cargaba, no se la vuelve a dar por abierta.
                    if (!_databases.ContainsKey(node.Profile)) return;
                    _databases[node.Profile] = names;
                    foreach (string name in names)
                        children.Add(MakeNode(name, new Node(NodeKind.Database, node.Profile, name), expandable: true));
                    UpdateChrome();
                    break;

                case NodeKind.Database:
                {
                    // Carpetas como en SSMS: Tablas, Vistas, Procedimientos, Funciones y Triggers.
                    var profile = node.Profile;
                    string database = node.Database!;
                    var objects = await Db.ListTablesAsync(profile, database);
                    var routines = await Db.ListRoutinesAsync(profile, database);
                    var triggers = await Db.ListTriggersAsync(profile, database);

                    TreeViewItem Leaf(NodeKind kind, string name, string? table = null, bool expandable = false) =>
                        MakeNode(name, new Node(kind, profile, database, name, table), expandable);

                    var tables = MakeFolder("Tablas", profile, objects.Where(o => !o.IsView).Select(o => Leaf(NodeKind.Table, o.Name, expandable: true)).ToList());
                    tables.IsExpanded = true;
                    children.Add(tables);
                    children.Add(MakeFolder("Vistas", profile, objects.Where(o => o.IsView).Select(o => Leaf(NodeKind.View, o.Name, expandable: true)).ToList()));
                    if (profile.Kind != DbKind.Sqlite)
                    {
                        children.Add(MakeFolder("Procedimientos", profile, routines.Where(r => !r.IsFunction).Select(r => Leaf(NodeKind.Procedure, r.Name)).ToList()));
                        children.Add(MakeFolder("Funciones", profile, routines.Where(r => r.IsFunction).Select(r => Leaf(NodeKind.Function, r.Name)).ToList()));
                    }
                    children.Add(MakeFolder("Triggers", profile, triggers.Select(t =>
                    {
                        var trigger = Leaf(NodeKind.Trigger, t.Name, t.Table);
                        trigger.ToolTip = $"Sobre la tabla {t.Table}";
                        return trigger;
                    }).ToList()));
                    break;
                }

                default:
                {
                    // Tabla o vista: sus columnas y, en las tablas, sus índices.
                    foreach (var column in await Db.GetColumnsAsync(node.Profile, node.Database!, node.Name!))
                    {
                        var columnItem = new TreeViewItem
                        {
                            Header = ExplorerIcons.Header(column.PrimaryKey ? ExplorerIcon.KeyColumn : ExplorerIcon.Column, column.ToString()),
                        };
                        columnItem.SetResourceReference(ForegroundProperty, "Brush.SecondaryText");
                        children.Add(columnItem);
                    }
                    if (node.Kind == NodeKind.Table)
                    {
                        var indexes = await Db.ListIndexesAsync(node.Profile, node.Database!, node.Name!);
                        if (indexes.Count > 0)
                            children.Add(MakeFolder("Índices", node.Profile, indexes.Select(i =>
                            {
                                var index = MakeNode(i.ToString(), new Node(NodeKind.Index, node.Profile, node.Database, i.Name, node.Name), expandable: false);
                                return index;
                            }).ToList()));
                    }
                    break;
                }
            }

            item.Items.Clear();
            foreach (var child in children)
                item.Items.Add(child);
            ApplyExplorerFilter();
        }
        catch (Exception ex)
        {
            placeholder.Header = "Error: " + ex.Message;
            placeholder.Tag = Placeholder;
        }
    }

    private void ExplorerFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        ExplorerFilterHint.Visibility = ExplorerFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyExplorerFilter();
    }

    /// <summary>
    /// Oculta tablas, vistas, rutinas y triggers que no contienen el texto del filtro (en las bases ya cargadas).
    /// No entra en las tablas: sus columnas e índices no se filtran.
    /// </summary>
    private void ApplyExplorerFilter()
    {
        string filter = ExplorerFilter.Text.Trim();

        void Walk(ItemCollection items)
        {
            foreach (var item in items.OfType<TreeViewItem>())
            {
                switch (item.Tag)
                {
                    case Node { Kind: NodeKind.Table or NodeKind.View or NodeKind.Procedure or NodeKind.Function or NodeKind.Trigger, Name: { } name }:
                        bool visible = filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                        item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                        break;
                    case Node { Kind: NodeKind.Server or NodeKind.Database or NodeKind.Folder }:
                        Walk(item.Items);
                        break;
                }
            }
        }

        Walk(Explorer.Items);
    }

    private async Task ScriptCreateAsync(Node node)
    {
        try
        {
            string script = await Db.GetCreateScriptAsync(node.Profile, node.Database!, ObjectKind(node.Kind), node.Name!, node.Table);
            AddTab(node.Profile, node.Database).SetText(script + "\n");
        }
        catch (Exception ex)
        {
            Errors.Show(this, "No se pudo generar el script", ex);
        }
    }
}
