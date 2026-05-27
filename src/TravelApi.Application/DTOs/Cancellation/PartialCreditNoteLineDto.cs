namespace TravelApi.Application.DTOs.Cancellation;

/// <summary>
/// FC1.3.F2.2 (plan tactico §FC1.3.F2.2 punto 2, 2026-05-27): una linea individual
/// de la Nota de Credito (NC) PARCIAL que se emite al ARCA cuando se cancela parte
/// de una reserva ya facturada.
///
/// <para><b>Quien arma estas lineas</b>: la etapa F2.3 toma los items de la factura
/// original, descarta los que tienen <c>IsRefundable=false</c> (cargo gestion, seguro
/// de cancelacion, anticipos no reembolsables) y reduce cantidades/totales segun cual
/// de los 8 casos de la matriz fiscal aplique. El resultado es una lista de estas lineas.</para>
///
/// <para><b>Para que sirve el <c>AlicuotaIvaId</c></b>: identifica la alicuota de IVA de la
/// linea (mismo codigo que usa ARCA: 3=0%, 4=10.5%, 5=21%, etc., igual que
/// <see cref="TravelApi.Application.DTOs.InvoiceItemDto"/>). La etapa F2.2 agrupa las
/// lineas por este id para prorratear el IVA a nivel grupo de alicuota.</para>
///
/// <para><b>Inmutable</b> (record posicional con init-only), igual que el resto de DTOs
/// de esta carpeta: el caller arma la linea y no la muta. Los tipos coinciden con
/// <see cref="TravelApi.Application.DTOs.InvoiceItemDto"/> a proposito, porque cada linea
/// termina mapeada a un <c>InvoiceItemDto</c> dentro del <c>CreateInvoiceRequest</c> de la NC.</para>
///
/// <para><b>Ejemplo pelotudo</b>: la factura original tiene un item "Hotel 3 noches" a
/// $100.000 la noche ($300.000 total, IVA 21%). El cliente cancela y solo se le acredita
/// 1 noche. La linea de la NC queda asi:
/// <list type="bullet">
///   <item><c>Description</c> = "Hotel 3 noches"</item>
///   <item><c>Quantity</c> = 1 (1 noche, no 3)</item>
///   <item><c>UnitPrice</c> = 100.000</item>
///   <item><c>Total</c> = 100.000</item>
///   <item><c>AlicuotaIvaId</c> = 5 (21%)</item>
/// </list>
/// </para>
/// </summary>
/// <param name="Description">Descripcion del item, copiada del item de la factura original.</param>
/// <param name="Quantity">Cantidad a acreditar (puede ser menor a la original si la cancelacion es parcial sobre el item).</param>
/// <param name="UnitPrice">Precio unitario (en la moneda de la factura original).</param>
/// <param name="Total">Total de la linea (en general <c>Quantity * UnitPrice</c>; el caller lo provee explicito para evitar redondeos sorpresa).</param>
/// <param name="AlicuotaIvaId">Codigo de alicuota de IVA segun ARCA (3=0%, 4=10.5%, 5=21%, etc.).</param>
public record PartialCreditNoteLineDto(
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal Total,
    int AlicuotaIvaId);
