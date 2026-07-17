using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs;

public class CustomerListItemDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? DocumentType { get; set; }
    public string? DocumentNumber { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public string? TaxId { get; set; }
    public decimal CreditLimit { get; set; }
    public bool IsActive { get; set; }
    public int? TaxConditionId { get; set; }
    public decimal CurrentBalance { get; set; }
    public List<CurrencyAmountDto> BalancesByCurrency { get; set; } = new();
    /// <summary>Creditos no aplicados a otra reserva, separados por moneda.</summary>
    public List<CurrencyAmountDto> UnappliedCreditsByCurrency { get; set; } = new();
    public bool AmountsVisible { get; set; } = true;
}

public class CustomerAccountOverviewDto
{
    public CustomerAccountCustomerDto Customer { get; set; } = new();
    public CustomerAccountSummaryDto Summary { get; set; } = new();

    /// <summary>
    /// "Multa pendiente de cobro" (UX 2026-07-15): las multas por anulación de TODAS las reservas anuladas
    /// del cliente, juntas en un solo bloque. Antes esta plata quedaba invisible: la cuenta del cliente
    /// filtraba afuera las reservas anuladas, y la multa solo existe ahí. Vacío (Items sin filas) si el
    /// cliente no tiene ninguna multa pendiente — el front esconde el bloque entero en ese caso.
    /// </summary>
    public CustomerPendingPenaltiesDto PendingPenalties { get; set; } = new();
}

/// <summary>
/// Tokens de estado del comprobante de una multa pendiente, para <see cref="CustomerPendingPenaltyItemDto.Status"/>.
/// El backend SOLO manda el token (camelCase, en inglés); el texto final que ve el usuario ("Pendiente de
/// cobro" / "Comprobante en camino" / "En revisión") lo arma el front (UX 2026-07-15). A propósito estos
/// tokens NO usan el castellano de <c>ReservationDebtRules.ToDtoString</c> (ese es el contrato viejo de
/// <c>CancelledMoneyContext</c>): este bloque es nuevo y sigue el contrato ya acordado con el front.
/// </summary>
public static class CustomerPendingPenaltyStatus
{
    /// <summary>El comprobante de la multa ya tiene CAE: es deuda firme, solo falta que el cliente la pague.</summary>
    public const string PendingCollection = "pendingCollection";

    /// <summary>La multa está confirmada pero su comprobante todavía se está emitiendo.</summary>
    public const string Issuing = "issuing";

    /// <summary>El comprobante de la multa falló su emisión o quedó para revisión manual (lo destraba back-office).</summary>
    public const string UnderReview = "underReview";
}

/// <summary>
/// Bloque "Multa pendiente de cobro" de la cuenta del cliente (UX 2026-07-15): una fila por cada multa de
/// anulación viva o en revisión, de cualquier reserva anulada del cliente, más el total por moneda. Es un
/// bloque APARTE del extracto "Estado de Cuenta" (decisión cerrada del dueño: no se mezclan) — el "Estado de
/// Cuenta" sigue mostrando solo reservas vivas, sin tocar.
/// </summary>
public class CustomerPendingPenaltiesDto
{
    /// <summary>Una fila por multa. Vacía si el cliente no tiene ninguna (el front esconde el bloque).</summary>
    public List<CustomerPendingPenaltyItemDto> Items { get; set; } = new();

    /// <summary>Un total por cada moneda que tenga al menos una multa. Nunca se suman monedas distintas.</summary>
    public List<CustomerPendingPenaltyTotalDto> TotalsByCurrency { get; set; } = new();
}

/// <summary>
/// UNA multa por anulación pendiente de cobro, de una reserva anulada del cliente. El monto es el BRUTO
/// congelado al momento del evento (<c>BookingCancellation.PenaltyAmountAtEvent</c>): el mismo número con el
/// que se emitió (o se va a emitir) el comprobante de la multa — a propósito NO se netea contra cobros
/// parciales (ese neteo es el criterio de la ficha/listado de reservas, un cartel distinto).
/// </summary>
public class CustomerPendingPenaltyItemDto
{
    /// <summary>PublicId de la reserva anulada; el front linkea la fila a su ficha (ahí se resuelve la multa).</summary>
    public Guid ReservaPublicId { get; set; }

