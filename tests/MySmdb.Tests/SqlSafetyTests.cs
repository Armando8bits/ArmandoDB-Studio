namespace MySmdb.Tests;

/// <summary>Avisos antes de ejecutar: sentencias sin WHERE, DROP/TRUNCATE y escrituras en producción.</summary>
public class SqlSafetyTests
{
    /// <summary>Los avisos que vería el usuario al ejecutar ese script, como los calcula la pestaña de consulta.</summary>
    private static List<string> Review(string script, DbKind kind = DbKind.MySql, bool production = false, bool dangerous = true, bool productionWrites = true)
    {
        var statements = SqlSplitter.Split(script, kind);
        var toReview = Db.IsTSql(kind) ? statements.SelectMany(SqlSplitter.SplitTSqlForReview).ToList() : statements;
        return SqlSafety.Review(toReview, 0, production, dangerous, productionWrites);
    }

    [Theory]
    [InlineData("UPDATE t SET a = 1;")]
    [InlineData("DELETE FROM t;")]
    [InlineData("delete from t")]
    [InlineData("  UPDATE t\n  SET a = 1")]
    public void Avisa_de_UPDATE_y_DELETE_sin_WHERE(string sql)
    {
        Assert.Contains("sin WHERE", Assert.Single(Review(sql)));
    }

    [Theory]
    [InlineData("UPDATE t SET a = 1 WHERE id = 2;")]
    [InlineData("DELETE FROM t WHERE id = 2;")]
    [InlineData("delete from t\nwhere id in (select id from u)")]
    [InlineData("SELECT * FROM t;")]
    [InlineData("INSERT INTO t VALUES (1);")]
    public void No_avisa_si_hay_WHERE_o_no_es_peligrosa(string sql)
    {
        Assert.Empty(Review(sql));
    }

    [Theory]
    [InlineData("DROP TABLE t;")]
    [InlineData("TRUNCATE TABLE t;")]
    [InlineData("drop database x")]
    public void Avisa_de_DROP_y_TRUNCATE(string sql)
    {
        Assert.Contains("irreversible", Assert.Single(Review(sql)));
    }

    [Theory]
    [InlineData("DELETE FROM cliente -- falta el where")]
    [InlineData("DELETE FROM cliente /* where id = 1 */")]
    [InlineData("UPDATE t SET nota = 'no where';")]
    [InlineData("UPDATE t SET `where` = 1;")]
    public void Un_WHERE_en_un_comentario_una_cadena_o_un_nombre_no_cuenta(string sql)
    {
        Assert.Contains("sin WHERE", Assert.Single(Review(sql)));
    }

    [Fact]
    public void Un_WHERE_real_vale_aunque_haya_cadenas_y_comentarios()
    {
        Assert.Empty(Review("UPDATE t SET a = 'x' /* nota */ WHERE id = 1; -- fin"));
        Assert.Empty(Review("delete from [t] where n = 'it''s'", DbKind.SqlServer));
    }

    [Fact]
    public void Un_DROP_mencionado_en_un_comentario_no_avisa()
    {
        Assert.Empty(Review("SELECT 1; -- drop table x\nSELECT 2;"));
    }

    [Fact]
    public void El_aviso_indica_la_linea_y_el_comienzo_de_la_sentencia()
    {
        string warning = Assert.Single(Review("SELECT 1;\n\nDELETE FROM cliente;"));
        Assert.Contains("Línea 3", warning);
        Assert.Contains("DELETE FROM cliente", warning);
    }

    [Fact]
    public void El_desplazamiento_de_linea_se_suma_cuando_se_ejecuta_una_seleccion()
    {
        var statements = SqlSplitter.Split("DELETE FROM t;", DbKind.MySql);
        Assert.Contains("Línea 41", Assert.Single(SqlSafety.Review(statements, 40, production: false)));
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1);")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 2;")]
    [InlineData("CREATE TABLE x (id INT);")]
    [InlineData("ALTER TABLE t ADD c INT;")]
    [InlineData("CALL procedimiento();")]
    public void En_produccion_avisa_de_cualquier_escritura(string sql)
    {
        Assert.Contains("modifica datos o estructura", Assert.Single(Review(sql, production: true)));
        Assert.Empty(Review(sql, production: false));
    }

    [Fact]
    public void En_produccion_las_lecturas_no_avisan()
    {
        Assert.Empty(Review("SELECT * FROM t; SHOW TABLES; EXPLAIN SELECT 1;", production: true));
    }

    [Theory]
    [InlineData(DbKind.SqlServer)]
    [InlineData(DbKind.Sybase)]
    public void En_produccion_un_EXEC_avisa_aunque_vaya_tras_declaraciones(DbKind kind)
    {
        string warning = Assert.Single(Review("declare @rc int\nexec @rc = base..proc @a = 1\nselect @rc", kind, production: true));
        Assert.Contains("Línea 2", warning);
        Assert.Empty(Review("declare @rc int\nexec @rc = base..proc @a = 1\nselect @rc", kind, production: false));
    }

    [Fact]
    public void TSql_detecta_el_UPDATE_sin_WHERE_aunque_le_siga_un_SELECT_con_WHERE()
    {
        string warning = Assert.Single(Review("select 1\nupdate cuenta set saldo = 0\nselect * from cuenta where id = 1", DbKind.SqlServer));
        Assert.Contains("Línea 2", warning);
        Assert.Contains("sin WHERE", warning);
    }

    [Fact]
    public void Las_dos_confirmaciones_se_pueden_desactivar_por_separado()
    {
        Assert.Empty(Review("DELETE FROM t;", dangerous: false));
        Assert.Empty(Review("INSERT INTO t VALUES (1);", production: true, productionWrites: false));
        // Con la de producción desactivada, la de sentencias peligrosas sigue activa.
        Assert.Single(Review("DELETE FROM t;", production: true, productionWrites: false));
    }

    [Fact]
    public void Un_aviso_por_sentencia()
    {
        Assert.Equal(3, Review("DELETE FROM a; SELECT 1; DROP TABLE b; UPDATE c SET x = 1;").Count);
    }
}
