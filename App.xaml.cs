using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MySmdb;

public partial class App : Application
{
    public const string Name = "ArmandoDB Studio";
    public const string Author = "Roque A Ramírez";
    /// <summary>Página del repositorio (sin el ".git" final, para que el enlace abra en el navegador).</summary>
    public const string RepositoryUrl = "https://github.com/Armando8bits/ArmandoDB-Studio";

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

            // Carpeta alternativa: las pruebas automáticas la usan para no tocar los datos reales del usuario.
            string? custom = Environment.GetEnvironmentVariable("ARMANDODB_DATA");
            if (!string.IsNullOrWhiteSpace(custom)) return _dataFolder = custom;

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
        // Lo primero: a partir de aquí ningún error cierra la aplicación sin mostrarse (ver Errors).
        Errors.Install(this);
        base.OnStartup(e);
        try
        {
            // Antes de que se cree la ventana principal (StartupUri).
            Theme.Apply(Theme.Parse(AppSettings.Current.Theme));
        }
        catch (Exception ex)
        {
            // Con el tema por defecto se puede trabajar igual.
            Errors.Show(null, "No se pudo aplicar el tema", ex);
        }
        // Historial: fuera lo caducado (y todo, si se conserva solo por sesión y la anterior no cerró bien).
        QueryHistory.ApplyRetention(sessionBoundary: true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { SshTunnels.CloseAll(); } catch { }
        // Salida normal: las consultas ya se guardaron o se descartaron a propósito.
        RecoveryStore.Clear();
        QueryHistory.ApplyRetention(sessionBoundary: true);
        base.OnExit(e);
    }
}
