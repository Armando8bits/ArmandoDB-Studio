namespace MySmdb.Tests;

/// <summary>División de un script en lo que se envía al servidor: sentencias (MySQL, SQLite) o lotes GO (SQL Server, Sybase).</summary>
public class SqlSplitterTests
{
    private static string[] Texts(string sql, DbKind kind) => SqlSplitter.Split(sql, kind).Select(s => s.Text).ToArray();

    // ---------- MySQL ----------

    [Fact]
    public void MySql_separa_por_punto_y_coma()
    {
        Assert.Equal(new[] { "SELECT 1", "SELECT 2" }, Texts("SELECT 1; SELECT 2;", DbKind.MySql));
    }

    [Fact]
    public void MySql_la_ultima_sentencia_no_necesita_punto_y_coma()
    {
        Assert.Equal(new[] { "SELECT 1", "SELECT 2" }, Texts("SELECT 1;\nSELECT 2", DbKind.MySql));
    }

    [Fact]
    public void MySql_un_punto_y_coma_dentro_de_una_cadena_no_separa()
    {
        Assert.Equal(new[] { "SELECT 'a;b'", "SELECT \"c;d\"", "SELECT `e;f`" }, Texts("SELECT 'a;b'; SELECT \"c;d\"; SELECT `e;f`;", DbKind.MySql));
    }

    [Fact]
    public void MySql_la_barra_invertida_escapa_la_comilla()
    {
        Assert.Equal(new[] { @"SELECT 'it\'s; ok'", "SELECT 2" }, Texts(@"SELECT 'it\'s; ok'; SELECT 2;", DbKind.MySql));
    }

    [Theory]
    [InlineData("#SELECT 1;")]
    [InlineData("-- SELECT 1;")]
    [InlineData("/* SELECT 1; */")]
    [InlineData("   \n\t\n")]
    [InlineData("")]
    public void MySql_un_script_solo_con_comentarios_o_vacio_no_tiene_sentencias(string sql)
    {
        Assert.Empty(SqlSplitter.Split(sql, DbKind.MySql));
    }

    [Fact]
    public void MySql_los_comentarios_previos_no_forman_parte_de_la_sentencia()
    {
        var statements = SqlSplitter.Split("# nota\n-- otra\n/* bloque */\nSELECT 1;", DbKind.MySql);
        Assert.Equal("SELECT 1", Assert.Single(statements).Text);
    }

    [Fact]
    public void MySql_dos_guiones_sin_espacio_no_son_comentario()
    {
        // "5--3" es 5 - (-3): MySQL exige un espacio tras "--" para que sea comentario.
        Assert.Equal(new[] { "SELECT 5--3", "SELECT 2" }, Texts("SELECT 5--3;\nSELECT 2;", DbKind.MySql));
    }

    [Fact]
    public void MySql_el_comentario_ejecutable_cuenta_como_sentencia()
    {
        Assert.Equal(new[] { "/*!40101 SET NAMES utf8 */" }, Texts("/*!40101 SET NAMES utf8 */;", DbKind.MySql));
    }

    [Fact]
    public void MySql_DELIMITER_permite_punto_y_coma_dentro_de_un_procedimiento()
    {
        var statements = SqlSplitter.Split(
            "SELECT 1;\nDELIMITER $$\nCREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\n  SELECT 2;\nEND$$\nDELIMITER ;\nSELECT 3;", DbKind.MySql);

        Assert.Equal(3, statements.Count);
        Assert.Equal("SELECT 1", statements[0].Text);
        Assert.StartsWith("CREATE PROCEDURE p()", statements[1].Text);
        Assert.EndsWith("END", statements[1].Text);
        Assert.Contains("SELECT 2;", statements[1].Text);
        Assert.Equal("SELECT 3", statements[2].Text);
    }

    [Fact]
    public void MySql_cada_sentencia_recuerda_su_linea()
    {
        var statements = SqlSplitter.Split("SELECT 1;\n\n-- nota\nSELECT 2;\n  SELECT\n  3;", DbKind.MySql);
        Assert.Equal(new[] { 1, 4, 5 }, statements.Select(s => s.Line).ToArray());
    }

    // ---------- SQLite ----------

    [Fact]
    public void Sqlite_la_almohadilla_no_es_comentario()
    {
        Assert.Equal(new[] { "SELECT '#' AS a" }, Texts("SELECT '#' AS a;", DbKind.Sqlite));
    }

    [Fact]
    public void Sqlite_un_trigger_con_punto_y_coma_dentro_es_una_sola_sentencia()
    {
        var statements = SqlSplitter.Split(
            "CREATE TRIGGER tr AFTER INSERT ON t BEGIN\n  UPDATE t SET n = 1;\n  DELETE FROM u;\nEND;\nSELECT 1;", DbKind.Sqlite);

        Assert.Equal(2, statements.Count);
        Assert.StartsWith("CREATE TRIGGER tr", statements[0].Text);
        Assert.EndsWith("END", statements[0].Text);
        Assert.Equal("SELECT 1", statements[1].Text);
    }