    public string NumeroReserva { get; set; } = string.Empty;

    /// <summary>Nombre del expediente (titular), para armar "R-1042 · Cancún" en la fila del bloque.</summary>
    public string Name { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>Moneda ISO 4217 ("ARS"/"USD") del monto: la MISMA moneda de la factura (ADR-012 §3.3), sin convertir.</summary>
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>Ver <see cref="CustomerPendingPenaltyStatus"/> para los tres valores posibles.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Nota de debito aprobada que documenta el open item. Solo viene para PendingCollection; el alta de cobro
    /// de una reserva anulada debe vincularse a este documento concreto.
    /// </summary>
    public Guid? DebitNotePublicId { get; set; }

    /// <summary>Numero legible del comprobante (punto de venta-numero), si la ND ya fue emitida.</summary>
    public string? DocumentRef { get; set; }
}

/// <summary>
/// Total de multas pendientes de UNA moneda, ya separado en "deuda firme" (comprobante emitido,
/// <see cref="CustomerPendingPenaltyStatus.PendingCollection"/>) vs "todavía sin comprobante" (emitiéndose o
/// en revisión). El front pinta el número grande en rojo con <see cref="FirmAmount"/> y, debajo, la segunda
/// línea en ámbar con <see cref="NotYetIssuedAmount"/> (UX 2026-07-15) sin tener que re-sumar los Items.
/// </summary>
public class CustomerPendingPenaltyTotalDto
{
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>Suma de las multas con comprobante ya emitido, en esta moneda.</summary>
    public decimal FirmAmount { get; set; }

    /// <summary>Suma de las multas sin comprobante todavía (emitiéndose o en revisión), en esta moneda.</summary>
    public decimal NotYetIssuedAmount { get; set; }
}

public class CustomerAccountCustomerDto
{
    public Guid PublicId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? TaxId { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal CurrentBalance { get; set; }
}

public class CustomerAccountSummaryDto
{
    /// <summary>Compatibilidad legacy: solo se completa cuando existe una unica moneda.</summary>
    public decimal TotalSales { get; set; }
    /// <summary>Compatibilidad legacy: solo se completa cuando existe una unica moneda.</summary>
    public decimal TotalPaid { get; set; }
    /// <summary>Compatibilidad legacy: solo se completa cuando existe una unica moneda.</summary>
    public decimal TotalBalance { get; set; }
    public int ReservaCount { get; set; }
    public int PaymentCount { get; set; }
    public int InvoiceCount { get; set; }

    /// <summary>Cargos de comprobantes aprobados, separados estrictamente por moneda.</summary>
    public List<CurrencyAmountDto> DocumentedByCurrency { get; set; } = new();

    /// <summary>Cobros reales (AffectsCash), separados estrictamente por moneda de imputacion.</summary>
    public List<CurrencyAmountDto> CollectedByCurrency { get; set; } = new();

    // ADR-022 Capa 8 (C2): la cuenta corriente del cliente deja de ser un escalar surrogate y pasa a
    // exponerse POR MONEDA, alineada con ADR-021 (el saldo en USD no compensa el saldo en ARS). Los
    // escalares de arriba quedan para compat (el front actual los usa); estas listas son ADITIVAS.

    /// <summary>
    /// Saldo a COBRAR por moneda (deuda del cliente con la agencia), desde ReservaMoneyByCurrency de sus
    /// reservas activas. Misma fuente que el AR de tesoreria. NUNCA mezcla monedas en un total.
    /// </summary>
    public List<CurrencyAmountDto> ReceivableByCurrency { get; set; } = new();

    /// <summary>
    /// Saldo A FAVOR del cliente por moneda (el "bolsillo" unificado): suma de los ClientCreditEntry
    /// activos (RemainingBalance &gt; 0), cualquier origen (cancelacion o sobrepago). Eje OPUESTO al de cobrar;
    /// se exponen separados y NO se netea uno contra otro (ni dentro de una moneda ni entre monedas).
    /// </summary>
    public List<CurrencyAmountDto> CreditBalanceByCurrency { get; set; } = new();

