using System.Collections.Generic;
using System.Linq;
using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-035 (2026-06-19): el resultado de UNA capacidad. <see cref="Allowed"/> dice si la accion se puede
/// ahora; <see cref="Reason"/> es el motivo LEGIBLE (en español, sin montos ni costos) cuando no se puede.
///
/// <para>El motivo respeta el enmascarado <c>see_cost</c>: nunca menciona costo/deuda con el proveedor. Es
/// texto neutro de estado, igual que los mensajes que ya viven en <see cref="Reserva"/>
/// (NotSaleFirmForChargeMessage, etc.).</para>
/// </summary>
public sealed record Cap(bool Allowed, string? Reason)
{
    /// <summary>La accion se puede. Sin motivo.</summary>
    public static readonly Cap Yes = new(true, null);

    /// <summary>La accion NO se puede, con el motivo legible (sin datos sensibles).</summary>
    public static Cap No(string reason) => new(false, reason);
}

/// <summary>
/// ADR-035 (2026-06-19): contexto MINIMO para evaluar las capacidades de una reserva. Son los datos que la
/// reserva ya tiene cargados al armar su DTO; la politica es PURA y no consulta base de datos.
/// </summary>
/// <param name="Status">Estado operativo actual de la reserva (<see cref="EstadoReserva"/>).</param>
/// <param name="Balance">Saldo escalar (surrogate). &gt; 0 = hay deuda; &lt;= 0 = saldado o saldo a favor.</param>
/// <param name="HasLiveCae">True si la reserva tiene una factura AFIP con CAE vivo (comprobante fiscal sellado).</param>
/// <param name="HasLiveVoucher">True si la reserva tiene un voucher emitido vivo (no anulado).</param>
/// <param name="HasLiveEditAuth">True si hay una autorizacion de edicion bajo candado VIVA (ventana abierta).</param>
/// <param name="HasAnyPayment">True si la reserva tiene al menos un cobro registrado (real o puente).</param>
public sealed record ReservaCapabilityContext(
    string Status,
    decimal Balance,
    bool HasLiveCae,
    bool HasLiveVoucher,
    bool HasLiveEditAuth,
    bool HasAnyPayment);

/// <summary>
/// ADR-035 (2026-06-19): conjunto de capacidades de una reserva, ya resueltas. Es la respuesta de
/// <see cref="ReservaCapabilities.For"/>: una <see cref="Cap"/> por accion + las listas de transiciones
/// legales. El frontend la consume para pintar cada boton (visible + apagado + motivo) y NO vuelve a
/// evaluar el estado por su cuenta. El backend la usa como PRIMERA COMPUERTA antes de los guards finos.
/// </summary>
public sealed record ReservaCapabilities(
    Cap CanInvoiceSale,
    Cap CanEmitCreditDebitNote,
    Cap CanRegisterPayment,
    Cap CanEditOrDeletePayment,
    Cap CanEditServices,
    Cap CanEditPassengers,
    Cap CanEditReservaData,
    Cap CanCancel,
    Cap CanAdvance,
    Cap CanEmitVoucher,
    Cap CanCorrectTravelingEntry,
    IReadOnlyList<string> AllowedForward,
    IReadOnlyList<string> AllowedRevert);

