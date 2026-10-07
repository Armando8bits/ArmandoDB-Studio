using System.Text.RegularExpressions;

namespace MySmdb.Tests;

/// <summary>Formateador: solo debe cambiar espacios, saltos de línea y la caja de las palabras clave.</summary>
public class SqlFormatterTests
{
    /// <summary>El texto sin espacios ni saltos y en minúsculas: lo que el formateador no puede alterar.</summary>
    private static string Essence(string sql) => Regex.Replace(sql, @"\s+", "").ToLowerInvariant();

    [Fact]
    public void Una_clausula_por_linea_y_columnas_una_por_linea()
    {
        Assert.Equal("SELECT\n    [mi col],\n    b\nFROM [dbo].[t]\nWHERE a = 1", SqlFormatter.Format("select [mi col], b from [dbo].[t] where a=1", mysql: false));
    }

    [Theory]
    [InlineData("select a,b from t where x=1 and y=2 order by a desc")]
    [InlineData("select count(*) as n, max(x) from t group by y having count(*) > 1")]
    [InlineData("update t set a=1, b='dos' where id in (select id from u where z is null)")]
    [InlineData("insert into t (a,b) values (1,'x'),(2,'y')")]
    [InlineData("select a from t left join u on u.id=t.id inner join v using (id)")]
    [InlineData("create table x (id int primary key, nombre varchar(50) not null)")]
    public void No_cambia_nada_mas_que_espacios_y_la_caja_de_las_palabras_clave(string sql)
    {
        string formatted = SqlFormatter.Format(sql, mysql: true);
        Assert.Equal(Essence(sql), Essence(formatted));
    }

    [Theory]
    [InlineData("select a,b from t where x=1 and y=2 order by a desc")]
    [InlineData("update t set a=1, b='dos' where id in (select id from u where z is null)")]
    [InlineData("select a from t left join u on u.id=t.id")]
    public void Formatear_dos_veces_da_lo_mismo_que_una(string sql)
    {
        string once = SqlFormatter.Format(sql, mysql: true);
        Assert.Equal(once, SqlFormatter.Format(once, mysql: true));
    }

    [Fact]
    public void Las_palabras_clave_pasan_a_mayusculas_y_los_nombres_no()
    {
        string formatted = SqlFormatter.Format("select Nombre from Cliente where Activo = 1", mysql: true);
        Assert.Contains("SELECT", formatted);
        Assert.Contains("FROM Cliente", formatted);
        Assert.Contains("WHERE Activo = 1", formatted);
        Assert.Contains("Nombre", formatted);
    }

    [Theory]
    [InlineData("select 'From Where  SELECT' as texto", "'From Where  SELECT'")]
    [InlineData("select `select`, `from` from t", "`select`")]
    [InlineData("select 1 -- nota:  select from\nfrom t", "-- nota:  select from")]
    [InlineData("select /* where   and */ 1", "/* where   and */")]
    public void Cadenas_nombres_entre_comillas_y_comentarios_quedan_intactos(string sql, string intact)
    {
        Assert.Contains(intact, SqlFormatter.Format(sql, mysql: true));
    }

    [Fact]
    public void En_MySql_la_almohadilla_es_comentario_y_en_los_demas_no()
    {
        Assert.Contains("# select  from", SqlFormatter.Format("select 1 # select  from\nfrom t", mysql: true));
        // Fuera de MySQL "#tmp" es un nombre (tabla temporal): no se come el resto de la línea.
        Assert.Contains("WHERE", SqlFormatter.Format("select a from #tmp where a = 1", mysql: false));
    }

    [Theory]
    [InlineData("select 1", true)]
    [InlineData("DELIMITER $$\ncreate procedure p() begin select 1; end$$", false)]
    [InlineData("select 1\nGO\nselect 2", false)]
    [InlineData("select 1\ngo\n", false)]
    [InlineData("select gobierno from t", true)]
    public void Los_scripts_con_DELIMITER_o_GO_no_se_formatean(string sql, bool canFormat)
    {
        Assert.Equal(canFormat, SqlFormatter.CanFormat(sql));
    }
}

