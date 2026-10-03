using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MySmdb;

public partial class App : Application
{
    public const string Name = "ArmandoDB Studio";

    private static string? _dataFolder;

    /// <summary>
    /// Carpeta de datos del usuario (conexiones, sesiones, opciones). La primera vez se migra
    /// desde la carpeta que usaba la aplicación con su nombre anterior.
    /// </summary>
    public static string DataFolder
    {
        get
        {
            if (_dataFolder != null) return _dataFolder;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, Name);
            string legacy = Path.Combine(appData, "MySmdb");
            try
            {
                if (!Directory.Exists(folder) && Directory.Exists(legacy))
                    Directory.Move(legacy, folder);
            }
            catch
            {
                // Si no se puede mover, se sigue usando la carpeta antigua para no perder nada.
                if (!Directory.Exists(folder)) folder = legacy;
            }
            return _dataFolder = folder;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Antes de que se cree la ventana principal (StartupUri).
        Theme.Apply(Theme.Parse(AppSettings.Current.Theme));
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, $"{Name} - Error inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