/// <summary>
/// ADR-035 (2026-06-19): FUENTE UNICA, declarativa y pura, de "que accion se puede hacer en cada estado de
/// la reserva y, si no, por que no". Es una FACHADA DE LECTURA sobre reglas que ya existen
/// (<see cref="EstadoReserva"/>, <see cref="ReservaStatusTransitions"/>) mas dos decisiones del dueño
/// (voucher solo desde Confirmed+, Closed no cancelable).
///
/// <para><b>Que NO es</b>: NO es la defensa final. Los gates de escritura existentes (EnsureCollectable,
/// guards fiscales CAE/recibo, puentes de saldo, matriz de transicion) siguen vigentes y DESPUES de esta
/// compuerta. Si esta politica y un guard fino discrepan, MANDA el guard (el backend rechaza). La politica
/// NUNCA debe decir <c>Allowed=true</c> para algo que un guard fiscal/de puente rechaza (test cruzado C2,
/// gate bloqueante de merge).</para>
///
/// <para>Clase PURA (sin EF, sin DB): se testea en aislamiento contra la matriz del ADR. El metodo
/// evaluador vive en <see cref="ReservaCapabilityPolicy"/> (el record <see cref="ReservaCapabilities"/>
/// es el RESULTADO; se separan para que el nombre <c>For</c> no choque con el record).</para>
/// </summary>
public static class ReservaCapabilityPolicy
{
    // ===== Motivos legibles (español, sin montos ni costos). Consistentes con los de Reserva.cs. =====

    /// <summary>No es venta firme (pre-venta o terminal-no-firme): no se puede facturar/cobrar todavia.</summary>
    public const string NotSaleFirmReason = Reserva.NotSaleFirmForChargeMessage;

    /// <summary>Es venta firme pero sin saldo pendiente: no hay nada para cobrar.</summary>
    public const string NoPendingBalanceReason = Reserva.NoPendingBalanceForChargeMessage;

    /// <summary>
    /// ADR-037 (2026-06-21, desacople de facturacion): la factura de venta se emite desde Confirmada en
    /// adelante (Confirmed/Traveling/Closed), SALVO en reservas anuladas (Cancelled/Lost/PendingOperatorRefund)
    /// y antes de la confirmacion (Quotation/Budget/InManagement). Motivo legible para el front.
    /// </summary>
    public const string NotInvoiceableStatusReason =
        "No se puede facturar en este estado. La factura de venta se emite desde Confirmada en adelante, " +
        "salvo en reservas anuladas.";

    /// <summary>Editar/borrar un cobro en una reserva terminal: hay que anularlo (queda registrado).</summary>
    public const string PaymentEditOnTerminalReason =
        "Para corregir este cobro, anulalo (queda registrado). En este estado no se puede editar ni borrar.";

    /// <summary>El voucher solo se emite desde Confirmada en adelante (Decision 3 del dueño).</summary>
    public const string VoucherBeforeConfirmedReason =
        "El voucher se puede emitir recien desde Confirmada en adelante.";

    /// <summary>Cancelar una Finalizada (Closed) no es una accion valida del ciclo (Decision 4).</summary>
    public const string ClosedNotCancellableReason =
        "Una reserva Finalizada no se puede cancelar.";

    /// <summary>
    /// ADR-035 (2026-06-19): una reserva En viaje (Traveling) ya NO se cancela (decision del dueño). El
    /// servicio ya empezo/se presto; las correcciones van por nota de credito o ajuste, no por cancelacion.
    /// </summary>
    public const string TravelingNotCancellableReason =
        "La reserva esta en viaje: no se cancela; corregí por nota de crédito/ajuste.";

    /// <summary>No hay accion de cancelacion valida en este estado (pre-venta o ya terminal).</summary>
    public const string NotCancellableStatusReason =
        "No se puede cancelar la reserva en este estado.";

    /// <summary>
    /// ADR-036 (2026-06-21): En viaje (Traveling) no se cobra — el viaje ya empezo y para llegar a Traveling
    /// el cliente debio quedar saldado. El cobro arranca y termina antes del viaje.
    /// </summary>
    public const string TravelingNotChargeableReason =
        "En viaje no se cobra: el viaje ya empezó.";

    /// <summary>
    /// ADR-036 (2026-06-21): una reserva con cobros vivos o factura con CAE vivo NO admite baja simple
    /// (cancelacion directa). Hay que ANULARLA por el camino formal (se emite Nota de Credito). Sin datos
    /// sensibles (ni montos ni nombres).
    /// </summary>
    public const string HasLiveMoneyMustAnnulReason =
        "Esta reserva tiene cobros o factura: para deshacerla hay que anularla (se emite Nota de Crédito).";

