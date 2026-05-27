namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.1, 2026-05-26, cierra RH-002):
/// value object owned de <see cref="BookingCancellation"/> que persiste el
/// resultado COMPLETO del calculo de la liquidacion fiscal de la NC parcial
/// (los montos, no solo el resumen).
///
/// <para><b>Por que existe (RH-002)</b>: en Fase 1 el detalle del calculo
/// (montos originales, penalidad, items no reintegrables, monto fiscal, etc.)
/// vivia SOLO como JSON dentro de <c>ApprovalRequest.Metadata</c>, y SOLO cuando
/// el caso pasaba por revision manual. Eso significaba que para reporting,
/// reconciliacion contable o reproceso, habia que parsear JSON de texto. Fase 2
/// promueve esos montos a columnas dedicadas para poder consultarlos con SQL
/// normal (sumas mensuales, validaciones via CHECK constraint, joins).</para>
///
/// <para><b>Doble-write invariante (RH-002)</b>: a partir de Fase 2, cada vez que
/// el flow escribe la liquidacion DEBE escribir las DOS representaciones a la vez:
/// (1) el JSON en <c>ApprovalRequest.Metadata</c> (fuente de verdad para el
/// reverse de la migracion M1) y (2) estas 10 columnas. No hay flag para saltear
/// ninguna de las dos. Si una version intermedia dejara de escribir el JSON, el
/// rollback de la migracion perderia datos sin forma de recuperarlos.</para>
///
/// <para><b>Por que es value object (mismo patron que <see cref="FiscalSnapshot"/>)</b>:
///  - No tiene identidad propia: el <see cref="BookingCancellation"/> lo posee.
///  - Se persiste como columnas con prefijo <c>FiscalLiquidation_</c> en la misma
///    tabla, sin tabla propia (configurado en <c>AppDbContext.OnModelCreating</c>
///    via <c>OwnsOne(bc =&gt; bc.FiscalLiquidation)</c>).</para>
///
/// <para><b>Nullable en el padre</b>: <c>BookingCancellation.FiscalLiquidation</c>
/// es nullable porque los BCs creados en Fase 1 (antes del deploy F2.1) tienen el
/// detalle solo en el Metadata. El backfill de la migracion M1 popula estas
/// columnas para los BCs Fase 1 que tienen un approval asociado (ver paso 5.B de
/// la migracion). Los BCs rechazados o que nunca pasaron por manual review quedan
/// con estas columnas en NULL — correcto, la liquidacion no aplica.</para>
///
/// <para><b>CRITICO — coherencia de <see cref="ComputedAt"/></b>: el CHECK SQL
/// <c>chk_BookingCancellations_fiscalliquidation_consistency</c> exige que, si
/// <see cref="ComputedAt"/> no es null, sea EXACTAMENTE igual a la columna summary
/// <c>BookingCancellation.LiquidationComputedAt</c> (sin tolerancia). Por eso, al
/// armar este VO en el service NO se debe usar un <c>DateTime.UtcNow</c> nuevo:
/// hay que copiar el mismo valor que ya quedo en <c>LiquidationComputedAt</c>.
/// En el backfill de la migracion se lee directamente de esa columna (RH3-003)
/// para evitar divergencias por serializacion de fechas en el JSON.</para>
///
/// <para><b>CHECK de suma</b>: el CHECK <c>chk_BookingCancellations_fiscalliquidation_sum</c>
/// valida (con tolerancia de 0.01) que
/// <c>FiscalAmountToCredit + NonRefundableItemsAmount + OperatorPenaltyAmount ==
/// OriginalInvoiceAmount</c>. Es la version SQL del invariante INV-FC1.3-005.</para>
/// </summary>
public class FiscalLiquidation
{
    /// <summary>Total de la factura original (sin tocar). Entrada del calculo.</summary>
    public decimal OriginalInvoiceAmount { get; set; }

    /// <summary>
    /// Monto a cancelar (input del flow). En general es igual a
    /// <see cref="OriginalInvoiceAmount"/> (cancelacion total), pero puede ser un
    /// sub-monto en cancelaciones parciales.
    /// </summary>
    public decimal CancellationAmount { get; set; }

    /// <summary>Penalidad cobrada por el operador (la retiene de lo devuelto).</summary>
    public decimal OperatorPenaltyAmount { get; set; }

    /// <summary>Suma de items con <c>IsRefundable=false</c> que NO se le devuelven al cliente.</summary>
    public decimal NonRefundableItemsAmount { get; set; }

    /// <summary>
    /// Monto que sale en la NC parcial (la parte del comprobante que pierde causa
    /// fiscal). Es el numero clave que se envia a ARCA.
    ///
    /// <para><b>Aclaracion (I4)</b>: esta propiedad es <c>decimal</c> NO nullable —
    /// nunca puede ser null por si misma. El concepto de "null = liquidacion no
    /// calculada" aplica al VO ENTERO: cuando <c>BookingCancellation.FiscalLiquidation</c>
    /// (la owned navigation) es null, TODAS sus columnas <c>FiscalLiquidation_*</c>
    /// quedan NULL en la BD, incluida <c>FiscalLiquidation_FiscalAmountToCredit</c>.
    /// Los CHECK constraints (suma + consistencia de timestamp) usan esa columna como
    /// guard: si esta NULL, el CHECK no valida (es el caso "VO no existe"). Mismo
    /// patron de "VO opcional = todas las columnas NULL" que <see cref="FiscalSnapshot"/>.</para>
    /// </summary>
    public decimal FiscalAmountToCredit { get; set; }

    /// <summary>Monto a devolver al cliente. Igual al fiscal cuando no hay split.</summary>
    public decimal AmountToRefundCustomer { get; set; }

    /// <summary>Saldo facturado vivo despues de la NC parcial (lo que queda con causa fiscal).</summary>
    public decimal FinalNetInvoiced { get; set; }

    /// <summary>Codigo de moneda ISO 4217 (ARS/USD/EUR/etc.). Default a nivel BD = 'ARS'.</summary>
    public string Currency { get; set; } = "ARS";

    /// <summary>
    /// Momento UTC en que corrio el calculator. DEBE coincidir EXACTAMENTE con
    /// <c>BookingCancellation.LiquidationComputedAt</c> (CHECK de consistencia).
    /// Null si todavia no se calculo (BCs legacy sin backfill, o VO recien creado).
    /// </summary>
    public DateTime? ComputedAt { get; set; }

    /// <summary>
    /// UserId del usuario que disparo el calculo. String libre sin FK formal a
    /// <c>AspNetUsers</c> — mismo patron que el resto del modulo: si la cuenta se
    /// elimina, el rastro fiscal sobrevive.
    /// </summary>
    public string? ComputedByUserId { get; set; }

    /// <summary>Nombre del usuario que disparo el calculo (denormalizado para auditoria).</summary>
    public string? ComputedByUserName { get; set; }
}
