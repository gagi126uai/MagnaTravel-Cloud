using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Estados del ciclo de vida de una Reserva (ADR-020, 2026-06-07). Los strings se persisten
/// asi en BD y los nombres de los miembros (ingles) reflejan la semantica funcional. La UI
/// muestra los labels en espanol.
///
/// <para>CICLO UNICO (ya NO hay flag ni ciclo dual; el rediseño Fase A+B con
/// <c>EnableSoldToSettleStates</c> y el estado "Sold"/Vendida murieron en ADR-020):</para>
///  - Quotation (Cotizacion): estado legacy para borradores históricos; no se crean nuevos.
///  - Budget (Presupuesto): estado inicial de toda propuesta nueva y documento que recibe el cliente.
///  - InManagement (En gestion): el cliente acepto; se gestionan los servicios con los operadores.
///    Reemplaza al viejo "Sold". El saldo del cliente nace POR SERVICIO CONFIRMADO en esta etapa.
///  - Confirmed (Confirmada): TODOS los servicios estan resueltos (aereo emitido, hotel confirmado,
///    etc.). Solo se ALCANZA y se ABANDONA-hacia-InManagement por el motor automatico
///    (ReservaAutoStateService) — NUNCA por transicion manual. La reserva queda bajo candado.
///  - Traveling (En viaje): el cliente ya esta viajando. ADR-036: SOLO LECTURA total (no se edita, ni se
///    cobra, ni se factura) — para llegar aca el cliente quedo saldado (prepago puro).
///  - Closed (Finalizada): cierre administrativo completo (saldo a favor o cero).
///  - Lost (Perdido): cotizacion/presupuesto que el cliente NO compro. Queda en historial.
///  - Cancelled (Cancelada): cancelada (flujo ADR-002 con factura viva, o transicion manual
///    sin factura viva ni cobros vivos desde {InManagement, Confirmed}).
///  - PendingOperatorRefund: ver abajo.
///
/// <para>Estado lateral legacy: "Archived" (soft-delete de reservas viejas) se referencia
/// como literal "Archived" — no esta como constante en este enum.</para>
/// </summary>
public static class EstadoReserva
{
    /// <summary>
    /// Estado legacy conservado para datos históricos. Las altas nuevas nacen en Budget.
    /// </summary>
    public const string Quotation = "Quotation";

    public const string Budget = "Budget";

    /// <summary>
    /// ADR-020 (2026-06-07): "En gestion". El cliente acepto el presupuesto y se gestionan los
    /// servicios con los operadores. Reemplaza al viejo "Sold" (Vendida). El saldo del cliente
    /// nace por servicio confirmado durante esta etapa. Edicion libre (sin candado).
    /// </summary>
    public const string InManagement = "InManagement";

    public const string Confirmed = "Confirmed";
    public const string Traveling = "Traveling";

    // ADR-036 (2026-06-21): el estado "A liquidar" (ToSettle) MURIO. El modelo es "prepago puro": el
    // cliente paga el 100% y el operador cobra el 100% ANTES del viaje, asi que ya no existe la etapa
    // de liquidacion posterior. Las reservas que quedaron en ToSettle se re-mapean en la migracion
    // Adr036_M1 a Closed (si estaban saldadas) o Traveling (si tenian saldo). La const se elimino de
    // este enum; cualquier referencia residual debe migrar a Closed/Traveling segun corresponda.

    public const string Closed = "Closed";

    /// <summary>
    /// ADR-020 (2026-06-07): "Perdido". Una cotizacion o presupuesto que el cliente no compro.
    /// Queda en el historial. Solo se alcanza desde {Quotation, Budget} y solo si NO hay pagos
    /// vivos. El "el cliente volvio a interesarse" se resuelve con revert al estado de origen
    /// (el <c>FromStatus</c> de la ultima transicion a Lost en ReservaStatusChangeLog).
    /// </summary>
    public const string Lost = "Lost";

    public const string Cancelled = "Cancelled";

    /// <summary>
    /// ADR-002 FC1 (2026-05-13): cancelacion con el cliente completada pero
    /// el operador aun no devolvio el dinero. La reserva ya no es operativa,
    /// pero queda visible en alertas de deuda y excluida de revenue queries
    /// hasta que llegue el refund (T2 del flujo) o se marque AbandonedByOperator.
    /// </summary>
    public const string PendingOperatorRefund = "PendingOperatorRefund";