    /// <summary>No hay ningun avance manual de estado disponible desde el estado actual.</summary>
    public const string NoForwardTransitionReason =
        "No hay un cambio de estado manual disponible desde el estado actual.";

    /// <summary>
    /// ADR-036 (2026-06-22): "Sacar de viaje" solo aplica a una reserva que esta En viaje (es la correccion
    /// de una entrada erronea a ese estado). En cualquier otro estado no hay nada que corregir.
    /// </summary>
    public const string CorrectTravelingOnlyFromTravelingReason =
        "Solo se puede sacar de viaje una reserva que está En viaje.";

    /// <summary>
    /// ADR-036 (2026-06-22): no se puede sacar de viaje una reserva con factura emitida (CAE vivo). La
    /// correccion fiscal de una venta facturada se hace por Nota de Credito/ajuste, no devolviendo el estado.
    /// </summary>
    public const string CorrectTravelingBlockedByCaeReason =
        "La reserva tiene factura emitida: la corrección se hace por Nota de Crédito/ajuste.";

    /// <summary>
    /// ADR-036 (2026-06-22): no se puede sacar de viaje una reserva con un voucher emitido vivo. Hay que
    /// anular el voucher primero (el caso de uso "entró por error" normalmente no tiene voucher).
    /// </summary>
    public const string CorrectTravelingBlockedByVoucherReason =
        "Anulá el voucher antes de sacar de viaje.";

    // =====================================================================================================
    // Conjuntos de estado (FACHADA: reusan las listas del dominio; no inventan taxonomia nueva).
    // =====================================================================================================

    /// <summary>
    /// Estados desde los que se emite la FACTURA de venta (allow-list). Coincide con
    /// <c>InvoiceService.ActiveInvoicingStatuses</c> (fuente unica).
    ///
    /// <para><b>ADR-037 (2026-06-21, desacople de facturacion estilo ERP): {Confirmed, Traveling, Closed}.</b>
    /// La factura de venta se DESACOPLA del estado: se puede emitir en cualquier estado firme no-anulado, no
    /// solo "antes de viajar". Esto REVIERTE la restriccion de ADR-036 (que la habia dejado en SOLO Confirmed)
    /// y ELIMINA el "reabrir para facturar": ahora se factura directo desde Closed (Finalizada). Decision del
    /// dueño H2=A (conservador): NO se incluye InManagement (servicios sin resolver: facturar ahi seria emitir
    /// CAE por algo que el operador podria rechazar), ni Quotation/Budget (pre-venta), ni los estados ANULADOS
    /// (Cancelled/Lost/PendingOperatorRefund: una venta anulada no se factura).</para>
    ///
    /// <para><b>Tension con ADR-036 ("en viaje no se factura") resuelta</b>: permitir factura en Traveling NO
    /// reabre la edicion de servicios/pasajeros/cobro en viaje. Emitir factura NO muta la reserva. El bloqueo
    /// de edicion en Traveling lo siguen enforzando <see cref="ServiceEditableStatuses"/> (sin Traveling) y
    /// <see cref="EvaluateRegisterPayment"/> (bloquea Traveling). Lo unico que ADR-037 abre en Traveling es
    /// emitir el comprobante fiscal de una venta firme.</para>
    ///
    /// <para>La NC/ND es excepcion aparte (ver CanEmitCreditDebitNote): corregir/anular una factura de una
    /// reserva ya finalizada o cancelada se hace con nota, sin reabrir el estado.</para>
    /// </summary>
    public static readonly string[] InvoiceableStatuses =
    {
        EstadoReserva.Confirmed,
        EstadoReserva.Traveling,
        EstadoReserva.Closed,
    };

