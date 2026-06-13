namespace TravelApi.Application.DTOs;

/// <summary>
/// Hallazgo auditoria ERP #9 (2026-06-13): respuesta del endpoint que sugiere los items de factura
/// armados desde los servicios CONFIRMADOS de una reserva. El modal de creacion de factura usa esto
/// para prerrellenar las lineas reales (una por servicio) en vez del unico item generico de hoy.
///
/// <para>Una factura ARCA lleva UNA moneda, asi que la respuesta trae un grupo por cada moneda presente
/// en la reserva. Si la reserva mezcla ARS y USD, el front muestra/usa el grupo de la moneda que va a
/// facturar (no mezcla monedas en un comprobante).</para>
/// </summary>
public class InvoiceSuggestedItemsResponse
{
    /// <summary>Grupos de items sugeridos, uno por moneda. Vacio si la reserva no tiene servicios confirmados.</summary>
    public List<InvoiceSuggestedItemGroupDto> Groups { get; set; } = new();
}

/// <summary>
/// Hallazgo #9: grupo de items sugeridos de UNA moneda (todos los servicios confirmados de esa moneda).
/// </summary>
public class InvoiceSuggestedItemGroupDto
{
    /// <summary>Moneda ISO del grupo ("ARS"/"USD"). El front la mapea al codigo ARCA al armar el comprobante.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Lineas sugeridas (una por servicio confirmado de esta moneda). Misma forma que <see cref="InvoiceItemDto"/>.</summary>
    public List<InvoiceItemDto> Items { get; set; } = new();

    /// <summary>Suma de los <c>Total</c> de las lineas (= venta confirmada de esta moneda), redondeada a 2 decimales.</summary>
    public decimal SuggestedTotal { get; set; }
}
