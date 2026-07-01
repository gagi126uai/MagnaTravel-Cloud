namespace TravelApi.Application.DTOs;

public class SupplierListItemDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? TaxId { get; set; }
    public string? TaxCondition { get; set; }
    public string? Address { get; set; }

    /// <summary>
    /// Rediseño alta de operador (2026-06-28): moneda por defecto del operador (ISO ARS/USD). El form de
    /// edicion la usa para preseleccionar la moneda. null solo en filas legacy aun no migradas (se lee ARS).
    /// </summary>
    public string? DefaultCurrency { get; set; }

    public bool IsActive { get; set; }
    public decimal CurrentBalance { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SupplierAccountOverviewDto
{
    public SupplierAccountSupplierDto Supplier { get; set; } = new();

    /// <summary>
    /// DEPRECADO para los montos (N2, TANDA 1): resumen ESCALAR historico. <c>TotalPurchases</c>,
    /// <c>TotalPaid</c> y <c>Balance</c> SUMAN ARS+USD en un solo numero (mezcla monedas) y SOLO son fieles si
    /// el proveedor opera en UNA sola moneda — NO usar para mostrar el saldo. La fuente CORRECTA del saldo es
    /// <see cref="BalancesByCurrency"/> (separado por moneda). Lo unico que sigue valido aca son los
    /// CONTADORES (<c>ServiceCount</c>/<c>PaymentCount</c>). Se conserva por compatibilidad del contrato.
    /// </summary>
    public SupplierAccountSummaryDto Summary { get; set; } = new();

    /// <summary>
    /// Saldo de la Cuenta por Pagar SEPARADO por moneda, leido tal cual de la proyeccion materializada
    /// <c>SupplierBalanceByCurrency</c> (compras confirmadas - pagado, por moneda). Es la unica fuente
    /// correcta del saldo: la plata NUNCA cruza ARS/USD, y el sobrepago de una moneda no compensa la deuda de
    /// otra. <c>Balance</c> positivo = la agencia le debe al operador en esa moneda; negativo = saldo a favor
    /// (le pago de mas). Vacio si el proveedor no tiene movimientos. Los montos respetan el masking see_cost
    /// igual que el resto de la cuenta.
    /// </summary>
    public List<SupplierAccountBalanceByCurrencyDto> BalancesByCurrency { get; set; } = new();
}

/// <summary>
/// Saldo de la Cuenta por Pagar de UNA moneda, proyectado desde <c>SupplierBalanceByCurrency</c>.
/// Espejo del DTO del lado cliente (saldo por moneda). NO se recalcula: se lee de la proyeccion ya
/// materializada por el persister de la deuda.
/// </summary>
public class SupplierAccountBalanceByCurrencyDto
{
    public string Currency { get; set; } = "ARS";
    public decimal ConfirmedPurchases { get; set; }
    public decimal TotalPaid { get; set; }

    /// <summary>Saldo = compras confirmadas - pagado en ESTA moneda. Positivo = deuda; negativo = saldo a favor.</summary>
    public decimal Balance { get; set; }
}

public class SupplierAccountSupplierDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? TaxId { get; set; }
    public string? TaxCondition { get; set; }
    public string? Address { get; set; }

    /// <summary>Rediseño alta de operador (2026-06-28): moneda por defecto del operador (ISO ARS/USD).</summary>
    public string? DefaultCurrency { get; set; }

    public bool IsActive { get; set; }
    public decimal CurrentBalance { get; set; }
}

public class SupplierAccountSummaryDto
{
    public decimal TotalPurchases { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal Balance { get; set; }
    public int ServiceCount { get; set; }
    public int PaymentCount { get; set; }
}

public class SupplierAccountServiceListItemDto
{
    public Guid PublicId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Confirmation { get; set; }
    public decimal NetCost { get; set; }
    public decimal SalePrice { get; set; }

    /// <summary>
    /// ADR-021 (multimoneda, eje proveedor, §15.4): moneda en que va este servicio (la deuda con el
    /// operador por este servicio esta en ESTA moneda). <c>null</c> = legacy = ARS (se normaliza con
    /// <c>Monedas.Normalizar</c> al agrupar la deuda por moneda). Proyectada desde <c>x.Currency</c>
    /// de cada tipo de servicio.
    /// </summary>
    public string? Currency { get; set; }

    public DateTime Date { get; set; }

