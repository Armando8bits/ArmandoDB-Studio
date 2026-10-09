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

    [Theory]
    [InlineData("WITH c AS (SELECT id FROM t) DELETE FROM t;")]
    [InlineData("with c as (select id from t where id > 5) delete from t")]                 // el WHERE es de la CTE
    [InlineData("WITH a AS (SELECT 1), b AS (SELECT 2 FROM a WHERE 1 = 1) UPDATE t SET x = 1;")]
    [InlineData("WITH RECURSIVE c (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c WHERE n < 9) DELETE FROM t;")]
    [InlineData("WITH `mi cte` AS (SELECT f(1, (2)) AS v) UPDATE t SET x = 1;")]
    [InlineData("WITH c AS MATERIALIZED (SELECT 1) DELETE FROM t;")]
    public void Avisa_de_UPDATE_y_DELETE_sin_WHERE_precedidos_de_WITH(string sql)
    {
        Assert.Contains("sin WHERE", Assert.Single(Review(sql)));
    }

    [Theory]
    [InlineData("WITH c AS (SELECT id FROM t) DELETE FROM t WHERE id IN (SELECT id FROM c);")]
    [InlineData("WITH c AS (SELECT id FROM t) SELECT * FROM c;")]
    [InlineData("WITH c AS (SELECT 'delete from t' AS texto) SELECT * FROM c;")]
    public void Con_WITH_no_avisa_si_la_sentencia_principal_no_es_peligrosa(string sql)
    {
        Assert.Empty(Review(sql));
    }

    [Fact]
    public void Con_WITH_en_produccion_cuenta_la_sentencia_principal()
    {
        Assert.Contains("modifica datos", Assert.Single(Review("WITH c AS (SELECT 1 AS id) INSERT INTO t SELECT id FROM c;", production: true)));
        Assert.Contains("modifica datos", Assert.Single(Review("WITH c AS (SELECT 1 AS id) DELETE FROM t WHERE id IN (SELECT id FROM c);", production: true)));
        Assert.Empty(Review("WITH c AS (SELECT 1 AS id) SELECT * FROM c;", production: true));
    }

    [Theory]
    [InlineData(";WITH c AS (SELECT id FROM t WHERE id > 5) DELETE FROM c")]
    [InlineData("WITH c AS (\n    SELECT id FROM t WHERE id > 5\n)\nDELETE FROM c")]
    public void TSql_avisa_del_DELETE_sin_WHERE_tras_un_WITH(string sql)
    {
        Assert.Contains(Review(sql, DbKind.SqlServer), w => w.Contains("sin WHERE"));
    }

    /// <summary>Lo que rechazaría el modo de línea de comandos por no ser de solo lectura.</summary>
    private static List<string> NotReadOnly(string script, DbKind kind = DbKind.MySql)
    {
        var statements = SqlSplitter.Split(script, kind);
        var toReview = Db.IsTSql(kind) ? statements.SelectMany(SqlSplitter.SplitTSqlForReview).ToList() : statements;
        return SqlSafety.ReadOnlyViolations(toReview, kind);
    }

    [Theory]
    [InlineData("SELECT * FROM t WHERE id = 1;")]
    [InlineData("select count(*) from t")]
    [InlineData("WITH c AS (SELECT id FROM t) SELECT * FROM c;")]
    [InlineData("SHOW TABLES; SHOW CREATE TABLE t; DESCRIBE t; DESC t;")]
    [InlineData("EXPLAIN SELECT * FROM t;")]
    [InlineData("USE ventas; SELECT 1;")]
    [InlineData("SELECT 'delete from t' AS texto, `update`, last_update, created_at FROM t;")]
    [InlineData("SELECT REPLACE(nombre, 'a', 'b') FROM t -- drop table t")]
    [InlineData("SELECT * FROM t /* insert into x */ WHERE nota = 'call me'")]
    public void Las_lecturas_son_de_solo_lectura(string sql)
    {
        Assert.Empty(NotReadOnly(sql));
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 2")]
    [InlineData("DELETE FROM t WHERE id = 2")]
    [InlineData("REPLACE INTO t VALUES (1)")]
    [InlineData("DROP TABLE t")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("CREATE TABLE x (id INT)")]
    [InlineData("ALTER TABLE t ADD c INT")]
    [InlineData("CALL procedimiento()")]
    [InlineData("GRANT ALL ON *.* TO 'x'@'%'")]
    [InlineData("SET GLOBAL max_connections = 1")]
    [InlineData("KILL 12")]
    [InlineData("WITH c AS (SELECT 1) DELETE FROM t")]
    [InlineData("SELECT * FROM t INTO OUTFILE '/tmp/x'")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("SELECT * FROM t LOCK IN SHARE MODE")]
    [InlineData("LOAD DATA INFILE 'x' INTO TABLE t")]
    [InlineData("PRAGMA writable_schema = 1")]
    [InlineData("algo que no se reconoce")]
    public void Lo_que_escribe_o_no_se_reconoce_no_es_de_solo_lectura(string sql)
    {
        Assert.Single(NotReadOnly(sql));
    }

    [Fact]
    public void En_un_script_se_senala_cada_sentencia_que_escribe_con_su_linea()
    {
        var found = NotReadOnly("SELECT 1;\nDELETE FROM a;\nSELECT 2;\nDROP TABLE b;");
        Assert.Equal(new[] { "Línea 2: DELETE FROM a", "Línea 4: DROP TABLE b" }, found);
    }

    [Theory]
    [InlineData("select top 10 * from cuenta where id = 1")]
    [InlineData("declare @n int\nset @n = 5\nselect @n\nprint 'listo'")]
    [InlineData("WITH c AS (\n    SELECT id FROM t\n)\nSELECT * FROM c")]
    [InlineData("if exists (select 1 from t) select 'hay' else select 'no hay'")]
    [InlineData("use ventas\nGO\nselect db_name()")]
    [InlineData("select [update], [delete] from [insert]")]
    public void TSql_las_lecturas_son_de_solo_lectura(string sql)
    {
        Assert.Empty(NotReadOnly(sql, DbKind.SqlServer));
        Assert.Empty(NotReadOnly(sql, DbKind.Sybase));
    }

    [Theory]
    [InlineData("exec sp_who")]
    [InlineData("sp_help cuenta")]                                      // ejecución implícita al empezar el lote
    [InlineData("declare @rc int\nexec @rc = base..proc @a = 1")]
    [InlineData("select name into #copia from sysobjects")]
    [InlineData("if 1 = 1 delete from cuenta")]
    [InlineData("select 1\nupdate cuenta set saldo = 0")]
    [InlineData(";WITH c AS (SELECT id FROM t) DELETE FROM c")]
    [InlineData("dbcc checkdb")]
    [InlineData("backup database ventas to disk = 'x'")]
    [InlineData("shutdown")]
    public void TSql_lo_que_escribe_o_ejecuta_no_es_de_solo_lectura(string sql)
    {
        Assert.NotEmpty(NotReadOnly(sql, DbKind.SqlServer));
        Assert.NotEmpty(NotReadOnly(sql, DbKind.Sybase));
    }

    [Fact]
    public void Un_aviso_por_sentencia()
    {
        Assert.Equal(3, Review("DELETE FROM a; SELECT 1; DROP TABLE b; UPDATE c SET x = 1;").Count);
    }
}
