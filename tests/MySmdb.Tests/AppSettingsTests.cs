namespace MySmdb.Tests;

/// <summary>Lista de "Abrir reciente".</summary>
public class RecentFilesTests
{
    [Fact]
    public void El_ultimo_archivo_va_primero()
    {
        var settings = new AppSettings();
        settings.AddRecentFile(@"C:\sql\a.sql");
        settings.AddRecentFile(@"C:\sql\b.sql");
        Assert.Equal(new[] { @"C:\sql\b.sql", @"C:\sql\a.sql" }, settings.RecentFiles);
    }

    [Fact]
    public void Un_archivo_repetido_sube_al_principio_sin_duplicarse()
    {
        var settings = new AppSettings();
        settings.AddRecentFile(@"C:\sql\a.sql");
        settings.AddRecentFile(@"C:\sql\b.sql");
        settings.AddRecentFile(@"c:\SQL\A.sql");   // en Windows es el mismo archivo
        Assert.Equal(new[] { @"c:\SQL\A.sql", @"C:\sql\b.sql" }, settings.RecentFiles);
    }

    [Fact]
    public void La_lista_no_pasa_del_tope_y_descarta_los_mas_antiguos()
    {
        var settings = new AppSettings();
        for (int i = 1; i <= AppSettings.MaxRecentFiles + 5; i++) settings.AddRecentFile($@"C:\sql\{i}.sql");

        Assert.Equal(AppSettings.MaxRecentFiles, settings.RecentFiles.Count);
        Assert.Equal($@"C:\sql\{AppSettings.MaxRecentFiles + 5}.sql", settings.RecentFiles[0]);
        Assert.Equal(@"C:\sql\6.sql", settings.RecentFiles[^1]);
    }

    [Fact]
    public void Quitar_un_archivo_no_distingue_mayusculas()
    {
        var settings = new AppSettings();
        settings.AddRecentFile(@"C:\sql\a.sql");
        settings.AddRecentFile(@"C:\sql\b.sql");
        settings.RemoveRecentFile(@"C:\SQL\B.SQL");
        Assert.Equal(new[] { @"C:\sql\a.sql" }, settings.RecentFiles);
    }
}