    /// <summary>
    /// Cobros/compensaciones que exceden el open item de su reserva y aun no fueron trasladados o aplicados.
    /// Se muestran separados y nunca reducen <see cref="ReceivableByCurrency"/> implicitamente.
    /// </summary>
    public List<CurrencyAmountDto> UnappliedCreditByCurrency { get; set; } = new();

    /// <summary>
    /// Tanda D2 (extracto profesional, 2026-07-16): composicion del saldo por moneda para la "foto de saldo"
    /// del encabezado. Una fila por cada moneda con al menos un componente (facturado, multa o credito).
    /// Ver <see cref="CustomerAccountBalanceCompositionDto"/> para el detalle de cada campo: el front pinta
    /// estos numeros TAL CUAL, sin volver a sumar nada.
    /// </summary>
    public List<CustomerAccountBalanceCompositionDto> BalanceCompositionByCurrency { get; set; } = new();
}

/// <summary>
/// Composicion del saldo de UNA moneda para la foto de saldo del encabezado de la cuenta del cliente (spec UX
/// 2026-07-16, Tanda D2 "extracto profesional"). Antes de esta tanda, <see cref="CustomerAccountSummaryDto.ReceivableByCurrency"/>
/// ya mezclaba venta documentada + multa firme en un solo numero sin distinguir (el front no podia separarlos
/// sin volver a sumar renglones); esto expone las piezas SEPARADAS que la pantalla nueva necesita pintar.
///
/// <para><b>Identidad que cierra SIEMPRE, sin excepcion</b>: <c>Saldo = FacturadoSinCobrar + MultasAbiertas -
/// CreditoAFavor</c>. En el caso normal, ademas, <c>FacturadoSinCobrar + (MultasAbiertas - MultasEnTramite)</c>
/// (la parte YA documentada) coincide con el <c>ClosingBalance</c> de la MISMA moneda en
/// <see cref="CustomerAccountStatementCurrencyBlockDto"/> (fuente unica: ambos salen del mismo extracto). La
/// parte "en tramite" de la multa todavia no tiene comprobante, asi que no es un open item documentado y no
/// esta en el extracto — solo en esta composicion, como aviso.</para>
///
/// <para><b>Caso de borde (review Tanda D2, M1)</b>: <see cref="FacturadoSinCobrar"/> NUNCA es negativo. Si
/// una reserva anulada tiene un credito grande sin imputar especificamente a su ND (nota de debito de la
/// multa), esa reserva puede cerrar en 0 en el extracto (el credito sobrante queda en UnappliedCredit) aunque
/// la ND siga mostrando outstanding — restar la multa firme del cierre daria un "Facturado" negativo. En ese
/// caso <see cref="FacturadoSinCobrar"/> se frena en 0 y el residuo se descuenta de <see cref="MultasAbiertas"/>
/// (tambien con piso 0): el SPLIT entre las dos lineas pasa a ser indicativo en ese escenario puntual, pero
/// <see cref="Saldo"/> es la fuente de verdad y jamas cambia por este ajuste.</para>
/// </summary>
public class CustomerAccountBalanceCompositionDto
{
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>
    /// Deuda de viajes VIVOS (venta confirmada y documentada, menos lo cobrado). NO incluye multas: esa plata
    /// vive aparte en <see cref="MultasAbiertas"/> para que el front pueda mostrarla en su propia linea ambar.
    /// Nunca es negativo (piso 0): ver el caso de borde documentado en la clase.
    /// </summary>
    public decimal FacturadoSinCobrar { get; set; }

    /// <summary>
    /// Total de multas por anulacion pendientes de cobro de esta moneda: la parte con comprobante emitido MAS
    /// la parte todavia sin comprobante (<see cref="MultasEnTramite"/>), juntas. Es el numero grande de la
    /// linea "Multas abiertas" de la foto de saldo.
    /// </summary>
    public decimal MultasAbiertas { get; set; }

    /// <summary>
    /// Subconjunto de <see cref="MultasAbiertas"/> que TODAVIA no tiene comprobante emitido (se esta emitiendo
    /// o quedo en revision manual). El front la muestra como nota chica ambar debajo del numero grande
    /// ("incluye $ X en tramite"), sin jerga fiscal. 0 si toda la multa abierta ya tiene comprobante.
    /// </summary>
    public decimal MultasEnTramite { get; set; }

