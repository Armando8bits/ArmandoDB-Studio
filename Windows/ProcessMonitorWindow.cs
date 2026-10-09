using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace MySmdb;

/// <summary>Una fila de SHOW FULL PROCESSLIST.</summary>
public sealed record ProcessRow(long Id, string User, string Host, string Database, string Command, long Time, string State, string Info);

/// <summary>
/// Monitor de procesos de MySQL: SHOW FULL PROCESSLIST, actualizado cada 2 segundos, con opción de cancelar
/// la consulta de un proceso (KILL QUERY) o cerrar su conexión (KILL).
/// </summary>
public class ProcessMonitorWindow : Window
{
    private readonly ConnectionProfile _profile;
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Single,
        SelectionUnit = DataGridSelectionUnit.FullRow,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        BorderThickness = new Thickness(0),
        CanUserAddRows = false,
    };
    private readonly CheckBox _auto = new() { Content = "Actualizar cada 2 s", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _hideSleep = new() { Content = "Ocultar conexiones inactivas (Sleep)", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _refreshing;

    public ProcessMonitorWindow(Window owner, ConnectionProfile profile)
    {
        _profile = profile;
        Owner = owner;
        Title = $"Monitor de procesos — {profile.Name}";
        Width = 1100;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = owner.Icon;
        RollUp.Attach(this);   // minimizar la pliega a su barra de título, dentro de la aplicación

        GridStyles.ApplyCompact(_grid);
        _grid.FontSize = AppSettings.Current.GridFontSize;
        void Column(string header, string path, double width, bool fill = false) => _grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path),
            Width = fill ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
        });
        Column("Id", nameof(ProcessRow.Id), 70);
        Column("Usuario", nameof(ProcessRow.User), 110);
        Column("Host", nameof(ProcessRow.Host), 150);
        Column("Base", nameof(ProcessRow.Database), 110);
        Column("Comando", nameof(ProcessRow.Command), 80);
        Column("Segundos", nameof(ProcessRow.Time), 70);
        Column("Estado", nameof(ProcessRow.State), 150);
        Column("Consulta", nameof(ProcessRow.Info), 0, fill: true);
        _grid.SelectionChanged += (_, _) => UpdateButtons();

        var refresh = new Button { Content = "Actualizar", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(16, 0, 0, 0) };
        refresh.Click += async (_, _) => await RefreshAsync();
        _killQuery = new Button { Content = "Cancelar consulta", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "KILL QUERY: detiene la consulta en curso; la conexión sigue abierta" };
        _killConnection = new Button { Content = "Cerrar conexión", Padding = new Thickness(10, 3, 10, 3),
            ToolTip = "KILL: cierra la conexión del proceso (y cancela lo que esté haciendo)" };
        _killQuery.Click += async (_, _) => await KillAsync(queryOnly: true);
        _killConnection.Click += async (_, _) => await KillAsync(queryOnly: false);

        var top = new DockPanel { Margin = new Thickness(10, 8, 10, 8), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_auto);
        left.Children.Add(_hideSleep);
        left.Children.Add(refresh);
        left.Children.Add(_status);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_killQuery);
        right.Children.Add(_killConnection);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(left);
        top.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_grid);
        Content = root;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.SecondaryText");

        _hideSleep.Click += async (_, _) => await RefreshAsync();
        _auto.Click += (_, _) => { if (_auto.IsChecked == true) _timer.Start(); else _timer.Stop(); };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
        UpdateButtons();
    }

    private readonly Button _killQuery;
    private readonly Button _killConnection;

    private ProcessRow? Selected => _grid.SelectedItem as ProcessRow;

    private void UpdateButtons() => _killQuery.IsEnabled = _killConnection.IsEnabled = Selected != null;

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var rows = await Db.QueryAsync(_profile, null, "SHOW FULL PROCESSLIST");
            var processes = rows.Select(r => new ProcessRow(
                    long.TryParse(r[0], out long id) ? id : 0, r[1] ?? "", r[2] ?? "", r[3] ?? "", r[4] ?? "",
                    long.TryParse(r[5], out long time) ? time : 0, r[6] ?? "", (r.Length > 7 ? r[7] : null) ?? ""))
                .Where(p => _hideSleep.IsChecked != true || p.Command != "Sleep")
                .OrderByDescending(p => p.Time)
                .ToList();

            long? selectedId = Selected?.Id;
            _grid.ItemsSource = processes;
            if (selectedId != null) _grid.SelectedItem = processes.FirstOrDefault(p => p.Id == selectedId);
            _status.Text = $"{processes.Count} procesos · actualizado {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _status.Text = "Error: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task KillAsync(bool queryOnly)
    {
        if (Selected is not { } process) return;
        string what = queryOnly ? "cancelar la consulta" : "cerrar la conexión";
        string where = _profile.IsProduction ? $"en PRODUCCIÓN ({_profile.Name})" : $"en {_profile.Name}";
        string info = process.Info.Length > 200 ? process.Info[..200] + "..." : process.Info;
        var answer = MessageBox.Show(this,
            $"¿Seguro que quieres {what} del proceso {process.Id} {where}?\n\nUsuario: {process.User}@{process.Host}\n" +
            $"Base: {process.Database}\nTiempo: {process.Time} s\n\n{info}",
            "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            // El Id viene del propio servidor como número: no hay riesgo de inyección.
            await Db.QueryAsync(_profile, null, $"KILL {(queryOnly ? "QUERY " : "")}{process.Id}");
            _status.Text = $"Proceso {process.Id}: {(queryOnly ? "consulta cancelada" : "conexión cerrada")}.";
        }
        catch (Exception ex)
        {
            Errors.Show(this, "No se pudo cancelar el proceso", ex);
        }
        await RefreshAsync();
    }
}
