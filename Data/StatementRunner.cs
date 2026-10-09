using System.Data.Common;

namespace MySmdb;

/// <summary>
/// Ejecuta una sentencia (o un lote) en una conexión abierta y recoge sus conjuntos de resultados.
/// Lo usan por igual la pestaña de consulta y el modo de línea de comandos.
/// </summary>
public static class StatementRunner
{
    /// <param name="RecordsAffected">Filas afectadas, o -1 si la sentencia no modifica filas.</param>
    /// <param name="AnyResultSet">Devolvió al menos un conjunto de resultados.</param>
    /// <param name="Truncated">Se alcanzó el tope de filas: el resto no se leyó, ni los resultados siguientes.</param>
    public sealed record Outcome(int RecordsAffected, bool AnyResultSet, bool Truncated);

    /// <param name="results">Lista a la que se añaden los conjuntos de resultados, en orden.</param>
    /// <param name="maxRows">Tope de filas por conjunto; al alcanzarlo se cancela la consulta.</param>
    /// <param name="onResult">Aviso al terminar de leer cada conjunto (para anotarlo en el registro).</param>
    public static async Task<Outcome> RunAsync(DbConnection connection, string sql, int maxRows, List<ResultSet> results,
        CancellationToken token, Action<ResultSet>? onResult = null)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        var reader = await cmd.ExecuteReaderAsync(token);

        bool anyResultSet = false, truncated = false;
        try
        {
            do
            {
                if (reader.FieldCount == 0) continue;
                anyResultSet = true;

                var columns = new string[reader.FieldCount];
                for (int i = 0; i < columns.Length; i++)
                    columns[i] = reader.GetName(i);

                var result = new ResultSet { Columns = columns, SourceTable = SingleSourceTable(reader) };
                while (await reader.ReadAsync(token))
                {
                    if (result.Rows.Count >= maxRows)
                    {
                        result.Truncated = truncated = true;
                        break;
                    }
                    var row = new object?[columns.Length];
                    for (int i = 0; i < row.Length; i++)
                        row[i] = ReadValue(reader, i);
                    result.Rows.Add(row);
                }
                results.Add(result);
                onResult?.Invoke(result);
            } while (!truncated && await reader.NextResultAsync(token));

            return new Outcome(truncated ? -1 : reader.RecordsAffected, anyResultSet, truncated);
        }
        finally
        {
            if (truncated)
            {
                // Sin cancelar, cerrar el lector seguiría trayendo del servidor todas las filas que faltan.
                // Tras cancelar, el propio cierre puede quejarse de que la consulta se interrumpió: es lo esperado.
                try { cmd.Cancel(); } catch { }
                try { await reader.DisposeAsync(); } catch { }
            }
            else
            {
                await reader.DisposeAsync();
            }
        }
    }

    /// <summary>Si todas las columnas del resultado vienen de la misma tabla, su nombre; si no (JOIN, cálculos), null.</summary>
    public static string? SingleSourceTable(DbDataReader reader)
    {
        try
        {
            var tables = reader.GetColumnSchema().Select(c => c.BaseTableName).Distinct().ToList();
            return tables.Count == 1 && !string.IsNullOrEmpty(tables[0]) ? tables[0] : null;
        }
        catch
        {
            return null;
        }
    }

    public static object? ReadValue(DbDataReader reader, int index)
    {
        try
        {
            return reader.IsDBNull(index) ? null : reader.GetValue(index);
        }
        catch
        {
            // Valores que .NET no puede representar (p. ej. fechas fuera de rango).
            try { return reader.GetString(index); }
            catch { return "<valor ilegible>"; }
        }
    }
}