    /// <summary>Saldo a favor disponible del cliente en esta moneda. Mismo numero que <c>CreditBalanceByCurrency</c>.</summary>
    public decimal CreditoAFavor { get; set; }

    /// <summary>
    /// Saldo final de la foto de saldo = FacturadoSinCobrar + MultasAbiertas - CreditoAFavor. Puede ser
    /// negativo (el cliente tiene mas credito que deuda): el front lo pinta en verde "a favor" en ese caso,
    /// en gris si es 0, y en rojo "debe" si es positivo.
    /// </summary>
    public decimal Saldo { get; set; }
}

/// <summary>
/// DETALLE de un saldo a favor disponible del cliente (un <c>ClientCreditEntry</c> con
/// <c>RemainingBalance &gt; 0</c>). El cartel del front usa el AGREGADO por moneda
/// (<see cref="CustomerAccountSummaryDto.CreditBalanceByCurrency"/>); este DTO es para el
/// botón "usar saldo a favor", donde el usuario elige DE QUÉ entry retirar.
///
/// <para>Trae el <c>EntryPublicId</c> para que el front lo pase tal cual al withdraw
/// (<c>POST /api/client-credit-entries/{EntryPublicId}/withdrawals</c>). Incluye el origen
/// (reserva de la cancelación o reserva sobre-pagada) solo como contexto para que el usuario
/// reconozca de qué viene el crédito; puede ser null en créditos legacy sin origen trazable.</para>
/// </summary>
public class CustomerAvailableCreditEntryDto
{
    public Guid EntryPublicId { get; set; }

    /// <summary>Saldo aún disponible para retirar (siempre &gt; 0 en esta lista).</summary>
    public decimal RemainingBalance { get; set; }

    /// <summary>Monto original acreditado (para mostrar "quedan $X de $Y").</summary>
    public decimal CreditedAmount { get; set; }

    /// <summary>Moneda del bolsillo. El saldo en USD no compensa deuda en ARS (bolsillo por moneda).</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Número de la reserva que originó el crédito (cancelación o sobrepago). Null si no es trazable.</summary>
    public string? OriginReservaNumber { get; set; }