    /// <summary>
    /// ADR-041 TANDA 5 (2026-06-27): vencimiento SUGERIDO de la deuda de esta compra/servicio con el operador.
    /// = <see cref="Date"/> + <c>Supplier.DefaultPaymentTermDays</c>. <c>null</c> cuando el operador no tiene
    /// plazo configurado (comportamiento actual). Es INFORMATIVO: seguimos prepago, NO bloquea nada; el front
    /// pinta los chips "por vencer"/"vencida" a partir de esta fecha.
    /// </summary>
    public DateTime? SuggestedDueDate { get; set; }

    public string Status { get; set; } = string.Empty;
    public string? NumeroReserva { get; set; }
    public string? FileName { get; set; }
    public Guid? ReservaPublicId { get; set; }
}

// ===================================================================================================
// Auditoria ERP hallazgo #4 (2026-06-12): deuda al proveedor DESGLOSADA POR EXPEDIENTE (reserva).
// La cuenta corriente del proveedor hasta hoy era GLOBAL (todo lo que se le debe al operador, sumado).
// El dueño concilia con los mayoristas POR EXPEDIENTE, asi que necesita ver, para un proveedor dado:
//   - cuanto se le debe en CADA reserva (por moneda), descontando los pagos imputados a esa reserva; y
//   - un bucket "a cuenta / anticipos" con los pagos que NO estan atados a ninguna reserva.
// Invariante (verificado por test): la suma de los saldos por reserva + los anticipos a cuenta RECONCILIA
// exactamente con el total global por moneda de SupplierService.CalculateSupplierDebtPorMonedaAsync.
// ===================================================================================================

/// <summary>
/// Vista completa de la deuda de la agencia con UN proveedor, abierta por expediente (reserva) y por
/// moneda, mas el bucket de anticipos a cuenta y el total global de reconciliacion.
/// </summary>
public class SupplierDebtByReservaDto
{
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>Una entrada por reserva donde el proveedor tiene deuda y/o pagos imputados.</summary>
    public List<SupplierDebtReservaLineDto> Reservas { get; set; } = new();

    /// <summary>
    /// Anticipos "a cuenta": pagos al proveedor SIN reserva imputada (incluye el legacy con ReservaId null).
    /// Una linea por moneda. Estos montos NO estan atados a un expediente concreto.
    /// </summary>
    public List<SupplierDebtCurrencyAmountDto> AdvancesToAccount { get; set; } = new();

    /// <summary>
    /// ADR-041 TANDA 3: saldo a favor del operador APLICADO a reservas (neto de reversas), por moneda. Es la
    /// cara consumida del balance negativo: baja la deuda-por-reserva del destino SIN mover caja. Se expone
    /// como bucket aparte para que la reconciliacion cierre: por moneda,
    /// <c>GlobalTotals == Σ saldos por reserva + anticipos a cuenta + saldo a favor aplicado</c>. Lista vacia
    /// si el operador no tiene saldo a favor aplicado en esa moneda (no rompe lectores previos).
    /// </summary>
    public List<SupplierDebtCurrencyAmountDto> CreditAppliedFromBalance { get; set; } = new();

    /// <summary>
    /// Total global de la deuda por moneda (el mismo numero que la cuenta corriente global ya calculaba).
    /// Sirve de control de reconciliacion: por moneda, debe igualar la suma de los saldos por reserva mas
    /// los anticipos a cuenta y el saldo a favor aplicado de esa moneda.
    /// </summary>
    public List<SupplierDebtCurrencyAmountDto> GlobalTotals { get; set; } = new();
}

/// <summary>
/// La deuda del proveedor en UNA reserva, abierta por moneda. Datos de identidad de la reserva (numero,
/// nombre) viajan SIEMPRE; los montos respetan el masking see_cost igual que el resto de la cuenta.
/// </summary>
public class SupplierDebtReservaLineDto
{
    public Guid ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }
    public string? FileName { get; set; }

    /// <summary>Una linea por moneda: compras confirmadas, pagado imputado y saldo de ESTE proveedor en ESTA reserva.</summary>
    public List<SupplierDebtCurrencyLineDto> Currencies { get; set; } = new();
}

/// <summary>Compras confirmadas, pagado y saldo de una moneda. Espejo de <c>SupplierDebtLine</c> del dominio.</summary>
public class SupplierDebtCurrencyLineDto
{
    public string Currency { get; set; } = "ARS";
    public decimal ConfirmedPurchases { get; set; }
    public decimal TotalPaid { get; set; }