    /// <summary>
    /// Estados "cobrables": la reserva tiene una venta cerrada con saldo que se le puede pedir al cliente
    /// (o aplicarle un saldo a favor). ADR-020: InManagement (En gestion) reemplaza al viejo Sold.
    /// Quotation/Budget/Lost/Cancelled NO entran (todavia no hay —o ya no hay— venta vigente).
    ///
    /// <para>ADR-036 (2026-06-21, prepago puro): se quito <c>ToSettle</c> (el estado murio). Ademas
    /// <c>Traveling</c> (En viaje) queda como SOLO LECTURA total: en viaje no se cobra (el viaje ya
    /// empezo, el cobro debio cerrarse para llegar a Traveling). Por eso Traveling ya NO esta en esta
    /// lista — cobrar arranca y termina antes del viaje. La asimetria con
    /// <see cref="SaleFirmStatuses"/> (que tampoco trae Traveling) es intencional y esta cubierta por
    /// el test cruzado de coherencia.</para>
    ///
    /// <para>FC4 (2026-06-14): se MOVIO aca desde <c>PaymentService.ActiveCollectionStatuses</c> (que era
    /// privado) para que el saldo a favor aplicado (<c>ClientCreditService.HandleAppliedToNewBookingAsync</c>)
    /// use exactamente la misma lista que el cobro normal — un saldo a favor solo se aplica a reservas a las
    /// que tambien se les podria cobrar. Fuente unica para no divergir.</para>
    /// </summary>
    public static readonly string[] ActiveCollectionStatuses =
    {
        InManagement,
        Confirmed
    };

    /// <summary>
    /// ADR-033 (2026-06-16): VENTA FIRME = la reserva ya paso por venta (NO es pre-venta ni descartada) y
    /// puede tener una cuenta por cobrar real, INCLUIDO el estado terminal Closed (Finalizada). La unica
    /// diferencia con <see cref="ActiveCollectionStatuses"/> es justamente <see cref="Closed"/>: una reserva
    /// finalizada esta operativamente terminada (el viaje paso) pero puede seguir financieramente abierta
    /// (deuda viva). Esta lista es la base de:
    ///   - la cobrabilidad por deuda real (ver <see cref="Reserva.IsCollectable"/>): cobrar se permite en
    ///     {InManagement, Confirmed, Closed} cuando hay deuda;
    ///   - el concepto "tiene cuenta por cobrar" que usan AR / cobranza / saldo del cliente / alertas
    ///     (FinancePositionService.ReceivableDebtStatuses, que expone exactamente esta lista).
    ///
    /// <para>ADR-036 (2026-06-21, prepago puro): se quito <c>ToSettle</c> (estado eliminado) y tambien
    /// <c>Traveling</c>. Decision del dueño: "En viaje" es SOLO LECTURA total — no se cobra ni se factura
    /// en viaje, porque para llegar a Traveling el cliente ya tuvo que quedar saldado (Balance &lt;= 0). Por
    /// eso Traveling no es "firme cobrable": no hay nada que cobrarle. Una reserva Traveling con deuda no
    /// deberia existir (el gate de pase a Traveling la frena); si por dato historico existiera, no se la
    /// trata como cuenta por cobrar viva. Esta exclusion es INTENCIONAL (camino estricto) y esta cubierta
    /// por el test cruzado de coherencia capacidades vs EnsureCollectable.</para>
    ///
    /// <para>Quotation/Budget/Lost (pre-venta o descartado-sin-venta) NUNCA son firmes. Cancelled y
    /// PendingOperatorRefund quedan FUERA a proposito: su plata se resuelve por el circuito de cancelacion
    /// /refund (NC, saldo a favor, devolucion del operador), no por el recibo de cobro normal.</para>
    ///
    /// <para>IMPORTANTE: esta lista NO reemplaza a <see cref="ActiveCollectionStatuses"/> para el concepto
    /// "venta operativa viva" (lead ganado). Cerrar una reserva NO debe marcar su lead como Ganado, por eso
    /// el lead-won sigue usando ActiveCollectionStatuses (sin Closed). Son ejes distintos (ADR-033 B1).</para>
    /// </summary>
    public static readonly string[] SaleFirmStatuses =
    {
        InManagement,
        Confirmed,
        Closed
    };

