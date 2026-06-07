namespace TravelApi.Application.DTOs;

/// <summary>
/// Respuesta de <c>GET /api/settings/operational-flags</c>: SOLO los feature flags de
/// comportamiento que el frontend necesita para decidir que UI montar (pestanas de reserva,
/// selector de moneda, rama de Nota de Debito, buscador del catalogo, avisos de proximos inicios).
///
/// <para><b>Por que existe</b> (bugfix 2026-06-06): el frontend leia los flags de
/// <c>GET /afip/settings</c>, pero ese endpoint (a) esta gateado por el permiso
/// <c>cobranzas.invoice</c> porque expone CUIT/punto de venta/condicion fiscal, asi que los
/// vendedores sin ese permiso recibian 403 y veian todos los flags en false; y (b) no
/// proyectaba los flags nuevos del catalogo (ADR-017). Este DTO es la fuente liviana para
/// CUALQUIER usuario autenticado.</para>
///
/// <para><b>Regla dura</b>: aca van SOLO booleanos de comportamiento. NUNCA agregar datos
/// fiscales (CUIT, punto de venta, condicion fiscal), umbrales de negocio (dias, montos) ni
/// nada sensible — para eso estan <c>GET /afip/settings</c> (permiso cobranzas.invoice) y
/// <c>GET /api/settings/operational-finance</c> (Admin). Hay un test de shape por reflection
/// que rompe si alguien agrega una propiedad que no sea bool.</para>
/// </summary>
public class OperationalFlagsResponse
{
    /// <summary>Ciclo de reserva extendido (Vendida / A liquidar). Gobierna pestanas y botonera de Reservas.</summary>
    public bool EnableSoldToSettleStates { get; set; }

    /// <summary>MVP facturar en USD (ADR-012). Gobierna el selector de moneda del modal de factura.</summary>
    public bool EnableMultiCurrencyInvoicing { get; set; }

    /// <summary>Nota de Debito por penalidad de cancelacion (ADR-013/014). Gobierna la rama de ND en el flujo de cancelacion.</summary>
    public bool EnableCancellationDebitNote { get; set; }

    /// <summary>Catalogo find-or-create desde la venta (ADR-017). Gobierna el buscador y la ficha inline del form de servicios.</summary>
    public bool EnableCatalogFindOrCreate { get; set; }

    /// <summary>Avisos "Proximos inicios" (ADR-019). Gobierna el bucket upcomingStarts de la campanita y la columna "Avisos" de la fila.</summary>
    public bool EnableServiceDeadlineAlerts { get; set; }
}