    /// <summary>
    /// ADR-041 TANDA 3: saldo a favor del operador APLICADO a ESTA reserva en esta moneda (neto de reversas).
    /// Baja la deuda exigible sin ser caja. 0 si no se aplico saldo a favor a esta reserva.
    /// </summary>
    public decimal CreditApplied { get; set; }

    /// <summary>
    /// Saldo = compras confirmadas - pagado - saldo a favor aplicado. Puede ser negativo (sobrepago a esta
    /// reserva/proveedor o saldo a favor aplicado por encima de la deuda).
    /// </summary>
    public decimal Balance { get; set; }
}

/// <summary>Par moneda + monto. Para anticipos a cuenta y totales globales de reconciliacion.</summary>
public class SupplierDebtCurrencyAmountDto
{
    public string Currency { get; set; } = "ARS";
    public decimal Amount { get; set; }
}

// ===================================================================================================
// ADR-036 punto 4c (2026-06-23): estado "pagado al operador" POR SERVICIO de una reserva.
// El aviso de pago al operador de ADR-036 era a nivel reserva (no podia trabar el viaje porque no se
// sabia que servicio estaba pagado). Este contrato expone, por cada servicio de la reserva, cuanto se le
// pago al operador y si quedo cubierto, para que el front pinte la etiqueta pagado/impago por servicio.
//
// Masking: el ESTADO (pagado/parcial/impago) lo ven todos (decision ADR-036 P4=B). Los MONTOS (costo,
// pagado, saldo) son costo -> sin cobranzas.see_cost se anulan a 0, igual que el resto de la cuenta.
// ===================================================================================================

/// <summary>
/// Estado de pago al operador de TODOS los servicios de una reserva. Una entrada por servicio,
/// identificada con el mismo par (recordKind, servicePublicId) que usa el front.
/// </summary>
public class ReservaSupplierPaymentStatusDto
{
    public Guid ReservaPublicId { get; set; }

    /// <summary>true si el caller puede ver montos (cobranzas.see_cost). Si false, los montos vienen en 0.</summary>
    public bool AmountsVisible { get; set; }

    public List<ServiceSupplierPaymentStatusDto> Services { get; set; } = new();
}

/// <summary>Estado de pago al operador de UN servicio de la reserva.</summary>
public class ServiceSupplierPaymentStatusDto
{
    /// <summary>Tipo de registro del servicio (flight/hotel/transfer/package/assistance/generic).</summary>
    public string RecordKind { get; set; } = string.Empty;

    /// <summary>PublicId del servicio. El front lo une con su lista de servicios por este id.</summary>
    public Guid ServicePublicId { get; set; }

    /// <summary>Proveedor/operador del servicio (puede ser null si el servicio no tiene proveedor cargado).</summary>
    public Guid? SupplierPublicId { get; set; }
    public string? SupplierName { get; set; }

    /// <summary>Moneda del costo del servicio (la deuda con el operador esta en esta moneda).</summary>
    public string Currency { get; set; } = "ARS";

    /// <summary>Costo del servicio con el operador. Enmascarado a 0 sin see_cost.</summary>
    public decimal NetCost { get; set; }

    /// <summary>Suma de pagos vivos al operador imputados a ESTE servicio (equivalente imputado). Enmascarado a 0 sin see_cost.</summary>
    public decimal PaidToOperator { get; set; }

    /// <summary>Saldo pendiente con el operador por este servicio = NetCost - PaidToOperator. Enmascarado a 0 sin see_cost.</summary>
    public decimal OutstandingToOperator { get; set; }

    /// <summary>
    /// Estado derivado, SIEMPRE visible (no se enmascara): "paid" (cubierto), "partial" (algo pagado pero
    /// no todo) o "unpaid" (nada pagado). Un servicio sin costo/sin proveedor se reporta "unpaid".
    /// </summary>
    public string Status { get; set; } = ServiceSupplierPaymentStatuses.Unpaid;
}

/// <summary>Valores de <see cref="ServiceSupplierPaymentStatusDto.Status"/>.</summary>
public static class ServiceSupplierPaymentStatuses
{
    public const string Paid = "paid";
    public const string Partial = "partial";
    public const string Unpaid = "unpaid";
}