    /// <summary>
    /// Estados desde los que se puede emitir VOUCHER (Decision 3 del dueño): Confirmed en adelante,
    /// incluido Closed. InManagement NO. Es el MISMO conjunto que enforza
    /// <c>VoucherService.EnsureReservaAllowsVoucher</c> (fuente compartida para que front y back coincidan).
    ///
    /// <para>ADR-036 (2026-06-21): se quito ToSettle (estado eliminado). Traveling SE MANTIENE: el voucher es
    /// el documento que el pasajero necesita para viajar, asi que debe poder emitirse/reimprimirse en viaje.
    /// Quedan {Confirmed, Traveling, Closed}.</para>
    /// </summary>
    public static readonly string[] VoucherStatuses =
    {
        EstadoReserva.Confirmed,
        EstadoReserva.Traveling,
        EstadoReserva.Closed,
    };

    /// <summary>
    /// Estados terminales/no-editables para CORRECCION de cobro: en estos el cobro no se edita ni se borra,
    /// solo se ANULA con rastro (AnnulPaymentAsync). Coincide con el criterio de ADR-035 punto 3:
    /// {Closed, Cancelled, Lost, PendingOperatorRefund}.
    /// </summary>
    private static readonly string[] PaymentLockedStatuses =
    {
        EstadoReserva.Closed,
        EstadoReserva.Cancelled,
        EstadoReserva.Lost,
        EstadoReserva.PendingOperatorRefund,
    };

    /// <summary>
    /// Estados donde EDITAR servicios/pasajeros/datos es una operacion normal del ciclo (no solo-lectura).
    /// {Quotation, Budget, InManagement, Confirmed}. En los terminales (Closed/Cancelled/Lost/
    /// PendingOperatorRefund) no se editan. NOTA: el candado real bajo estado Confirmed+ (ReservaLockGuard)
    /// lo enforza el backend con su autorizacion; esta capacidad solo indica que la edicion es
    /// CONCEPTUALMENTE valida en el estado (con autorizacion si corresponde).
    ///
    /// <para>ADR-036 (2026-06-21, prepago puro): se quito ToSettle (estado eliminado) y <b>Traveling</b>
    /// (Decision 2: "En viaje" es SOLO LECTURA TOTAL, igual que Closed — no se edita ni con autorizacion).
    /// Este conjunto es la PRIMERA COMPUERTA que consultan todos los write-paths de servicio/pasajero/cabecera
    /// (ReservaCapacityRules.Ensure*EditableByStateAsync): al quedar Traveling fuera, esas compuertas rechazan
    /// de raiz cualquier mutacion en viaje, ANTES del candado de autorizacion. Por eso sacar Traveling del
    /// ReservaLockGuard NO lo deja editable: el bloqueo real vive aca.</para>
    /// </summary>
    private static readonly string[] ServiceEditableStatuses =
    {
        EstadoReserva.Quotation,
        EstadoReserva.Budget,
        EstadoReserva.InManagement,
        EstadoReserva.Confirmed,
    };

    /// <summary>
    /// ADR-035: evalua TODAS las capacidades de una reserva a partir de su contexto minimo. Pura: no toca DB.
    /// </summary>
    public static ReservaCapabilities For(ReservaCapabilityContext ctx)
    {
        return new ReservaCapabilities(
            CanInvoiceSale: EvaluateInvoiceSale(ctx),
            CanEmitCreditDebitNote: EvaluateCreditDebitNote(ctx),
            CanRegisterPayment: EvaluateRegisterPayment(ctx),
            CanEditOrDeletePayment: EvaluateEditOrDeletePayment(ctx),
            CanEditServices: EvaluateEditServices(ctx),
            CanEditPassengers: EvaluateEditPassengers(ctx),
            CanEditReservaData: EvaluateEditReservaData(ctx),
            CanCancel: EvaluateCancel(ctx),
            CanAdvance: EvaluateAdvance(ctx),
            CanEmitVoucher: EvaluateVoucher(ctx),
            CanCorrectTravelingEntry: EvaluateCorrectTravelingEntry(ctx),
            AllowedForward: ResolveForwardTargets(ctx.Status),
            AllowedRevert: ResolveRevertTargets(ctx.Status));
    }

