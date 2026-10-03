using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace MySmdb;

public partial class ConnectionDialog : Window
{
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34));

    public ConnectionProfile? Profile { get; private set; }

    public ConnectionDialog()
    {
        InitializeComponent();
        ReloadSaved(null);

        Loaded += (_, _) =>
        {
            if (IsSqlite) FileBox.Focus();
            else if (PasswordBox.Password.Length == 0 && SavedList.SelectedItem != null) PasswordBox.Focus();
            else HostBox.Focus();
        };
    }

    private bool IsSqlite => TypeCombo.SelectedIndex == 1;

    private ConnectionProfile? Selected => SavedList.SelectedItem as ConnectionProfile;

    // ---------- Lista de conexiones guardadas ----------

    /// <param name="selectName">Conexión a dejar seleccionada; null selecciona la más reciente.</param>
    private void ReloadSaved(string? selectName)
    {
        var saved = ProfileStore.Load();
        SavedList.ItemsSource = saved;
        EmptyHint.Visibility = saved.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var select = saved.FirstOrDefault(p => p.Name == selectName) ?? saved.FirstOrDefault();
        if (select != null)
            SavedList.SelectedItem = select;
        else
            ShowProfile(null);
    }

    private void SavedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowProfile(Selected);
        SetStatus("", null);
    }

    private void SavedList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Doble clic sobre una conexión (no sobre el hueco vacío de la lista): conectar directamente.
        if (Selected != null && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(SavedList, source) is ListBoxItem)
            Connect_Click(sender, e);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        SavedList.SelectedItem = null;
        ShowProfile(null);
        SetStatus("", null);
        NameBox.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } profile) return;
        var answer = MessageBox.Show(this, $"¿Borrar la conexión guardada \"{profile.Name}\"?", Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        ProfileStore.Delete(profile.Name);
        ReloadSaved(null);
        SetStatus($"Se borró \"{profile.Name}\".", null);
    }

    // ---------- Formulario ----------

    /// <summary>Carga una conexión guardada en el formulario; null lo deja listo para una nueva.</summary>
    private void ShowProfile(ConnectionProfile? saved)
    {
        var p = saved ?? new ConnectionProfile();
        FormTitle.Text = saved != null ? saved.Name : "Nueva conexión";
        DeleteButton.IsEnabled = saved != null;

        NameBox.Text = p.Alias ?? "";
        TypeCombo.SelectedIndex = p.Kind == DbKind.Sqlite ? 1 : 0;
        FileBox.Text = p.FilePath ?? "";
        HostBox.Text = p.Host;
        PortBox.Text = p.Port.ToString();
        UserBox.Text = p.User;
        PasswordBox.Password = p.Password;
        DatabaseBox.Text = p.Kind == DbKind.Sqlite ? "" : p.Database ?? "";
        RememberCheck.IsChecked = saved == null || p.ProtectedPassword != null;
        UpdateTypePanels();
    }

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Se dispara durante la carga del XAML, antes de que existan los demás controles.
        if (SqlitePanel == null) return;
        UpdateTypePanels();
        SetStatus("", null);
    }

    private void UpdateTypePanels()
    {
        MySqlPanel.Visibility = IsSqlite ? Visibility.Collapsed : Visibility.Visible;
        SqlitePanel.Visibility = IsSqlite ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar la base de datos SQLite",
            Filter = "Bases de datos SQLite (*.db;*.sqlite;*.sqlite3;*.db3)|*.db;*.sqlite;*.sqlite3;*.db3|Todos los archivos (*.*)|*.*",
            // Permite escribir el nombre de un archivo nuevo para crear la base.
            CheckFileExists = false,
        };
        if (dialog.ShowDialog(this) == true)
            FileBox.Text = dialog.FileName;
    }

    /// <param name="ok">true: éxito (verde); false: error (rojo); null: informativo.</param>
    private void SetStatus(string text, bool? ok)
    {
        StatusLine.Text = text;
        StatusLine.Foreground = ok switch { true => OkBrush, false => Brushes.Firebrick, _ => Brushes.Gray };
    }

    private void SetBusy(bool busy)
    {
        TestButton.IsEnabled = SaveButton.IsEnabled = ConnectButton.IsEnabled = !busy;
        NewButton.IsEnabled = SavedList.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy && Selected != null;
    }

    /// <summary>Valida el formulario; si algo falta lo indica en la línea de estado y enfoca el campo.</summary>
    private ConnectionProfile? BuildProfile()
    {
        ConnectionProfile? Invalid(string message, Control field)
        {
            SetStatus(message, false);
            field.Focus();
            return null;
        }

        string? alias = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim();

        if (IsSqlite)
        {
            string file = FileBox.Text.Trim().Trim('"');
            if (file.Length == 0)
                return Invalid("Indica el archivo de la base de datos SQLite.", FileBox);
            try
            {
                file = Path.GetFullPath(file);
            }
            catch (Exception)
            {
                return Invalid("La ruta del archivo no es válida.", FileBox);
            }
            return new ConnectionProfile { Kind = DbKind.Sqlite, Alias = alias, FilePath = file, Database = "main" };
        }

        if (string.IsNullOrWhiteSpace(HostBox.Text))
            return Invalid("Indica el servidor (por ejemplo, localhost).", HostBox);
        if (!uint.TryParse(PortBox.Text.Trim(), out uint port) || port == 0 || port > 65535)
            return Invalid("El puerto debe ser un número entre 1 y 65535.", PortBox);
        if (string.IsNullOrWhiteSpace(UserBox.Text))
            return Invalid("Indica el usuario.", UserBox);

        return new ConnectionProfile
        {
            Alias = alias,
            Host = HostBox.Text.Trim(),
            Port = port,
            User = UserBox.Text.Trim(),
            Password = PasswordBox.Password,
            Database = string.IsNullOrWhiteSpace(DatabaseBox.Text) ? null : DatabaseBox.Text.Trim(),
        };
    }

    /// <summary>Abre y cierra una conexión. Devuelve el mensaje de error, o null si funcionó.</summary>
    private static async Task<string?> TryOpenAsync(ConnectionProfile profile)
    {
        try
        {
            await using var conn = profile.CreateConnection(profile.Database);
            await conn.OpenAsync();
            if (profile.Kind == DbKind.Sqlite)
            {
                // Abrir no valida el archivo; esta consulta falla si no es una base SQLite.
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
                await cmd.ExecuteScalarAsync();
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void Store(ConnectionProfile profile)
    {
        string? previous = Selected?.Name;
        ProfileStore.Save(profile, profile.Kind == DbKind.MySql && RememberCheck.IsChecked == true, replaces: previous);
        if (previous != null)
            SessionStore.Rename(previous, profile.Name);
    }

    // ---------- Acciones ----------

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var profile = BuildProfile();
        if (profile == null) return;

        // Probar no debe crear el archivo como efecto secundario.
        if (profile.Kind == DbKind.Sqlite && !File.Exists(profile.FilePath))
        {
            SetStatus("El archivo todavía no existe; se creará una base de datos nueva al conectar.", null);
            return;
        }

        SetBusy(true);
        SetStatus("Probando la conexión...", null);
        var watch = Stopwatch.StartNew();
        string? error = await TryOpenAsync(profile);
        SetBusy(false);

        if (error == null)
            SetStatus($"✔ Conexión correcta ({watch.ElapsedMilliseconds} ms).", true);
        else
            SetStatus("✖ No se pudo conectar: " + error, false);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var profile = BuildProfile();
        if (profile == null) return;

        Store(profile);
        ReloadSaved(profile.Name);
        SetStatus($"Conexión guardada como \"{profile.Name}\".", true);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var profile = BuildProfile();
        if (profile == null) return;

        if (profile.Kind == DbKind.Sqlite && !File.Exists(profile.FilePath))
        {
            var answer = MessageBox.Show(this, $"El archivo no existe:\n{profile.FilePath}\n\n¿Crear una base de datos nueva?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        SetBusy(true);
        SetStatus("Conectando...", null);
        string? error = await TryOpenAsync(profile);
        SetBusy(false);

        if (error != null)
        {
            SetStatus("✖ No se pudo conectar: " + error, false);
            return;
        }

        Store(profile);
        Profile = profile;
        DialogResult = true;
    }
}
