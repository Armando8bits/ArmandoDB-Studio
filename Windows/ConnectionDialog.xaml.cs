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
    public ConnectionProfile? Profile { get; private set; }

    public ConnectionDialog()
    {
        InitializeComponent();
        ReloadSaved(null);

        Loaded += (_, _) =>
        {
            if (IsSqlite) FileBox.Focus();
            else if (PasswordBox.Password.Length == 0 && SavedList.SelectedItem != null && PasswordBox.IsEnabled) PasswordBox.Focus();
            else HostBox.Focus();
        };
    }

    // Orden del desplegable de tipo: MySQL, SQLite, SQL Server.
    private bool IsSqlite => TypeCombo.SelectedIndex == 1;
    private bool IsSqlServer => TypeCombo.SelectedIndex == 2;

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
        _filling = true;
        TypeCombo.SelectedIndex = p.Kind switch { DbKind.Sqlite => 1, DbKind.SqlServer => 2, _ => 0 };
        WindowsAuthCheck.IsChecked = p.IntegratedSecurity;
        _filling = false;
        FileBox.Text = p.FilePath ?? "";
        HostBox.Text = p.Host;
        PortBox.Text = p.Port.ToString();
        UserBox.Text = p.User;
        PasswordBox.Password = p.Password;
        DatabaseBox.Text = p.Kind == DbKind.Sqlite ? "" : p.Database ?? "";
        RememberCheck.IsChecked = saved == null || p.ProtectedPassword != null;
        ProductionCheck.IsChecked = p.IsProduction;

        _filling = true;
        SshCheck.IsChecked = p.UseSsh;
        _filling = false;
        _forgetHostKey = false;
        SshHostBox.Text = p.SshHost ?? "";
        SshPortBox.Text = p.SshPort.ToString();
        SshUserBox.Text = p.SshUser ?? "";
        SshPasswordBox.Password = p.SshPassword;
        SshKeyBox.Text = p.SshKeyFile ?? "";
        SshPassphraseBox.Password = p.SshPassphrase;
        ConnectionTabs.SelectedIndex = 0;
        UpdateTypePanels();
    }

    private bool _filling;
    /// <summary>Desmarcar y volver a marcar el túnel olvida la huella guardada del servidor SSH (para aceptar una nueva).</summary>
    private bool _forgetHostKey;

    private void SshCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (SshPanel == null) return;   // durante la carga del XAML
        if (!_filling) _forgetHostKey = true;
        UpdateSshState();
    }

    /// <summary>Los campos del túnel solo se pueden editar con la casilla marcada; la pestaña indica si está activo.</summary>
    private void UpdateSshState()
    {
        bool enabled = SshCheck.IsChecked == true;
        SshPanel.IsEnabled = enabled;
        SshTab.Header = enabled ? "Túnel SSH  ✔" : "Túnel SSH";
        TunnelHint.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar la clave privada SSH",
            Filter = "Todos los archivos (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (dialog.ShowDialog(this) == true)
            SshKeyBox.Text = dialog.FileName;
    }

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Se dispara durante la carga del XAML, antes de que existan los demás controles.
        if (SqlitePanel == null) return;
        if (!_filling)
        {
            // Al cambiar de motor a mano, el puerto y el usuario por defecto pasan a los del nuevo (si no se tocaron).
            var (port, otherPort, user, otherUser) = IsSqlServer ? ("1433", "3306", "sa", "root") : ("3306", "1433", "root", "sa");
            if (PortBox.Text.Trim() == otherPort) PortBox.Text = port;
            if (UserBox.Text.Trim() == otherUser) UserBox.Text = user;
        }
        UpdateTypePanels();
        SetStatus("", null);
    }

    private void WindowsAuth_Changed(object sender, RoutedEventArgs e)
    {
        if (SqlitePanel == null) return;   // durante la carga del XAML
        UpdateTypePanels();
    }

    private void UpdateTypePanels()
    {
        MySqlPanel.Visibility = IsSqlite ? Visibility.Collapsed : Visibility.Visible;
        SqlitePanel.Visibility = IsSqlite ? Visibility.Visible : Visibility.Collapsed;

        ServerTab.Header = IsSqlServer ? "Servidor SQL Server" : "Servidor MySQL";
        WindowsAuthCheck.Visibility = IsSqlServer ? Visibility.Visible : Visibility.Collapsed;
        // Con autenticación de Windows no hay usuario ni contraseña que escribir.
        UserBox.IsEnabled = PasswordBox.IsEnabled = !(IsSqlServer && WindowsAuthCheck.IsChecked == true);
        HostBox.ToolTip = IsSqlServer
            ? "Nombre o IP del servidor. Con instancia: SERVIDOR\\INSTANCIA (el puerto no se usa). LocalDB: (localdb)\\MSSQLLocalDB"
            : "Nombre o dirección IP del servidor";
        UpdateSshState();
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
        StatusLine.SetResourceReference(TextBlock.ForegroundProperty,
            ok switch { true => "Brush.OkText", false => "Brush.ErrorText", _ => "Brush.SecondaryText" });
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
            // El campo puede estar en la otra pestaña (servidor / túnel SSH): se muestra antes de enfocarlo.
            if (!IsSqlite)
                ConnectionTabs.SelectedIndex = field == SshHostBox || field == SshPortBox || field == SshUserBox
                    || field == SshPasswordBox || field == SshKeyBox ? 1 : 0;
            Dispatcher.BeginInvoke(() => field.Focus(), System.Windows.Threading.DispatcherPriority.Input);
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
            return new ConnectionProfile
            {
                Kind = DbKind.Sqlite, Alias = alias, FilePath = file, Database = "main", IsProduction = ProductionCheck.IsChecked == true,
            };
        }

        if (string.IsNullOrWhiteSpace(HostBox.Text))
            return Invalid("Indica el servidor (por ejemplo, localhost).", HostBox);
        if (!uint.TryParse(PortBox.Text.Trim(), out uint port) || port == 0 || port > 65535)
            return Invalid("El puerto debe ser un número entre 1 y 65535.", PortBox);
        bool windowsAuth = IsSqlServer && WindowsAuthCheck.IsChecked == true;
        if (!windowsAuth && string.IsNullOrWhiteSpace(UserBox.Text))
            return Invalid("Indica el usuario.", UserBox);

        var profile = new ConnectionProfile
        {
            Kind = IsSqlServer ? DbKind.SqlServer : DbKind.MySql,
            IntegratedSecurity = windowsAuth,
            Alias = alias,
            IsProduction = ProductionCheck.IsChecked == true,
            Host = HostBox.Text.Trim(),
            Port = port,
            User = UserBox.Text.Trim(),
            Password = PasswordBox.Password,
            Database = string.IsNullOrWhiteSpace(DatabaseBox.Text) ? null : DatabaseBox.Text.Trim(),
        };

        if (SshCheck.IsChecked != true) return profile;

        if (string.IsNullOrWhiteSpace(SshHostBox.Text))
            return Invalid("Indica el servidor SSH.", SshHostBox);
        if (!uint.TryParse(SshPortBox.Text.Trim(), out uint sshPort) || sshPort == 0 || sshPort > 65535)
            return Invalid("El puerto SSH debe ser un número entre 1 y 65535.", SshPortBox);
        if (string.IsNullOrWhiteSpace(SshUserBox.Text))
            return Invalid("Indica el usuario SSH.", SshUserBox);
        string keyFile = SshKeyBox.Text.Trim().Trim('"');
        if (keyFile.Length > 0 && !File.Exists(keyFile))
            return Invalid("No se encuentra el archivo de la clave privada.", SshKeyBox);
        if (keyFile.Length == 0 && SshPasswordBox.Password.Length == 0)
            return Invalid("Indica la contraseña SSH o una clave privada.", SshPasswordBox);

        profile.UseSsh = true;
        profile.SshHost = SshHostBox.Text.Trim();
        profile.SshPort = sshPort;
        profile.SshUser = SshUserBox.Text.Trim();
        profile.SshPassword = SshPasswordBox.Password;
        profile.SshKeyFile = keyFile.Length > 0 ? keyFile : null;
        profile.SshPassphrase = SshPassphraseBox.Password;
        // Se conserva la huella ya aceptada del mismo servidor SSH, salvo que se haya pedido olvidarla.
        if (!_forgetHostKey && Selected is { UseSsh: true } saved && saved.SshHost == profile.SshHost && saved.SshPort == profile.SshPort)
            profile.SshHostKey = saved.SshHostKey;
        return profile;
    }

    /// <summary>Abre y cierra una conexión. Devuelve el mensaje de error, o null si funcionó.</summary>
    private static async Task<string?> TryOpenAsync(ConnectionProfile profile)
    {
        try
        {
            // Fuera del hilo de la interfaz: abrir el túnel SSH puede tardar unos segundos.
            await using var conn = await Task.Run(() => profile.CreateConnection(profile.Database));
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
        ProfileStore.Save(profile, profile.Kind != DbKind.Sqlite && RememberCheck.IsChecked == true, replaces: previous);
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