/// <summary>Autocompletado: qué se sugiere según lo escrito.</summary>
public class SqlCompletionTests
{
    private static SchemaInfo Schema(DbKind kind = DbKind.MySql)
    {
        var schema = new SchemaInfo { Kind = kind };
        schema.Tables["cliente"] = new() { ("id", "int"), ("nombre", "varchar(80)"), ("saldo", "decimal(10,2)") };
        schema.Tables["pedido"] = new() { ("id", "int"), ("cliente_id", "int"), ("total", "decimal(10,2)") };
        schema.Tables["order"] = new() { ("id", "int") };
        return schema;
    }

    [Fact]
    public void Sin_calificador_sugiere_tablas_palabras_clave_y_funciones()
    {
        var items = SqlCompletion.Suggestions("SELECT ", 7, null, Schema());
        Assert.Contains(items, i => i is { Kind: CompletionKind.Table, Text: "cliente" });
        Assert.Contains(items, i => i is { Kind: CompletionKind.Keyword, Text: "SELECT" });
        Assert.Contains(items, i => i is { Kind: CompletionKind.Function, Text: "COUNT" });
    }

    [Fact]
    public void Sugiere_las_columnas_de_las_tablas_que_ya_aparecen_en_la_sentencia()
    {
        const string sql = "SELECT  FROM pedido";
        var columns = SqlCompletion.Suggestions(sql, 7, null, Schema()).Where(i => i.Kind == CompletionKind.Column).Select(i => i.Text).ToList();
        Assert.Equal(new[] { "id", "cliente_id", "total" }, columns);
    }

    [Fact]
    public void Las_columnas_son_las_de_la_sentencia_actual_no_las_de_otra()
    {
        const string sql = "SELECT * FROM cliente;\nSELECT  FROM pedido;";
        var columns = SqlCompletion.Suggestions(sql, sql.IndexOf("SELECT  ", StringComparison.Ordinal) + 7, null, Schema())
            .Where(i => i.Kind == CompletionKind.Column).Select(i => i.Text).ToList();
        Assert.Contains("total", columns);
        Assert.DoesNotContain("saldo", columns);
    }

    [Theory]
    [InlineData("SELECT c. FROM cliente c", "c")]
    [InlineData("SELECT c. FROM cliente AS c", "c")]
    [InlineData("SELECT cliente. FROM cliente", "cliente")]
    [InlineData("SELECT c. FROM `tienda`.`cliente` c", "c")]
    [InlineData("SELECT c. FROM [dbo].[cliente] c", "c")]
    [InlineData("SELECT c. FROM pedido p JOIN cliente c ON c.id = p.cliente_id", "c")]
    [InlineData("SELECT c. FROM pedido p, cliente c", "c")]
    public void Tras_un_alias_o_una_tabla_sugiere_solo_sus_columnas(string sql, string qualifier)
    {
        var items = SqlCompletion.Suggestions(sql, 9, qualifier, Schema());
        Assert.Equal(new[] { "id", "nombre", "saldo" }, items.Select(i => i.Text).ToArray());
        Assert.All(items, i => Assert.Equal(CompletionKind.Column, i.Kind));
    }

    [Fact]
    public void Un_calificador_desconocido_no_sugiere_nada()
    {
        Assert.Empty(SqlCompletion.Suggestions("SELECT x. FROM cliente c", 9, "x", Schema()));
    }

    [Fact]
    public void Sin_esquema_cargado_aun_ofrece_palabras_clave()
    {
        var items = SqlCompletion.Suggestions("SEL", 3, null, null);
        Assert.Contains(items, i => i.Text == "SELECT");
        Assert.DoesNotContain(items, i => i.Kind is CompletionKind.Table or CompletionKind.Column);
    }

