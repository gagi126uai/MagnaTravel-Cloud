using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-017 F1.3 (§2.8, D8c): accion EXPLICITA "Confirmar costo". Capa fina sobre el update: confirma el
/// costo resuelto (o lo corrige) de un servicio marcado "a confirmar", limpia la marca y dispara el upsert
/// DIFERIDO de <c>RateSupplierSale</c> con <c>LastSoldAt</c> = fecha de la VENTA (no de la confirmacion: la
/// venta ocurrio entonces; usar la fecha de hoy mentiria el "hace N semanas" del dropdown).
///
/// <para>Un GUARDAR normal NUNCA confirma: solo este boton lo hace (decision D8c). Confirmar un costo 0 desde
/// el boton VALE (aserción humana deliberada). Es idempotente: confirmar un servicio sin marca es no-op.</para>
///
/// <para><b>Decision del dueño 2 (2026-06-05) — se permite confirmar aunque la reserva este FACTURADA</b>:
/// confirm-cost NO pasa por los MutationGuards de inmutabilidad post-CAE/voucher. El comprobante fiscal se
/// arma por SalePrice (precio de venta), que esta accion NO toca: solo corrige el COSTO INTERNO (la deuda al
/// operador y la ganancia de la agencia). Por eso es seguro confirmar el costo despues de emitir la factura.
/// (ADR-017 §2.8.)</para>
/// </summary>
public partial class BookingService
{
    // Decision del dueño 2 (ver summary de la clase): confirm-cost se permite aunque la reserva este
    // facturada -> a proposito NO pasa por MutationGuards (solo corrige costo interno, no el comprobante).
    public async Task<HotelBookingDto> ConfirmHotelCostAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, ConfirmCostRequest body, CancellationToken ct)
    {
        await EnsureCatalogEnabledForConfirmAsync(ct);
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var id = await ResolveRequiredIdAsync<HotelBooking>(publicIdOrLegacyId, ct);

        return await RunCatalogTransactionAsync(async () =>
        {
            var hotel = await _hotelRepo.GetByIdAsync(id, ct);
            if (hotel == null || hotel.ReservaId != reservaId) throw new KeyNotFoundException("Hotel no encontrado");

            if (hotel.CostToConfirm)
            {
                ApplyConfirmedCost(body, () => hotel.NetCost, () => hotel.Tax, v => hotel.NetCost = v, v => hotel.Tax = v,
                    () => hotel.SalePrice, v => hotel.Commission = v);
                hotel.CostToConfirm = false; hotel.CostToConfirmReason = null;
                await _hotelRepo.UpdateAsync(hotel, ct);
                await RefreshBalancesAfterCostConfirmAsync(hotel.SupplierId, reservaId, ct);

                var divisor = CatalogUnitization.HotelDivisor(hotel.Nights, hotel.Rooms);
                var unit = CatalogUnitization.ForHotel(hotel.NetCost, hotel.Tax, hotel.SalePrice, hotel.Nights, hotel.Rooms);
                await UpsertConfirmedSaleAsync(hotel.RateId, hotel.SupplierId, unit, hotel.Currency, hotel.CreatedAt, ct);
            }

            var dto = _mapper.Map<HotelBookingDto>(hotel);
            await CostMasking.MaskHotelAsync(dto, _httpContextAccessor, _permissionResolver, ct);
            return dto;
        }, ct);
    }

    // Decision del dueño 2 (ver summary de la clase): confirm-cost se permite aunque la reserva este
    // facturada -> a proposito NO pasa por MutationGuards (solo corrige costo interno, no el comprobante).
    public async Task<FlightSegmentDto> ConfirmFlightCostAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, ConfirmCostRequest body, CancellationToken ct)
    {
        await EnsureCatalogEnabledForConfirmAsync(ct);
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var id = await ResolveRequiredIdAsync<FlightSegment>(publicIdOrLegacyId, ct);

        return await RunCatalogTransactionAsync(async () =>
        {
            var flight = await _flightRepo.GetByIdAsync(id, ct);
            if (flight == null || flight.ReservaId != reservaId) throw new KeyNotFoundException("Vuelo no encontrado");

            if (flight.CostToConfirm)
            {
                ApplyConfirmedCost(body, () => flight.NetCost, () => flight.Tax, v => flight.NetCost = v, v => flight.Tax = v,
                    () => flight.SalePrice, v => flight.Commission = v);
                flight.CostToConfirm = false; flight.CostToConfirmReason = null;
                await _flightRepo.UpdateAsync(flight, ct);
                await RefreshBalancesAfterCostConfirmAsync(flight.SupplierId, reservaId, ct);

                var unit = CatalogUnitization.ForFlight(flight.NetCost, flight.Tax, flight.SalePrice, flight.PassengerCount ?? 1);
                await UpsertConfirmedSaleAsync(flight.RateId, flight.SupplierId, unit, flight.Currency, flight.CreatedAt, ct);
            }

            var dto = _mapper.Map<FlightSegmentDto>(flight);
            await CostMasking.MaskFlightAsync(dto, _httpContextAccessor, _permissionResolver, ct);
            return dto;
        }, ct);
    }

    // Decision del dueño 2 (ver summary de la clase): confirm-cost se permite aunque la reserva este
    // facturada -> a proposito NO pasa por MutationGuards (solo corrige costo interno, no el comprobante).
    public async Task<TransferBookingDto> ConfirmTransferCostAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, ConfirmCostRequest body, CancellationToken ct)
    {
        await EnsureCatalogEnabledForConfirmAsync(ct);
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var id = await ResolveRequiredIdAsync<TransferBooking>(publicIdOrLegacyId, ct);

        return await RunCatalogTransactionAsync(async () =>
        {
            var transfer = await _transferRepo.GetByIdAsync(id, ct);
            if (transfer == null || transfer.ReservaId != reservaId) throw new KeyNotFoundException("Traslado no encontrado");

            if (transfer.CostToConfirm)
            {
                ApplyConfirmedCost(body, () => transfer.NetCost, () => transfer.Tax, v => transfer.NetCost = v, v => transfer.Tax = v,
                    () => transfer.SalePrice, v => transfer.Commission = v);
                transfer.CostToConfirm = false; transfer.CostToConfirmReason = null;
                await _transferRepo.UpdateAsync(transfer, ct);
                await RefreshBalancesAfterCostConfirmAsync(transfer.SupplierId, reservaId, ct);

                var unit = CatalogUnitization.ForTransfer(transfer.NetCost, transfer.Tax, transfer.SalePrice);
                await UpsertConfirmedSaleAsync(transfer.RateId, transfer.SupplierId, unit, transfer.Currency, transfer.CreatedAt, ct);
            }

            var dto = _mapper.Map<TransferBookingDto>(transfer);
            await CostMasking.MaskTransferAsync(dto, _httpContextAccessor, _permissionResolver, ct);
            return dto;
        }, ct);
    }

    // Decision del dueño 2 (ver summary de la clase): confirm-cost se permite aunque la reserva este
    // facturada -> a proposito NO pasa por MutationGuards (solo corrige costo interno, no el comprobante).
    public async Task<PackageBookingDto> ConfirmPackageCostAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, ConfirmCostRequest body, CancellationToken ct)
    {
        await EnsureCatalogEnabledForConfirmAsync(ct);
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var id = await ResolveRequiredIdAsync<PackageBooking>(publicIdOrLegacyId, ct);

        return await RunCatalogTransactionAsync(async () =>
        {
            var package = await _packageRepo.GetByIdAsync(id, ct);
            if (package == null || package.ReservaId != reservaId) throw new KeyNotFoundException("Paquete no encontrado");

            if (package.CostToConfirm)
            {
                ApplyConfirmedCost(body, () => package.NetCost, () => package.Tax, v => package.NetCost = v, v => package.Tax = v,
                    () => package.SalePrice, v => package.Commission = v);
                package.CostToConfirm = false; package.CostToConfirmReason = null;
                await _packageRepo.UpdateAsync(package, ct);
                await RefreshBalancesAfterCostConfirmAsync(package.SupplierId, reservaId, ct);

                var unit = CatalogUnitization.ForPackage(package.NetCost, package.Tax, package.SalePrice, package.Adults, package.Children);
                await UpsertConfirmedSaleAsync(package.RateId, package.SupplierId, unit, package.Currency, package.CreatedAt, ct);
            }

            var dto = _mapper.Map<PackageBookingDto>(package);
            await CostMasking.MaskPackageAsync(dto, _httpContextAccessor, _permissionResolver, ct);
            return dto;
        }, ct);
    }

    // Decision del dueño 2 (ver summary de la clase): confirm-cost se permite aunque la reserva este
    // facturada -> a proposito NO pasa por MutationGuards (solo corrige costo interno, no el comprobante).
    public async Task<AssistanceBookingDto> ConfirmAssistanceCostAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, ConfirmCostRequest body, CancellationToken ct)
    {
        await EnsureCatalogEnabledForConfirmAsync(ct);
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var id = await ResolveRequiredIdAsync<AssistanceBooking>(publicIdOrLegacyId, ct);

        return await RunCatalogTransactionAsync(async () =>
        {
            var assistance = await _assistanceRepo.GetByIdAsync(id, ct);
            if (assistance == null || assistance.ReservaId != reservaId) throw new KeyNotFoundException("Asistencia no encontrada");

            if (assistance.CostToConfirm)
            {
                ApplyConfirmedCost(body, () => assistance.NetCost, () => assistance.Tax, v => assistance.NetCost = v, v => assistance.Tax = v,
                    () => assistance.SalePrice, v => assistance.Commission = v);
                assistance.CostToConfirm = false; assistance.CostToConfirmReason = null;
                await _assistanceRepo.UpdateAsync(assistance, ct);
                await RefreshBalancesAfterCostConfirmAsync(assistance.SupplierId, reservaId, ct);

                var days = CatalogUnitization.AssistanceDays(assistance.ValidFrom, assistance.ValidTo);
                var unit = CatalogUnitization.ForAssistance(assistance.NetCost, assistance.Tax, assistance.SalePrice, assistance.Adults, assistance.Children, days);
                await UpsertConfirmedSaleAsync(assistance.RateId, assistance.SupplierId, unit, assistance.Currency, assistance.CreatedAt, ct);
            }

            var dto = _mapper.Map<AssistanceBookingDto>(assistance);
            await CostMasking.MaskAssistanceAsync(dto, _httpContextAccessor, _permissionResolver, ct);
            return dto;
        }, ct);
    }

    // ============================================================ helpers ============================================================

    /// <summary>Flag OFF -> el endpoint "no existe" (404). Mismo criterio que catalog-search.</summary>
    private async Task EnsureCatalogEnabledForConfirmAsync(CancellationToken ct)
    {
        if (!await IsCatalogFindOrCreateEnabledAsync(ct))
            throw new FeatureNotEnabledException("El catalogo find-or-create no esta habilitado.");
    }

    /// <summary>
    /// Aplica el costo confirmado al servicio: si el body trae correccion la usa, si no conserva el costo
    /// resuelto; recalcula la ganancia canonica (SalePrice - NetCost - Tax). Confirmar 0 vale.
    /// </summary>
    private static void ApplyConfirmedCost(
        ConfirmCostRequest body,
        Func<decimal> getNet, Func<decimal> getTax, Action<decimal> setNet, Action<decimal> setTax,
        Func<decimal> getSale, Action<decimal> setCommission)
    {
        // Decision del dueño 1 (2026-06-05): una correccion de costo negativa se rechaza (400). El 0 vale.
        EnsureNonNegativeCost(body.NetCost ?? 0m, body.Tax ?? 0m);

        if (body.NetCost.HasValue) setNet(body.NetCost.Value);
        if (body.Tax.HasValue) setTax(body.Tax.Value);
        setCommission(getSale() - getNet() - getTax());
    }

    /// <summary>
    /// B1 (backend review 2026-06-05): tras confirmar un costo cambian NetCost/Tax del servicio, asi que hay
    /// que refrescar los saldos CACHEADOS — la deuda al operador (<c>Supplier.CurrentBalance</c>) y el saldo
    /// de la reserva — igual que hace el alta del catalogo (<c>BookingService.CatalogCreates</c>). Sin esto,
    /// confirmar un costo 0 -> 200 dejaba la deuda al operador subestimada hasta el proximo evento que
    /// recalculara el balance. Corre DENTRO de la misma transaccion del confirm (ver invariante en
    /// <see cref="RunCatalogTransactionAsync{T}"/>).
    /// </summary>
    private async Task RefreshBalancesAfterCostConfirmAsync(int supplierId, int reservaId, CancellationToken ct)
    {
        if (supplierId > 0) await _supplierService.UpdateBalanceAsync(supplierId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
    }

    /// <summary>Upsert diferido tras confirmar: solo si el servicio tiene producto y operador real.</summary>
    private async Task UpsertConfirmedSaleAsync(
        int? rateId, int supplierId, CatalogUnitization.Unitized unit, string? currency, DateTime soldAt,
        CancellationToken ct)
    {
        if (!rateId.HasValue || supplierId <= 0) return;
        await UpsertRateSupplierSaleAsync(rateId.Value, supplierId, unit, NormalizeCurrency(currency), soldAt, ct);
    }
}
