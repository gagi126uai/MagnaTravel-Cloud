using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-048 (modelo de estados derivados, 2026-07-17 — bloqueante B1 del review de arquitectura):
/// calcula el terminal del PAR (Anulada / Esperando reembolso del operador) a NIVEL RESERVA, mirando
/// TODAS las líneas de TODAS las cancelaciones (<see cref="BookingCancellation"/>) de esa reserva —
/// no solo la más reciente ni la que disparó el evento que dispara la evaluación.
///
/// <para><b>Por qué "a nivel reserva" y no "por cancelación"</b>: una reserva puede tener varias
/// cancelaciones a lo largo del tiempo (una por cada tanda de servicios cancelados). Si el cierre se
/// decidiera mirando solo LA cancelación que acaba de recibir un reembolso, la PRIMERA que se salda
/// cerraría la reserva entera aunque otra cancelación de la misma reserva siga esperando su
/// reembolso — un cierre prematuro que deja plata pendiente invisible. Por eso este helper exige que
/// el caller junte las líneas de TODAS las cancelaciones antes de preguntar.</para>
///
/// <para>Función PURA: no hace queries, no conoce EF. El caller (capa de infraestructura) es
/// responsable de traer TODAS las líneas de TODAS las cancelaciones de la reserva antes de invocar
/// este helper — si trae solo un subconjunto, el resultado queda mal (el mismo cierre prematuro que
/// esta regla existe para evitar).</para>
/// </summary>
public static class ReservaTerminalDerivation
{
    /// <summary>
    /// Estados donde el motor EN VIVO puede auto-anular una reserva POR PRIMERA VEZ (entrar al par
    /// desde un estado vivo). "En viaje" (Traveling) queda AFUERA a propósito: una vez que el cliente
    /// está viajando, la reserva es de solo lectura (no se edita, no se cancela un servicio desde ahí).
    /// Si datos viejos quedaron mintiendo en Traveling, los corrige la reparación única (migración),
    /// nunca el motor en vivo.
    /// </summary>
    public static bool IsLiveEngineStatus(string? status)
        => string.Equals(status, EstadoReserva.InManagement, StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, EstadoReserva.Confirmed, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True si el estado YA es uno de los dos del par terminal (Anulada / Esperando reembolso del
    /// operador) — sin importar cuál de los dos.
    /// </summary>
    public static bool IsInTerminalPar(string? status)
        => string.Equals(status, EstadoReserva.Cancelled, StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, EstadoReserva.PendingOperatorRefund, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Estados de ENTRADA desde los que el applier puede (re)derivar el terminal: los estados VIVOS
    /// donde el motor auto-anula por primera vez (<see cref="IsLiveEngineStatus"/>) MÁS los dos estados
    /// del propio par (<see cref="IsInTerminalPar"/>), para poder CORREGIR entre ellos cuando aparece
    /// (o se salda) una deuda del operador que no se vio a tiempo.
    ///
    /// <para><b>Por qué hace falta (bloqueante B-1, backend review 2026-07-17, 2da pasada)</b>: en
    /// <c>CancelServiceAsync</c>, la línea de cancelación con su <c>RefundCap</c> se crea DESPUÉS de la
    /// primera derivación del terminal (que corre dentro de <c>ReservaMoneyPersister</c>, antes de que
    /// esa línea exista). Si en ese primer paso no había ninguna línea pendiente, el terminal derivaba
    /// "Anulada" — y como el applier solo aceptaba estados VIVOS como entrada, una segunda pasada
    /// (ya con la línea creada) no podía corregirlo: la reserva quedaba "Anulada" mintiendo con el
    /// operador debiendo plata, para siempre. Este método amplía la puerta de entrada SOLO para permitir
    /// esa corrección lateral dentro del par.</para>
    ///
    /// <para><b>Lo que esto NUNCA hace</b> (la función que USA este gate sigue siendo la misma:
    /// <c>DetermineTerminalStatus</c>, que solo devuelve <c>Cancelled</c> o <c>PendingOperatorRefund</c>):
    /// nunca saca una reserva del par hacia un estado vivo, y nunca toca <c>Traveling</c>, <c>Closed</c>,
    /// <c>Lost</c>, <c>Budget</c> ni <c>Quotation</c> — esos estados devuelven <c>false</c> acá y el
    /// applier no los toca.</para>
    /// </summary>
    public static bool CanReDeriveTerminalStatus(string? status)
        => IsLiveEngineStatus(status) || IsInTerminalPar(status);

    /// <summary>
    /// El operador TODAVÍA debe algún reembolso a NIVEL RESERVA si existe AL MENOS una línea (de
    /// CUALQUIERA de las cancelaciones de la reserva) que esperaba plata del operador
    /// (<c>RefundCap &gt; 0</c>) y todavía no terminó de cobrarla (<c>RefundStatus != Settled</c>).
    /// </summary>
    public static bool IsOperatorRefundPending(IEnumerable<BookingCancellationLine> allLinesOfReserva)
        => allLinesOfReserva.Any(line =>
            line.RefundCap > 0m && line.RefundStatus != BookingCancellationLineRefundStatus.Settled);

    /// <summary>
    /// Decide cuál de los dos estados del terminal corresponde: mientras el operador deba algo (ver
    /// <see cref="IsOperatorRefundPending"/>) la reserva queda "Esperando reembolso del operador";
    /// una vez saldado, queda "Anulada".
    /// </summary>
    public static string DetermineTerminalStatus(IEnumerable<BookingCancellationLine> allLinesOfReserva)
        => IsOperatorRefundPending(allLinesOfReserva)
            ? EstadoReserva.PendingOperatorRefund
            : EstadoReserva.Cancelled;
}
