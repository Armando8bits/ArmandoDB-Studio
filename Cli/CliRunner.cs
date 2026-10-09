using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MySmdb;

/// <summary>
/// Modo de línea de comandos (armandodb.exe): ejecuta consultas con una conexión guardada en la aplicación,
/// abriendo su túnel SSH si lo tiene, y escribe el resultado. Pensado para scripts y agentes: sin ventanas y
/// sin preguntas. Dos formas de uso:
///   - "consulta": abre, ejecuta, responde y cierra. El error va por la salida de error y el código de salida lo indica.
///   - "sesion": deja abierta la conexión (y el túnel) y responde a las consultas que van llegando por la entrada
///     estándar, hasta que se cierra. Todas las respuestas, también los errores, van por la salida estándar.
/// Todo vive aquí, en el proyecto de la aplicación, para compartir el mismo código de conexión, ejecución y
/// exportación; el ejecutable de consola solo llama a <see cref="RunAsync"/>. La referencia de uso está en CLI.md.
/// </summary>
public static class CliRunner
{
    public const int Ok = 0, UsageError = 1, ConnectionError = 2, Rejected = 3, QueryError = 4;

    /// <summary>Filas por resultado si no se indica otra cosa: un SELECT sin límite no debe inundar a quien lo lee.</summary>
    public const int DefaultMaxRows = 1000;

    /// <summary>Segundos sin recibir nada tras los que una sesión se cierra sola, si no se indica otra cosa.</summary>
    public const int DefaultIdleSeconds = 600;

    /// <summary>Variables de entorno con las contraseñas, para conexiones que no las tienen guardadas.</summary>
    public const string PasswordVariable = "ARMANDODB_PASSWORD", SshPasswordVariable = "ARMANDODB_SSH_PASSWORD";

    /// <summary>En una sesión con formato csv o tabla, la línea que cierra cada respuesta empieza así.</summary>
    public const string EndMarker = "#FIN";