    // =====================================================================================================
    // Una funcion por capacidad (corta y enfocada). Cada No lleva un motivo legible sin datos sensibles.
    // =====================================================================================================

    /// <summary>
    /// Factura de venta: en {Confirmed, Traveling, Closed} (ADR-037: desacoplada del estado, se emite en
    /// cualquier estado firme no-anulado). Fuera de la allow-list (pre-venta o anulados) da No.
    /// </summary>
    private static Cap EvaluateInvoiceSale(ReservaCapabilityContext ctx)
    {
        if (!ContainsStatus(InvoiceableStatuses, ctx.Status))
            return Cap.No(NotInvoiceableStatusReason);
        return Cap.Yes;
    }

    /// <summary>
    /// NC/ND: las notas de credito/debito corrigen o anulan una factura ya emitida, asi que se permiten
    /// donde la factura de venta (firme) Y ademas sobre reservas terminales-con-factura (Cancelled/Closed),
    /// que es justo donde se anula. Solo tiene sentido si hay un comprobante fiscal vivo que corregir.
    /// </summary>
    private static Cap EvaluateCreditDebitNote(ReservaCapabilityContext ctx)
    {
        // Sin CAE vivo no hay factura que corregir/anular: no aplica la NC/ND.
        if (!ctx.HasLiveCae)
            return Cap.No("No hay una factura emitida para corregir o anular con una nota.");
        return Cap.Yes;
    }

    /// <summary>
    /// Registrar cobro: EXACTAMENTE la misma regla que el guard fino <c>Reserva.EnsureCollectable</c> de
    /// ADR-033 = VENTA FIRME (<see cref="EstadoReserva.IsSaleFirmStatus"/>, incluye Closed) Y deuda real
    /// (Balance &gt; 0). Una Finalizada (Closed) con deuda SI admite cobro (ADR-033 A1/E2): la compuerta NO
    /// puede ser mas estricta que el guard fiscal (si lo fuera, el front apagaria un boton que el back si
    /// aceptaria, y peor: bloquearia un cobro legitimo). Por eso esta capacidad NO usa
    /// <see cref="EstadoReserva.ActiveCollectionStatuses"/> (sin Closed) sino la misma <c>SaleFirmStatuses</c>
    /// que EnsureCollectable.
    ///
    /// <para>ADR-035 NO reabre la cobrabilidad de alta de ADR-033: la compuerta la LEE, no la redefine. El
    /// test cruzado C2 garantiza que politica y EnsureCollectable nunca discrepen.</para>
    /// </summary>
    private static Cap EvaluateRegisterPayment(ReservaCapabilityContext ctx)
    {
        // ADR-036 (2026-06-21): bloqueo EXPLICITO de Traveling con su propio motivo. En viaje no se cobra
        // (el viaje ya empezo). Aunque Traveling ya no esta en SaleFirmStatuses (caeria igual por el chequeo
        // de abajo), damos un mensaje propio para que el front no diga "pasala a En gestion primero".
        if (EqualsStatus(ctx.Status, EstadoReserva.Traveling))
            return Cap.No(TravelingNotChargeableReason);
        if (!EstadoReserva.IsSaleFirmStatus(ctx.Status))
            return Cap.No(NotSaleFirmReason);
        if (ctx.Balance <= 0)
            return Cap.No(NoPendingBalanceReason);
        return Cap.Yes;
    }

    /// <summary>
    /// Editar/borrar cobro: NO en estados terminales {Closed, Cancelled, Lost, PendingOperatorRefund}. En
    /// esos, corregir = anular con rastro (AnnulPaymentAsync, que no pasa por esta compuerta). Los guards
    /// fiscales (recibo emitido / CAE vivo) y de puente son la defensa final por encima de esto.
    /// </summary>
    private static Cap EvaluateEditOrDeletePayment(ReservaCapabilityContext ctx)
    {
        if (ContainsStatus(PaymentLockedStatuses, ctx.Status))
            return Cap.No(PaymentEditOnTerminalReason);
        return Cap.Yes;
    }