    [Fact]
    public void Tablas_y_alias_de_la_sentencia()
    {
        var map = SqlCompletion.TablesInStatement("SELECT * FROM cliente c JOIN pedido AS p ON p.cliente_id = c.id", 10, Schema());
        Assert.Equal("cliente", map["c"]);
        Assert.Equal("pedido", map["p"]);
        Assert.Equal("cliente", map["cliente"]);
    }

    [Fact]
    public void Una_palabra_clave_tras_la_tabla_no_se_toma_por_alias()
    {
        var map = SqlCompletion.TablesInStatement("SELECT * FROM cliente WHERE id = 1", 10, Schema());
        Assert.False(map.ContainsKey("WHERE"));
        Assert.Single(map);
    }

    [Theory]
    [InlineData("SELECT 'texto", 13, true, true)]        // dentro de una cadena sin cerrar
    [InlineData("SELECT 'texto' ", 15, true, false)]      // cadena ya cerrada
    [InlineData("SELECT 1 -- nota", 14, true, true)]      // tras un comentario de línea
    [InlineData("SELECT 1 # nota", 13, true, true)]       // '#' en MySQL
    [InlineData("SELECT 1 # nota", 13, false, false)]     // '#' fuera de MySQL no es comentario
    [InlineData("SELECT `col", 11, true, false)]          // dentro de un nombre sí se sugiere
    public void No_se_sugiere_dentro_de_cadenas_ni_de_comentarios(string line, int column, bool mysql, bool inside)
    {
        Assert.Equal(inside, SqlCompletion.InStringOrComment(line, column, mysql));
    }
}

/// <summary>Textos que la aplicación genera: fragmentos, plantillas de script y nombres entre comillas.</summary>
public class GeneratedSqlTests
{
    private static readonly ColumnInfo[] Columns =
    {
        new("id", "int", PrimaryKey: true, Nullable: false, AutoIncrement: true),
        new("nombre cliente", "varchar(80)", PrimaryKey: false, Nullable: true, AutoIncrement: false),
        new("saldo", "decimal(10,2)", PrimaryKey: false, Nullable: false, AutoIncrement: false),
    };

    [Theory]
    [InlineData(DbKind.MySql, "LIMIT 100")]
    [InlineData(DbKind.Sqlite, "LIMIT 100")]
    [InlineData(DbKind.SqlServer, "SELECT TOP 100")]
    [InlineData(DbKind.Sybase, "SELECT TOP 100")]
    public void El_fragmento_sel_limita_las_filas_como_lo_hace_cada_motor(DbKind kind, string expected)
    {
        Assert.Contains(expected, Snippets.Get("sel", kind));
    }

    [Theory]
    [InlineData(DbKind.MySql, "INT PRIMARY KEY AUTO_INCREMENT")]
    [InlineData(DbKind.Sqlite, "INTEGER PRIMARY KEY AUTOINCREMENT")]
    [InlineData(DbKind.SqlServer, "INT IDENTITY(1,1) PRIMARY KEY")]
    [InlineData(DbKind.Sybase, "NUMERIC(10,0) IDENTITY PRIMARY KEY")]
    public void El_fragmento_ct_usa_la_columna_autonumerica_de_cada_motor(DbKind kind, string expected)
    {
        Assert.Contains(expected, Snippets.Get("ct", kind));
    }

    [Fact]
    public void Todos_los_fragmentos_marcan_donde_queda_el_cursor_y_existen_en_todos_los_motores()
    {
        foreach (var (key, _) in Snippets.All)
            foreach (var kind in Enum.GetValues<DbKind>())
                Assert.Equal(1, Snippets.Get(key, kind)!.Count(c => c == '|'));
        Assert.Null(Snippets.Get("no_existe", DbKind.MySql));
        Assert.NotNull(Snippets.Get("SEL", DbKind.MySql));   // sin distinguir mayúsculas
    }