    private const string Help = """
        armandodb: consultas a las conexiones guardadas en ArmandoDB Studio, desde la línea de comandos.

        USO
          armandodb conexiones [--formato json|tabla]
          armandodb consulta --conexion NOMBRE [opciones] --sql "SELECT ..."
          armandodb sesion   --conexion NOMBRE [opciones]
          armandodb ayuda | version

        OPCIONES DE "consulta" Y "sesion"
          --conexion NOMBRE       Conexión guardada en la aplicación (su nombre, como sale en "conexiones").
          --base NOMBRE           Base de datos; por defecto, la de la conexión.
          --formato json|csv|tabla   Formato de la salida (por defecto, json).
          --max-filas N           Tope de filas por resultado (por defecto, 1000; 0 = el máximo de la aplicación).
          --permitir-escritura    Sin esto solo se aceptan lecturas (SELECT, SHOW, EXPLAIN...). Nunca se permite
                                  escribir en una conexión marcada como producción.

        SOLO "consulta": abre la conexión, ejecuta, responde y cierra
          --sql "TEXTO"           La consulta. Puede ser un script con varias sentencias.
          --archivo RUTA          Lee la consulta de un archivo .sql. Sin --sql ni --archivo, se lee de la entrada estándar.

        SOLO "sesion": mantiene abierta la conexión (y su túnel SSH) y responde a cada consulta que recibe
          --inactividad SEG       Se cierra sola tras ese tiempo sin recibir nada (por defecto, 600; 0 = nunca).
          Las consultas llegan por la entrada estándar, de una de estas dos formas:
            {"sql": "SELECT ...", "id": 7}     una línea JSON por consulta ("id" es opcional y se devuelve tal cual)
            SELECT ...                         texto en una o varias líneas, terminado con una línea que diga GO
            GO
          Cada respuesta va por la salida estándar: con --formato json, una línea JSON por consulta
          ({"ok": true, ...} o {"ok": false, "error": "..."}); con csv o tabla, el resultado seguido de una
          línea que empieza por #FIN. Un error en una consulta no cierra la sesión.
          Se termina con una línea "salir", cerrando la entrada o por inactividad.

        CONTRASEÑAS
          Se usan las guardadas con la conexión. Si no lo están, se toman de las variables de entorno
          ARMANDODB_PASSWORD y ARMANDODB_SSH_PASSWORD. Nunca se piden por pantalla.

        CÓDIGOS DE SALIDA
          0 correcto · 1 uso incorrecto · 2 no se pudo conectar · 3 consulta rechazada (no es de solo lectura) · 4 error de la consulta

        EJEMPLOS
          armandodb conexiones
          armandodb consulta --conexion "Tienda" --sql "SELECT id, nombre FROM cliente LIMIT 10"
          armandodb consulta --conexion "Tienda" --base ventas --archivo informe.sql --formato csv > informe.csv
          armandodb sesion --conexion "Tienda"

        También en inglés: connections, query, session, help; --connection, --database, --file, --format,
        --max-rows, --allow-write, --idle. La referencia completa está en CLI.md, en el repositorio.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, TextReader input)
    {
        try
        {
            string command = args.Length == 0 ? "ayuda" : args[0].ToLowerInvariant();
            var options = ParseOptions(args.Skip(1).ToArray());
            switch (command)
            {
                case "ayuda" or "help" or "--help" or "-h" or "/?":
                    output.WriteLine(Help);
                    return Ok;
                case "version" or "--version":
                    output.WriteLine(typeof(CliRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                        ?? typeof(CliRunner).Assembly.GetName().Version?.ToString(3) ?? "");
                    return Ok;
                case "conexiones" or "connections":
                    return ListConnections(options, output);
                case "consulta" or "query":
                    return await QueryAsync(options, output, error, input);
                case "sesion" or "sesión" or "session":
                    return await SessionAsync(options, output, error, input);
                default:
                    throw new UsageException($"Comando desconocido: {args[0]}");
            }
        }
        catch (UsageException ex)
        {
            error.WriteLine("Error: " + ex.Message);
            error.WriteLine("Escribe \"armandodb ayuda\" para ver cómo se usa.");
            return UsageError;
        }
    }

    private sealed class UsageException(string message) : Exception(message);

    // ---------- Opciones ----------

    // Nombre canónico de cada opción (en español) y sus equivalentes.
    private static readonly Dictionary<string, string> OptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--conexion"] = "conexion", ["--conexión"] = "conexion", ["--connection"] = "conexion", ["-c"] = "conexion",
        ["--base"] = "base", ["--database"] = "base", ["-d"] = "base",
        ["--sql"] = "sql", ["-q"] = "sql",
        ["--archivo"] = "archivo", ["--file"] = "archivo", ["-f"] = "archivo",
        ["--formato"] = "formato", ["--format"] = "formato",
        ["--max-filas"] = "max-filas", ["--max-rows"] = "max-filas",
        ["--inactividad"] = "inactividad", ["--idle"] = "inactividad",
        ["--permitir-escritura"] = "escritura", ["--allow-write"] = "escritura",
    };

    /// <summary>"--opcion valor" (o "--opcion=valor"); las que no llevan valor quedan como "".</summary>
    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? value = null;
            int equals = arg.StartsWith('-') ? arg.IndexOf('=') : -1;
            if (equals > 0) (arg, value) = (arg[..equals], arg[(equals + 1)..]);

            if (!OptionNames.TryGetValue(arg, out string? name)) throw new UsageException($"Opción desconocida: {arg}");
            if (name == "escritura")
            {
                options[name] = "";
                continue;
            }
            if (value == null)
            {
                if (i + 1 >= args.Length) throw new UsageException($"Falta el valor de {arg}.");
                value = args[++i];
            }
            options[name] = value;
        }
        return options;
    }

    private static string Format(Dictionary<string, string> options, params string[] allowed)
    {
        string format = options.GetValueOrDefault("formato", allowed[0]).ToLowerInvariant();
        if (format == "table") format = "tabla";
        return allowed.Contains(format) ? format
            : throw new UsageException($"Formato desconocido: {format}. Los admitidos son: {string.Join(", ", allowed)}.");
    }

    private static int Number(Dictionary<string, string> options, string name, string option, int byDefault)
    {
        if (!options.TryGetValue(name, out string? text)) return byDefault;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number >= 0 ? number
            : throw new UsageException($"{option} debe ser un número entero, 0 o mayor: {text}");
    }

    /// <summary>Con qué conexión y con qué límites se trabaja: lo común a "consulta" y "sesion".</summary>
    private sealed record Target(ConnectionProfile Profile, string? Database, int MaxRows, bool AllowWrite, string Format);

    /// <summary>Resuelve la conexión guardada y las opciones comunes. Devuelve null (ya explicado) si la conexión no existe.</summary>
    private static Target? ResolveTarget(Dictionary<string, string> options, TextWriter error)
    {
        string format = Format(options, "json", "csv", "tabla");
        if (!options.TryGetValue("conexion", out string? name) || name.Trim().Length == 0)
            throw new UsageException("Falta --conexion con el nombre de una conexión guardada.");
        int maxRows = Number(options, "max-filas", "--max-filas", DefaultMaxRows);
        if (maxRows == 0) maxRows = QueryTab.MaxRows;

        var profiles = ProfileStore.Load();
        var profile = profiles.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (profile == null)
        {
            error.WriteLine($"Error: no hay ninguna conexión guardada que se llame \"{name}\".");
            error.WriteLine(profiles.Count == 0 ? "No hay conexiones guardadas." : "Las que hay: " + string.Join(", ", profiles.Select(p => $"\"{p.Name}\"")));
            return null;
        }

        if (profile.Password.Length == 0) profile.Password = Environment.GetEnvironmentVariable(PasswordVariable) ?? "";
        if (profile.SshPassword.Length == 0) profile.SshPassword = Environment.GetEnvironmentVariable(SshPasswordVariable) ?? "";
        string? database = options.TryGetValue("base", out string? chosen) && chosen.Trim().Length > 0 ? chosen.Trim() : profile.Database;
        return new Target(profile, string.IsNullOrWhiteSpace(database) ? null : database, maxRows, options.ContainsKey("escritura"), format);
    }

    // ---------- Lo que comparten "consulta" y "sesion" ----------

    /// <summary>Resultado de ejecutar un script: sus conjuntos de resultados o el motivo por el que no se pudo.</summary>
    private sealed record Execution(List<ResultSet> Results, int Affected, string? Failure, int Exit, TimeSpan Elapsed, bool ConnectionLost = false);

    /// <summary>
    /// Solo lectura, salvo permiso explícito; y en producción, ni con permiso. Devuelve el motivo del rechazo
    /// (una línea de resumen y las sentencias que lo causan) o null si se puede ejecutar.
    /// </summary>
    private static List<string>? Rejection(Target target, List<SqlStatement> statements)
    {
        var profile = target.Profile;
        var toReview = Db.IsTSql(profile.Kind) ? statements.SelectMany(SqlSplitter.SplitTSqlForReview).ToList() : statements;
        var writes = SqlSafety.ReadOnlyViolations(toReview, profile.Kind);
        if (writes.Count == 0 || (target.AllowWrite && !profile.IsProduction)) return null;

        writes.Insert(0, profile.IsProduction
            ? $"Rechazada: \"{profile.Name}\" está marcada como producción y desde la línea de comandos solo se puede leer de ella."
            : "Rechazada: no es una consulta de solo lectura. Si de verdad quieres modificar datos, añade --permitir-escritura.");
        return writes;
    }

    /// <summary>Abre la conexión (y su túnel SSH) fuera del hilo que llama, como en la aplicación.</summary>
    private static async Task<DbConnection> OpenAsync(Target target)
    {
        var connection = await Task.Run(() => target.Profile.CreateConnection(target.Database));
        try
        {
            await Db.OpenAsync(connection);
            return connection;
        }
        catch
        {
            try { await connection.DisposeAsync(); } catch { }
            throw;
        }
    }

    private static async Task<Execution> ExecuteAsync(DbConnection connection, List<SqlStatement> statements, int maxRows)
    {
        var results = new List<ResultSet>();
        int affected = -1;
        var watch = Stopwatch.StartNew();
        foreach (var statement in statements)
        {
            try
            {
                var outcome = await StatementRunner.RunAsync(connection, statement.Text, maxRows, results, CancellationToken.None);
                if (outcome.RecordsAffected >= 0) affected = Math.Max(affected, 0) + outcome.RecordsAffected;
            }
            catch (Exception ex) when (Db.IsDatabaseError(ex) && connection.State == System.Data.ConnectionState.Open)
            {
                return new Execution(results, affected, $"Error {Db.ErrorCode(ex)}, línea {statement.Line}: {ex.Message.ReplaceLineEndings(" ")}",
                    QueryError, watch.Elapsed);
            }
            catch (Exception ex)
            {
                // La conexión se cayó a mitad (red, túnel, servidor): no es un error de la consulta.
                return new Execution(results, affected, "se perdió la conexión: " + ex.Message.ReplaceLineEndings(" "),
                    ConnectionError, watch.Elapsed, ConnectionLost: true);
            }
        }
        return new Execution(results, affected, null, Ok, watch.Elapsed);
    }

    /// <summary>Lo que ejecuta un script o un agente queda en el mismo historial que lo ejecutado desde la ventana.</summary>
    private static void Record(Target target, string sql, Execution execution) =>
        QueryHistory.Add(new HistoryEntry(DateTime.Now, target.Profile.Name, target.Database, sql.Trim(), execution.Elapsed.TotalSeconds,
            execution.Failure == null ? execution.Results.Sum(r => r.Rows.Count) : null,
            execution.Failure == null ? HistoryEntry.Ok : HistoryEntry.Failed, execution.Failure) { Source = "CLI" });

    private static string ErrorText(string failure) => failure.StartsWith("Error", StringComparison.Ordinal) ? failure : "Error: " + failure;

    // ---------- conexiones ----------

    private static int ListConnections(Dictionary<string, string> options, TextWriter output)
    {
        string format = Format(options, "tabla", "json");
        var profiles = ProfileStore.Load();
        if (format == "json")
        {
            output.WriteLine(Json(indented: true, json =>
            {
                json.WriteStartArray();
                foreach (var profile in profiles)
                {
                    json.WriteStartObject();
                    json.WriteString("nombre", profile.Name);
                    json.WriteString("motor", EngineName(profile.Kind));
                    json.WriteString("detalle", profile.Summary);
                    json.WriteString("base", profile.Database);
                    json.WriteBoolean("produccion", profile.IsProduction);
                    json.WriteBoolean("tunelSsh", profile.UseSsh);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }));
            return Ok;
        }

        if (profiles.Count == 0)
        {
            output.WriteLine("No hay conexiones guardadas. Se crean desde ArmandoDB Studio (Archivo > Conectar > Guardar).");
            return Ok;
        }
        WriteTable(output, new[] { "Nombre", "Producción", "Detalle" },
            profiles.Select(p => new object?[] { p.Name, p.IsProduction ? "sí" : "", p.Summary }).ToList());
        return Ok;
    }

    private static string EngineName(DbKind kind) => kind switch
    {
        DbKind.Sqlite => "SQLite", DbKind.SqlServer => "SQL Server", DbKind.Sybase => "Sybase ASE", _ => "MySQL",
    };

    // ---------- consulta: abre, ejecuta, responde y cierra ----------

    private static async Task<int> QueryAsync(Dictionary<string, string> options, TextWriter output, TextWriter error, TextReader input)
    {
        if (options.ContainsKey("sql") && options.ContainsKey("archivo"))
            throw new UsageException("Indica la consulta con --sql o con --archivo, no con los dos.");
        if (options.ContainsKey("inactividad")) throw new UsageException("--inactividad solo vale para \"sesion\".");
        if (ResolveTarget(options, error) is not { } target) return ConnectionError;

        string sql;
        if (options.TryGetValue("archivo", out string? file))
        {
            if (!File.Exists(file)) throw new UsageException($"No existe el archivo: {file}");
            sql = TextFiles.Read(file, out _);
        }
        else
        {
            sql = options.TryGetValue("sql", out string? text) ? text : await input.ReadToEndAsync();
        }
        // PowerShell antepone la marca de orden de bytes (BOM) al texto que pasa por una tubería: no es parte de la consulta.
        sql = sql.TrimStart('﻿');
        var statements = SqlSplitter.Split(sql, target.Profile.Kind);
        if (statements.Count == 0) throw new UsageException("No hay ninguna sentencia que ejecutar: el texto está vacío o solo contiene comentarios.");

        if (Rejection(target, statements) is { } rejection)
        {
            error.WriteLine(rejection[0]);
            foreach (string write in rejection.Skip(1)) error.WriteLine("  " + write);
            return Rejected;
        }

        Execution execution;
        try
        {
            await using var connection = await OpenAsync(target);
            execution = await ExecuteAsync(connection, statements, target.MaxRows);
        }
        catch (Exception ex)
        {
            // Túnel SSH que no abre, archivo de SQLite que no existe, servidor que no responde, contraseña incorrecta...
            error.WriteLine("Error: no se pudo conectar: " + ex.Message.ReplaceLineEndings(" "));
            return ConnectionError;
        }
        finally
        {
            try { SshTunnels.CloseAll(); } catch { }
        }

        Record(target, sql, execution);
        if (execution.Failure != null)
        {
            error.WriteLine(ErrorText(execution.Failure));
            return execution.Exit;
        }

        if (target.Format == "json")
        {
            output.WriteLine(Json(indented: true, json =>
            {
                json.WriteStartObject();
                json.WriteString("conexion", target.Profile.Name);
                json.WriteString("base", target.Database);
                WriteExecution(json, execution);
                json.WriteEndObject();
            }));
        }
        else
        {
            WriteText(output, target, execution);
            // En CSV el aviso no cabe en los datos: va por la salida de error, que no se mezcla con ellos.
            if (target.Format == "csv" && execution.Results.Any(r => r.Truncated))
                error.WriteLine($"Aviso: algún resultado se truncó a {target.MaxRows} filas. Usa --max-filas para cambiar el tope.");
        }
        return Ok;
    }

    // ---------- sesion: la conexión queda abierta y se responde a cada consulta que llega ----------

    private static async Task<int> SessionAsync(Dictionary<string, string> options, TextWriter output, TextWriter error, TextReader input)
    {
        if (options.ContainsKey("sql") || options.ContainsKey("archivo"))
            throw new UsageException("En una sesión las consultas llegan por la entrada estándar, no con --sql ni --archivo.");
        int idleSeconds = Number(options, "inactividad", "--inactividad", DefaultIdleSeconds);
        if (ResolveTarget(options, error) is not { } target) return ConnectionError;
        bool asJson = target.Format == "json";

        DbConnection? connection;
        try
        {
            connection = await OpenAsync(target);
        }
        catch (Exception ex)
        {
            error.WriteLine("Error: no se pudo conectar: " + ex.Message.ReplaceLineEndings(" "));
            try { SshTunnels.CloseAll(); } catch { }
            return ConnectionError;
        }

        // Primera línea: la sesión está lista. Quien la maneja espera a verla antes de enviar nada.
        void Notice(string state, string? reason = null)
        {
            if (asJson)
                output.WriteLine(Json(indented: false, json =>
                {
                    json.WriteStartObject();
                    json.WriteBoolean("ok", true);
                    json.WriteString("sesion", state);
                    if (reason != null) json.WriteString("motivo", reason);
                    json.WriteString("conexion", target.Profile.Name);
                    json.WriteString("base", target.Database);
                    json.WriteBoolean("soloLectura", !target.AllowWrite || target.Profile.IsProduction);
                    json.WriteEndObject();
                }));
            else
                output.WriteLine($"{EndMarker} sesion {state}{(reason != null ? $" ({reason})" : "")}: {target.Profile.Name}{(target.Database != null ? $" / {target.Database}" : "")}");
            output.Flush();
        }

        try
        {
            Notice("lista");
            var pending = new StringBuilder();
            while (true)
            {
                // Console.In lee de forma síncrona: se espera en otro hilo para poder cortar por inactividad.
                var read = Task.Run(input.ReadLine);
                if (idleSeconds > 0 && await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(idleSeconds))) != read)
                {
                    Notice("cerrada", $"{idleSeconds} s de inactividad");
                    return Ok;
                }
                string? line = await read;
                string trimmed = (line ?? "").Trim().TrimStart('﻿').Trim();

                JsonElement? id = null;
                string? sql = null;
                bool invalid = false;
                if (line == null)
                {
                    // Se cerró la entrada: lo que hubiera a medias se ejecuta, y se termina.
                    if (pending.ToString().Trim().Length > 0) sql = pending.ToString();
                }
                else if (pending.Length == 0 && trimmed.Length == 0)
                {
                    continue;
                }
                else if (pending.Length == 0 && trimmed.ToLowerInvariant() is "salir" or "exit" or "quit")
                {
                    break;
                }
                else if (pending.Length == 0 && trimmed.StartsWith('{'))
                {
                    // Una línea JSON por consulta: {"sql": "...", "id": cualquier cosa}.
                    try
                    {
                        using var request = JsonDocument.Parse(trimmed);
                        if (request.RootElement.TryGetProperty("id", out var given)) id = given.Clone();
                        sql = request.RootElement.TryGetProperty("sql", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                        invalid = sql == null;
                    }
                    catch (JsonException)
                    {
                        invalid = true;
                    }
                }
                else if (trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase))
                {
                    sql = pending.ToString();
                    pending.Clear();
                }
                else
                {
                    pending.AppendLine(line);
                    continue;
                }

                if (invalid)
                {
                    Respond(output, target, id, null, new[] { "Error: la línea no es una petición válida. Se espera {\"sql\": \"...\"} en una sola línea." }, UsageError);
                }
                else if (sql != null)
                {
                    // Si la conexión se cayó en la consulta anterior (o mientras tanto), se vuelve a abrir, con su túnel.
                    bool reconnected = false;
                    string? reconnectFailure = null;
                    if (connection == null || connection.State != System.Data.ConnectionState.Open)
                    {
                        try
                        {
                            if (connection != null) await connection.DisposeAsync();
                        }
                        catch { }
                        connection = null;
                        try
                        {
                            connection = await OpenAsync(target);
                            reconnected = true;
                        }
                        catch (Exception ex)
                        {
                            reconnectFailure = "Error: no se pudo volver a conectar: " + ex.Message.ReplaceLineEndings(" ");
                        }
                    }

                    var statements = SqlSplitter.Split(sql, target.Profile.Kind);
                    if (reconnectFailure != null)
                    {
                        Respond(output, target, id, null, new[] { reconnectFailure }, ConnectionError);
                    }
                    else if (statements.Count == 0)
                    {
                        Respond(output, target, id, null, new[] { "Error: no hay ninguna sentencia que ejecutar: el texto está vacío o solo contiene comentarios." }, UsageError);
                    }
                    else if (Rejection(target, statements) is { } rejection)
                    {
                        Respond(output, target, id, null, rejection, Rejected);
                    }
                    else
                    {
                        var execution = await ExecuteAsync(connection!, statements, target.MaxRows);
                        Record(target, sql, execution);
                        if (execution.ConnectionLost)
                        {
                            try { await connection!.DisposeAsync(); } catch { }
                            connection = null;
                        }
                        Respond(output, target, id, execution, execution.Failure == null ? null : new[] { ErrorText(execution.Failure) }, execution.Exit, reconnected);
                    }
                }

                if (line == null) break;
            }
            Notice("cerrada");
            return Ok;
        }
        finally
        {
            try
            {
                if (connection != null) await connection.DisposeAsync();
            }
            catch { }
            try { SshTunnels.CloseAll(); } catch { }
        }
    }

    /// <summary>
    /// Una respuesta de la sesión, completa y con su final reconocible: en JSON, una sola línea; en csv o tabla,
    /// el resultado (o el error) seguido de una línea que empieza por <see cref="EndMarker"/>.
    /// </summary>
    /// <param name="problem">Primera línea: el error; las demás, detalle (las sentencias rechazadas).</param>
    private static void Respond(TextWriter output, Target target, JsonElement? id, Execution? execution, IReadOnlyList<string>? problem, int code, bool reconnected = false)
    {
        if (target.Format == "json")
        {
            output.WriteLine(Json(indented: false, json =>
            {
                json.WriteStartObject();
                if (id is { } given)
                {
                    json.WritePropertyName("id");
                    given.WriteTo(json);
                }
                json.WriteBoolean("ok", problem == null);
                if (problem != null)
                {
                    json.WriteString("error", string.Join(" ", problem.Select((line, i) => i == 0 ? line : "[" + line + "]")));
                    json.WriteNumber("codigo", code);
                }
                // Tras reconectar se perdió lo que hubiera en la sesión anterior (USE, variables, tablas temporales).
                if (reconnected) json.WriteBoolean("reconectado", true);
                if (execution != null && problem == null) WriteExecution(json, execution);
                json.WriteEndObject();
            }));
        }
        else
        {
            if (problem != null)
            {
                foreach (string line in problem) output.WriteLine(line);
                output.WriteLine($"{EndMarker} error {code}");
            }
            else
            {
                WriteText(output, target, execution!);
                int rows = execution!.Results.Sum(r => r.Rows.Count);
                output.WriteLine($"{EndMarker} ok ({rows} filas, {execution.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s"
                    + (execution.Results.Any(r => r.Truncated) ? $", truncado a {target.MaxRows}" : "") + (reconnected ? ", reconectado" : "") + ")");
            }
        }
        output.Flush();
    }

    // ---------- Escritura de resultados ----------

    private static string Json(bool indented, Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, ResultExporter.JsonOptions with { Indented = indented }))
            write(json);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Duración, filas afectadas y conjuntos de resultados, dentro de un objeto JSON ya abierto.</summary>
    private static void WriteExecution(Utf8JsonWriter json, Execution execution)
    {
        json.WriteNumber("segundos", Math.Round(execution.Elapsed.TotalSeconds, 3));
        if (execution.Affected >= 0) json.WriteNumber("filasAfectadas", execution.Affected);
        else json.WriteNull("filasAfectadas");
        json.WriteStartArray("resultados");
        foreach (var result in execution.Results)
        {
            json.WriteStartObject();
            json.WriteStartArray("columnas");
            foreach (string column in result.Columns) json.WriteStringValue(column);
            json.WriteEndArray();
            json.WriteNumber("totalFilas", result.Rows.Count);
            json.WriteBoolean("truncado", result.Truncated);
            json.WritePropertyName("filas");
            ResultExporter.WriteJsonRows(json, result.Columns, result.Rows);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    /// <summary>Los resultados como CSV o como tabla de texto.</summary>
    private static void WriteText(TextWriter output, Target target, Execution execution)
    {
        if (target.Format == "csv")
        {
            for (int i = 0; i < execution.Results.Count; i++)
            {
                if (i > 0) output.WriteLine();
                ResultExporter.WriteDelimited(output, execution.Results[i].Columns, execution.Results[i].Rows, csv: true);
            }
            return;
        }

        foreach (var result in execution.Results)
        {
            WriteTable(output, result.Columns, result.Rows);
            output.WriteLine($"({result.Rows.Count} filas{(result.Truncated ? $"; truncado a {target.MaxRows}" : "")})");
            output.WriteLine();
        }
        if (execution.Affected >= 0) output.WriteLine($"{execution.Affected} filas afectadas.");
    }

    /// <summary>Tabla de texto alineada, para leerla en la terminal.</summary>
    private static void WriteTable(TextWriter output, string[] columns, IReadOnlyList<object?[]> rows)
    {
        const int MaxWidth = 60;
        static string Cell(object? value)
        {
            string text = value == null ? "NULL" : ResultExporter.Text(value).ReplaceLineEndings(" ").Replace('\t', ' ');
            return text.Length > MaxWidth ? text[..(MaxWidth - 3)] + "..." : text;
        }

        var cells = rows.Select(row => row.Select(Cell).ToArray()).ToList();
        var widths = columns.Select((name, i) => Math.Max(Cell(name).Length, cells.Count == 0 ? 0 : cells.Max(row => i < row.Length ? row[i].Length : 0))).ToArray();
        string Line(IEnumerable<string> values) => string.Join("  ", values.Select((value, i) => value.PadRight(widths[i]))).TrimEnd();

        output.WriteLine(Line(columns.Select(c => Cell(c))));
        output.WriteLine(Line(widths.Select(width => new string('-', width))));
        foreach (var row in cells) output.WriteLine(Line(row));
    }
}