// ===================================================================================================
// TANDA 1 (cuenta corriente del proveedor, 2026-06-27): EXTRACTO de la Cuenta por Pagar como LIBRO MAYOR.
// Espejo del extracto de la reserva (ReservaAccountStatementDto), pero del lado COSTO: cargo = compra
// confirmada al operador, abono = pago al operador, separado por moneda, con saldo corriente.
// Invariante (test): el ClosingBalance de cada moneda == SupplierBalanceByCurrency.Balance de esa moneda.
// ===================================================================================================

/// <summary>
/// "Estado de Cuenta" del PROVEEDOR como LIBRO MAYOR (extracto estilo banco). Read-model DERIVADO (no se
/// persiste): una linea cronologica por cada compra confirmada (cargo) y cada pago al operador (abono), con
/// saldo corriente, SEPARADO por moneda.
///
/// <para><b>SEGURIDAD</b>: es del lado COSTO (deuda con el operador). Sin <c>cobranzas.see_cost</c> los montos
/// (cargos, abonos, saldos) se anulan a 0 y <see cref="AmountsVisible"/> viene en false; la estructura
/// (movimientos, fechas, monedas) sigue visible, igual que el resto de la cuenta del proveedor.</para>
/// </summary>
public class SupplierAccountStatementDto
{
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;

    /// <summary>true si el caller puede ver montos (cobranzas.see_cost). Si false, los montos vienen en 0.</summary>
    public bool AmountsVisible { get; set; }

    /// <summary>Un bloque por cada moneda presente (ARS/USD/...). Vacio si el proveedor no tiene movimientos.</summary>
    public List<SupplierAccountStatementCurrencyBlockDto> Currencies { get; set; } = new();
}

/// <summary>
/// Bloque del extracto del proveedor de UNA moneda: sus lineas en orden cronologico y su saldo de cierre.
/// </summary>
public class SupplierAccountStatementCurrencyBlockDto
{
    public string Currency { get; set; } = "ARS";

    /// <summary>
    /// Movimientos de la moneda en orden cronologico, con saldo corriente. Desde el saldo UNICO (2026-06-30)
    /// esta es la secuencia MERGEADA: las lineas de CAJA (compras / pagos al operador) intercaladas con las
    /// del "Circuito de cancelacion" (multa retenida + reembolso recibido), todas ordenadas por fecha. El
    /// <see cref="SupplierAccountStatementLineDto.RunningBalance"/> se recalcula de corrido sobre la secuencia
    /// completa y CIERRA en el saldo ECONOMICO (<see cref="ClosingBalance"/> = <see cref="EconomicClosingBalance"/>),
    /// que reconcilia con los dos numeros del header. Desempate estable: ante misma fecha, primero la linea de
    /// caja, despues la de circuito.
    /// </summary>
    public List<SupplierAccountStatementLineDto> Lines { get; set; } = new();

    /// <summary>
    /// Saldo de cierre MOSTRADO de la moneda = saldo corriente de la ultima linea de <see cref="Lines"/>.
    ///
    /// <para><b>CAMBIO DE SIGNIFICADO (saldo unico, 2026-06-30)</b>: antes reflejaba SOLO la caja (compras -
    /// pagos). Ahora refleja el saldo ECONOMICO (caja + circuito de cancelacion) e IGUALA a
    /// <see cref="EconomicClosingBalance"/>, para que el saldo del pie del extracto coincida con los recuadros
    /// del header. El saldo de SOLO caja (el que iguala la proyeccion <c>SupplierBalanceByCurrency.Balance</c>)
    /// se movio a <see cref="CashClosingBalance"/>.</para>
    /// </summary>
    public decimal ClosingBalance { get; set; }

    // ====================================================================================================
    // Cara ECONOMICA de la cuenta del operador (Pasos B/C, 2026-06-29; saldo unico, 2026-06-30). Estos campos
    // alimentan los DOS numeros del header y el nuevo saldo unico. Respetan el masking see_cost igual que el
    // resto (0 si el caller no puede ver costos).
    // ====================================================================================================

    /// <summary>
    /// Saldo de cierre de SOLO CAJA (compras - pagos al operador), SIN el circuito de cancelacion. Es el eco de
    /// la proyeccion <c>SupplierBalanceByCurrency.Balance</c> por moneda (invariante verificado por test). Se
    /// expone aparte porque <see cref="ClosingBalance"/> paso a ser el saldo economico.
    /// </summary>
    public decimal CashClosingBalance { get; set; }

