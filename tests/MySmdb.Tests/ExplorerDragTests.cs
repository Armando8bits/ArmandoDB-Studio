using System.Windows;

namespace MySmdb.Tests;

/// <summary>El texto que se escribe al arrastrar un elemento del explorador al editor.</summary>
public class ExplorerDragTests
{
    [Theory]
    [InlineData(DbKind.MySql, "cliente", "cliente")]
    [InlineData(DbKind.MySql, "pedido_2024", "pedido_2024")]
    [InlineData(DbKind.MySql, "año", "año")]
    [InlineData(DbKind.MySql, "detalle pedido", "`detalle pedido`")]
    [InlineData(DbKind.MySql, "100%", "`100%`")]
    [InlineData(DbKind.MySql, "con.punto", "`con.punto`")]            // sin esquemas, un punto es parte del nombre
    [InlineData(DbKind.Sqlite, "mi tabla", "`mi tabla`")]             // como en el resto de la aplicación: SQLite admite los acentos graves
    [InlineData(DbKind.SqlServer, "fecha alta", "[fecha alta]")]
    [InlineData(DbKind.Sybase, "saldo", "saldo")]
    public void Un_nombre_normal_va_tal_cual_y_el_resto_entre_comillas_del_motor(DbKind kind, string name, string expected)
    {
        Assert.Equal(expected, ExplorerDrag.Text(kind, name, qualified: false));
    }

    [Theory]
    [InlineData(DbKind.SqlServer, "dbo.cliente", "dbo.cliente")]
    [InlineData(DbKind.SqlServer, "ventas.detalle pedido", "[ventas].[detalle pedido]")]
    [InlineData(DbKind.Sybase, "dbo.detalle pedido", "dbo.[detalle pedido]")]
    public void Con_esquema_cada_parte_se_entrecomilla_por_separado(DbKind kind, string name, string expected)
    {
        Assert.Equal(expected, ExplorerDrag.Text(kind, name, qualified: true));
    }

    [Fact]
    public void Lo_arrastrado_es_texto_normal_con_una_marca_para_reconocerlo()
    {
        var data = ExplorerDrag.Data("cliente");
        Assert.Equal("cliente", data.GetData(DataFormats.UnicodeText));
        Assert.True(data.GetDataPresent(ExplorerDrag.Format));
        // Un texto arrastrado desde otro sitio no lleva la marca.
        Assert.False(new DataObject(DataFormats.UnicodeText, "otro").GetDataPresent(ExplorerDrag.Format));
    }
}
