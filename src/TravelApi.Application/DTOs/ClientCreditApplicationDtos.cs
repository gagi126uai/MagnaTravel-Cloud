namespace TravelApi.Application.DTOs;

/// <summary>
/// FC4 (saldo a favor del cliente aplicado a otra reserva): vista del saldo a favor DISPONIBLE del cliente,
/// agrupado por moneda. Espejo conceptual de <see cref="SupplierCreditOverviewDto"/> del lado operador.
///
/// <para>El total por moneda es la suma de <c>RemainingBalance</c> de los bolsillos activos
/// (<see cref="ClientCreditEntry"/>), que es la fuente AUTORITATIVA: el saldo a favor del cliente es un ledger
/// de PRIMERA CLASE (se decrementa atomicamente en cada retiro), no un numero derivado. A diferencia del lado
/// operador, los montos NO se enmascaran por <c>cobranzas.see_cost</c>: es plata del cliente (venta/cobranza),
/// no un costo de la agencia.</para>
/// </summary>
public class ClientCreditOverviewDto
{
    public Guid CustomerPublicId { get; set; }
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>Una linea por moneda con saldo a favor disponible (&gt; 0).</summary>
    public List<ClientCreditCurrencyLineDto> Currencies { get; set; } = new();

    /// <summary>
    /// Aplicaciones VIVAS de saldo a favor a otras reservas del cliente (cada retiro
    /// <see cref="ClientCreditWithdrawal"/> de kind <c>AppliedToNewBooking</c> con su Payment puente todavia
    /// activo). El front las lista en el extracto para revertir cada una por su <c>ApplicationPublicId</c>. Asi
    /// un apply que drenó N bolsillos queda como N filas, cada una revertible de forma independiente.
    /// </summary>
    public List<ClientCreditApplicationLineDto> ActiveApplications { get; set; } = new();
}

/// <summary>
/// Tanda D1 (2026-07-16): valores posibles de <see cref="ClientCreditApplicationLineDto.DestinationKind"/>. El
/// mismo mecanismo de puente (<c>ClientCreditWithdrawal</c> + Payment puente) hoy tiene DOS destinos posibles
/// (otra reserva del cliente, o una multa de una reserva anulada); el front necesita saber cual es cual para
/// etiquetar la fila correctamente ("aplicado a R-XXXX" vs "aplicado a la multa de R-XXXX").
/// </summary>
public static class ClientCreditApplicationDestinationKind
{
    public const string Reserva = "reserva";
    public const string Penalty = "multa";
}

/// <summary>
/// FC4: una aplicacion VIVA de saldo a favor (un retiro <c>AppliedToNewBooking</c> con puente activo). Es la
/// fila revertible que el front muestra en el extracto del cliente ("Saldo a favor aplicado a R-XXXX −$monto").
///
/// <para><b>Tanda D1 (2026-07-16)</b>: ahora tambien lista las aplicaciones contra una MULTA (puente
/// <c>SaldoAFavorAplicadoAMulta</c>). <see cref="DestinationKind"/> distingue el caso; cuando es
/// <see cref="ClientCreditApplicationDestinationKind.Penalty"/>, <see cref="TargetReservaPublicId"/> /
/// <see cref="TargetReservaNumber"/> siguen apuntando a la reserva ANULADA dueña de la multa (el puente vive
/// ahi), y <see cref="DebitNotePublicId"/> / <see cref="DebitNoteDisplayNumber"/> identifican la Nota de Debito
/// puntual que se pago.</para>
/// </summary>
public class ClientCreditApplicationLineDto
{
    /// <summary>PublicId del retiro a revertir (lo recibe el endpoint <c>.../credit/applications/{publicId}/reverse</c>).</summary>
    public Guid ApplicationPublicId { get; set; }

    /// <summary>PublicId del bolsillo del que salio.</summary>
    public Guid EntryPublicId { get; set; }

    public string Currency { get; set; } = "ARS";
    public decimal Amount { get; set; }

    public Guid TargetReservaPublicId { get; set; }
    public string? TargetReservaNumber { get; set; }

    /// <summary>Titular de la reserva destino. Por INV-093 siempre es el MISMO cliente del saldo a favor.</summary>
    public string? TargetReservaHolderName { get; set; }