    /// <summary>
    /// ADR-040 (cuenta corriente del cliente, 2026-06-26): estados que cuentan para la EXPOSICION DE CREDITO de
    /// un cliente — la deuda viva que consume su limite de cuenta corriente. A diferencia de
    /// <see cref="SaleFirmStatuses"/> (el AR de cobranza), esta lista SI INCLUYE <see cref="Traveling"/>.
    ///
    /// <para><b>Por que difiere de <c>SaleFirmStatuses</c></b> (review B1, leccion ADR-033 "no mezclar sets de
    /// estados"): el AR de cobranza saca Traveling a proposito porque en prepago puro una reserva NO entra a "En
    /// viaje" debiendo, asi que una Traveling con deuda "no deberia existir". Con cuenta corriente eso CAMBIA: un
    /// cliente a cuenta SI viaja debiendo, asi que sus reservas Traveling con saldo son deuda real y viva que
    /// DEBE contar contra su limite. Si reusaramos <c>SaleFirmStatuses</c> (sin Traveling) la exposicion
    /// quedaria subestimada justo para los clientes que la feature habilita — un agujero de plata. Por eso es
    /// una lista DEDICADA y SEPARADA, no un alias.</para>
    ///
    /// <para>Cubre todo el ciclo en firme donde el cliente puede deber: en gestion, confirmada, en viaje y
    /// finalizada con deuda. Pre-venta (Quotation/Budget) y descartados (Lost/Cancelled/PendingOperatorRefund)
    /// NO consumen credito (no hay venta firme exigible).</para>
    /// </summary>
    public static readonly string[] CreditExposureStatuses =
    {
        InManagement,
        Confirmed,
        Traveling,
        Closed
    };

    /// <summary>
    /// ADR-032 (2026-06-15): FUENTE UNICA de la regla "se puede cobrar / tocar plata en este estado".
    /// Antes esta pregunta estaba escrita de tres formas distintas (PaymentService solo bloqueaba Budget,
    /// el endpoint anidado no bloqueaba nada, y la cobranza/FC4 usaban la lista canonica). Ahora los tres
    /// caminos convergen aca: cobrable == el estado esta en <see cref="ActiveCollectionStatuses"/>.
    ///
    /// <para>ADR-033 (2026-06-16): este predicado de SOLO-ESTADO se conserva como helper, pero ya NO es la
    /// regla de cobrabilidad completa. La cobrabilidad ahora es "venta firme + deuda real" (ver
    /// <see cref="IsSaleFirmStatus"/> y <see cref="Reserva.IsCollectable"/>). FC4 (saldo a favor) lo
    /// reemplazo por <see cref="IsSaleFirmStatus"/> para permitir destino Closed con deuda.</para>
    ///
    /// <para>Comparacion case-insensitive para alinearse con el resto del dominio (los strings se
    /// persisten tal cual, pero no dependemos del casing exacto). Un estado nulo/vacio NO es cobrable.</para>
    /// </summary>
    public static bool IsCollectableStatus(string? status)
        => ContainsStatus(ActiveCollectionStatuses, status);

    /// <summary>
    /// ADR-033 (2026-06-16): true si el estado es una VENTA FIRME (ver <see cref="SaleFirmStatuses"/>,
    /// incluye Closed). Es la mitad-de-estado de la regla de cobrabilidad: la otra mitad es que haya deuda
    /// real (<see cref="Reserva.IsCollectable"/> combina ambas). Tambien lo usa FC4 para aceptar destino
    /// Closed con deuda sin abrir el cobro a estados pre-venta/terminales-no-firmes.
    ///
    /// <para>Comparacion case-insensitive; estado nulo/vacio NO es firme.</para>
    /// </summary>
    public static bool IsSaleFirmStatus(string? status)
        => ContainsStatus(SaleFirmStatuses, status);