    /// <summary>PublicId de la reserva de origen, para que el front pueda enlazarla. Null si no es trazable.</summary>
    public Guid? OriginReservaPublicId { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class CustomerAccountReservaListItemDto
{
    public Guid PublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal TotalSale { get; set; }
    public decimal Balance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartDate { get; set; }
    public decimal Paid { get; set; }

    // ADR-021 / Tanda 6 (C4, 2026-07-05): los escalares TotalSale/Balance/Paid de arriba son legacy
    // mono-moneda; el front los mostraba SIEMPRE como "ARS" (hardcodeado), lo cual miente en una reserva
    // en USD o multimoneda. Esta lista ADITIVA trae la plata REAL por moneda (misma fuente que el listado
    // de reservas: ReservaMoneyByCurrency). El front la usa para etiquetar cada fila con su moneda real.

    /// <summary>
    /// Venta/cobrado/saldo del cliente en ESTA reserva, abierto por moneda (lado VENTA, sin costo). Una sola
    /// linea = reserva mono-moneda. Vacia si la reserva no tiene filas de plata materializadas (nueva/legacy):
    /// en ese caso el front cae a los escalares. Nunca mezcla monedas en un total.
    /// </summary>
    public List<CustomerAccountReservaMoneyLineDto> PorMoneda { get; set; } = new();

    /// <summary>True si la reserva mueve mas de una moneda (el front muestra las lineas separadas).</summary>
    public bool EsMultimoneda { get; set; }

    // "Multas en la cuenta del cliente" (2026-07-15): estos 3 campos completan el contexto de plata de una
    // reserva ANULADA en la solapa "Reservas" de la cuenta del cliente. El front (CustomerAccountPage.jsx) ya
    // tenia la logica de pintado lista (ContextoAnuladaCuenta) pero muda: el servidor no mandaba el dato. Se
    // llenan con el MISMO derivador que usa el listado general de reservas (ReservaService, via los helpers
    // compartidos CancellationPenaltyRules/ReservationDebtRules) — no hay una segunda definicion de la regla.
    // Quedan null en cualquier reserva que NO este anulada (una reserva viva no tiene "contexto de anulacion").

    /// <summary>
    /// Token del contexto de plata de una anulada (ver <c>ReservationDebtRules.ToDtoString</c>): por ejemplo
    /// "MultaPorCobrar" o "MultaEnRevision". Null si la reserva no esta anulada.
    /// </summary>
    public string? CancelledMoneyContext { get; set; }

    /// <summary>
    /// Monto de la multa PENDIENTE de cobro (neto de lo ya pagado), solo cuando <see cref="CancelledMoneyContext"/>
    /// es "MultaPorCobrar". Null en cualquier otro caso.
    /// </summary>
    public decimal? CancelledPenaltyAmount { get; set; }

    /// <summary>Moneda ISO 4217 ("ARS"/"USD") de <see cref="CancelledPenaltyAmount"/>. Null si ese monto es null.</summary>
    public string? CancelledPenaltyCurrency { get; set; }
}

/// <summary>
/// Tanda 6 (C4): plata del cliente en una reserva, en UNA moneda. Es el espejo SOLO-VENTA de
/// <c>ReservaMoneyLineDto</c>: a proposito NO incluye costo ni margen, porque la cuenta del cliente es del
/// lado venta y no debe exponer cifras de costo (a diferencia de la cuenta del proveedor).
/// </summary>
public class CustomerAccountReservaMoneyLineDto
{
    /// <summary>Moneda ISO de la linea ("ARS"/"USD").</summary>
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>Venta total cotizada en esta moneda.</summary>
    public decimal TotalSale { get; set; }

    /// <summary>Cobrado (imputado) en esta moneda.</summary>
    public decimal Paid { get; set; }

    /// <summary>Saldo pendiente del cliente en esta moneda (venta exigible - cobrado). Positivo = debe.</summary>
    public decimal Balance { get; set; }
}

// ===================================================================================================
// Deuda del cliente DESGLOSADA POR RESERVA y por moneda. Espejo conceptual del lado proveedor
// (SupplierDebtByReservaDto). Alimenta el buscador del flujo "usar saldo a favor -> aplicar a otra
// reserva" del cliente: el front necesita saber EN QUE reservas y EN QUE moneda debe el cliente para
// ofrecer solo destinos con deuda en la misma moneda del saldo a favor (el saldo en USD no cancela
// deuda en ARS). El total global por moneda ya viaja en CustomerAccountSummaryDto.ReceivableByCurrency;
// esto es el MISMO numero abierto por reserva.
//
// Masking: la cuenta del cliente (lado VENTA) NO enmascara montos, a diferencia de la cuenta del
// proveedor (lado COSTO, que oculta cifras sin cobranzas.see_cost). El gate es clientes.view +
// cobranzas.view, igual que el resto de los endpoints de montos de la cuenta del cliente.
// ===================================================================================================

/// <summary>
/// Deuda del cliente con la agencia, abierta por reserva (expediente) y por moneda. Lista solo las
/// reservas donde el cliente es el pagador y tiene saldo pendiente (deuda &gt; 0) en al menos una moneda.
/// </summary>
public class CustomerDebtByReservaDto
{
    public Guid CustomerPublicId { get; set; }
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>Una entrada por reserva con deuda viva del cliente; dentro, una linea por moneda.</summary>
    public List<CustomerDebtReservaLineDto> Reservas { get; set; } = new();
}

/// <summary>
/// La deuda del cliente en UNA reserva, abierta por moneda. La identidad (numero, nombre del expediente)
/// viaja siempre; los montos son la deuda viva por moneda (Balance &gt; 0 de ReservaMoneyByCurrency).
/// </summary>
public class CustomerDebtReservaLineDto
{
    public Guid ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }

    /// <summary>Nombre del expediente (titular) = <c>Reserva.Name</c>. Mismo campo que la pestaña Pagos.</summary>
    public string? FileName { get; set; }

    /// <summary>Una linea por moneda en la que el cliente debe en esta reserva. Nunca mezcla monedas.</summary>
    public List<CurrencyAmountDto> DebtByCurrency { get; set; } = new();
}

// ===================================================================================================
// EXTRACTO (libro mayor) de la CUENTA POR COBRAR del cliente. Espejo del extracto del proveedor
// (SupplierAccountStatementDto) pero del lado VENTA y CRUZANDO todas las reservas del cliente: una linea
// cronologica por cada venta confirmada (cargo) y cada cobro (abono), con saldo corriente, SEPARADO por
// moneda. Se calcula EN EL SERVIDOR y reconcilia por construccion con el "Debe" por moneda del header
// (ReceivableByCurrency), a diferencia del extracto viejo que se armaba en el navegador con un techo de 500
// movimientos y no cerraba con el resumen.
//
// Fuente autoritativa: el saldo de cierre de cada moneda = suma de ConfirmedSale - TotalPaid (=
// ReservaMoneyByCurrency.Balance) sobre las reservas EN FIRME del cliente, la MISMA fuente que
// FinancePositionService.GetCustomerReceivableByCurrencyAsync (el header). Es venta confirmada como cargo (NO
// facturas): por eso cierra aunque la venta todavia no este facturada ("facturar tarde"). El saldo a favor del
// cliente (creditBalanceByCurrency del header) es un ledger APARTE (ClientCreditEntry) y NO entra en este
// extracto: el flujo normal traslada el sobrepago a ese bolsillo dejando cada reserva en 0.
// ===================================================================================================

/// <summary>
/// "Estado de Cuenta" del CLIENTE como LIBRO MAYOR (extracto estilo banco). Read-model DERIVADO (no se
/// persiste): una linea cronologica por cada venta confirmada (cargo) y cada cobro (abono), con saldo
/// corriente, SEPARADO por moneda.
///
/// <para><b>SEGURIDAD</b>: es del lado VENTA (venta/cobros del cliente, sin costo ni margen), asi que NO se
/// enmascara por <c>see_cost</c> — igual que el resto de la cuenta del cliente. El gate de acceso
/// (clientes.view + cobranzas.view) vive en el controller. <see cref="AmountsVisible"/> se conserva por
/// paridad de forma con el extracto del proveedor y viene siempre en true tras pasar el gate.</para>
/// </summary>
public class CustomerAccountStatementDto
{
    public Guid CustomerPublicId { get; set; }
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>
    /// Paridad de forma con el extracto del proveedor. En el lado VENTA NO hay masking de costo, asi que tras
    /// pasar el gate de permiso el caller SIEMPRE ve los montos: viene en true. Se expone para que el front
    /// comparta el mismo componente de render que el extracto del proveedor.
    /// </summary>
    public bool AmountsVisible { get; set; } = true;

