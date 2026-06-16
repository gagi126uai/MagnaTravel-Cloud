using System.Text.Json.Serialization;

namespace TravelApi.Application.DTOs;

/// <summary>
/// Contrato de <c>GET /alerts</c> cuando al menos un bucket nuevo
/// (<c>UpcomingStarts</c> / <c>CostsToConfirm</c>) esta activo.
///
/// <para><b>Por que un DTO TIPADO y NO un <c>Dictionary&lt;string,object&gt;</c></b>: System.Text.Json aplica el
/// <c>PropertyNamingPolicy</c> (camelCase, configurado por <c>JsonSerializerDefaults.Web</c> que usa la API) a las
/// PROPIEDADES de un tipo, pero NO a las CLAVES de un diccionario. Con el diccionario, prender un flag cambiaba en
/// SILENCIO el casing de las claves historicas (de <c>urgentTrips</c> a <c>UrgentTrips</c>) y rompia a cualquier
/// consumidor que sirviera para el path con flag OFF. Con este DTO el casing del contrato es el MISMO con flag OFF
/// y ON: siempre camelCase.</para>
///
/// <para><b>Presencia condicional</b>: los buckets nuevos son nullable + <see cref="JsonIgnoreAttribute"/> con
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/>. Cuando su gate esta apagado se pasan en null y se OMITEN del
/// JSON — exactamente la misma presencia condicional que tenia el path viejo con diccionario (lo unico que cambia
/// es que ahora el casing es consistente). Las listas viajan como <c>object</c>: sus elementos son objetos
/// anonimos que STJ serializa por su tipo runtime.</para>
///
/// <para><b>ADR-019 (2026-06-06)</b>: el bucket <c>ServiceDeadlines</c> (fechas limite manuales, ADR-017 F1.4,
/// nunca prendido en prod) fue REEMPLAZADO por <c>UpcomingStarts</c> — aviso automatico por reserva, X dias antes
/// del primer servicio. Contrato limpio, sin clave legacy (decision D1 del ADR).</para>
/// </summary>
public sealed class AlertsResponse
{
    public AlertsResponse(
        object urgentTrips,
        object supplierDebts,
        object? upcomingStarts,
        int? upcomingStartsWindowDays,
        object? costsToConfirm,
        int totalCount,
        object? operatorPaymentDeadlines = null,
        object? ticketingDeadlines = null,
        object? passportExpiries = null,
        object? confirmedWithChanges = null,
        object? stuckOperatorRefunds = null)
    {
        UrgentTrips = urgentTrips;
        SupplierDebts = supplierDebts;
        UpcomingStarts = upcomingStarts;
        UpcomingStartsWindowDays = upcomingStartsWindowDays;
        CostsToConfirm = costsToConfirm;
        TotalCount = totalCount;
        OperatorPaymentDeadlines = operatorPaymentDeadlines;
        TicketingDeadlines = ticketingDeadlines;
        PassportExpiries = passportExpiries;
        ConfirmedWithChanges = confirmedWithChanges;
        StuckOperatorRefunds = stuckOperatorRefunds;
    }

    public object UrgentTrips { get; }

    public object SupplierDebts { get; }

    /// <summary>
    /// ADR-019: UN aviso POR RESERVA cuyo primer servicio empieza dentro de la ventana. Item:
    /// <c>{ reservaPublicId, numeroReserva, name, holderName, firstStartDate, daysLeft }</c>
    /// (<c>daysLeft == 0</c> ⇒ "Empieza HOY" rojo). Se OMITE del JSON con el flag apagado (null).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? UpcomingStarts { get; }

    /// <summary>
    /// ADR-019 D1: los X dias de la ventana (setting <c>ServiceDeadlineAlertDays</c>), para que la pill
    /// por servicio de la fila (100 % frontend) use el MISMO umbral que la campanita. Viaja ACA y no en
    /// <c>OperationalFlagsResponse</c> porque ese DTO tiene la regla dura "solo booleanos" (test de shape).
    /// No es dato sensible: es un umbral de UI. Se OMITE del JSON con el flag apagado (null).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UpcomingStartsWindowDays { get; }

    /// <summary>Servicios "costo a confirmar". Se OMITE del JSON cuando el bucket esta apagado (null).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? CostsToConfirm { get; }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (item 5): alarma "vence el pago al operador" — un aviso POR SERVICIO no
    /// cancelado, de reservas vivas, cuya fecha limite de pago cae dentro de la ventana (3 dias por
    /// defecto) o ya vencio. Item: <c>{ reservaPublicId, numeroReserva, serviceKind, serviceLabel,
    /// deadline, daysLeft }</c> (<c>daysLeft &lt; 0</c> = vencido). Se OMITE del JSON cuando no hay nada
    /// que avisar (null) — el path "sin alarmas y sin buckets nuevos" sigue siendo el objeto historico.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? OperatorPaymentDeadlines { get; }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (item 5): alarma "vence la emision del aereo" (time-limit). Mismo shape
    /// que <see cref="OperatorPaymentDeadlines"/>, solo para segmentos de vuelo no cancelados. Se OMITE
    /// del JSON cuando no hay nada que avisar (null).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? TicketingDeadlines { get; }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (item 8): alarma "pasaporte por vencer" — un aviso POR PASAJERO de
    /// reservas vivas cuyo pasaporte vence dentro de los 6 meses POSTERIORES al inicio del viaje (regla
    /// tipica de vigencia). Item: <c>{ reservaPublicId, numeroReserva, passengerName, passportExpiry,
    /// tripStartDate }</c>. NO expone documento ni nacionalidad (PII minima: solo el nombre que ya viaja
    /// en otros buckets). Se OMITE del JSON cuando no hay nada que avisar (null).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? PassportExpiries { get; }

    /// <summary>
    /// ADR-027 (auditoria ERP, hallazgo #10): reservas "confirmadas con cambios" sin revisar — un aviso POR
    /// RESERVA viva marcada (el operador confirmo con otro precio/condicion y el dueño todavia no dio el OK).
    /// Item: <c>{ reservaPublicId, numeroReserva, name, status, holderName, changesPendingSince }</c>. NO
    /// expone montos. Se OMITE del JSON cuando no hay nada que avisar (null).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ConfirmedWithChanges { get; }

    /// <summary>
    /// ADR-033 (E6/B3, 2026-06-16): reservas atascadas en "esperando refund del operador" cuyo saldo a favor
    /// del cliente quedo SIN consumir — un aviso POR RESERVA + moneda. Item: <c>{ reservaPublicId,
    /// numeroReserva, name, holderName, currency, remainingCredit, since }</c>. Solo VISIBILIDAD (la baja o
    /// devolucion del remanente espera firma de contador). Se OMITE del JSON cuando no hay nada (null).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? StuckOperatorRefunds { get; }

    public int TotalCount { get; }
}