    /// <summary>
    /// ADR-048 B3 (2026-07-17, modelo de estados derivados): el PAR de estados que significan "esta
    /// reserva quedo SIN EFECTO" (una anulacion, completa o esperando que el operador termine de
    /// devolver la plata). Es la UNICA definicion de este concepto en todo el dominio — antes estaba
    /// hardcodeada en varios lugares sueltos (backend Y frontend) comparando el string a mano, y
    /// bastaba con olvidarse de uno de los dos estados para que una pantalla mintiera (ej: una reserva
    /// <see cref="PendingOperatorRefund"/> mostrando "Debe" en vez del circuito de anulacion).
    ///
    /// <para><b>Por que un PAR y no un solo estado</b>: <see cref="PendingOperatorRefund"/> es el paso
    /// intermedio normal de TODA anulacion mientras el operador todavia no termino de devolver la
    /// plata (ver <see cref="PendingOperatorRefund"/>); recien pasa a <see cref="Cancelled"/> cuando esa
    /// devolucion se completa. Las dos son "sin efecto" para el cliente: el viaje no va a pasar, la
    /// diferencia es solo si queda un tramite de plata pendiente puertas adentro.</para>
    /// </summary>
    public static readonly string[] VoidedStatuses =
    {
        Cancelled,
        PendingOperatorRefund
    };

    /// <summary>
    /// True si el estado significa "reserva sin efecto" (ver <see cref="VoidedStatuses"/>). Es el booleano
    /// <c>IsVoided</c> que expone <c>ReservaDto</c>/<c>ReservaListDto</c> al frontend: el front deja de
    /// comparar el string de estado a mano y lee este campo (ADR-048 T4, INV-048-08).
    /// </summary>
    public static bool IsVoidedStatus(string? status)
        => ContainsStatus(VoidedStatuses, status);

