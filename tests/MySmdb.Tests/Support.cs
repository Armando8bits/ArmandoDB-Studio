using System.Runtime.CompilerServices;

namespace MySmdb.Tests;

/// <summary>Preparación común: las pruebas nunca tocan los datos reales del usuario.</summary>
internal static class TestSetup
{
    /// <summary>Carpeta de datos de la aplicación durante las pruebas (conexiones, opciones, copias de recuperación).</summary>
    public static readonly string DataFolder = Path.Combine(Path.GetTempPath(), "ArmandoDBStudio.Tests", Guid.NewGuid().ToString("N"));

    // Se ejecuta al cargar el ensamblado, antes que cualquier prueba y que cualquier acceso a App.DataFolder.
    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(DataFolder);
        Environment.SetEnvironmentVariable("ARMANDODB_DATA", DataFolder);
        // Páginas de códigos (Windows-1252...) para las pruebas de codificación.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }
}

/// <summary>Carpeta temporal que se borra al terminar la prueba.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ArmandoDBStudio.Tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* un archivo aún en uso no debe hacer fallar la prueba */ }
    }
}

/// <summary>Base de datos SQLite en un archivo temporal, con utilidades para prepararla y consultarla.</summary>
public sealed class SqliteDb : IDisposable
{
    private readonly TempFolder _folder = new();

    public ConnectionProfile Profile { get; }
    public string Folder => _folder.Path;

    public SqliteDb(string name = "prueba.db")
    {
        Profile = new ConnectionProfile { Kind = DbKind.Sqlite, FilePath = _folder.File(name), Database = "main", Alias = "prueba" };
    }

    public string File(string name) => _folder.File(name);

    /// <summary>Ejecuta cada sentencia del script (separadas como lo haría la aplicación).</summary>
    public async Task RunAsync(string script)
    {
        foreach (var statement in SqlSplitter.Split(script, DbKind.Sqlite))
            await Db.QueryAsync(Profile, null, statement.Text);
    }

    public Task<List<string?[]>> QueryAsync(string sql) => Db.QueryAsync(Profile, null, sql);

    public async Task<string?> ScalarAsync(string sql) => (await QueryAsync(sql))[0][0];

    public void Dispose() => _folder.Dispose();
}

/// <summary>Avances que no se muestran en ninguna parte.</summary>
internal static class NoProgress
{
    public static readonly IProgress<BackupProgress> Backup = new Progress<BackupProgress>();
    public static readonly IProgress<int> Rows = new Progress<int>();
}