    /// <summary>Un bloque por cada moneda presente (ARS/USD/...). Vacio si el cliente no tiene movimientos.</summary>
    public List<CustomerAccountStatementCurrencyBlockDto> Currencies { get; set; } = new();
}

/// <summary>
/// Bloque del extracto del cliente de UNA moneda: sus lineas en orden cronologico y su saldo de cierre.
/// </summary>
public class CustomerAccountStatementCurrencyBlockDto
{
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>Movimientos de la moneda en orden cronologico, con saldo corriente.</summary>
    public List<CustomerAccountStatementLineDto> Lines { get; set; } = new();

    /// <summary>
    /// Saldo de cierre de la moneda = saldo corriente de la ultima linea. Positivo = el cliente debe;
    /// 0 = saldado. Reconcilia con el "Debe" de esta moneda del header (ReceivableByCurrency) por construccion.
    /// </summary>
    public decimal ClosingBalance { get; set; }

    /// <summary>Credito no aplicado de esta moneda; no compensa el saldo de cierre de otras reservas.</summary>
    public decimal UnappliedCredit { get; set; }
}

/// <summary>
/// Una linea del extracto del cliente. Estilo banco: <see cref="Charge"/> SUMA a la deuda (venta confirmada),
/// <see cref="Credit"/> la RESTA (cobro). Una linea trae uno u otro (el otro en 0). <see cref="RunningBalance"/>
/// es el saldo acumulado de la moneda hasta esta linea inclusive. Como el extracto cruza varias reservas, cada
/// linea identifica su reserva (<see cref="ReservaPublicId"/> / <see cref="NumeroReserva"/>).
/// </summary>
public class CustomerAccountStatementLineDto
{
    /// <summary>Fecha del movimiento (alta de la reserva en la venta; fecha del cobro en el abono).</summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Tipo de movimiento documentado: Invoice, DebitNote, CreditNote, Payment o CreditApplication.
    /// Payment siempre mueve caja; CreditApplication es una compensacion explicita sin caja.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Texto legible del movimiento (nombre del expediente; nº de recibo o metodo del cobro).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Referencia del documento (nº de recibo del cobro), o null.</summary>
    public string? DocumentRef { get; set; }

