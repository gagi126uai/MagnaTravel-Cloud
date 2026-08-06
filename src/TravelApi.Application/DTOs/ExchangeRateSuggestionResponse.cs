namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real") + "ayuda invisible del tipo de cambio"
/// (spec firmada 2026-08-06): respuesta de <c>GET /api/exchange-rates/suggestion</c>. SOLO conceptos
/// de negocio (regla T-5): nada de enteros de enum, nombre de proveedor, Id de fila, ni si el dato
/// es de ensayo o real — esas cosas son de auditoria interna, no de la pantalla.
/// </summary>
public class ExchangeRateSuggestionResponse
{
    /// <summary>
    /// El tipo de cambio sugerido, para precargar el casillero.
    ///
    /// <para><c>null</c> SOLO cuando <see cref="LoCompletaElSistema"/> es <c>true</c>: en ese caso no
    /// hay casillero que precargar y el numero que corresponde declarar no es plata de verdad, asi que
    /// no se manda a la pantalla (spec A3, "ni se entera el que opera el sistema").</para>
    /// </summary>
    public decimal? TipoCambio { get; set; }

    /// <summary>Fecha REAL del dato (puede ser anterior a la pedida si vino del respaldo de dias atras).</summary>
    public DateOnly Fecha { get; set; }

    /// <summary>true = la fecha del dato no es la que se pidio (fin de semana/feriado, o respaldo).
    /// El front lo usa para mostrar la leyenda de "de otra fecha" sin tener que comparar fechas el mismo.</summary>
    public bool EsDeOtraFecha { get; set; }

    /// <summary>Texto ya armado por el servidor para mostrar debajo del casillero (T-13: el front
    /// recibe el texto derivado, no lo deduce). Cadena vacia = no hay nada que mostrar.</summary>
    public string Leyenda { get; set; } = string.Empty;

    /// <summary>
    /// "Ayuda invisible" (spec A5.7): el tipo de cambio MAS ALTO que la factura admite ese dia. El front
    /// lo muestra tal cual (`En la factura entra hasta $ X.`) y lo usa para acomodar el numero al salir
    /// del casillero; JAMAS lo calcula el mismo ni le suma nada (regla T-13).
    ///
    /// <para><c>null</c> = no se conoce el techo de ese dia. En ese caso la pantalla no acomoda nada y
    /// se comporta como antes de esta obra.</para>
    /// </summary>
    public decimal? TopeDelDia { get; set; }

    /// <summary>
    /// "Ayuda invisible" (spec A3): <c>true</c> = el casillero del tipo de cambio NO se dibuja (tampoco
    /// el "≈ equivalente en pesos" del pie) y el front no manda ningun tipo de cambio al emitir: lo
    /// completa el motor solo, con el numero que el comprobante exige en ese momento.
    ///
    /// <para>El nombre es a proposito neutro: describe lo que la pantalla tiene que HACER, no en que
    /// modo esta corriendo el sistema por dentro (reglas T-5 / P-17).</para>
    /// </summary>
    public bool LoCompletaElSistema { get; set; }
}
