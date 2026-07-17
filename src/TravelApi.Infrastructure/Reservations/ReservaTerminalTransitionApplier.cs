using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-048 (modelo de estados derivados, 2026-07-17): aplica el terminal del par (Anulada / Esperando
/// reembolso del operador) cuando una reserva "tuvo servicios y los tiene todos anulados"
/// (INV-048-01). Es el ÚNICO lugar que junta "¿tuvo servicios y todos anulados?"
/// (<see cref="ReservaDerivedState"/>) con "¿el operador todavía debe algo, a nivel reserva?"
/// (<see cref="ReservaTerminalDerivation"/>) y los aplica de verdad, vía el punto único de transición
/// (<see cref="ReservaStatusTransitioner"/>).
///
/// <para><b>NO hace <c>SaveChangesAsync</c></b>: corre dentro de la unidad de trabajo del caller, así el
/// cambio de estado se persiste en la MISMA transacción que la mutación que lo disparó (regla 9: nada
/// de "la próxima mutación lo corrige"). Lo usan DOS callers:</para>
/// <list type="bullet">
/// <item><see cref="ReservaMoneyPersister"/> — la vía atómica (B2): se invoca justo ANTES de su
///   <c>SaveChangesAsync</c>, así plata y estado quedan en el mismo commit.</item>
/// <item><see cref="Services.Reservations.ReservaAutoStateService"/> — para el caller que NO pasa por el
///   persister (el job nocturno de reconciliación, que no es el mecanismo de diseño pero incidentalmente
///   también cura reservas desincronizadas).</item>
/// </list>
///
/// <para>Idempotente: si la reserva ya está en el terminal correcto, es un no-op (no vuelve a escribir
/// el log de auditoría).</para>
/// </summary>
public static class ReservaTerminalTransitionApplier
{
    // Mismo actor "sistema" que usa el motor de estados para el rastro auditable (regla 10).
    private const string SystemActorUserId = "system:auto-state";
    private const string SystemActorUserName = "Sistema (motor de estados)";

    /// <summary>
    /// Si la reserva está en un estado desde el que este llamado puede (re)derivar el terminal — SIEMPRE
    /// InManagement/Confirmed para entrar al par por primera vez
    /// (<see cref="ReservaTerminalDerivation.IsLiveEngineStatus"/>), y ADEMÁS los propios
    /// {Cancelled, PendingOperatorRefund} SOLO cuando <paramref name="allowCorrectionWithinPar"/> es
    /// <c>true</c> (ver por qué es opt-in más abajo) — y "tuvo servicios y los tiene todos anulados"
    /// (INV-048-01), la lleva al terminal del par que corresponda (nivel RESERVA, N BC — B1) y deja el
    /// rastro auditable. M5: Traveling/Closed/Lost/etc. quedan SIEMPRE afuera, en cualquier modo. Devuelve
    /// <c>true</c> si transicionó.
    ///
    /// <para><b>Por qué <paramref name="allowCorrectionWithinPar"/> es OPT-IN y no un ensanche general del
    /// gate (bloqueante B-1, backend review 2026-07-17, 2da y 3ra pasada)</b>: en <c>CancelServiceAsync</c>
    /// la línea de cancelación con su <c>RefundCap</c> se crea DESPUÉS de la primera derivación (que corre
    /// dentro de <c>ReservaMoneyPersister</c>, antes de que esa línea exista). Sin una puerta que permita
    /// re-evaluar el par, una reserva que derivó mal a <c>Cancelled</c> en esa primera pasada (porque la
    /// línea todavía no existía) quedaba así PARA SIEMPRE. La primera versión de este fix ABRIÓ el gate
    /// para TODOS los callers — y rompió 3 tests reales: <c>ConfirmAsync</c> (anulación TOTAL) deja la
    /// reserva en <c>PendingOperatorRefund</c> A PROPÓSITO al confirmar, aunque el <c>RefundCap</c> ya
    /// compute 0, porque el cierre real se decide DESPUÉS (post-CAE) con
    /// <c>ShouldAutoCloseWithoutOperatorRefundAsync</c> — una regla MÁS RICA que también chequea NDs a
    /// medio emitir y sincroniza <c>BookingCancellation.Status</c> a <c>Closed</c>, cosa que este applier
    /// NUNCA hace. Y <c>AnnulWithPaymentsToCreditAsync</c> deja <c>Cancelled</c> A PROPÓSITO aunque queden
    /// líneas de cancelaciones PARCIALES previas con <c>RefundCap</c> sin saldar: en ese flujo el
    /// receivable del operador se rastrea a nivel PROVEEDOR (<c>SupplierCreditReconciler</c>), no bloquea
    /// el cartel de la reserva. Un ensanche general pisaba ambas decisiones. Por eso la puerta ampliada
    /// queda OPT-IN: solo la pasa en <c>true</c> el ÚNICO caller que la necesita (paso 6 de
    /// <c>CancelServiceAsync.RunCancellationUnitAsync</c>, vía <c>ReservaMoneyPersister.PersistAsync</c>);
    /// todos los demás callers (el resto de <c>ReservaMoneyPersister</c>, <c>ReservaAutoStateService</c>)
    /// siguen usando el default <c>false</c> — comportamiento BYTE-IDÉNTICO al de antes de este fix.</para>
    ///
    /// <para><b>No pisa a los callbacks de cierre</b>: cuando SÍ está habilitado, este método puede además
    /// cerrar <c>PendingOperatorRefund → Cancelled</c> si al re-evaluar ya no queda nada pendiente — pero
    /// eso sigue siendo un CASO DE BORDE (defensivo) del único caller que lo habilita, no el mecanismo
    /// principal: ese lo siguen siendo los callbacks de <c>BookingCancellationService</c>
    /// (<c>CloseReservaIfOperatorRefundComplete</c>/<c>OnAllCreditConsumedAsync</c>), que reaccionan al
    /// evento de la imputación del reembolso en memoria, antes de que se commitee. Ambos caminos usan el
    /// MISMO criterio de dominio (<see cref="ReservaTerminalDerivation.IsOperatorRefundPending"/>) y el
    /// mismo punto único de transición, que es idempotente — si uno de los dos ya cerró la reserva, el
    /// otro encuentra el Status ya correcto y no vuelve a escribir nada (sin doble log).</para>
    /// </summary>
    /// <param name="db">Contexto EF de la unidad de trabajo del caller (no se persiste acá).</param>
    /// <param name="reserva">
    /// Reserva ya cargada CON sus 6 colecciones de servicios (FlightSegments, HotelBookings,
    /// TransferBookings, PackageBookings, AssistanceBookings, Servicios) — es responsabilidad del
    /// caller incluirlas todas. Si a alguna le falta el <c>Include</c>, EF la devuelve vacía y este
    /// método la trata como "esa reserva no tiene servicios de ese tipo": un servicio VIVO de un tipo no
    /// incluido pasaría desapercibido y podría disparar una anulación automática incorrecta. Los dos
    /// callers de hoy (<see cref="ReservaMoneyPersister"/> y
    /// <see cref="TravelApi.Infrastructure.Services.Reservations.ReservaAutoStateService"/>) ya traen
    /// las 6 — cualquier caller nuevo debe hacer lo mismo.
    /// </param>
    /// <param name="allowCorrectionWithinPar">
    /// Default <c>false</c> (comportamiento de siempre: solo entra desde InManagement/Confirmed). Pasarlo
    /// en <c>true</c> SOLO desde el caller que necesita corregir DENTRO del par (hoy: el paso 6 de
    /// <c>CancelServiceAsync</c>, el fix de B-1). No pasarlo en <c>true</c> desde flujos que ya deciden el
    /// cierre por su cuenta con una regla más completa (anulación total, "anular con saldo a favor").
    /// </param>
    public static async Task<bool> ApplyIfNeededAsync(
        AppDbContext db, Reserva reserva, DateTime now, CancellationToken ct,
        bool allowCorrectionWithinPar = false)
    {
        bool canReDerive = ReservaTerminalDerivation.IsLiveEngineStatus(reserva.Status)
            || (allowCorrectionWithinPar && ReservaTerminalDerivation.IsInTerminalPar(reserva.Status));
        if (!canReDerive)
            return false;

        if (!ReservaDerivedState.HadServicesAndAllCancelled(reserva))
            return false;

        // B1: juntamos las líneas de TODAS las cancelaciones (BookingCancellation) de la reserva, no solo
        // de una — el terminal correcto (Anulada vs Esperando reembolso) es una pregunta a NIVEL RESERVA.
        var allBookingCancellationLinesOfReserva = await db.BookingCancellationLines
            .AsNoTracking()
            .Where(line => line.BookingCancellation.ReservaId == reserva.Id)
            .ToListAsync(ct);

        var targetStatus = ReservaTerminalDerivation.DetermineTerminalStatus(allBookingCancellationLinesOfReserva);

        // Idempotente: si ya está en el terminal que le corresponde (haya llegado por acá o por los
        // callbacks de BookingCancellationService), no hay nada que transicionar ni que volver a loguear.
        if (string.Equals(reserva.Status, targetStatus, StringComparison.OrdinalIgnoreCase))
            return false;

        // Si YA estaba en el par (Cancelled o PendingOperatorRefund) y se corrige al OTRO estado del
        // par, es una CORRECCIÓN lateral, no un avance ni una reversión — mismo Direction que usa el
        // vigía de coherencia para este tipo de saneo (CoherenceChecks.cs). Si venía de un estado vivo
        // (InManagement/Confirmed), sigue siendo la entrada normal al par: "Forward".
        bool isCorrectionWithinPar = ReservaTerminalDerivation.IsInTerminalPar(reserva.Status);
        var direction = isCorrectionWithinPar ? "Correction" : "Forward";

        string reason;
        if (isCorrectionWithinPar && string.Equals(targetStatus, EstadoReserva.PendingOperatorRefund, StringComparison.OrdinalIgnoreCase))
        {
            // B-1: la reserva ya figuraba "Anulada", pero apareció (o se registró tarde) una línea de
            // cancelación con reembolso del operador todavía pendiente.
            reason = "Se corrige el estado: apareció una devolución del operador todavía pendiente que no " +
                     "se había registrado a tiempo. La reserva pasa de 'anulada' a 'esperando reembolso del " +
                     "operador' (sistema).";
        }
        else if (isCorrectionWithinPar)
        {
            // La reserva figuraba "Esperando reembolso" pero, al re-evaluar, ya no queda ninguna línea
            // pendiente (mismo desenlace que ya cubren los callbacks de OperatorRefundService/
            // ClientCreditService; esta rama es la red de respaldo si algo más vuelve a tocar la plata
            // de la reserva antes de que esos callbacks hayan corrido).
            reason = "Se corrige el estado: ya no queda ningún reembolso del operador pendiente. La reserva " +
                     "pasa de 'esperando reembolso del operador' a 'anulada' (sistema).";
        }
        else if (string.Equals(targetStatus, EstadoReserva.PendingOperatorRefund, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Todos los servicios de la reserva quedaron anulados. Como el operador todavía no devolvió " +
                     "el dinero de alguna cancelación, la reserva queda esperando ese reembolso (sistema).";
        }
        else
        {
            reason = "Todos los servicios de la reserva quedaron anulados y no hay ningún reembolso del operador " +
                     "pendiente: la reserva queda anulada (sistema).";
        }

        // INV-048-02: esta transición SOLO cambia Status + rastro (vía el punto único de transición). NO
        // crea comprobantes ni mueve plata — la plata ya se acreditó línea por línea al cancelar cada
        // servicio; esto solo actualiza el CARTEL de la reserva para que deje de mentir.
        await ReservaStatusTransitioner.ApplyAsync(
            db, reserva, targetStatus, direction,
            actorUserId: SystemActorUserId, actorUserName: SystemActorUserName,
            reason: reason, ct: ct, occurredAt: now);

        return true;
    }
}