    /// <summary>Editar servicios: valido en los estados vivos del ciclo, no en los terminales.</summary>
    private static Cap EvaluateEditServices(ReservaCapabilityContext ctx)
    {
        if (!ContainsStatus(ServiceEditableStatuses, ctx.Status))
            return Cap.No("No se pueden editar los servicios en este estado de la reserva.");
        return Cap.Yes;
    }

    /// <summary>
    /// ADR-035 (2026-06-19): editar PASAJEROS (agregar / completar datos / cambiar identidad / borrar) sigue
    /// la MISMA matriz por estado que los servicios: valido en los estados vivos del ciclo (EN ARMADO y EN
    /// FIRME), bloqueado de raiz en los terminales (Closed/Cancelled/Lost/PendingOperatorRefund), donde el
    /// roster es solo lectura dura.
    ///
    /// <para>Importante: esta capacidad NO toca la regla de ADR-031 (completar un dato faltante de un pasajero
    /// no pide autorizacion en estados firmes). Eso se sigue evaluando aparte en el servicio. Lo unico que
    /// suma ADR-035 es el HARD BLOCK en terminales: ahi no se puede ni completar, ni agregar, ni borrar.</para>
    /// </summary>
    private static Cap EvaluateEditPassengers(ReservaCapabilityContext ctx)
    {
        if (!ContainsStatus(ServiceEditableStatuses, ctx.Status))
            return Cap.No("No se pueden editar los pasajeros en este estado de la reserva.");
        return Cap.Yes;
    }

    /// <summary>
    /// ADR-035 (2026-06-19): editar DATOS DE CABECERA de la reserva (fechas de salida/regreso y demas datos
    /// generales) sigue la MISMA matriz por estado que los servicios: valido en los estados vivos del ciclo,
    /// bloqueado en los terminales. El candado de autorizacion (Confirmed+) y los guards fiscales (factura /
    /// voucher con periodo declarado) se siguen aplicando aparte, DESPUES de esta compuerta.
    /// </summary>
    private static Cap EvaluateEditReservaData(ReservaCapabilityContext ctx)
    {
        if (!ContainsStatus(ServiceEditableStatuses, ctx.Status))
            return Cap.No("No se pueden editar los datos de la reserva en este estado.");
        return Cap.Yes;
    }

    /// <summary>
    /// Cancelar la reserva: NO en {Traveling, Closed, Lost, Cancelled, PendingOperatorRefund}. Traveling y
    /// Closed tienen su propio motivo (Decision 4 / ADR-035: una Finalizada no se cancela; una En viaje
    /// tampoco — se corrige por NC/ajuste). El resto comparte el motivo generico de estado. Confirmed e
    /// InManagement SI permiten cancelar (es el flujo normal de cancelacion antes/durante la gestion firme).
    /// </summary>
    private static Cap EvaluateCancel(ReservaCapabilityContext ctx)
    {
        if (EqualsStatus(ctx.Status, EstadoReserva.Closed))
            return Cap.No(ClosedNotCancellableReason);
        // ADR-035: En viaje deja de ser cancelable. Antes Traveling -> Cancelled estaba en la matriz forward;
        // se quito (ReservaStatusTransitions) y aca se refleja con un motivo propio para el front.
        if (EqualsStatus(ctx.Status, EstadoReserva.Traveling))
            return Cap.No(TravelingNotCancellableReason);
        if (EqualsStatus(ctx.Status, EstadoReserva.Lost)
            || EqualsStatus(ctx.Status, EstadoReserva.Cancelled)
            || EqualsStatus(ctx.Status, EstadoReserva.PendingOperatorRefund))
            return Cap.No(NotCancellableStatusReason);

        // ADR-036 (2026-06-21): una reserva con PLATA VIVA no admite baja simple. Si ya hay cobros (reales o
        // puente) o una factura con CAE vivo, deshacerla NO es "cancelar" sino ANULAR por el camino formal
        // (NC/ND, ADR-002). Aca cerramos el agujero a nivel capacidad (el front apaga el boton "Cancelar" y
        // enruta a "Anular"); el guard de escritura en la transicion a Cancelled lo refuerza server-side.
        if (ctx.HasLiveCae || ctx.HasAnyPayment)
            return Cap.No(HasLiveMoneyMustAnnulReason);

        return Cap.Yes;
    }

