using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1 (ADR-002 §2.3 / §2.7, 2026-05-13): value object inmutable que captura
/// la "foto fiscal" del momento en que se confirma la cancelacion (T0). Es una
/// owned entity de <see cref="BookingCancellation"/> — se persiste como columnas
/// con prefijo en la misma tabla, sin tabla propia.
///
/// Por que es value object:
///  - Es inmutable post-T0 (INV-093): cualquier "modificacion" requiere
///    <c>ApprovalRequest.InvariantOverride</c> + audit especial.
///  - No tiene identidad propia: el BookingCancellation lo posee.
///  - Captura el "snapshot" de variables que pueden cambiar despues:
///    condicion fiscal del cliente, del operador, de la agencia, currency,
///    y los TC en 3 momentos (T0/T2/T3).
///
/// Convencion EF Core: configurado en <c>AppDbContext.OnModelCreating</c> via
/// <c>OwnsOne(bc =&gt; bc.FiscalSnapshot)</c> — Npgsql persiste todas las props
/// del VO como columnas en la tabla padre con el prefijo
/// <c>FiscalSnapshot_</c> por default.
///
/// Why crece tanto: el modulo es transversal a la condicion fiscal de
/// agencia + operador + cliente y debe persistir los 3 momentos de TC
/// (T0 = NC, T2 = ingreso operador, T3 = retiro cliente) porque la
/// diferencia de cambio entre ellos genera asientos contables propios.
///
/// IMPORTANTE — no usar defaults peligrosos (review BR2, 2026-05-14):
///  - <see cref="Source"/> arranca como <c>ExchangeRateSource.Unset</c> (no <c>BNA_Mayorista</c>):
///    afirmar una fuente sin que el cashier la haya elegido seria falsificar evidencia fiscal.
///  - <see cref="FetchedAt"/> arranca como <c>default(DateTime)</c> (no <c>DateTime.UtcNow</c>):
///    un timestamp en construccion del objeto seria un fetch falso.
///  - <see cref="ExchangeRateAtOriginalInvoice"/> queda en <c>0m</c> hasta que el flow lo setee:
///    el CHECK <c>chk_BookingCancellations_fiscalsnapshot_consistent</c> bloquea persistir
///    el BC en estados &gt;= <c>AwaitingFiscalConfirmation</c> con TC = 0 (INV-118).
///
/// El BC arranca en <c>Drafted</c>, donde se permite snapshot incompleto. Para
/// transicionar a T0 (<c>AwaitingFiscalConfirmation</c>), <c>BookingCancellationService</c>
/// (FC1.2) DEBE llenar Source, FetchedAt, CurrencyAtEvent y ExchangeRateAtOriginalInvoice.
/// </summary>
public class FiscalSnapshot
{
    /// <summary>CUIT/CUIL/DNI del cliente al momento de T0 (snapshot).</summary>
    [MaxLength(20)]
    public string? CustomerTaxIdAtEvent { get; set; }

    /// <summary>Condicion fiscal del cliente al momento de T0 (Responsable Inscripto / Monotributo / Exento / Consumidor Final).</summary>
    [MaxLength(50)]
    public string? CustomerTaxConditionAtEvent { get; set; }

    /// <summary>CUIT del operador al momento de T0 (snapshot).</summary>
    [MaxLength(20)]
    public string? SupplierTaxIdAtEvent { get; set; }

    /// <summary>Condicion fiscal del operador al momento de T0 (RI / Mono / Exento / Extranjero).</summary>
    [MaxLength(50)]
    public string? SupplierTaxConditionAtEvent { get; set; }

    /// <summary>
    /// Condicion fiscal de la AGENCIA al momento de T0. CRITICO: el modulo se
    /// comporta diferente segun esta variable (matriz cruzada del ADR-002 §2.8).
    /// Persistir el snapshot evita que un cambio de regimen agencia rompa
    /// retroactivamente cancelaciones historicas (INV-117).
    /// </summary>
    [MaxLength(50)]
    public string? AgencyTaxConditionAtEvent { get; set; }

    /// <summary>Codigo de moneda ISO 4217 al momento de T0 (ARS/USD/EUR/etc.).</summary>
    [MaxLength(3)]
    public string? CurrencyAtEvent { get; set; }

    /// <summary>
    /// T0 — TC congelado de la factura original al emitir la NC.
    /// Por regla AFIP de coherencia fiscal (INV-118), la NC se emite con el MISMO
    /// TC que la factura original, no con el TC del dia de la NC.
    /// </summary>
    public decimal ExchangeRateAtOriginalInvoice { get; set; }

    /// <summary>T2 — TC del dia en que se recibe la devolucion fisica del operador. Null hasta que ocurra T2.</summary>
    public decimal? ExchangeRateAtOperatorRefundReceipt { get; set; }

    /// <summary>T3 — TC del dia en que el cliente retira su saldo. Null hasta que ocurra T3.</summary>
    public decimal? ExchangeRateAtClientWithdrawal { get; set; }

    /// <summary>
    /// Fuente del TC capturado (ver <see cref="ExchangeRateSource"/>). Permite reconstruir
    /// el calculo en auditoria. Default = <see cref="ExchangeRateSource.Unset"/> (sin elegir);
    /// el CHECK SQL bloquea persistir BCs T0+ con este valor (review BR2, INV-118).
    /// </summary>
    public ExchangeRateSource Source { get; set; }

    /// <summary>
    /// Timestamp UTC del fetch del TC desde la fuente seleccionada. Util cuando la fuente es
    /// <c>BNA</c> intradia. Sin default: el service que crea el BC en estado <c>Drafted</c>
    /// puede dejarlo en <c>default(DateTime)</c>; <c>BookingCancellationService.ConfirmAsync</c>
    /// debe setearlo a <c>DateTime.UtcNow</c> al transicionar a <c>AwaitingFiscalConfirmation</c>.
    /// </summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>
    /// Justificacion del cashier cuando <see cref="Source"/> = <c>Manual</c>.
    /// INV-120: el guard de dominio rechaza guardar Manual sin esto.
    /// </summary>
    [MaxLength(500)]
    public string? ManualJustification { get; set; }

    /// <summary>
    /// JSON opcional con metadata adicional (ej. tasa BNA usada para conversion
    /// intermedia, razon de override de TC, etc.). El frontend interpreta segun
    /// el caso. Sin esquema estricto a proposito — el campo es para casos raros.
    /// </summary>
    public string? ExtrasJson { get; set; }
}
