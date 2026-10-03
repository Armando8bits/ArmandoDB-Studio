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

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ConnectAsync();

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
            if (Current != null) await Current.ExplainAsync();
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
    private void CloseTab_Click(object sender, RoutedEventArgs e) { if (ActiveEntry != null) CloseTab(ActiveEntry); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private async void Execute_Click(object sender, RoutedEventArgs e) => await ExecuteCurrentAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Current?.Cancel();

    private async void Explain_Click(object sender, RoutedEventArgs e)
    {
        if (Current != null) await Current.ExplainAsync();
    }
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
        if (Current != null)
            await Current.ExecuteAsync();
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
            "Ctrl+W\t\t\tCerrar la pestaña\n" +
            "Ctrl+Tab\t\tPestaña siguiente (con Mayús, anterior)\n\n" +
            "Ctrl+Z / Ctrl+Y\t\tDeshacer / Rehacer\n" +
            "Ctrl+F / Ctrl+H\t\tBuscar / Reemplazar\n" +
            "Ctrl+Mayús+F\t\tFormatear SQL\n" +
            "Ctrl+Espacio\t\tAutocompletar\n" +
            "Tab\t\t\tExpandir un fragmento (sel, upd, ij...)\n\n" +
            "Ctrl+Mayús+L\t\tFiltrar los resultados\n" +
            "Ctrl+B\t\t\tMostrar u ocultar el explorador",
            "Atajos de teclado", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------- Conexión ----------

    private async Task ConnectAsync()
    {
        var dialog = new ConnectionDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile == null) return;

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

            if (RestoreSession(profile, dialog.Profile.Database)) return;
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

        AddTab(profile, dialog.Profile.Database);
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
                try
                {
                    // Cada archivo vuelve al grupo de la vista dividida en el que estaba.
                    if (saved.Group == 1 && _main.Groups.Count == 1)
                    {
                        CreateGroup(_main);
                        LayoutGroups(_main);
                    }
                    _activeGroup = _main.Groups[Math.Clamp(saved.Group, 0, _main.Groups.Count - 1)];
                    AddTab(profile, saved.Database ?? defaultDatabase).LoadFile(saved.Path);
                    _tabs[^1].Pinned = saved.Pinned;
                    restored = true;
                }
                catch
                {
                    // Un archivo ilegible no impide restaurar el resto; su pestaña queda vacía.
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
        var current = Current;
        if (current != null)
            AddTab(current.Profile, current.CurrentDatabase);
        else if (_databases.Count > 0)
            AddTab(_databases.Keys.First(), null);
        else
            _ = ConnectAsync();
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

    private QueryTab AddTab(ConnectionProfile profile, string? database)
    {
        var tab = new QueryTab(profile, database, $"Consulta{++_queryCounter}.sql");
        tab.StateChanged += Tab_StateChanged;

        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var pin = HeaderButton("📌", "Anclar pestaña");
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
        AddMenu("Anclar o desanclar", () => TogglePin(entry));
        AddMenu("Cerrar", () => CloseTab(entry));
        AddMenu("Cerrar las demás (excepto ancladas)", () =>
        {
            foreach (var other in _tabs.Where(t => t != entry && !t.Pinned && t.Group == entry.Group).ToList())
                if (!CloseTab(other)) break;
        });
        menu.Items.Add(new Separator());
        AddMenu("Dividir: izquierda / derecha", () => { Select(entry); Split(sideBySide: true); });
        AddMenu("Dividir: arriba / abajo", () => { Select(entry); Split(sideBySide: false); });
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
        };
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
    /// Divide en dos grupos la zona de la pestaña actual (o cambia la orientación si ya está dividida).
    /// La pestaña actual pasa al grupo nuevo; si es la única, el grupo nuevo abre una consulta en blanco.
    /// </summary>
    private void Split(bool sideBySide)
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

        var entry = ActiveEntry;
        if (entry == null) return;

        var group = CreateGroup(area);
        LayoutGroups(area);
        if (VisualOrder(entry.Group).Count < 2)
        {
            _activeGroup = group;
            AddTab(entry.Tab.Profile, entry.Tab.CurrentDatabase);
            return;
        }

        MoveToGroup(entry, group);
        _tabs.Remove(entry);
        _tabs.Add(entry);
        RefreshTabs();
        SaveSessions();
        entry.Tab.FocusEditor();
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
        void AddButton(string text, string toolTip, Action action)
        {
            var button = new Button { Content = text, ToolTip = toolTip, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
            button.Click += (_, _) =>
            {
                ActivateArea(area);
                action();
            };
            bar.Children.Add(button);
        }
        AddButton("▶ Ejecutar", "Ejecutar (Ctrl+E o F5)", () => _ = ExecuteCurrentAsync());
        AddButton("Plan", "Plan de ejecución (Ctrl+L)", () => _ = Current?.ExplainAsync());
        AddButton("■ Cancelar", "Cancelar la ejecución (Alt+Pausa)", () => Current?.Cancel());
        AddButton("Dividir ◧", "Dividir esta ventana: izquierda / derecha", () => Split(sideBySide: true));
        AddButton("Dividir ⬒", "Dividir esta ventana: arriba / abajo", () => Split(sideBySide: false));
        AddButton("Quitar división", "Volver a un solo grupo de pestañas en esta ventana", Unsplit);
        AddButton("Devolver a la ventana principal", "Devuelve la pestaña actual a la ventana principal",
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
        if (tab.IsDirty && tab.HasText)
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
        if (tab == Current) UpdateChrome();
        else UpdateFloatingWindows();
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
        }
    }

    /// <summary>
    /// A qué apunta la pestaña, para su título. En SQLite la base siempre se llama "main", así que se usa
    /// el nombre de la conexión; en MySQL, la base, y la conexión si hay más de una abierta.
    /// </summary>
    private string TargetLabel(QueryTab tab) =>
        tab.Profile.Kind == DbKind.Sqlite ? tab.Profile.Name
        : (tab.CurrentDatabase ?? "(sin base)") + (_databases.Count > 1 ? $" ({tab.Profile.Name})" : "");

    private void UpdateHeader(TabEntry entry)
    {
        var tab = entry.Tab;
        entry.Title.Text = $"{tab.Title}{(tab.IsDirty ? "*" : "")} - {TargetLabel(tab)}"
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

        ExecuteButton.IsEnabled = ExplainButton.IsEnabled = tab is { IsRunning: false };
        CancelButton.IsEnabled = tab is { IsRunning: true };
        DisconnectButton.IsEnabled = _databases.Count > 0;

        // La barra de la principal muestra la pestaña visible de esta ventana (no la de una flotante).
        StatusView.Update(VisibleTab(_main), _databases.Count > 0 ? "Listo" : "Sin conexión");
        var mainTab = VisibleTab(_main);
        Title = mainTab != null ? $"{mainTab.Title} - {mainTab.Profile.Name} - {App.Name}" : App.Name;

        // Deshacer y Rehacer solo están disponibles si hay algo que deshacer o rehacer.
        UndoButton.IsEnabled = UndoMenuItem.IsEnabled = tab?.SqlEditor.CanUndo == true;
        RedoButton.IsEnabled = RedoMenuItem.IsEnabled = tab?.SqlEditor.CanRedo == true;
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

    private void OpenFile()
    {
        if (_databases.Count == 0)
        {
            MessageBox.Show(this, "Conéctate primero a un servidor.", App.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog { Filter = FileFilter, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;

        var current = Current;
        var profile = current?.Profile ?? _databases.Keys.First();
        foreach (string path in dialog.FileNames)
        {
            try
            {
                AddTab(profile, current?.CurrentDatabase).LoadFile(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "No se pudo abrir el archivo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        SaveSessions();
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
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "No se pudo guardar el archivo", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
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
                Add("Actualizar", () => _ = RefreshAsync(item, node));
                menu.Items.Add(new Separator());
                Add("Desconectar", () => Disconnect(node.Profile));
                ((MenuItem)menu.Items[^1]).Icon = ExplorerIcons.Create(ExplorerIcon.Disconnect);
                break;

            case NodeKind.Folder:
                return null;

            case NodeKind.Table:
            case NodeKind.View:
                string fullName = $"{Db.QuoteId(node.Database!)}.{Db.QuoteId(node.Name!)}";
                Add("Seleccionar las primeras 1000 filas", () =>
                {
                    var tab = AddTab(node.Profile, node.Database);
                    tab.SetText($"SELECT *\nFROM {fullName}\nLIMIT 1000;\n");
                    _ = tab.ExecuteAsync();
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
                AddScript("SELECT", columns => ScriptTemplates.Select(fullName, columns));
                if (node.Kind == NodeKind.Table)
                {
                    AddScript("INSERT", columns => ScriptTemplates.Insert(fullName, columns));
                    AddScript("UPDATE", columns => ScriptTemplates.Update(fullName, columns));
                    AddScript("DELETE", columns => ScriptTemplates.Delete(fullName, columns));
                }
                menu.Items.Add(scriptAs);
                Add("Actualizar", () => _ = RefreshAsync(item, node));
                break;

            case NodeKind.Procedure:
                Add("Generar script CREATE", () => _ = ScriptCreateAsync(node));
                Add("Generar llamada (CALL)", () =>
                    AddTab(node.Profile, node.Database).SetText($"CALL {Db.QuoteId(node.Database!)}.{Db.QuoteId(node.Name!)}();\n"));
                break;

            case NodeKind.Function:
                Add("Generar script CREATE", () => _ = ScriptCreateAsync(node));
                Add("Generar llamada (SELECT)", () =>
                    AddTab(node.Profile, node.Database).SetText($"SELECT {Db.QuoteId(node.Database!)}.{Db.QuoteId(node.Name!)}();\n"));
                break;

            default:
                Add("Generar script CREATE", () => _ = ScriptCreateAsync(node));
                break;
        }
        return menu;
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
            MessageBox.Show(this, ex.Message, "No se pudo generar el script", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    if (profile.Kind == DbKind.MySql)
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
            MessageBox.Show(this, ex.Message, "No se pudo generar el script", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
