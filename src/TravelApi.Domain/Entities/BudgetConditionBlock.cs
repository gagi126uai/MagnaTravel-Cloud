using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Categoría de servicio a la que aplica un bloque de condiciones del presupuesto (letra chica que se
/// imprime al pie del PDF: politica de cambios, equipaje, documentación, etc). Es un ENUM cerrado a
/// propósito — son las 6 categorías que el dueño definió para la obra "PDF de presupuesto"
/// (2026-08-11/12) — y no texto libre, para que no existan dos filas con el mismo significado escritas
/// distinto ("Aereo" vs "Aéreos").
/// </summary>
public enum BudgetConditionBlockKind
{
    Flights = 0,
    Hotels = 1,
    Transfers = 2,
    Packages = 3,
    Assistances = 4,

    /// <summary>Condiciones generales del presupuesto (no atadas a un tipo de servicio puntual).</summary>
    General = 5,
}

/// <summary>
/// Traduce <see cref="BudgetConditionBlockKind"/> a/desde el texto que viaja en la API y que el front
/// usa como clave de cada pestaña ("Aereos", "Hoteles", ...). Mismo patrón que
/// <see cref="ServiceGeographicScopeText"/>: el enum interno nunca sale crudo (evita que un número de
/// enum llegue a la pantalla si el front cambia el orden de las pestañas).
/// </summary>
public static class BudgetConditionBlockKindText
{
    public const string Flights = "Aereos";
    public const string Hotels = "Hoteles";
    public const string Transfers = "Traslados";
    public const string Packages = "Paquetes";
    public const string Assistances = "Asistencias";
    public const string General = "Generales";

    /// <summary>Los 6 tokens, en el orden fijo en que se muestran las pestañas del PDF.</summary>
    public static readonly string[] All = { Flights, Hotels, Transfers, Packages, Assistances, General };

    public static string ToDisplayText(BudgetConditionBlockKind kind) => kind switch
    {
        BudgetConditionBlockKind.Flights => Flights,
        BudgetConditionBlockKind.Hotels => Hotels,
        BudgetConditionBlockKind.Transfers => Transfers,
        BudgetConditionBlockKind.Packages => Packages,
        BudgetConditionBlockKind.Assistances => Assistances,
        BudgetConditionBlockKind.General => General,
        _ => General,
    };

    /// <summary>Null si el texto no coincide con ninguna de las 6 categorías conocidas.</summary>
    public static BudgetConditionBlockKind? ParseOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.Trim();
        if (string.Equals(normalized, Flights, StringComparison.OrdinalIgnoreCase)) return BudgetConditionBlockKind.Flights;
        if (string.Equals(normalized, Hotels, StringComparison.OrdinalIgnoreCase)) return BudgetConditionBlockKind.Hotels;
        if (string.Equals(normalized, Transfers, StringComparison.OrdinalIgnoreCase)) return BudgetConditionBlockKind.Transfers;
        if (string.Equals(normalized, Packages, StringComparison.OrdinalIgnoreCase)) return BudgetConditionBlockKind.Packages;
        if (string.Equals(normalized, Assistances, StringComparison.OrdinalIgnoreCase)) return BudgetConditionBlockKind.Assistances;
        if (string.Equals(normalized, General, StringComparison.OrdinalIgnoreCase)) return BudgetConditionBlockKind.General;
        return null;
    }
}

/// <summary>
/// Un bloque de texto de "condiciones del presupuesto" (letra chica del PDF) por categoría de
/// servicio. Obra "PDF de presupuesto" (2026-08-11/12), TANDA 1 — modelo backend, el PDF en sí y la
/// pantalla de edición se arman en tandas siguientes.
///
/// <para><b>Por qué una tabla (clave, texto) y no 6 columnas en <see cref="AgencySettings"/></b>
/// (criterio T-8, compatibilidad de datos): <see cref="AgencySettings"/> es una fila única de
/// configuración general; agregarle 6 columnas de texto largo la infla para un concepto que es, en
/// esencia, una LISTA de bloques editables. Con una fila por categoría el modelo queda más legible, la
/// migración no ensancha una tabla con datos existentes, y si el día de mañana se necesita un
/// historial de cambios por bloque (quién lo editó, cuándo) alcanza con agregar columnas a ESTA tabla
/// sin tocar <see cref="AgencySettings"/>.</para>
///
/// <para>No hay fila para una categoría hasta que alguien la edita por primera vez (no se
/// pre-siembra en la migración): el service de lectura devuelve las 6 categorías SIEMPRE, con texto
/// vacío para las que todavía no tienen fila — el front nunca ve "falta la categoría X".</para>
/// </summary>
public class BudgetConditionBlock
{
    public int Id { get; set; }

    /// <summary>Única por diseño (índice UNIQUE en la migración): a lo sumo una fila por categoría.</summary>
    public BudgetConditionBlockKind Kind { get; set; }

    /// <summary>
    /// Texto libre de la condición ("Tarifas sujetas a disponibilidad y cambio sin previo aviso...").
    /// Null/vacío = la agencia no cargó condiciones para esta categoría (el PDF simplemente no imprime
    /// esa sección, mismo criterio "nada obligatorio" que el resto de la obra).
    /// </summary>
    [MaxLength(4000)]
    public string? Text { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