    [Theory]
    [InlineData(DbKind.MySql, "tabla", "`tabla`")]
    [InlineData(DbKind.Sqlite, "tabla", "`tabla`")]
    [InlineData(DbKind.SqlServer, "tabla", "[tabla]")]
    [InlineData(DbKind.SqlServer, "a]b", "[a]]b]")]
    [InlineData(DbKind.MySql, "a`b", "`a``b`")]
    [InlineData(DbKind.Sybase, "tabla", "tabla")]
    [InlineData(DbKind.Sybase, "mi tabla", "[mi tabla]")]
    public void Comillas_de_identificador_de_cada_motor(DbKind kind, string name, string expected)
    {
        Assert.Equal(expected, Db.QuoteId(kind, name));
    }

    [Theory]
    [InlineData(DbKind.MySql, "tienda", "pedido", "`tienda`.`pedido`")]
    [InlineData(DbKind.Sqlite, "main", "pedido", "`pedido`")]
    [InlineData(DbKind.SqlServer, "ventas", "dbo.pedido", "[dbo].[pedido]")]
    [InlineData(DbKind.SqlServer, "ventas", "pedido", "[dbo].[pedido]")]
    [InlineData(DbKind.Sybase, "ventas", "dbo.pedido", "dbo.pedido")]
    [InlineData(DbKind.Sybase, "ventas", "mi esquema.t", "[mi esquema].t")]
    public void Nombre_completo_de_una_tabla_segun_el_motor(DbKind kind, string database, string name, string expected)
    {
        Assert.Equal(expected, Db.FullName(new ConnectionProfile { Kind = kind }, database, name));
    }

    [Fact]
    public void Plantilla_SELECT_con_limite_segun_el_motor()
    {
        Assert.Equal("SELECT `id`,\n       `nombre cliente`,\n       `saldo`\nFROM `t`\nLIMIT 1000;\n", ScriptTemplates.Select(DbKind.MySql, "`t`", Columns));
        Assert.StartsWith("SELECT TOP 1000 [id],", ScriptTemplates.Select(DbKind.SqlServer, "[dbo].[t]", Columns));
        Assert.EndsWith("FROM [dbo].[t];\n", ScriptTemplates.Select(DbKind.SqlServer, "[dbo].[t]", Columns));
    }

    [Fact]
    public void Plantilla_INSERT_omite_la_columna_autonumerica()
    {
        string sql = ScriptTemplates.Insert(DbKind.MySql, "`t`", Columns);
        Assert.Contains("(`nombre cliente`, `saldo`)", sql);
        Assert.DoesNotContain("`id`", sql);
        Assert.Contains("<saldo: decimal(10,2)>", sql);
    }

    [Fact]
    public void Plantillas_UPDATE_y_DELETE_filtran_por_la_clave_primaria()
    {
        string update = ScriptTemplates.Update(DbKind.SqlServer, "[dbo].[t]", Columns);
        Assert.Contains("SET [nombre cliente] = <nombre cliente: varchar(80)>", update);
        Assert.Contains("WHERE [id] = <id: int>", update);
        Assert.DoesNotContain("SET [id]", update);

        Assert.Equal("DELETE FROM `t`\nWHERE `id` = <id: int>;\n", ScriptTemplates.Delete(DbKind.MySql, "`t`", Columns));
    }

    [Fact]
    public void Sin_clave_primaria_la_plantilla_deja_la_condicion_por_completar()
    {
        var noKey = new[] { new ColumnInfo("a", "int", false, true, false) };
        Assert.Contains("WHERE <condición>", ScriptTemplates.Delete(DbKind.MySql, "`t`", noKey));
        // Los valores a completar no son SQL válido: la plantilla no se puede ejecutar por accidente.
        Assert.Contains("<a: int>", ScriptTemplates.Update(DbKind.MySql, "`t`", noKey));
    }

    [Theory]
    [InlineData(DbKind.MySql, false)]
    [InlineData(DbKind.Sqlite, false)]
    [InlineData(DbKind.SqlServer, true)]
    [InlineData(DbKind.Sybase, true)]
    public void SqlServer_y_Sybase_comparten_dialecto(DbKind kind, bool tsql)
    {
        Assert.Equal(tsql, Db.IsTSql(kind));
    }
}
