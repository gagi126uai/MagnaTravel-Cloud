using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// B1.15 Fase 0.2 — helper compartido para enmascarar costos en DTOs cuando el
/// caller no tiene <see cref="Permissions.CobranzasSeeCost"/>. Centralizado aca
/// para garantizar simetria entre los endpoints de detalle (ReservaService) y
/// los endpoints de mutacion de bookings (BookingService).
///
/// Reglas:
///  - Admin (rol "Admin") siempre ve costos (bypass).
///  - Si <c>cobranzas.see_cost</c> NO esta presente, NetCost/Commission se
///    setean a 0 en el DTO retornado.
///  - SalePrice nunca se enmascara.
///  - Si no hay HttpContext (tests unitarios sin accessor) o no hay resolver
///    inyectado, se considera "no autorizado a ver costos" — fail-closed para
///    evitar leakage accidental.
///
/// Observacion: este helper opera sobre DTOs ya proyectados (post _mapper.Map).
/// No toca la entidad persistida — los costos siguen viviendo en DB intactos.
/// </summary>
public static class CostMasking
{
    /// <summary>
    /// Devuelve true si el caller actual puede ver costos. Usado por callers
    /// que necesitan la decision para enmascarar a mano (ej: ReservaService
    /// con coleccion de DTOs heterogeneos).
    /// </summary>
    public static async Task<bool> CanSeeCostAsync(
        IHttpContextAccessor? httpContextAccessor,
        IUserPermissionResolver? permissionResolver,
        CancellationToken ct)
    {
        var httpContextUser = httpContextAccessor?.HttpContext?.User;
        // Admin bypass por rol.
        if (httpContextUser?.IsInRole("Admin") ?? false) return true;

        // Sin resolver o sin user resoluble: fail-closed.
        if (permissionResolver is null || httpContextUser is null) return false;

        var userId = httpContextUser.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return false;

        var perms = await permissionResolver.GetPermissionsAsync(userId, ct);
        return perms.Contains(Permissions.CobranzasSeeCost);
    }

    /// <summary>
    /// Enmascara <see cref="HotelBookingDto.NetCost"/> a 0 si el caller no
    /// tiene <c>cobranzas.see_cost</c> y no es Admin. NoOp si el dto es null.
    /// </summary>
    public static async Task MaskHotelAsync(
        HotelBookingDto? dto,
        IHttpContextAccessor? httpContextAccessor,
        IUserPermissionResolver? permissionResolver,
        CancellationToken ct)
    {
        if (dto is null) return;
        if (await CanSeeCostAsync(httpContextAccessor, permissionResolver, ct)) return;
        dto.NetCost = 0m;
        // HotelBookingDto no expone Commission al frontend (verificado 2026-05-09).
        // Si en el futuro se agrega, enmascarar aca tambien.
    }

    // Los DTOs de Flight/Package/Transfer exponen NetCost (no Commission), igual
    // que Hotel (verificado 2026-05-29 leyendo los DTOs). Por eso cada Mask*
    // enmascara solo NetCost. Si manana se agrega Commission al DTO, hay que
    // sumar el enmascarado aca tambien.

    /// <summary>
    /// Enmascara <see cref="FlightSegmentDto.NetCost"/> a 0 si el caller no puede
    /// ver costos. Mismo criterio fail-closed que <see cref="MaskHotelAsync"/>.
    /// </summary>
    public static async Task MaskFlightAsync(
        FlightSegmentDto? dto,
        IHttpContextAccessor? httpContextAccessor,
        IUserPermissionResolver? permissionResolver,
        CancellationToken ct)
    {
        if (dto is null) return;
        if (await CanSeeCostAsync(httpContextAccessor, permissionResolver, ct)) return;
        dto.NetCost = 0m;
    }

    /// <summary>
    /// Enmascara <see cref="PackageBookingDto.NetCost"/> a 0 si el caller no puede
    /// ver costos. Mismo criterio fail-closed que <see cref="MaskHotelAsync"/>.
    /// </summary>
    public static async Task MaskPackageAsync(
        PackageBookingDto? dto,
        IHttpContextAccessor? httpContextAccessor,
        IUserPermissionResolver? permissionResolver,
        CancellationToken ct)
    {
        if (dto is null) return;
        if (await CanSeeCostAsync(httpContextAccessor, permissionResolver, ct)) return;
        dto.NetCost = 0m;
    }

    /// <summary>
    /// Enmascara <see cref="TransferBookingDto.NetCost"/> a 0 si el caller no puede
    /// ver costos. Mismo criterio fail-closed que <see cref="MaskHotelAsync"/>.
    /// </summary>
    public static async Task MaskTransferAsync(
        TransferBookingDto? dto,
        IHttpContextAccessor? httpContextAccessor,
        IUserPermissionResolver? permissionResolver,
        CancellationToken ct)
    {
        if (dto is null) return;
        if (await CanSeeCostAsync(httpContextAccessor, permissionResolver, ct)) return;
        dto.NetCost = 0m;
    }
}