    /// <summary>Busqueda case-insensitive de un estado en una lista (helper interno de las reglas de estado).</summary>
    private static bool ContainsStatus(string[] statuses, string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        foreach (var candidate in statuses)
        {
            if (string.Equals(candidate, status, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

public class Reserva : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    
    [Required]
    [MaxLength(50)]
    public string NumeroReserva { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = EstadoReserva.Budget;
    
    // Dates
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? ClosedAt { get; set; }

    // Payer/Main Client
    public int? PayerId { get; set; }
    public Customer? Payer { get; set; }

    // Commercial traceability
    public int? SourceQuoteId { get; set; }
    public Quote? SourceQuote { get; set; }

    public int? SourceLeadId { get; set; }
    public Lead? SourceLead { get; set; }

    public string? ResponsibleUserId { get; set; }

    /// <summary>
    /// Snapshot denormalizado del FullName del usuario responsable al momento de
    /// asignacion. Se mantiene aca para evitar que Domain dependa de
    /// ASP.NET Identity (ApplicationUser vive en Infrastructure). Patron consistente
    /// con Voucher.CreatedByUserName.
    /// </summary>
    [MaxLength(200)]
    public string? ResponsibleUserName { get; set; }

    [MaxLength(50)]
    public string? WhatsAppPhoneOverride { get; set; }

    // Financials
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCost { get; set; } = 0;

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalSale { get; set; } = 0;

    /// <summary>
    /// ADR-020 (2026-06-07): venta CONFIRMADA = suma de SalePrice de los servicios RESUELTOS
    /// (<see cref="TravelApi.Domain.Reservations.ServiceResolutionRules"/>.IsResolved). Es la deuda
    /// EXIGIBLE al cliente: un servicio recien "Solicitado" NO suma. Se diferencia de
    /// <see cref="TotalSale"/> (valor comercial del presupuesto = todos los no cancelados, lo que
    /// el cliente ve cotizado). El saldo del cliente es <see cref="Balance"/> = ConfirmedSale - TotalPaid.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal ConfirmedSale { get; set; } = 0;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Balance { get; set; } = 0;

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalPaid { get; set; } = 0;

    // Passenger counts (only used while Status = Budget; replaced by individual Passengers when promoted)
    public int AdultCount { get; set; } = 0;
    public int ChildCount { get; set; } = 0;
    public int InfantCount { get; set; } = 0;

    // Navigation
    public ICollection<ServicioReserva> Servicios { get; set; } = new List<ServicioReserva>();
    public ICollection<Passenger> Passengers { get; set; } = new List<Passenger>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    
    // Servicios específicos
    public ICollection<HotelBooking> HotelBookings { get; set; } = new List<HotelBooking>();
    public ICollection<TransferBooking> TransferBookings { get; set; } = new List<TransferBooking>();
    public ICollection<PackageBooking> PackageBookings { get; set; } = new List<PackageBooking>();
    // Asistencias al viajero (seguros). Tipo de servicio propio, espejo de los otros 4.
    public ICollection<AssistanceBooking> AssistanceBookings { get; set; } = new List<AssistanceBooking>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
    public ICollection<FlightSegment> FlightSegments { get; set; } = new List<FlightSegment>();
    public ICollection<ReservaAttachment> Attachments { get; set; } = new List<ReservaAttachment>();
    public ICollection<Voucher> Vouchers { get; set; } = new List<Voucher>();
    public ICollection<WhatsAppDelivery> WhatsAppDeliveries { get; set; } = new List<WhatsAppDelivery>();
    public ICollection<MessageDelivery> MessageDeliveries { get; set; } = new List<MessageDelivery>();
    public ICollection<ManualCashMovement> ManualCashMovements { get; set; } = new List<ManualCashMovement>();

    // ADR-020 F4 (candado): autorizaciones de edicion bajo candado (estado Confirmada en adelante).
    public ICollection<ReservaEditAuthorization> EditAuthorizations { get; set; } = new List<ReservaEditAuthorization>();

    /// <summary>
    /// Motivo por el que la reserva quedo "confirmada con cambios / revisar". Lo SETEA el motor de estados
    /// (ReservaAutoStateService) cuando una reserva confirmada deja de tener todos sus servicios resueltos (el
    /// operador cancelo/reprogramo, se agrego un servicio nuevo, o se quedo sin servicios vivos): la reserva NO
    /// regresa de estado (eso se elimino el 2026-06-24, antes era una regresion automatica Confirmed ->
    /// InManagement), queda EN Confirmed pero marcada (<see cref="HasUnacknowledgedChanges"/>). Lo LIMPIA (null)
    /// el endpoint acknowledge-changes cuando una persona da el OK, junto con la marca. El frontend lo usa para
    /// mostrar la franja informativa con el motivo. Es informativo, no afecta plata.
    ///
    /// <para>El nombre del campo es historico (cuando existia la regresion automatica). Hoy significa "motivo
    /// de revision", no "motivo de regresion". Se conservo el nombre/columna para no migrar la base.</para>
    /// </summary>
    [MaxLength(300)]
    public string? LastRegressionReason { get; set; }

    /// <summary>Cuando se marco el ultimo motivo de revision (par de <see cref="LastRegressionReason"/>).</summary>
    public DateTime? LastRegressionAt { get; set; }

    // === ADR-027 (auditoria ERP, hallazgo #10): "confirmada con cambios" ===
    // Cuando el operador confirma un servicio PERO con otro precio/condicion, el vendedor edita el
    // servicio para reflejarlo. Si la reserva YA estaba en un estado vivo (En gestion en adelante),
    // ese cambio recalcula el saldo del cliente solo (ReservaMoneyPersister) Y deja la reserva
    // "marcada" para que el dueño la revise y de su OK. Nada cambia a sus espaldas.

    /// <summary>
    /// ADR-027: true cuando se edito el precio/costo de un servicio (SalePrice o NetCost) de esta
    /// reserva estando en un estado VIVO (InManagement/Confirmed/Traveling; ADR-036 quito ToSettle) y todavia nadie
    /// dio el OK. La pone el trigger de edicion; la limpia el endpoint acknowledge-changes. El front
    /// muestra la marca "confirmada con cambios" y el bucket de alertas la lista mientras este en true.
    /// </summary>
    public bool HasUnacknowledgedChanges { get; set; } = false;

    /// <summary>
    /// ADR-027: cuando se marco POR PRIMERA VEZ el cambio sin revisar (par de
    /// <see cref="HasUnacknowledgedChanges"/>). Una segunda edicion mientras sigue sin acusar NO pisa
    /// esta fecha: representa "desde cuando hay algo pendiente de revisar". Se pone en null al acusar.
    /// </summary>
    public DateTime? ChangesPendingSince { get; set; }

    /// <summary>ADR-027: usuario que dio el OK a los cambios (auditoria). Se setea al acusar.</summary>
    [MaxLength(200)]
    public string? ChangesAckByUserId { get; set; }

    /// <summary>ADR-027: nombre snapshot de quien dio el OK (par de <see cref="ChangesAckByUserId"/>).</summary>
    [MaxLength(200)]
    public string? ChangesAckByUserName { get; set; }

    /// <summary>ADR-027: cuando se dio el OK a los cambios (auditoria). Par de <see cref="ChangesAckByUserId"/>.</summary>
    public DateTime? ChangesAckAt { get; set; }

    /// <summary>
    /// ADR-027 (detalle de cambios, 2026-06-13): lista de cambios de precio/costo pendientes de revisar
    /// (que servicio, que campo, de cuanto a cuanto). Se acumulan mientras
    /// <see cref="HasUnacknowledgedChanges"/> es true y se borran de una al dar el OK. El front las muestra
    /// en la franja "confirmada con cambios". Ver <see cref="ReservaPendingChange"/>.
    /// </summary>
    public ICollection<ReservaPendingChange> PendingChanges { get; set; } = new List<ReservaPendingChange>();

    // ====================================================================================
    // ADR-033 (2026-06-16): COBRABILIDAD = venta firme + deuda real (NO solo-estado como en ADR-032).
    // Una reserva Finalizada (Closed) con deuda SI es cobrable; una firme ya saldada NO lo es. El alta
    // de cobro usa esta regla; editar/borrar cobro YA NO mira el estado (solo guardas fiscal/puente).
    // Ver EstadoReserva.IsSaleFirmStatus y Reserva.IsCollectable.
    // ====================================================================================

    /// <summary>
    /// ADR-033 (2026-06-16): mensaje cuando se intenta cobrar en una reserva que NO es venta firme
    /// (pre-venta: Cotizacion/Presupuesto/Perdido, o terminal-no-firme: Cancelada/esperando refund).
    /// Sin datos sensibles (ni montos ni nombres).
    /// </summary>
    public const string NotSaleFirmForChargeMessage =
        "No se puede registrar un cobro en este estado de la reserva. Pasala a En gestion primero.";

    /// <summary>
    /// ADR-033 (2026-06-16): mensaje cuando la reserva ES venta firme pero ya no tiene saldo pendiente
    /// (Balance &lt;= 0). Cobrar sobre saldo cero seria un sobrepago a ciegas; el excedente legitimo se
    /// maneja por el puente a saldo a favor, no abriendo el cobro normal. Sin datos sensibles.
    /// </summary>
    public const string NoPendingBalanceForChargeMessage =
        "Esta reserva no tiene saldo pendiente para cobrar.";

    /// <summary>
    /// ADR-033 (2026-06-16): COBRABILIDAD = venta firme (ver <see cref="EstadoReserva.IsSaleFirmStatus"/>,
    /// incluye Closed) Y deuda real (<see cref="Balance"/> &gt; 0). Antes (ADR-032) era solo-estado; ahora
    /// una reserva Finalizada (Closed) con deuda SI es cobrable, y una firme ya saldada NO lo es.
    ///
    /// <para>PRECONDICION DURA (ADR-033 D3): este predicado debe evaluarse con el <see cref="Balance"/> YA
    /// recalculado. Los call-sites de alta cargan la reserva con su Balance persistido (fresco) antes del
    /// guard; si en el futuro alguien recalcula despues del guard, el resultado seria falso.</para>
    ///
    /// <para>El <see cref="Balance"/> es el escalar (suma cross-moneda). Es un PISO ("hay ALGO de deuda");
    /// la imputacion por moneda la resuelve el flujo de pago (ReservaMoneyByCurrency, A5/B4). No reemplaza
    /// al calculo por moneda.</para>
    /// </summary>
    public bool IsCollectable() => EstadoReserva.IsSaleFirmStatus(Status) && Balance > 0;

    /// <summary>
    /// ADR-033 (2026-06-16): guard de ALTA de cobro. Corta con <see cref="InvalidOperationException"/> (los
    /// controllers de cobro ya la mapean a 409) en dos casos, con mensajes distintos para no confundir:
    ///   (a) NO es venta firme (pre-venta/terminal-no-firme) -&gt; "pasala a En gestion primero";
    ///   (b) es firme pero sin saldo pendiente (Balance &lt;= 0) -&gt; "no hay saldo pendiente para cobrar".
    ///
    /// <para>Se usa en los DOS puntos de entrada de alta de pago del usuario (PaymentService.CreatePaymentAsync
    /// y el endpoint anidado ReservaService.AddPaymentAsync). NO se usa en los caminos internos (puentes de
    /// sobrepago/FC4, cancelacion/refund), que crean Payments directamente y no pasan por aca.</para>
    /// </summary>
    public void EnsureCollectable()
    {
        if (!EstadoReserva.IsSaleFirmStatus(Status))
            throw new InvalidOperationException(NotSaleFirmForChargeMessage);

        if (Balance <= 0)
            throw new InvalidOperationException(NoPendingBalanceForChargeMessage);
    }
}