    /// <summary>
    /// OBSOLETO desde el saldo unico (2026-06-30): las lineas del circuito ahora viven intercaladas en
    /// <see cref="Lines"/>. Se conserva vacio por compatibilidad de forma del JSON hasta que el front migre;
    /// no volver a poblarlo. TODO: eliminar cuando <c>SupplierExtractoSection.jsx</c> deje de leerlo.
    /// </summary>
    public List<SupplierAccountStatementLineDto> CircuitLines { get; set; } = new();

    /// <summary>Saldo economico = caja + circuito (multa retenida + reembolso recibido). Igual a <see cref="ClosingBalance"/>.</summary>
    public decimal EconomicClosingBalance { get; set; }

    /// <summary>"Me tiene que devolver" (Y): reembolso del operador todavia por cobrar en esta moneda.</summary>
    public decimal TheyOweMe { get; set; }

    /// <summary>"Le debo" (X): lo que la agencia todavia le tiene que pagar al operador en esta moneda. NUNCA se netea con Y.</summary>
    public decimal ITheyOwe { get; set; }

    /// <summary>Saldo a favor CONSUMIBLE (prepago) con el operador en esta moneda. Convive con Y solo si X = 0.</summary>
    public decimal Prepayment { get; set; }
}

/// <summary>
/// Una linea del extracto del proveedor. Estilo banco: <see cref="Charge"/> SUMA a la deuda con el operador
/// (compra confirmada), <see cref="Credit"/> la RESTA (pago al operador). Una linea trae uno u otro (el otro
/// en 0). <see cref="RunningBalance"/> es el saldo acumulado de la moneda hasta esta linea inclusive.
/// </summary>
public class SupplierAccountStatementLineDto
{
    /// <summary>Fecha del movimiento (alta del servicio comprado o fecha del pago al operador).</summary>
    public DateTime Date { get; set; }

    /// <summary>Tipo de movimiento: "Purchase" (compra confirmada) / "Payment" (pago al operador).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Texto legible del movimiento (descripcion del servicio; metodo del pago).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Referencia del documento (nº de expediente de la compra; referencia del pago), o null.</summary>
    public string? DocumentRef { get; set; }

    /// <summary>
    /// PublicId del documento de origen: el servicio comprado (lineas Purchase) o el pago al operador
    /// (lineas Payment). El front lo cruza para colgar acciones por renglon. Es solo un identificador.
    /// </summary>
    public Guid? SourcePublicId { get; set; }

    /// <summary>Moneda de la linea (igual que la del bloque).</summary>
    public string Currency { get; set; } = "ARS";

    /// <summary>Monto que SUMA a la deuda (compra confirmada). 0 si la linea es un pago. Enmascarado a 0 sin see_cost.</summary>
    public decimal Charge { get; set; }

    /// <summary>Monto que RESTA de la deuda (pago al operador). 0 si la linea es una compra. Enmascarado a 0 sin see_cost.</summary>
    public decimal Credit { get; set; }

    /// <summary>Saldo corriente de la moneda hasta esta linea inclusive. Enmascarado a 0 sin see_cost.</summary>
    public decimal RunningBalance { get; set; }
}

public class SupplierPaymentDto
{
    public Guid PublicId { get; set; }
    public decimal Amount { get; set; }
    /// <summary>ADR-021: moneda REAL del egreso (lo que salio de caja). Default ARS para el legacy.</summary>
    public string Currency { get; set; } = "ARS";
    public string Method { get; set; } = string.Empty;
    public DateTime PaidAt { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string? NumeroReserva { get; set; }
    public string? FileName { get; set; }
    public Guid? ReservaPublicId { get; set; }
    /// <summary>
    /// ADR-022 §4 P4: true = anticipo "a cuenta" del proveedor (no imputado a ninguna reserva). Se deriva de
    /// la ausencia de reserva (<see cref="ReservaPublicId"/> null). El front lo usa para mostrar "a cuenta"
    /// en vez de un numero de reserva.
    /// </summary>
    public bool IsAdvanceToAccount { get; set; }

    /// <summary>
    /// ADR-036 4c: si el pago se imputo a UN servicio puntual de la reserva, su tipo de registro
    /// (flight/hotel/transfer/package/assistance/generic). Null = pago a nivel reserva o anticipo.
    /// </summary>
    public string? ServiceRecordKind { get; set; }

    /// <summary>ADR-036 4c: PublicId del servicio imputado. Null = pago a nivel reserva o anticipo.</summary>
    public Guid? ServicePublicId { get; set; }
}