    /// <summary>
    /// Avanzar de estado (transicion forward manual): permitido si hay al menos un destino legal en la
    /// matriz forward del dominio. NO incluye el salto automatico InManagement -&gt; Confirmed (lo hace el motor).
    /// </summary>
    private static Cap EvaluateAdvance(ReservaCapabilityContext ctx)
    {
        if (ResolveForwardTargets(ctx.Status).Count == 0)
            return Cap.No(NoForwardTransitionReason);
        return Cap.Yes;
    }

    /// <summary>
    /// Emitir voucher: solo desde Confirmada en adelante (Decision 3). Mismo conjunto que enforza
    /// <c>VoucherService.EnsureReservaAllowsVoucher</c>.
    /// </summary>
    private static Cap EvaluateVoucher(ReservaCapabilityContext ctx)
    {
        if (!ContainsStatus(VoucherStatuses, ctx.Status))
            return Cap.No(VoucherBeforeConfirmedReason);
        return Cap.Yes;
    }

    /// <summary>
    /// ADR-036 (2026-06-22): "Sacar de viaje" (correccion de una entrada erronea a "En viaje"). La capacidad
    /// PURA es allowed solo si: la reserva esta En viaje (Traveling) Y no tiene factura con CAE vivo Y no tiene
    /// voucher emitido vivo. Los bloqueos fiscal (CAE) y de voucher NO son bypasseables ni por Admin, por eso
    /// viven en la capacidad pura (la lee tambien el guard del service).
    ///
    /// <para>El PERMISO <c>reservas.correct_traveling</c> (solo Admin) NO se evalua aca a proposito: el contexto
    /// de capacidades es SIN identidad (ADR-035). El permiso lo compone el front (mostrar el boton) y lo
    /// revalida el controller. Asi la capacidad responde "el estado lo permite", y la autorizacion es una
    /// compuerta aparte — misma separacion que el resto de las capacidades.</para>
    /// </summary>
    private static Cap EvaluateCorrectTravelingEntry(ReservaCapabilityContext ctx)
    {
        if (!EqualsStatus(ctx.Status, EstadoReserva.Traveling))
            return Cap.No(CorrectTravelingOnlyFromTravelingReason);
        if (ctx.HasLiveCae)
            return Cap.No(CorrectTravelingBlockedByCaeReason);
        if (ctx.HasLiveVoucher)
            return Cap.No(CorrectTravelingBlockedByVoucherReason);
        return Cap.Yes;
    }

    // ===== Transiciones (leen la fuente unica del dominio) =====

    private static IReadOnlyList<string> ResolveForwardTargets(string status)
        => ReservaStatusTransitions.Forward.TryGetValue(status, out var targets)
            ? targets.ToList()
            : new List<string>();

    private static IReadOnlyList<string> ResolveRevertTargets(string status)
        => ReservaStatusTransitions.Revert.TryGetValue(status, out var targets)
            ? targets.ToList()
            : new List<string>();

    // ===== Helpers de comparacion case-insensitive (alineados con EstadoReserva) =====

    private static bool ContainsStatus(string[] statuses, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        foreach (var candidate in statuses)
        {
            if (EqualsStatus(status, candidate)) return true;
        }
        return false;
    }

    private static bool EqualsStatus(string? a, string? b)
        => string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
}
