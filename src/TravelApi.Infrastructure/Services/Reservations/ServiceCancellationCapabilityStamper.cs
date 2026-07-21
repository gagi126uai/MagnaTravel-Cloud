using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// Fix E2E de la Tanda 7 (plan "contrato pantalla-motor", 2026-07-20): UNICO lugar que calcula el
/// pre-chequeo de "anular servicio" (voucher / R1 / sin cliente) y lo traduce a un <see cref="CapabilityDto"/>.
///
/// <para><b>Por que existe</b>: la ficha de la reserva NO arma <c>reserva.hotelBookings[]</c> (ni
/// flights/transfers/packages/assistances) desde <c>ReservaService.GetReservaByIdAsync</c> — los carga por 5
/// endpoints de sub-coleccion DEDICADOS (<c>GET /reservas/{id}/hotels</c>, etc., que atiende
/// <c>BookingService</c>), <c>useReservaDetail.js</c> lo confirma. Antes de este fix, esos 5 endpoints NO
/// calculaban <c>CanCancel</c>: el DTO del detalle completo decia "bloqueado" pero la fila que el usuario
/// ve DE VERDAD en pantalla mostraba la papelera habilitada (hallazgo E2E real, mismo agujero que el que la
/// Tanda 6 ya habia encontrado y cerrado para <c>canEdit</c>/<c>canDelete</c> de los pagos).</para>
///
/// <para>Mismo patron que <c>PaymentCapabilityDtoMapper</c> (Tanda 6): un UNICO armado que usan los DOS
/// caminos (<c>ReservaService.GetReservaByIdAsync</c> Y los 5 <c>BookingService.Get*Async</c>), para que
/// nunca puedan divergir sobre el mismo servicio.</para>
/// </summary>
public static class ServiceCancellationCapabilityStamper
{
    /// <summary>
    /// Hechos UNIFORMES de la reserva (voucher + preflight R1/sin-cliente), calculados UNA sola vez POR
    /// REQUEST — nunca por fila. Se le pasan a <see cref="Evaluate"/> para cada servicio puntual.
    /// </summary>
    public sealed record ReservaInputs(bool HasLiveVoucher, ServiceCancellationPreflightResult Preflight);

    /// <summary>
    /// Junta los 2 hechos reserva-nivel UNA sola vez por request: el motivo de voucher (el MISMO guard real
    /// que usa <c>CancelServiceAsync</c>, <see cref="MutationGuards.GetReservaVoucherOnlyBlockReasonAsync"/>)
    /// y el preflight R1/sin-cliente (short-circuit de <see cref="IBookingCancellationService.GetServiceCancellationPreflightAsync"/>).
    ///
    /// <para><c>null</c> cuando no hay <see cref="IBookingCancellationService"/> inyectado (tests con ctor
    /// viejo) — mismo criterio "no calculado, nunca Allowed=false sin motivo" de toda la tanda. El caller NO
    /// debe llamar a <see cref="Evaluate"/> en ese caso; simplemente deja <c>CanCancel</c> en <c>null</c>.</para>
    ///
    /// <para><paramref name="knownHasLiveVoucher"/> es un atajo OPCIONAL: si el caller YA calculo el motivo
    /// de voucher para otra cosa (ej. <c>ReservaService</c> ya lo necesita para
    /// <c>dto.ServiceCancellationBlockReason</c>), se lo pasa para no pagar esa consulta dos veces en el
    /// mismo request. Los 5 endpoints de sub-coleccion (que NO arman ese campo) lo dejan en <c>null</c> y
    /// esta funcion lo resuelve sola.</para>
    /// </summary>
    public static async Task<ReservaInputs?> LoadInputsAsync(
        AppDbContext db,
        IBookingCancellationService? cancellationService,
        int reservaId,
        CancellationToken ct,
        bool? knownHasLiveVoucher = null)
    {
        if (cancellationService is null) return null;

        bool hasLiveVoucher = knownHasLiveVoucher
            ?? (await MutationGuards.GetReservaVoucherOnlyBlockReasonAsync(db, reservaId, ct)) is not null;

        var preflight = await cancellationService.GetServiceCancellationPreflightAsync(reservaId, ct);
        // Defensivo: un mock PARCIAL de IBookingCancellationService (MockBehavior.Loose, sin configurar este
        // metodo) devuelve null en vez de tirar. Mismo criterio "no calculado" que arriba.
        if (preflight is null) return null;

        return new ReservaInputs(hasLiveVoucher, preflight);
    }

    /// <summary>
    /// Evalua la capacidad de UN servicio puntual a partir de los hechos ya cargados por
    /// <see cref="LoadInputsAsync"/>. Pura (no toca la base): delega en
    /// <see cref="ServiceCancellationPreflightPolicy.Evaluate"/>, la MISMA fuente de texto/orden que usa el
    /// guard real.
    /// </summary>
    public static CapabilityDto Evaluate(ReservaInputs inputs, CancellableServiceTable table, Guid servicePublicId)
    {
        var ctx = new ServiceCancellationPreflightContext(
            HasLiveVoucher: inputs.HasLiveVoucher,
            HasLiveSaleInvoiceWithoutPayer: inputs.Preflight.HasLiveSaleInvoiceWithoutPayer,
            HasUnanchoredOperatorRefund: inputs.Preflight.ServicesBlockedByUnanchoredOperatorRefund
                .Contains((table, servicePublicId)));
        var cap = ServiceCancellationPreflightPolicy.Evaluate(ctx);
        return new CapabilityDto { Allowed = cap.Allowed, Reason = cap.Reason };
    }
}
