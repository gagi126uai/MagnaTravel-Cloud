namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): respuesta de
/// <c>GET /api/exchange-rates/suggestion</c>. SOLO conceptos de negocio (regla T-5): nada de
/// enteros de enum, nombre de proveedor, Id de fila, ni si el dato es de produccion u
/// homologacion — esas cosas son de auditoria interna, no de la pantalla.
/// </summary>
public class ExchangeRateSuggestionResponse
{
    /// <summary>El tipo de cambio sugerido.</summary>
    public decimal TipoCambio { get; set; }

    /// <summary>Fecha REAL del dato (puede ser anterior a la pedida si vino del respaldo de dias atras).</summary>
    public DateOnly Fecha { get; set; }

    /// <summary>true = la fecha del dato no es la que se pidio (fin de semana/feriado, o respaldo).
    /// El front lo usa para mostrar la leyenda de "de otra fecha" sin tener que comparar fechas el mismo.</summary>
    public bool EsDeOtraFecha { get; set; }

    /// <summary>Texto ya armado por el servidor para mostrar debajo del casillero (T-13: el front
    /// recibe el texto derivado, no lo deduce).</summary>
    public string Leyenda { get; set; } = string.Empty;
}