    public DateTime AppliedAt { get; set; }

    /// <summary>Ver <see cref="ClientCreditApplicationDestinationKind"/>.</summary>
    public string DestinationKind { get; set; } = ClientCreditApplicationDestinationKind.Reserva;

    /// <summary>Solo cuando <see cref="DestinationKind"/> es <c>Penalty</c>: la Nota de Debito pagada.</summary>
    public Guid? DebitNotePublicId { get; set; }

    /// <summary>Solo cuando <see cref="DestinationKind"/> es <c>Penalty</c>: numero legible de la ND (ej. "00003-00000123").</summary>
    public string? DebitNoteDisplayNumber { get; set; }
}

/// <summary>Saldo a favor disponible del cliente en UNA moneda, con el detalle de bolsillos activos.</summary>
public class ClientCreditCurrencyLineDto
{
    public string Currency { get; set; } = "ARS";

    /// <summary>Suma de <c>RemainingBalance</c> de los bolsillos activos en esta moneda. Disponible para aplicar.</summary>
    public decimal AvailableBalance { get; set; }

    public List<ClientCreditEntryLineDto> Entries { get; set; } = new();
}

/// <summary>Un bolsillo de saldo a favor del cliente (entry) con su saldo disponible.</summary>
public class ClientCreditEntryLineDto
{
    public Guid PublicId { get; set; }
    public decimal CreditedAmount { get; set; }
    public decimal RemainingBalance { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// FC4: pedido de APLICAR saldo a favor del cliente a otra reserva del mismo cliente (misma moneda). El backend
/// drena los bolsillos por antiguedad (FIFO) hasta cubrir el <see cref="Amount"/>.
/// </summary>
public record ApplyClientCreditRequest(
    string Currency,
    decimal Amount,
    Guid TargetReservaPublicId);

/// <summary>
/// FC4: pedido de REVERTIR una aplicacion de saldo a favor del cliente. El motivo es OPCIONAL (decision del
/// dueño): puede venir null/vacio y la reversa procede igual; si viene, se registra en la auditoria.
/// </summary>
public record ReverseClientCreditApplicationRequest(string? Reason);

/// <summary>FC4: resultado de aplicar/revertir saldo a favor del cliente (para el front).</summary>
public class ClientCreditApplicationResultDto
{
    /// <summary>
    /// PublicId de la "aplicacion" (el <see cref="ClientCreditWithdrawal"/> de kind <c>AppliedToNewBooking</c>).
    /// Si la aplicacion drena VARIOS bolsillos (FIFO), aca viaja el PRIMER retiro creado; cada retiro se revierte
    /// de forma independiente con su propio PublicId (mismo contrato que el lado operador).
    /// </summary>
    public Guid ApplicationPublicId { get; set; }

    /// <summary>PublicId del bolsillo (<see cref="ClientCreditEntry"/>) del primer retiro.</summary>
    public Guid EntryPublicId { get; set; }

    public string Currency { get; set; } = "ARS";
    public decimal Amount { get; set; }
    public Guid TargetReservaPublicId { get; set; }
    public bool IsReversal { get; set; }

    /// <summary>Saldo a favor que le queda al cliente en esa moneda DESPUES del movimiento.</summary>
    public decimal AvailableBalanceAfter { get; set; }

    /// <summary>
    /// Tanda D1 (2026-07-16): solo viene cuando la aplicacion (o su reversa) fue contra una MULTA — la Nota de
    /// Debito pagada. <c>null</c> para una aplicacion contra otra reserva (el caso original de FC4).
    /// </summary>
    public Guid? DebitNotePublicId { get; set; }
}

// =============================================================================
// Tanda D1 (2026-07-16): saldo a favor del cliente APLICADO CONTRA UNA MULTA + neteo automatico en devolucion.
// =============================================================================

/// <summary>
/// Pedido de aplicar saldo a favor del cliente contra UNA multa (Nota de Debito de una reserva anulada de ese
/// mismo cliente, misma moneda). El backend drena los bolsillos por antiguedad (FIFO) hasta cubrir
/// <see cref="Amount"/>, topeado por el saldo pendiente REAL de la ND (nunca se sobre-aplica).
/// </summary>
public record ApplyCreditToPenaltyRequest(
    string Currency,
    decimal Amount,
    Guid DebitNotePublicId);

/// <summary>
/// Una multa ABIERTA (con comprobante fiscal aprobado, saldo pendiente &gt; 0) candidata a recibir saldo a favor
/// en el neteo de una devolucion. Es una fila de <see cref="RefundNettingPreviewDto.OpenPenalties"/>.
/// </summary>
public class RefundNettingPenaltyLineDto
{
    public Guid ReservaPublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public Guid DebitNotePublicId { get; set; }
    public string DebitNoteDisplayNumber { get; set; } = string.Empty;

    /// <summary>Saldo pendiente de la ND en este momento (puede cambiar entre el preview y la confirmacion).</summary>
    public decimal OutstandingAmount { get; set; }
}

/// <summary>
/// Vista PREVIA (solo lectura) de "si el cliente pide su saldo a favor de vuelta ahora, cuanto se le devuelve
/// despues de netear contra sus multas abiertas en esa moneda". El backend revalida TODO al confirmar — esta
/// vista es orientativa para que el cashier explique el numero antes de tocar nada.
/// </summary>
public class RefundNettingPreviewDto
{
    public string Currency { get; set; } = "ARS";

    /// <summary>Saldo a favor disponible del cliente en esta moneda (Σ RemainingBalance de sus bolsillos).</summary>
    public decimal AvailableCredit { get; set; }

    /// <summary>Multas abiertas del cliente en esta moneda, mas antigua primero (orden en que se netean).</summary>
    public List<RefundNettingPenaltyLineDto> OpenPenalties { get; set; } = new();

    /// <summary>Suma de <see cref="RefundNettingPenaltyLineDto.OutstandingAmount"/>.</summary>
    public decimal TotalOpenPenalties { get; set; }

    /// <summary>Lo que efectivamente se le devolveria al cliente: <c>max(0, AvailableCredit - TotalOpenPenalties)</c>.</summary>
    public decimal NetToRefund { get; set; }

    /// <summary>Texto en criollo armado server-side, ej. "Te devolvemos $7.000 = $10.000 a favor − $3.000 de multa".</summary>
    public string PlainExplanation { get; set; } = string.Empty;
}

/// <summary>
/// Pedido de devolver el saldo a favor del cliente en una moneda, NETEANDO automaticamente contra sus multas
/// abiertas de esa moneda antes de calcular el egreso. NO lleva <c>Amount</c>: el neto SIEMPRE es el que resulta
/// de netear todo el saldo disponible contra toda la deuda de multas abierta (decision del dueño) — nunca un
/// monto tecleado a mano.
/// </summary>
public record RefundWithNettingRequest(
    string Currency,
    string RefundMethod,
    string? Reference);

/// <summary>Resultado de la devolucion con neteo: el detalle de cada multa pagada + el egreso final.</summary>
public class RefundWithNettingResultDto
{
    public string Currency { get; set; } = "ARS";

    /// <summary>Saldo a favor disponible ANTES de esta operacion.</summary>
    public decimal AvailableCreditBefore { get; set; }

    /// <summary>Una fila por cada multa que efectivamente se pago con saldo a favor en esta operacion.</summary>
    public List<ClientCreditApplicationResultDto> PenaltyApplications { get; set; } = new();

    /// <summary>Suma de lo aplicado contra multas.</summary>
    public decimal TotalAppliedToPenalties { get; set; }

    /// <summary>Lo que efectivamente se le devolvio al cliente (egreso de caja).</summary>
    public decimal NetRefunded { get; set; }

    public string RefundMethod { get; set; } = string.Empty;

    /// <summary>
    /// PublicId del PRIMER retiro del egreso (si el neto drena varios bolsillos, cada uno queda como su propio
    /// <c>ClientCreditWithdrawal</c>; este es el primero, mismo criterio que <see cref="ClientCreditApplicationResultDto.ApplicationPublicId"/>).
    /// <c>null</c> si el neto a devolver fue 0 (todo el saldo se consumio neteando multas).
    /// </summary>
    public Guid? WithdrawalPublicId { get; set; }

    /// <summary>Texto en criollo del comprobante del egreso, con el desglose exacto (saldo a favor, multas descontadas, total devuelto).</summary>
    public string ReceiptText { get; set; } = string.Empty;
}