    /// <summary>Reserva (expediente) a la que pertenece el movimiento. El front la usa para enlazar la linea.</summary>
    public Guid ReservaPublicId { get; set; }

    /// <summary>Numero de la reserva del movimiento (para mostrar "Reserva #N").</summary>
    public string? NumeroReserva { get; set; }

    /// <summary>
    /// PublicId del documento de origen: la reserva (lineas Sale) o el cobro (lineas Payment). El front lo
    /// cruza para colgar acciones por renglon. Es solo un identificador.
    /// </summary>
    public Guid? SourcePublicId { get; set; }

    /// <summary>Moneda de la linea (igual que la del bloque).</summary>
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>Monto que SUMA a la deuda (venta confirmada). 0 si la linea es un cobro.</summary>
    public decimal Charge { get; set; }

    /// <summary>Monto que RESTA de la deuda (cobro). 0 si la linea es una venta.</summary>
    public decimal Credit { get; set; }

    /// <summary>Saldo corriente de la moneda hasta esta linea inclusive.</summary>
    public decimal RunningBalance { get; set; }
}

public class CustomerAccountPaymentListItemDto
{
    public Guid PublicId { get; set; }
    public decimal Amount { get; set; }

    /// <summary>
    /// Moneda ISO 4217 del cobro ("ARS"/"USD"), tomada de <c>Payment.Currency</c> (la moneda real
    /// en la que entro la plata, sobre la que esta expresado <see cref="Amount"/>). El front la usa
    /// para agrupar y llevar saldo corriente POR MONEDA: el saldo en USD nunca se mezcla con el de ARS.
    /// Es un codigo limpio, no un entero interno; el front lo mapea a la etiqueta visible.
    /// </summary>
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>
    /// Moneda ISO 4217 ("ARS"/"USD") de la DEUDA a la que se IMPUTO el cobro (ADR-021 "pagar en otra
    /// moneda"): si el cobro entro en USD pero canceló deuda en ARS, aca viaja "ARS". Sale de
    /// <c>Payment.ImputedCurrency ?? Payment.Currency</c>. El extracto de la cuenta del cliente AGRUPA
    /// y lleva saldo corriente por ESTA moneda (no por <see cref="Currency"/>), para que el saldo por
    /// moneda reconcilie con lo que el cliente debe — igual criterio que el extracto por reserva.
    /// </summary>
    public string ImputedCurrency { get; set; } = Monedas.ARS;

    /// <summary>
    /// Monto del cobro EXPRESADO en la moneda imputada (<see cref="ImputedCurrency"/>): lo que
    /// efectivamente bajo del saldo de esa moneda. Sale de <c>Payment.ImputedAmount ?? Payment.Amount</c>.
    /// En un cobro NO cruzado coincide con <see cref="Amount"/>; en uno cruzado es el equivalente ya
    /// convertido. El extracto usa este monto para el saldo corriente por moneda.
    /// </summary>
    public decimal ImputedAmount { get; set; }

    public string Method { get; set; } = string.Empty;
    public DateTime PaidAt { get; set; }
    public string? Notes { get; set; }
    public Guid? ReservaPublicId { get; set; }
    public string? NumeroReserva { get; set; }
    public string? FileName { get; set; }
    public Guid? ReceiptPublicId { get; set; }
    public string? ReceiptNumber { get; set; }
    public string? ReceiptStatus { get; set; }
}