    [Fact]
    public void Sqlite_GO_no_es_un_separador()
    {
        Assert.Equal(new[] { "SELECT 1", "SELECT 2" }, Texts("SELECT 1; SELECT 2;", DbKind.Sqlite));
        Assert.Single(SqlSplitter.Split("SELECT 1\nGO\nSELECT 2", DbKind.Sqlite));
    }

    // ---------- Transact-SQL: lotes ----------

    [Theory]
    [InlineData(DbKind.SqlServer)]
    [InlineData(DbKind.Sybase)]
    public void TSql_GO_separa_lotes_y_el_punto_y_coma_no(DbKind kind)
    {
        Assert.Equal(new[] { "SELECT 1;\nSELECT 2", "SELECT 3" }, Texts("SELECT 1;\nSELECT 2\nGO\nSELECT 3\nGO\n", kind));
    }

    [Fact]
    public void TSql_el_lote_conserva_variables_y_sentencias_juntas()
    {
        var batch = Assert.Single(SqlSplitter.Split("declare @n int\nset @n = 2\nselect @n", DbKind.SqlServer));
        Assert.Equal("declare @n int\nset @n = 2\nselect @n", batch.Text);
        Assert.Equal(1, batch.Line);
    }

    [Theory]
    [InlineData("go")]
    [InlineData("Go")]
    [InlineData("   GO   ")]
    [InlineData("GO -- fin del lote")]
    public void TSql_GO_vale_en_cualquier_caja_con_espacios_o_comentario(string separator)
    {
        Assert.Equal(new[] { "SELECT 1", "SELECT 2" }, Texts($"SELECT 1\n{separator}\nSELECT 2", DbKind.SqlServer));
    }

    [Fact]
    public void TSql_GO_dentro_de_una_cadena_o_de_un_comentario_no_separa()
    {
        Assert.Single(SqlSplitter.Split("SELECT 'uno\nGO\ndos'", DbKind.SqlServer));
        Assert.Single(SqlSplitter.Split("SELECT 1\n/*\nGO\n*/\nSELECT 2", DbKind.SqlServer));
    }

    [Fact]
    public void TSql_GO_como_parte_de_otra_palabra_no_separa()
    {
        Assert.Single(SqlSplitter.Split("SELECT gobierno FROM t\nGOTO fin", DbKind.SqlServer));
    }

    [Fact]
    public void TSql_GO_con_numero_repite_el_lote()
    {
        var batches = SqlSplitter.Split("PRINT 'x'\nGO 3", DbKind.SqlServer);
        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Equal("PRINT 'x'", b.Text));
    }

    [Fact]
    public void TSql_un_lote_vacio_o_solo_con_comentarios_no_se_envia()
    {
        Assert.Equal(new[] { "SELECT 1" }, Texts("-- cabecera\nGO\n\nGO\n/* nada */\nGO\nSELECT 1\nGO", DbKind.SqlServer));
    }

    [Fact]
    public void TSql_cada_lote_recuerda_su_primera_linea_con_contenido()
    {
        var batches = SqlSplitter.Split("SELECT 1\nGO\n\n\nSELECT 2\nGO", DbKind.SqlServer);
        Assert.Equal(new[] { 1, 5 }, batches.Select(b => b.Line).ToArray());
    }

    // ---------- Transact-SQL: sentencias de un lote, para la revisión de seguridad ----------

    [Fact]
    public void Revision_TSql_separa_las_sentencias_aunque_no_lleven_punto_y_coma()
    {
        var batch = new SqlStatement("select 1\nupdate cuenta set saldo = 0\nselect * from cuenta where id = 1", 10);
        var statements = SqlSplitter.SplitTSqlForReview(batch);

        Assert.Equal(new[] { "select 1", "update cuenta set saldo = 0", "select * from cuenta where id = 1" }, statements.Select(s => s.Text).ToArray());
        Assert.Equal(new[] { 10, 11, 12 }, statements.Select(s => s.Line).ToArray());
    }

    [Fact]
    public void Revision_TSql_una_sentencia_de_varias_lineas_sigue_entera()
    {
        var statements = SqlSplitter.SplitTSqlForReview(new SqlStatement("update c\nset saldo = 0\nfrom cuenta c\nwhere c.id = 1", 1));
        Assert.Equal("update c\nset saldo = 0\nfrom cuenta c\nwhere c.id = 1", Assert.Single(statements).Text);
    }

    [Fact]
    public void Revision_TSql_localiza_el_EXEC_tras_las_declaraciones()
    {
        var statements = SqlSplitter.SplitTSqlForReview(new SqlStatement("declare @rc int\n\nexec @rc = base..proc\n    @a = 1\n\nselect @rc", 1));
        var exec = Assert.Single(statements, s => s.Text.StartsWith("exec", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, exec.Line);
    }
}
