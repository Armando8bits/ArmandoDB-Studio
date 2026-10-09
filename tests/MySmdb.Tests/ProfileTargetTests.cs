using System.Text.Json;

namespace MySmdb.Tests;

/// <summary>Editar una conexión que ya está abierta: ¿sigue apuntando al mismo sitio?</summary>
public class ProfileTargetTests
{
    private static ConnectionProfile MySql() => new()
    {
        Kind = DbKind.MySql, Host = "db.local", Port = 3306, User = "app", Password = "secreto", Alias = "Tienda",
    };

    [Fact]
    public void Contrasena_alias_y_produccion_no_cambian_el_destino()
    {
        var other = MySql();
        other.Password = "otra";
        other.Alias = "Otro nombre";
        other.IsProduction = true;
        other.Database = "ventas";
        Assert.True(MySql().SameTarget(other));
    }

    [Fact]
    public void El_nombre_del_servidor_no_distingue_mayusculas()
    {
        var other = MySql();
        other.Host = "DB.LOCAL";
        Assert.True(MySql().SameTarget(other));
    }

    [Fact]
    public void Servidor_puerto_usuario_o_motor_distintos_son_otro_destino()
    {
        var host = MySql(); host.Host = "db2.local";
        var port = MySql(); port.Port = 3307;
        var user = MySql(); user.User = "root";
        var kind = MySql(); kind.Kind = DbKind.SqlServer;
        var windows = MySql(); windows.IntegratedSecurity = true;
        Assert.All(new[] { host, port, user, kind, windows }, other => Assert.False(MySql().SameTarget(other)));
    }

    [Fact]
    public void El_tunel_SSH_forma_parte_del_destino()
    {
        var direct = MySql();
        var tunnel = MySql(); tunnel.UseSsh = true; tunnel.SshHost = "salto"; tunnel.SshUser = "ops";
        var otherTunnel = MySql(); otherTunnel.UseSsh = true; otherTunnel.SshHost = "salto2"; otherTunnel.SshUser = "ops";
        var sameTunnel = MySql(); sameTunnel.UseSsh = true; sameTunnel.SshHost = "SALTO"; sameTunnel.SshUser = "ops"; sameTunnel.SshPassword = "x";

        Assert.False(direct.SameTarget(tunnel));
        Assert.False(tunnel.SameTarget(otherTunnel));
        Assert.True(tunnel.SameTarget(sameTunnel));

        // Sin túnel, los datos SSH que hayan quedado escritos no cuentan.
        var leftovers = MySql(); leftovers.SshHost = "salto";
        Assert.True(direct.SameTarget(leftovers));
    }

    [Fact]
    public void En_SQLite_el_destino_es_el_archivo()
    {
        var a = new ConnectionProfile { Kind = DbKind.Sqlite, FilePath = @"C:\datos\a.db" };
        var same = new ConnectionProfile { Kind = DbKind.Sqlite, FilePath = @"c:\DATOS\a.db" };
        var b = new ConnectionProfile { Kind = DbKind.Sqlite, FilePath = @"C:\datos\b.db" };
        Assert.True(a.SameTarget(same));
        Assert.False(a.SameTarget(b));
    }

    [Fact]
    public void CopyFrom_copia_todos_los_datos_incluidas_las_contrasenas()
    {
        var source = new ConnectionProfile
        {
            Kind = DbKind.SqlServer, Host = "srv", Port = 1433, User = "sa", Database = "ventas", Password = "p1", ProtectedPassword = "pp",
            Alias = "Ventas", IsProduction = true, IntegratedSecurity = true, FilePath = "x",
            UseSsh = true, SshHost = "salto", SshPort = 2222, SshUser = "ops", SshKeyFile = "clave", SshHostKey = "huella",
            ProtectedSshPassword = "a", ProtectedSshPassphrase = "b", SshPassword = "p2", SshPassphrase = "p3",
        };
        var target = MySql();

        target.CopyFrom(source);

        // Todo lo que se guarda en disco, de una vez; y aparte lo que no se guarda (las contraseñas en claro).
        Assert.Equal(JsonSerializer.Serialize(source), JsonSerializer.Serialize(target));
        Assert.Equal(("p1", "p2", "p3"), (target.Password, target.SshPassword, target.SshPassphrase));
        Assert.True(target.SameTarget(source));
    }
}
