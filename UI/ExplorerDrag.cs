using System.Text.RegularExpressions;
using System.Windows;

namespace MySmdb;

/// <summary>
/// Arrastrar un elemento del explorador de objetos al editor escribe su nombre donde se suelta.
/// Aquí está lo común a los dos extremos: qué texto se arrastra y cómo reconoce el editor que viene de aquí.
/// </summary>
public static class ExplorerDrag
{
    /// <summary>Formato propio que acompaña al texto, para distinguir este arrastre de cualquier otro texto soltado.</summary>
    public const string Format = "ArmandoDBStudio.ExplorerName";

    // Nombres que se pueden escribir tal cual: letras, dígitos y _ (y un punto entre esquema y objeto).
    private static readonly Regex Plain = new(@"^[\p{L}_][\p{L}\p{Nd}_$#@]*(\.[\p{L}_][\p{L}\p{Nd}_$#@]*)?$");

    /// <summary>
    /// El nombre como hay que escribirlo en una consulta: tal cual si es un nombre normal; entre comillas del
    /// motor (`nombre` o [nombre]) si lleva espacios u otros caracteres.
    /// </summary>
    /// <param name="qualified">El nombre puede llevar esquema ("dbo.cliente"): cada parte se entrecomilla por separado.</param>
    public static string Text(DbKind kind, string name, bool qualified)
    {
        if (Plain.IsMatch(name) && (qualified || !name.Contains('.'))) return name;
        return kind switch
        {
            DbKind.SqlServer => qualified ? SqlServerCatalog.QuoteFull(name) : SqlServerCatalog.Quote(name),
            DbKind.Sybase => qualified ? SybaseCatalog.QuoteFull(name) : SybaseCatalog.Quote(name),
            _ => Db.QuoteId(kind, name),
        };
    }

    public static DataObject Data(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData(DataFormats.Text, text);
        data.SetData(Format, text);
        return data;
    }
}
