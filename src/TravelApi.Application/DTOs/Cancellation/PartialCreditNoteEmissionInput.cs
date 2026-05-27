namespace TravelApi.Application.DTOs.Cancellation;

/// <summary>
/// FC1.3.F2.2 (plan tactico §FC1.3.F2.2 punto 2, 2026-05-27): lo que recibe
/// <c>IInvoiceService.EnqueuePartialCreditNoteAsync</c> para emitir una Nota de
/// Credito (NC) PARCIAL real al ARCA.
///
/// <para><b>Nota de naming</b>: el plan lo llamaba <c>FiscalLiquidationInput</c>, pero ese
/// nombre YA estaba ocupado por <see cref="TravelApi.Application.Interfaces.FiscalLiquidationInput"/>
/// (el input del calculator de Fase 1). Tener dos tipos con el mismo nombre simple en
/// namespaces distintos rompia el build de los archivos que usan ambos <c>using</c>
/// (ej. <c>FiscalLiquidationCalculator</c>). Se renombro a
/// <c>PartialCreditNoteEmissionInput</c>, que ademas describe mejor su rol: es el input de
/// EMISION de la NC, no el input generico de clasificacion del calculator.</para>
///
/// <para><b>Diferencia con <see cref="FiscalLiquidationDto"/></b>: el
/// <c>FiscalLiquidationDto</c> es el resultado que devuelve el calculator (con el caso de
/// la matriz, el kind, los motivos de revision, etc., orientado a la decision y la
/// auditoria). Este input es mas chico y esta orientado a la EMISION: trae solo lo que el
/// job necesita para armar el <c>CreateInvoiceRequest</c> de la NC (montos originales,
/// monto a acreditar, moneda, tipo de cambio y las lineas).</para>
///
/// <para><b>Por que estan los montos originales de la factura</b> (<c>OriginalNetAmount</c>,
/// <c>OriginalVatAmount</c>, <c>OriginalTotalAmount</c>): la etapa F2.2 valida de forma
/// defensiva, ANTES de tocar el ARCA, que <c>OriginalNetAmount + OriginalVatAmount ==
/// OriginalTotalAmount</c> (dentro de la tolerancia de redondeo). Si la factura origen
/// venia inconsistente, mejor rebotar aca que mandar un XML roto al ARCA.</para>
///
/// <para><b>Por que el tipo de cambio</b> (<c>ExchangeRateAtOriginalInvoice</c>): si la
/// factura original fue en USD, la NC tiene que salir con el MISMO tipo de cambio del
/// comprobante origen (T0), no con el del dia de la cancelacion. Asi la NC anula valor
/// fiscal en la misma moneda y cotizacion que la factura que esta corrigiendo.</para>
///
/// <para><b>Inmutable</b> (record posicional con init-only), igual que el resto de DTOs
/// de esta carpeta.</para>
///
/// <para><b>Ejemplo pelotudo</b>: factura B en pesos por $1.000.000 (neto $826.446 + IVA
/// $173.554). El cliente cancela y se le acredita $300.000 fiscales. El input queda:
/// <list type="bullet">
///   <item><c>OriginalNetAmount</c> = 826.446</item>
///   <item><c>OriginalVatAmount</c> = 173.554</item>
///   <item><c>OriginalTotalAmount</c> = 1.000.000</item>
///   <item><c>FiscalAmountToCredit</c> = 300.000</item>
///   <item><c>Currency</c> = "ARS"</item>
///   <item><c>ExchangeRateAtOriginalInvoice</c> = 1 (pesos)</item>
///   <item><c>Lines</c> = las lineas que suman 300.000 (neto+iva) que arma F2.3</item>
/// </list>
/// </para>
/// </summary>
/// <param name="OriginalNetAmount">Neto de la factura original (ImpNeto), en moneda original.</param>
/// <param name="OriginalVatAmount">IVA de la factura original (ImpIVA), en moneda original.</param>
/// <param name="OriginalTotalAmount">Total de la factura original (ImpTotal), en moneda original.</param>
/// <param name="FiscalAmountToCredit">Monto neto+iva a acreditar en la NC parcial, en moneda original. Debe coincidir con la suma de <c>Lines</c> dentro de la tolerancia de redondeo.</param>
/// <param name="Currency">Moneda de la factura original en formato ISO 4217 ("ARS", "USD", ...).</param>
/// <param name="ExchangeRateAtOriginalInvoice">Tipo de cambio del comprobante origen (T0). Para pesos es 1.</param>
/// <param name="Lines">Lineas individuales de la NC parcial (ver <see cref="PartialCreditNoteLineDto"/>).</param>
public record PartialCreditNoteEmissionInput(
    decimal OriginalNetAmount,
    decimal OriginalVatAmount,
    decimal OriginalTotalAmount,
    decimal FiscalAmountToCredit,
    string Currency,
    decimal ExchangeRateAtOriginalInvoice,
    IReadOnlyList<PartialCreditNoteLineDto> Lines);
