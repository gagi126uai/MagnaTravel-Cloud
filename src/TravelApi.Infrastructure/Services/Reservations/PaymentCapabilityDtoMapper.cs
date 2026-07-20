using TravelApi.Application.DTOs;
using TravelApi.Domain.Reservations;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// Tanda 6 (plan de remediacion "contrato pantalla-motor", 2026-07-20): UNICO lugar que traduce el
/// resultado de <see cref="PaymentCapabilityPolicy"/> (un <see cref="Cap"/> por accion) al
/// <see cref="CapabilityDto"/> que viaja en <see cref="PaymentDto.CanEdit"/>/<see cref="PaymentDto.CanDelete"/>.
///
/// <para>Existe porque HAY DOS caminos que arman <c>payments[]</c> para la ficha de la reserva
/// (<c>ReservaService.GetReservaByIdAsync</c> y <c>PaymentService.GetPaymentsForReservaAsync</c>, este
/// ultimo el que el frontend usa de verdad — <c>GET /api/payments/reserva/{id}</c>). Cada uno junta los
/// HECHOS de una forma distinta (uno desde entidades <c>Payment</c> ya cargadas, el otro desde un
/// <c>PaymentDto</c> que ya salio de un <c>ProjectTo</c> + una consulta extra de facturas vivas), pero el
/// PASO FINAL — construir el <c>CapabilityDto</c> a partir del <c>Cap</c> — tiene que ser identico en los
/// dos, sino un mismo pago podria mostrar un candado distinto segun por donde entro a la pantalla.</para>
/// </summary>
public static class PaymentCapabilityDtoMapper
{
    /// <summary>
    /// Evalua <paramref name="context"/> con <see cref="PaymentCapabilityPolicy"/> y estampa el resultado
    /// en <paramref name="dto"/>. Sobrescribe <see cref="PaymentDto.CanEdit"/>/<see cref="PaymentDto.CanDelete"/>
    /// SIEMPRE (nunca los deja en null): llamar a este metodo es la señal de "este endpoint SI calcula la
    /// capacidad de este pago".
    /// </summary>
    public static void Apply(PaymentDto dto, PaymentCapabilityContext context)
    {
        var caps = PaymentCapabilityPolicy.For(context);
        dto.CanEdit = new CapabilityDto { Allowed = caps.CanEdit.Allowed, Reason = caps.CanEdit.Reason };
        dto.CanDelete = new CapabilityDto { Allowed = caps.CanDelete.Allowed, Reason = caps.CanDelete.Reason };
    }
}
