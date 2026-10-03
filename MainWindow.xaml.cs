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
    private enum NodeKind { Server, Database, Table, View }

    private record Node(NodeKind Kind, ConnectionProfile Profile, string? Database = null, string? Name = null);

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
        public TabEntry? Selected { get; set; }
    }

    private const string FileFilter = "Archivos SQL (*.sql)|*.sql|Todos los archivos (*.*)|*.*";
    private static readonly object Placeholder = new();
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));

    private readonly Dictionary<ConnectionProfile, List<string>> _databases = new();
    private readonly List<TabEntry> _tabs = new();
    private readonly List<TabGroup> _groups = new();
    private TabGroup _activeGroup;
    private TabEntry? _dragCandidate;
    private Point _dragStart;
    private int _queryCounter;
    private bool _syncingCombo;
    private bool _suspendSessionSave;
    private GridLength _explorerWidth = new(270);

    public MainWindow()
    {
        InitializeComponent();
        _activeGroup = CreateGroup();
        _groups.Add(_activeGroup);
        LayoutGroups();
        SetExplorerVisible(AppSettings.Current.ShowExplorer);
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
        else if (modifiers == ModifierKeys.Control && e.Key == Key.B)
        {
            e.Handled = true;
            ToggleExplorer_Click(sender, e);
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

    private async void DatabaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCombo || Current == null || DatabaseCombo.SelectedItem is not string database) return;
        await Current.ChangeDatabaseAsync(database);
        Current.FocusEditor();
    }

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
        }

        AddTab(profile, dialog.Profile.Database);
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        // La conexión del nodo seleccionado en el explorador; si no hay, la de la pestaña actual.
        ConnectionProfile? profile = null;
        for (var item = Explorer.SelectedItem as TreeViewItem; item != null && profile == null; item = item.Parent as TreeViewItem)
            profile = (item.Tag as Node)?.Profile;
        profile ??= Current?.Profile;
        if (profile != null)
            Disconnect(profile);
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
                    if (saved.Group == 1 && _groups.Count == 1)
                    {
                        _groups.Add(CreateGroup());
                        LayoutGroups();
                    }
                    _activeGroup = _groups[Math.Min(saved.Group, _groups.Count - 1)];
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
            _activeGroup = _groups[0];
            RemoveEmptyGroup();
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
            profile => _groups.SelectMany(VisualOrder)
                .Where(t => t.Tab.Profile == profile && t.Tab.FilePath != null)
                .Select(t => new SessionTab
                {
                    Path = t.Tab.FilePath!,
                    Pinned = t.Pinned,
                    Database = t.Tab.CurrentDatabase,
                    Group = _groups.IndexOf(t.Group),
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
                RemoveEmptyGroup();
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
        AddMenu("Quitar la división", Unsplit);
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
        // Posición en pantalla de cada encabezado antes del cambio, para deslizarlo hasta la nueva.
        var before = new Dictionary<TabEntry, Point>();
        if (animate)
        {
            foreach (var entry in _tabs.Where(t => t.Header.IsVisible))
                before[entry] = entry.Header.TranslatePoint(new Point(), this);
        }

        foreach (var group in _groups)
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
        foreach (var group in _groups)
            group.PinnedRow.Visibility = group.PinnedStrip.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (before.Count == 0) return;
        UpdateLayout();
        var duration = TimeSpan.FromMilliseconds(180);
        foreach (var (entry, oldPosition) in before)
        {
            var delta = oldPosition - entry.Header.TranslatePoint(new Point(), this);
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

    private TabGroup CreateGroup()
    {
        var pinnedStrip = new WrapPanel();
        var normalStrip = new WrapPanel { MinHeight = 27 };
        var pinnedRow = new Border
        {
            Child = pinnedStrip,
            Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xE4, 0xF2)),
            Visibility = Visibility.Collapsed,
            AllowDrop = true,
        };
        var normalRow = new Border
        {
            Child = normalStrip,
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xF2)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCE, 0xDB)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            AllowDrop = true,
        };
        var host = new Grid();

        var root = new DockPanel();
        DockPanel.SetDock(pinnedRow, Dock.Top);
        DockPanel.SetDock(normalRow, Dock.Top);
        root.Children.Add(pinnedRow);
        root.Children.Add(normalRow);
        root.Children.Add(host);

        var group = new TabGroup { Root = root, PinnedRow = pinnedRow, PinnedStrip = pinnedStrip, NormalStrip = normalStrip, Host = host };

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
            RemoveEmptyGroup();
            RefreshTabs(animate: true);
            SaveSessions();
        };
        pinnedRow.DragOver += dragOver;
        normalRow.DragOver += dragOver;
        return group;
    }

    private void Activate(TabGroup group)
    {
        if (_activeGroup == group || !_groups.Contains(group)) return;
        _activeGroup = group;
        ApplySelection();
    }

    /// <summary>Coloca los grupos en pantalla: uno solo, o dos separados por un divisor arrastrable.</summary>
    private void LayoutGroups()
    {
        GroupsGrid.Children.Clear();
        GroupsGrid.ColumnDefinitions.Clear();
        GroupsGrid.RowDefinitions.Clear();
        foreach (var group in _groups)
        {
            Grid.SetRow(group.Root, 0);
            Grid.SetColumn(group.Root, 0);
            GroupsGrid.Children.Add(group.Root);
        }
        if (_groups.Count < 2) return;

        var splitter = new GridSplitter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Background = new SolidColorBrush(Color.FromRgb(0xB8, 0xC2, 0xD6)),
        };
        if (AppSettings.Current.SplitSideBySide)
        {
            GroupsGrid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 120 });
            GroupsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            GroupsGrid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 120 });
            splitter.Width = 5;
            splitter.ResizeDirection = GridResizeDirection.Columns;
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(_groups[1].Root, 2);
        }
        else
        {
            GroupsGrid.RowDefinitions.Add(new RowDefinition { MinHeight = 100 });
            GroupsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            GroupsGrid.RowDefinitions.Add(new RowDefinition { MinHeight = 100 });
            splitter.Height = 5;
            splitter.ResizeDirection = GridResizeDirection.Rows;
            Grid.SetRow(splitter, 1);
            Grid.SetRow(_groups[1].Root, 2);
        }
        GroupsGrid.Children.Add(splitter);
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
        _activeGroup = target;
    }

    /// <summary>Si la vista está dividida y un grupo se quedó sin pestañas, se deshace la división.</summary>
    private void RemoveEmptyGroup()
    {
        if (_groups.Count < 2) return;
        var empty = _groups.FirstOrDefault(g => !_tabs.Any(t => t.Group == g));
        if (empty == null) return;

        _groups.Remove(empty);
        _activeGroup = _groups[0];
        LayoutGroups();
    }

    /// <summary>
    /// Divide la vista en dos grupos (o cambia la orientación si ya está dividida).
    /// La pestaña actual pasa al grupo nuevo; si es la única, el grupo nuevo abre una consulta en blanco.
    /// </summary>
    private void Split(bool sideBySide)
    {
        AppSettings.Current.SplitSideBySide = sideBySide;
        try { AppSettings.Current.Save(); } catch { }

        if (_groups.Count == 2)
        {
            LayoutGroups();
            return;
        }

        var entry = ActiveEntry;
        if (entry == null) return;

        var group = CreateGroup();
        _groups.Add(group);
        LayoutGroups();
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
        if (entry == null || _groups.Count < 2) return;

        MoveToGroup(entry, _groups.First(g => g != entry.Group));
        _tabs.Remove(entry);
        _tabs.Add(entry);
        RemoveEmptyGroup();
        RefreshTabs();
        SaveSessions();
        entry.Tab.FocusEditor();
    }

    private void Unsplit()
    {
        if (_groups.Count < 2) return;

        var active = ActiveEntry;
        foreach (var entry in _tabs.Where(t => t.Group != _groups[0]).ToList())
            MoveToGroup(entry, _groups[0]);
        if (active != null) _groups[0].Selected = active;
        RemoveEmptyGroup();
        RefreshTabs();
        SaveSessions();
        active?.Tab.FocusEditor();
    }

    private void SplitSideBySide_Click(object sender, RoutedEventArgs e) => Split(sideBySide: true);
    private void SplitStacked_Click(object sender, RoutedEventArgs e) => Split(sideBySide: false);
    private void MoveToOtherGroup_Click(object sender, RoutedEventArgs e) => MoveActiveToOtherGroup();
    private void Unsplit_Click(object sender, RoutedEventArgs e) => Unsplit();

    private void StyleHeader(TabEntry entry)
    {
        bool selected = entry == entry.Group.Selected;
        entry.Header.Background = selected ? Brushes.White : Brushes.Transparent;
        // Azul en el grupo activo; gris en la pestaña visible del otro grupo.
        entry.Header.BorderBrush = !selected ? Brushes.Transparent : entry.Group == _activeGroup ? AccentBrush : Brushes.DarkGray;
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
        RemoveEmptyGroup();
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
        if (entry != null) UpdateHeader(entry);
        if (tab == Current) UpdateChrome();
    }

    private static void UpdateHeader(TabEntry entry)
    {
        var tab = entry.Tab;
        entry.Title.Text = $"{tab.Title}{(tab.IsDirty ? "*" : "")} - {tab.CurrentDatabase ?? "(sin base)"}"
            + (tab.IsRunning ? " (ejecutando...)" : "");
        entry.Header.ToolTip = $"{tab.FilePath ?? tab.Title}\n{tab.Profile.Name}";
    }

    /// <summary>Sincroniza barra de estado, título y combo de bases de datos con la pestaña actual.</summary>
    private void UpdateChrome()
    {
        var tab = Current;

        _syncingCombo = true;
        DatabaseCombo.ItemsSource = tab != null && _databases.TryGetValue(tab.Profile, out var list) ? list : null;
        DatabaseCombo.SelectedItem = tab?.CurrentDatabase;
        DatabaseCombo.IsEnabled = tab is { IsRunning: false };
        _syncingCombo = false;

        ExecuteButton.IsEnabled = tab is { IsRunning: false };
        CancelButton.IsEnabled = tab is { IsRunning: true };
        DisconnectButton.IsEnabled = _databases.Count > 0;

        StatusText.Text = tab?.StatusText ?? (_databases.Count > 0 ? "Listo" : "Sin conexión");
        ConnectionText.Text = tab != null ? $"{tab.Profile.Name}  |  {tab.CurrentDatabase ?? "(sin base)"}" : "";
        TimeText.Text = tab?.TimeText ?? "";
        RowsText.Text = tab?.RowsText ?? "";
        Title = tab != null ? $"{tab.Title} - {tab.Profile.Name} - {App.Name}" : App.Name;
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
            NodeKind.View => ExplorerIcon.View,
            _ => ExplorerIcon.Table,
        };
        var item = new TreeViewItem { Header = ExplorerIcons.Header(icon, header), Tag = node };
        if (expandable)
            item.Items.Add(new TreeViewItem { Header = "Cargando...", Tag = Placeholder });
        item.ContextMenu = BuildMenu(item, node);
        return item;
    }

    private ContextMenu BuildMenu(TreeViewItem item, Node node)
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
                break;

            default:
                string fullName = $"{Db.QuoteId(node.Database!)}.{Db.QuoteId(node.Name!)}";
                Add("Seleccionar las primeras 1000 filas", () =>
                {
                    var tab = AddTab(node.Profile, node.Database);
                    tab.SetText($"SELECT *\nFROM {fullName}\nLIMIT 1000;\n");
                    _ = tab.ExecuteAsync();
                });
                Add("Generar script CREATE", () => _ = ScriptCreateAsync(node, fullName));
                Add("Actualizar", () => _ = RefreshAsync(item, node));
                break;
        }
        return menu;
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
                    foreach (var (name, isView) in await Db.ListTablesAsync(node.Profile, node.Database!))
                    {
                        children.Add(MakeNode(isView ? $"{name} (vista)" : name,
                            new Node(isView ? NodeKind.View : NodeKind.Table, node.Profile, node.Database, name),
                            expandable: true));
                    }
                    break;

                default:
                    foreach (string column in await Db.ListColumnsAsync(node.Profile, node.Database!, node.Name!))
                    {
                        // La descripción de la columna marca la clave primaria con ", PK".
                        var columnIcon = column.Contains(", PK") ? ExplorerIcon.KeyColumn : ExplorerIcon.Column;
                        children.Add(new TreeViewItem { Header = ExplorerIcons.Header(columnIcon, column), Foreground = Brushes.DimGray });
                    }
                    break;
            }

            item.Items.Clear();
            foreach (var child in children)
                item.Items.Add(child);
        }
        catch (Exception ex)
        {
            placeholder.Header = "Error: " + ex.Message;
            placeholder.Tag = Placeholder;
        }
    }

    private async Task ScriptCreateAsync(Node node, string fullName)
    {
        try
        {
            string script = await Db.GetCreateScriptAsync(node.Profile, node.Database!, node.Name!, node.Kind == NodeKind.View);
            AddTab(node.Profile, node.Database).SetText(script + "\n");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "No se pudo generar el script", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
